#Requires -Version 7.0
<#
.SYNOPSIS
    End-to-end test of the one-line installer ("irm <url> | iex") without putting anything online.

.DESCRIPTION
    Publishes a release pointed at http://localhost:8765, serves dist\ from a tiny local web server that
    answers like GitHub Releases (application/octet-stream), then checks:
      - the literal "irm ... | iex" line works in Windows PowerShell 5.1 and in PowerShell 7;
      - the downloaded exe is verified and actually runs (categories + a real read-only scan);
      - the full Phase 1 sandbox safety test passes against the published (trimmed) exe;
      - a tampered download is rejected and nothing runs;
      - a tampered cached exe is refused and left alone (not deleted, not run).
    Downloads go to a new folder under artifacts\launcher-test, never to your real %LOCALAPPDATA%\KvaliTool.
#>
[CmdletBinding()]
param([int]$Port = 8765)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $repo "artifacts\launcher-test\$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$served = Join-Path $work 'served'
$script:failures = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  PASS  $what" -ForegroundColor Green } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failures++ }
}

& (Join-Path $PSScriptRoot 'Publish-Release.ps1') -DownloadBaseUrl "http://localhost:$Port/good" | Out-Host
$dist = Join-Path $repo 'dist'

# good\ = the real release. bad\ = same launcher, but the exe has one extra byte (a "tampered" download).
New-Item -ItemType Directory -Force "$served\good", "$served\bad" | Out-Null
Copy-Item "$dist\kvalitool.exe", "$dist\kvalitool.ps1" "$served\good"
Copy-Item "$dist\kvalitool.exe" "$served\bad"
[IO.File]::AppendAllText("$served\bad\kvalitool.exe", 'x')
(Get-Content "$dist\kvalitool.ps1" -Raw).Replace("/good/kvalitool.exe", "/bad/kvalitool.exe") |
    Set-Content "$served\bad\kvalitool.ps1" -Encoding ascii -NoNewline

$server = Start-Job -ArgumentList $served, "http://localhost:$Port/" -ScriptBlock {
    param($root, $prefix)
    $listener = [Net.HttpListener]::new()
    $listener.Prefixes.Add($prefix)
    $listener.Start()
    while ($listener.IsListening) {
        $ctx = $listener.GetContext()
        $relative = $ctx.Request.Url.AbsolutePath.TrimStart('/').Replace('/', '\')
        $file = Join-Path $root $relative
        if ($relative -notmatch '\.\.' -and (Test-Path -LiteralPath $file -PathType Leaf)) {
            $bytes = [IO.File]::ReadAllBytes($file)
            $ctx.Response.ContentType = 'application/octet-stream'   # what GitHub Releases sends
            $ctx.Response.ContentLength64 = $bytes.Length
            $ctx.Response.OutputStream.Write($bytes, 0, $bytes.Length)
        }
        else { $ctx.Response.StatusCode = 404 }
        $ctx.Response.Close()
    }
}

# Runs the literal one-liner in a fresh shell. LOCALAPPDATA is pointed at a test folder so the launcher's
# default install location is inside the test folder, exactly like a first run on a new PC.
function Invoke-OneLiner([string]$shell, [string]$url, [string]$localAppData, [string]$action) {
    New-Item -ItemType Directory -Force $localAppData | Out-Null
    $cmd = "`$env:LOCALAPPDATA = '$($localAppData.Replace("'", "''"))'; `$env:KVALITOOL_ACTION = '$action'; irm $url | iex"
    $output = & $shell -NoProfile -NonInteractive -Command $cmd 2>&1 | Out-String
    [pscustomobject]@{ Exit = $LASTEXITCODE; Output = $output }
}

try {
    Start-Sleep -Milliseconds 800
    Write-Host "`nResults" -ForegroundColor Cyan

    foreach ($shell in 'powershell.exe', 'pwsh.exe') {
        $lad = Join-Path $work "home-$($shell.Replace('.exe',''))"
        $r = Invoke-OneLiner $shell "http://localhost:$Port/good/kvalitool.ps1" $lad 'categories'
        $exe = Join-Path $lad 'KvaliTool\bin\0.1.0\kvalitool.exe'
        Check ($r.Exit -eq 0 -and $r.Output -match 'downloaded and verified' -and $r.Output -match 'UserTemp') "${shell}: 'irm | iex' downloads, verifies and runs the exe"
        if ($r.Exit -ne 0) { $r.Output | Out-Host }
        Check (Test-Path -LiteralPath $exe) "${shell}: exe installed under LOCALAPPDATA\KvaliTool\bin\0.1.0"

        $again = Invoke-OneLiner $shell "http://localhost:$Port/good/kvalitool.ps1" $lad 'categories'
        Check ($again.Exit -eq 0 -and $again.Output -match 'already downloaded') "${shell}: second run reuses the verified copy"
    }

    $exe = Join-Path $work 'home-pwsh\KvaliTool\bin\0.1.0\kvalitool.exe'
    $json = Join-Path $work 'real-scan.json'
    & $exe scan --only UserTemp,ShaderCache,ChromeCache --top 0 --json $json | Out-Null
    Check ($LASTEXITCODE -eq 0 -and (Get-Content $json -Raw | ConvertFrom-Json).mode -eq 'DryRun') "Published (trimmed) exe runs a real read-only scan and writes a report"

    Write-Host "  ...running the full sandbox safety test against the published exe" -ForegroundColor DarkGray
    pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-Phase1.ps1') -Exe $exe *> (Join-Path $work 'phase1-with-exe.log')
    Check ($LASTEXITCODE -eq 0) "Phase 1 sandbox safety test passes with the published exe (log: phase1-with-exe.log)"

    $lad = Join-Path $work 'home-tampered-download'
    $r = Invoke-OneLiner 'powershell.exe' "http://localhost:$Port/bad/kvalitool.ps1" $lad 'categories'
    Check ($r.Exit -ne 0 -and $r.Output -match 'Download rejected') "Tampered download is rejected"
    Check (-not (Get-ChildItem -LiteralPath $lad -Recurse -Filter 'kvalitool.exe*' -ErrorAction SilentlyContinue)) "...and nothing from it is left or run"

    $lad = Join-Path $work 'home-powershell'
    $cached = Join-Path $lad 'KvaliTool\bin\0.1.0\kvalitool.exe'
    [IO.File]::AppendAllText($cached, 'x')
    $sizeBefore = (Get-Item -LiteralPath $cached).Length
    $r = Invoke-OneLiner 'powershell.exe' "http://localhost:$Port/good/kvalitool.ps1" $lad 'categories'
    Check ($r.Exit -ne 0 -and $r.Output -match 'does not match' -and $r.Output -notmatch 'UserTemp') "Modified cached exe is refused and not run"
    Check ((Get-Item -LiteralPath $cached).Length -eq $sizeBefore) "...and left in place for you to inspect (not deleted)"
}
finally {
    Stop-Job $server; Remove-Job $server
}

Write-Host ''
if ($script:failures -eq 0) { Write-Host "ALL LAUNCHER CHECKS PASSED  (work folder: $work)" -ForegroundColor Green; exit 0 }
else { Write-Host "$script:failures LAUNCHER CHECK(S) FAILED  (work folder: $work)" -ForegroundColor Red; exit 1 }
