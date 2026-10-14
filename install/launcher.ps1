<#
    KvaliTool launcher.

    Run it with:
        irm <launcher url> | iex

    What it does:
      1. Opens the KvaliTool window (or, with -Action menu, shows a menu with the console scanner too).
         Phase 2 scans and shows results only: nothing on your PC is changed.
      2. Downloads only what the chosen option needs, to %LOCALAPPDATA%\KvaliTool\bin\<version>\:
           - the window app: KvaliTool-<version>-win-x64.zip (self-contained, no .NET install needed),
             unzipped to %LOCALAPPDATA%\KvaliTool\app\<version>\;
           - the console scanner: kvalitool.exe (self-contained single file).
      3. Refuses to run anything unless its SHA-256 matches the hash written into this launcher at release
         time. For the window app that check is repeated on every start: the cached zip, and every unzipped
         file against the SHA256SUMS.txt list inside that verified zip. An unexpected extra file (for example
         a planted DLL) also makes it refuse.

    It never asks for administrator rights on its own. Only the "system locations" scan does, and it says so first.
    It never deletes anything except its own half-finished download.

    Options (when started with: & ([scriptblock]::Create((irm <url>))) -Action scan):
      -Action      app (default) | menu | app-install | scan | scan-admin | categories
                   (or set $env:KVALITOOL_ACTION). app-install downloads and verifies the window app without
                   opening it.
      -InstallDir  where to keep downloads (default %LOCALAPPDATA%\KvaliTool\bin; the app is unzipped
                   next to it, in ..\app)

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
    $AppZipUrl = '__APP_ZIP_URL__'
    $AppZipSha256 = '__APP_ZIP_SHA256__'

    if ($Version.StartsWith('__')) {
        throw 'This is the launcher template. Use the copy in dist\ produced by tools\Publish-Release.ps1.'
    }

    if (-not $Action) { $Action = $env:KVALITOOL_ACTION }
    if (-not $Action) { $Action = 'app' }
    if (-not $InstallDir) { $InstallDir = Join-Path $env:LOCALAPPDATA 'KvaliTool\bin' }

    if (-not [Environment]::Is64BitOperatingSystem) { throw 'KvaliTool needs 64-bit Windows.' }

    # Windows PowerShell 5.1 may default to TLS 1.0, which GitHub rejects.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }

    $dir = Join-Path $InstallDir $Version
    $exe = Join-Path $dir 'kvalitool.exe'
    $appZip = Join-Path $dir ([IO.Path]::GetFileName(([uri]$AppZipUrl).AbsolutePath))
    $appDir = Join-Path (Join-Path (Split-Path $InstallDir -Parent) 'app') $Version
    $appExe = Join-Path $appDir 'KvaliTool.exe'

    # Downloads $Url to $Target unless a verified copy is already there. A cached copy that doesn't match is
    # refused and left alone for you to inspect; only our own half-finished download is ever removed.
    function Get-VerifiedFile([string]$Url, [string]$Target, [string]$Sha256) {
        $name = [IO.Path]::GetFileName($Target)
        if (Test-Path -LiteralPath $Target) {
            if ((Get-Sha256 $Target) -ne $Sha256) {
                throw "The cached $Target does not match the expected SHA-256, so it was NOT run. It may have been modified. Delete the folder $(Split-Path $Target -Parent) yourself and run the launcher again."
            }
            Write-Host "KvaliTool $Version $name (verified, already downloaded)" -ForegroundColor Cyan
            return
        }

        New-Item -ItemType Directory -Force -Path (Split-Path $Target -Parent) | Out-Null
        $partial = "$Target.download"
        Write-Host "Downloading KvaliTool $Version from $Url ..." -ForegroundColor Cyan
        # curl.exe (in Windows since 10 1803) is faster and shows a progress bar. If it is missing or fails (for
        # example behind a proxy it doesn't know about), Invoke-WebRequest tries again with the Windows settings.
        $downloaded = $false
        $curl = Get-Command curl.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($curl) {
            # Its progress bar goes to stderr, which Windows PowerShell 5.1 can turn into a terminating error
            # under 'Stop' when output is redirected: only the exit code counts here.
            $ErrorActionPreference = 'Continue'
            & $curl.Source --fail --location --retry 2 --progress-bar --output $partial $Url
            $downloaded = ($LASTEXITCODE -eq 0)
            $ErrorActionPreference = 'Stop'
        }
        if (-not $downloaded) { Invoke-WebRequest -Uri $Url -OutFile $partial -UseBasicParsing }
        $actual = Get-Sha256 $partial
        if ($actual -ne $Sha256) {
            # Our own half-finished download, never anything of yours.
            Remove-Item -LiteralPath $partial -Force
            throw "Download rejected: SHA-256 is $actual but this launcher expects $Sha256. Nothing was run."
        }
        Rename-Item -LiteralPath $partial -NewName $name
        Write-Host "KvaliTool $Version $name downloaded and verified (SHA-256 $Sha256)." -ForegroundColor Cyan
    }

    function Get-Cli { Get-VerifiedFile $ExeUrl $exe $ExeSha256 }

    # Reads SHA256SUMS.txt from inside the (already verified) zip: "<HASH>  <relative\path>" per line.
    function Read-ZipManifest([string]$Zip) {
        Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
        try {
            $entry = $archive.GetEntry('SHA256SUMS.txt')
            if (-not $entry) { throw "$Zip has no SHA256SUMS.txt, so its files can't be checked. Nothing was run." }
            $reader = New-Object IO.StreamReader($entry.Open())
            try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
        finally { $archive.Dispose() }

        $manifest = @{}
        foreach ($line in ($text -split "`r?`n")) {
            if ($line -match '^([0-9A-Fa-f]{64})  (.+)$') { $manifest[$Matches[2].Replace('/', '\')] = $Matches[1].ToUpperInvariant() }
        }
        if ($manifest.Count -eq 0) { throw "SHA256SUMS.txt in $Zip is empty. Nothing was run." }
        return $manifest
    }

    # Every file in $Folder must be on the list with the same hash, and every listed file must exist.
    # Returns the problems found (none = verified).
    function Test-AppFolder([string]$Folder, [hashtable]$Manifest) {
        $problems = New-Object Collections.Generic.List[string]
        $root = (Resolve-Path -LiteralPath $Folder).ProviderPath.TrimEnd('\') + '\'
        $seen = @{}
        foreach ($file in Get-ChildItem -LiteralPath $Folder -Recurse -Force -File) {
            $relative = $file.FullName.Substring($root.Length)
            if ($relative -eq 'SHA256SUMS.txt') { continue }
            $seen[$relative] = $true
            if (-not $Manifest.ContainsKey($relative)) { $problems.Add("unexpected file: $relative"); continue }
            if ((Get-Sha256 $file.FullName) -ne $Manifest[$relative]) { $problems.Add("modified: $relative") }
        }
        foreach ($relative in $Manifest.Keys) {
            if (-not $seen.ContainsKey($relative)) { $problems.Add("missing: $relative") }
        }
        return , $problems
    }

    function Get-App {
        Get-VerifiedFile $AppZipUrl $appZip $AppZipSha256
        $manifest = Read-ZipManifest $appZip

        if (-not (Test-Path -LiteralPath $appDir)) {
            # Unzip into a new uniquely named folder, check it, and only then give it its final name, so a
            # half-finished unzip is never mistaken for a complete one.
            $staging = "$appDir.unzipping-$([guid]::NewGuid().ToString('N').Substring(0, 8))"
            Write-Host "Unzipping to $appDir ..." -ForegroundColor Cyan
            [IO.Compression.ZipFile]::ExtractToDirectory($appZip, $staging)
            $problems = Test-AppFolder $staging $manifest
            if ($problems.Count -gt 0) {
                throw "The unzipped files don't match the verified list ($($problems[0])). Nothing was run. The unzipped copy was left at $staging; you can delete it."
            }
            Rename-Item -LiteralPath $staging -NewName ([IO.Path]::GetFileName($appDir))
            Write-Host "KvaliTool $Version window app ready ($($manifest.Count) files verified)." -ForegroundColor Cyan
            return
        }

        Write-Host "Checking $($manifest.Count) files in $appDir ..." -ForegroundColor Cyan
        $problems = Test-AppFolder $appDir $manifest
        if ($problems.Count -gt 0) {
            $shown = ($problems | Select-Object -First 5) -join '; '
            throw "The installed window app does not match the verified release ($shown), so it was NOT run. It may have been modified. Delete the folder $appDir yourself and run the launcher again."
        }
        Write-Host "KvaliTool $Version window app (verified)." -ForegroundColor Cyan
    }

    function Start-App {
        Get-App
        $process = Start-Process -FilePath $appExe -WorkingDirectory $appDir -PassThru
        # Say "opened" only once the window is really there, and say so if the app closes instead.
        $clock = [Diagnostics.Stopwatch]::StartNew()
        while ($clock.Elapsed.TotalSeconds -lt 30) {
            $process.Refresh()
            if ($process.HasExited) {
                throw ('KvaliTool closed right after starting (exit code 0x{0:X8}). Please report this; it is a bug.' -f $process.ExitCode)
            }
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) {
                Write-Host 'KvaliTool opened in its own window. It scans and shows results; this version changes nothing.'
                return
            }
            Start-Sleep -Milliseconds 50
        }
        Write-Host 'KvaliTool was started but its window has not appeared yet.' -ForegroundColor Yellow
    }

    function Start-Scan {
        Get-Cli
        & $exe scan
        if ($LASTEXITCODE -eq 2) { Write-Host 'The safety layer blocked items (see above). Please report this; it is a bug.' -ForegroundColor Red }
    }

    function Start-AdminScan {
        Write-Host ''
        Write-Host 'Windows will ask for administrator rights. They are needed only to READ these locations:' -ForegroundColor Yellow
        Write-Host '  Windows Temp, Windows Update downloads, Delivery Optimization, system error reports and logs,'
        Write-Host '  Windows.old, the component store (DISM analysis), System Restore points.'
        Write-Host 'The scan opens in a new window and still changes nothing.'
        Get-Cli
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
        'app' { Start-App; Write-Host "For the console scanner: `$env:KVALITOOL_ACTION = 'menu', then run the same line again."; return }
        'app-install' { Get-App; Write-Host "Installed and verified, not opened: $appExe"; return }
        'scan' { Start-Scan; return }
        'scan-admin' { Start-AdminScan; return }
        'categories' { Get-Cli; & $exe categories; return }
        'menu' { }
        default { throw "Unknown action '$Action'. Use app, menu, app-install, scan, scan-admin or categories." }
    }

    while ($true) {
        Write-Host ''
        Write-Host "KvaliTool $Version - Phase 2: scan and show results only. Nothing on your PC is changed." -ForegroundColor Cyan
        Write-Host '  1  Open KvaliTool (window)'
        Write-Host '  2  Scan in this console (no administrator rights)'
        Write-Host '  3  Scan including system locations (asks for administrator rights, new window)'
        Write-Host '  4  List categories and how each would be cleaned'
        Write-Host '  5  Open the folder with saved scan reports'
        Write-Host '  Q  Quit'
        $choice = Read-Host 'Choose'
        # A failed download or check ends that option, not the menu.
        try {
            switch ($choice.Trim().ToUpperInvariant()) {
                '1' { Start-App }
                '2' { Start-Scan }
                '3' { Start-AdminScan }
                '4' { Get-Cli; & $exe categories }
                '5' {
                    $reports = Join-Path $env:LOCALAPPDATA 'KvaliTool\reports'
                    if (Test-Path -LiteralPath $reports) { Start-Process explorer.exe -ArgumentList "`"$reports`"" }
                    else { Write-Host 'No reports yet. Run a scan first.' }
                }
                'Q' { return }
                default { Write-Host 'Please type 1, 2, 3, 4, 5 or Q.' }
            }
        }
        catch { Write-Host $_.Exception.Message -ForegroundColor Red }
    }
} $Action $InstallDir
