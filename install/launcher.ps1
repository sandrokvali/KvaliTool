<#
    KvaliTool launcher.

    Run it with:
        irm <launcher url> | iex

    What it does:
      1. Downloads ONE file, kvalitool.exe (a self-contained build, no .NET install needed), to
         %LOCALAPPDATA%\KvaliTool\bin\<version>\.
      2. Refuses to run it unless its SHA-256 matches the hash written into this launcher at release time.
      3. Shows a menu. Phase 1 only scans and reports: nothing on your PC is changed.

    It never asks for administrator rights on its own. Only menu option 2 does, and it says so first.

    Options (when started with: & ([scriptblock]::Create((irm <url>))) -Action scan):
      -Action      menu | scan | scan-admin | categories    (or set $env:KVALITOOL_ACTION)
      -InstallDir  where to keep kvalitool.exe (default %LOCALAPPDATA%\KvaliTool\bin)

    This file must stay plain ASCII and Windows PowerShell 5.1 compatible.
    It must never call 'exit': it runs inside your PowerShell window and would close it.
#>
param(
    [string]$Action = '',
    [string]$InstallDir = ''
)

& {
    param([string]$Action, [string]$InstallDir)

    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'   # Invoke-WebRequest is many times faster without the progress bar in 5.1

    # Filled in by tools\Publish-Release.ps1. Do not edit by hand.
    $Version = '__VERSION__'
    $ExeUrl = '__EXE_URL__'
    $ExeSha256 = '__EXE_SHA256__'

    if ($Version.StartsWith('__')) {
        throw 'This is the launcher template. Use the copy in dist\ produced by tools\Publish-Release.ps1.'
    }

    if (-not $Action) { $Action = $env:KVALITOOL_ACTION }
    if (-not $Action) { $Action = 'menu' }
    if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'KvaliTool\bin' }

    if (-not [Environment]::Is64BitOperatingSystem) { throw 'KvaliTool needs 64-bit Windows.' }

    # Windows PowerShell 5.1 may default to TLS 1.0, which GitHub rejects.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }

    $dir = Join-Path $InstallDir $Version
    $exe = Join-Path $dir 'kvalitool.exe'

    if (Test-Path -LiteralPath $exe) {
        if ((Get-Sha256 $exe) -ne $ExeSha256) {
            throw "The cached $exe does not match the expected SHA-256, so it was NOT run. It may have been modified. Delete the folder $dir yourself and run the launcher again."
        }
        Write-Host "KvaliTool $Version (verified, already downloaded)" -ForegroundColor Cyan
    }
    else {
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        $partial = "$exe.download"
        Write-Host "Downloading KvaliTool $Version from $ExeUrl ..." -ForegroundColor Cyan
        Invoke-WebRequest -Uri $ExeUrl -OutFile $partial -UseBasicParsing
        $actual = Get-Sha256 $partial
        if ($actual -ne $ExeSha256) {
            # Our own half-finished download, never anything of yours.
            Remove-Item -LiteralPath $partial -Force
            throw "Download rejected: SHA-256 is $actual but this launcher expects $ExeSha256. Nothing was run."
        }
        Rename-Item -LiteralPath $partial -NewName 'kvalitool.exe'
        Write-Host "KvaliTool $Version downloaded and verified (SHA-256 $ExeSha256)." -ForegroundColor Cyan
    }

    function Start-Scan {
        & $exe scan
        if ($LASTEXITCODE -eq 2) { Write-Host 'The safety layer blocked items (see above). Please report this; it is a bug.' -ForegroundColor Red }
    }

    function Start-AdminScan {
        Write-Host ''
        Write-Host 'Windows will ask for administrator rights. They are needed only to READ these locations:' -ForegroundColor Yellow
        Write-Host '  Windows Temp, Windows Update downloads, Delivery Optimization, system error reports and logs,'
        Write-Host '  Windows.old, the component store (DISM analysis), System Restore points.'
        Write-Host 'The scan opens in a new window and still changes nothing.'
        # -EncodedCommand avoids every quoting problem with spaces or apostrophes in the path.
        $command = "& '$($exe.Replace("'", "''"))' scan; Write-Host ''; Read-Host 'Press Enter to close'"
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        try {
            Start-Process -FilePath 'powershell.exe' -ArgumentList '-NoProfile', '-EncodedCommand', $encoded -Verb RunAs
        }
        catch {
            Write-Host 'Elevation was cancelled. Nothing was run.' -ForegroundColor Yellow
        }
    }

    switch ($Action) {
        'scan' { Start-Scan; return }
        'scan-admin' { Start-AdminScan; return }
        'categories' { & $exe categories; return }
        'menu' { }
        default { throw "Unknown action '$Action'. Use menu, scan, scan-admin or categories." }
    }

    while ($true) {
        Write-Host ''
        Write-Host "KvaliTool $Version - Phase 1: scan and report only. Nothing on your PC is changed." -ForegroundColor Cyan
        Write-Host '  1  Scan (no administrator rights)'
        Write-Host '  2  Scan including system locations (asks for administrator rights, new window)'
        Write-Host '  3  List categories and how each would be cleaned'
        Write-Host '  4  Open the folder with saved scan reports'
        Write-Host '  Q  Quit'
        $choice = Read-Host 'Choose'
        switch ($choice.Trim().ToUpperInvariant()) {
            '1' { Start-Scan }
            '2' { Start-AdminScan }
            '3' { & $exe categories }
            '4' {
                $reports = Join-Path $env:LOCALAPPDATA 'KvaliTool\reports'
                if (Test-Path -LiteralPath $reports) { Start-Process explorer.exe -ArgumentList "`"$reports`"" }
                else { Write-Host 'No reports yet. Run a scan first.' }
            }
            'Q' { return }
            default { Write-Host 'Please type 1, 2, 3, 4 or Q.' }
        }
    }
} $Action $InstallDir
