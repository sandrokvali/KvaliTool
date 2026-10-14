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
      - the window app zip is downloaded, verified, unzipped, re-verified on the next start, and opens;
      - a tampered download (exe or zip) is rejected and nothing runs;
      - a tampered cached exe or zip is refused and left alone (not deleted, not run);
      - a modified, or an extra (planted), file in the unzipped app is refused and left alone.
    Downloads go to a new folder under artifacts\launcher-test, never to your real %LOCALAPPDATA%\KvaliTool.
#>
[CmdletBinding()]
param([int]$Port = 8765)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $repo "artifacts\launcher-test\$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$served = Join-Path $work 'served'
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
$zipName = "KvaliTool-$version-win-x64.zip"
$script:failures = 0
function Check([bool]$ok, [string]$what) {
    if ($ok) { Write-Host "  PASS  $what" -ForegroundColor Green } else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failures++ }
}

& (Join-Path $PSScriptRoot 'Publish-Release.ps1') -DownloadBaseUrl "http://localhost:$Port/good" | Out-Host
$dist = Join-Path $repo 'dist'

# good\ = the real release. bad\ = same launcher, but the exe and the zip each have one extra byte (a "tampered" download).
New-Item -ItemType Directory -Force "$served\good", "$served\bad" | Out-Null
Copy-Item "$dist\kvalitool.exe", "$dist\$zipName", "$dist\kvalitool.ps1" "$served\good"
Copy-Item "$dist\kvalitool.exe", "$dist\$zipName" "$served\bad"
[IO.File]::AppendAllText("$served\bad\kvalitool.exe", 'x')
[IO.File]::AppendAllText("$served\bad\$zipName", 'x')
(Get-Content "$dist\kvalitool.ps1" -Raw).Replace("/good/", "/bad/") |
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

function Get-AppProcess([string]$under) {
    @(Get-Process -Name KvaliTool -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($under, [StringComparison]::OrdinalIgnoreCase) })
}

# A fresh LOCALAPPDATA with a good, verified install of the window app (for the tamper cases).
function New-GoodInstall([string]$name) {
    $l = Join-Path $work $name
    $null = Invoke-OneLiner 'pwsh.exe' $good $l 'app-install'
    $l
}

$good = "http://localhost:$Port/good/kvalitool.ps1"
$bad = "http://localhost:$Port/bad/kvalitool.ps1"

try {
    Start-Sleep -Milliseconds 800
    Write-Host "`nResults" -ForegroundColor Cyan

    # --- Console scanner ---
    foreach ($shell in 'powershell.exe', 'pwsh.exe') {
        $lad = Join-Path $work "home-$($shell.Replace('.exe',''))"
        $r = Invoke-OneLiner $shell $good $lad 'categories'
        $exe = Join-Path $lad "KvaliTool\bin\$version\kvalitool.exe"
        Check ($r.Exit -eq 0 -and $r.Output -match 'downloaded and verified' -and $r.Output -match 'UserTemp') "${shell}: 'irm | iex' downloads, verifies and runs the exe"
        if ($r.Exit -ne 0) { $r.Output | Out-Host }
        Check (Test-Path -LiteralPath $exe) "${shell}: exe installed under LOCALAPPDATA\KvaliTool\bin\$version"

        $again = Invoke-OneLiner $shell $good $lad 'categories'
        Check ($again.Exit -eq 0 -and $again.Output -match 'already downloaded') "${shell}: second run reuses the verified copy"
    }

    $exe = Join-Path $work "home-pwsh\KvaliTool\bin\$version\kvalitool.exe"
    $json = Join-Path $work 'real-scan.json'
    & $exe scan --only UserTemp,ShaderCache,ChromeCache --top 0 --json $json | Out-Null
    Check ($LASTEXITCODE -eq 0 -and (Get-Content $json -Raw | ConvertFrom-Json).mode -eq 'DryRun') "Published (trimmed) exe runs a real read-only scan and writes a report"

    Write-Host "  ...running the full sandbox safety test against the published exe" -ForegroundColor DarkGray
    pwsh -NoProfile -File (Join-Path $PSScriptRoot 'Test-Phase1.ps1') -Exe $exe *> (Join-Path $work 'phase1-with-exe.log')
    Check ($LASTEXITCODE -eq 0) "Phase 1 sandbox safety test passes with the published exe (log: phase1-with-exe.log)"

    $lad = Join-Path $work 'home-tampered-download'
    $r = Invoke-OneLiner 'powershell.exe' $bad $lad 'categories'
    Check ($r.Exit -ne 0 -and $r.Output -match 'Download rejected') "Tampered download is rejected"
    Check (-not (Get-ChildItem -LiteralPath $lad -Recurse -Filter 'kvalitool.exe*' -ErrorAction SilentlyContinue)) "...and nothing from it is left or run"

    $lad = Join-Path $work 'home-powershell'
    $cached = Join-Path $lad "KvaliTool\bin\$version\kvalitool.exe"
    [IO.File]::AppendAllText($cached, 'x')
    $sizeBefore = (Get-Item -LiteralPath $cached).Length
    $r = Invoke-OneLiner 'powershell.exe' $good $lad 'categories'
    Check ($r.Exit -ne 0 -and $r.Output -match 'does not match' -and $r.Output -notmatch 'UserTemp') "Modified cached exe is refused and not run"
    Check ((Get-Item -LiteralPath $cached).Length -eq $sizeBefore) "...and left in place for you to inspect (not deleted)"

    # --- Window app (zip) ---
    Write-Host "  ...window app" -ForegroundColor DarkGray
    $manifest = Join-Path $repo 'src\KvaliTool.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\SHA256SUMS.txt'
    $manifestCount = @(Get-Content -LiteralPath $manifest).Count
    foreach ($shell in 'powershell.exe', 'pwsh.exe') {
        $lad = Join-Path $work "app-$($shell.Replace('.exe',''))"
        $appDir = Join-Path $lad "KvaliTool\app\$version"
        $r = Invoke-OneLiner $shell $good $lad 'app-install'
        Check ($r.Exit -eq 0 -and $r.Output -match "$([regex]::Escape($zipName)) downloaded and verified" -and $r.Output -match 'window app ready') "${shell}: zip downloaded, verified and unzipped"
        if ($r.Exit -ne 0) { $r.Output | Out-Host }
        $files = @(Get-ChildItem -LiteralPath $appDir -Recurse -File -ErrorAction SilentlyContinue)
        Check ((Test-Path -LiteralPath (Join-Path $appDir 'KvaliTool.exe')) -and $files.Count -eq $manifestCount + 1) "${shell}: KvaliTool.exe and all $manifestCount listed files are in LOCALAPPDATA\KvaliTool\app\$version"
        Check (@(Get-ChildItem -LiteralPath (Split-Path $appDir -Parent) -Directory -Filter '*.unzipping-*').Count -eq 0) "${shell}: no half-finished unzip folder left"
        Check (-not (Test-Path -LiteralPath (Join-Path $lad "KvaliTool\bin\$version\kvalitool.exe"))) "${shell}: the window app doesn't download the console exe"

        $again = Invoke-OneLiner $shell $good $lad 'app-install'
        Check ($again.Exit -eq 0 -and $again.Output -match 'already downloaded' -and $again.Output -match 'window app \(verified\)') "${shell}: second start re-verifies the zip and every unzipped file"
    }

    $lad = Join-Path $work 'app-pwsh'
    $r = Invoke-OneLiner 'pwsh.exe' $good $lad 'app'
    Start-Sleep -Seconds 4
    $running = Get-AppProcess (Join-Path $lad 'KvaliTool\app')
    Check ($r.Exit -eq 0 -and $running.Count -ge 1 -and -not $running[0].HasExited) "'app' opens the verified KvaliTool window (process running from the unzipped folder)"
    $running | Stop-Process -Force

    $lad = Join-Path $work 'app-tampered-download'
    $r = Invoke-OneLiner 'powershell.exe' $bad $lad 'app-install'
    Check ($r.Exit -ne 0 -and $r.Output -match 'Download rejected') "Tampered zip download is rejected"
    Check (@(Get-ChildItem -LiteralPath $lad -Recurse -File -ErrorAction SilentlyContinue).Count -eq 0) "...and nothing from it is left, unzipped or run"

    $lad = New-GoodInstall 'app-tampered-zip'
    $cachedZip = Join-Path $lad "KvaliTool\bin\$version\$zipName"
    [IO.File]::AppendAllText($cachedZip, 'x')
    $sizeBefore = (Get-Item -LiteralPath $cachedZip).Length
    $r = Invoke-OneLiner 'powershell.exe' $good $lad 'app'
    Check ($r.Exit -ne 0 -and $r.Output -match 'does not match' -and (Get-AppProcess (Join-Path $lad 'KvaliTool\app')).Count -eq 0) "Modified cached zip is refused and the app is not run"
    Check ((Get-Item -LiteralPath $cachedZip).Length -eq $sizeBefore) "...and the zip is left in place (not deleted)"

    $lad = New-GoodInstall 'app-modified-file'
    $victim = Get-ChildItem -LiteralPath (Join-Path $lad "KvaliTool\app\$version") -Filter '*.dll' -File | Select-Object -First 1
    [IO.File]::AppendAllText($victim.FullName, 'x')
    $sizeBefore = (Get-Item -LiteralPath $victim.FullName).Length
    $r = Invoke-OneLiner 'powershell.exe' $good $lad 'app'
    Check ($r.Exit -ne 0 -and $r.Output -match "modified: $([regex]::Escape($victim.Name))" -and (Get-AppProcess (Join-Path $lad 'KvaliTool\app')).Count -eq 0) "Modified unzipped file ($($victim.Name)) is refused and the app is not run"
    Check ((Get-Item -LiteralPath $victim.FullName).Length -eq $sizeBefore) "...and the file is left in place (not deleted)"

    $lad = New-GoodInstall 'app-planted-dll'
    $planted = Join-Path $lad "KvaliTool\app\$version\version.dll"
    Set-Content -LiteralPath $planted -Value 'not really a dll' -Encoding ascii
    $r = Invoke-OneLiner 'pwsh.exe' $good $lad 'app'
    Check ($r.Exit -ne 0 -and $r.Output -match 'unexpected file: version\.dll' -and (Get-AppProcess (Join-Path $lad 'KvaliTool\app')).Count -eq 0) "Extra (planted) DLL in the app folder is refused and the app is not run"
    Check (Test-Path -LiteralPath $planted) "...and the planted file is left for you to inspect (not deleted)"
}
finally {
    Stop-Job $server; Remove-Job $server
}

Write-Host ''
if ($script:failures -eq 0) { Write-Host "ALL LAUNCHER CHECKS PASSED  (work folder: $work)" -ForegroundColor Green; exit 0 }
else { Write-Host "$script:failures LAUNCHER CHECK(S) FAILED  (work folder: $work)" -ForegroundColor Red; exit 1 }
