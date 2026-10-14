#Requires -Version 7.0
<#
.SYNOPSIS
    Builds a release in dist\:
      - KvaliTool-<version>-win-x64.zip: the self-contained WinUI 3 window app, with a SHA256SUMS.txt inside
        listing every other file in the zip;
      - kvalitool.exe: the self-contained single-file console scanner;
      - kvalitool.ps1: the launcher, with the SHA-256 of the zip and of the exe pinned inside it;
      - SHA256SUMS.txt for the three downloads.

.EXAMPLE
    # For GitHub Releases. Files are expected at https://github.com/<owner>/<repo>/releases/download/v<version>/
    .\tools\Publish-Release.ps1 -Repo yourname/KvaliTool

.EXAMPLE
    # For a local test server
    .\tools\Publish-Release.ps1 -DownloadBaseUrl http://localhost:8765/good
#>
[CmdletBinding(DefaultParameterSetName = 'GitHub')]
param(
    [Parameter(Mandatory, ParameterSetName = 'GitHub')]
    [ValidatePattern('^[\w.-]+/[\w.-]+$')]
    [string]$Repo,

    [Parameter(Mandatory, ParameterSetName = 'Custom')]
    [string]$DownloadBaseUrl,

    [string]$OutDir
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutDir) { $OutDir = Join-Path $repoRoot 'dist' }

$version = ([xml](Get-Content (Join-Path $repoRoot 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }

$base = if ($PSCmdlet.ParameterSetName -eq 'GitHub') { "https://github.com/$Repo/releases/download/v$version" } else { $DownloadBaseUrl.TrimEnd('/') }
function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant() }

# --- Console scanner -------------------------------------------------------------------------------------------
Write-Host "Publishing kvalitool.exe $version (win-x64, self-contained, single file)..." -ForegroundColor Cyan
# No -o: MSBuild mishandles output paths containing an apostrophe (as in "Sandro's files").
dotnet publish (Join-Path $repoRoot 'src\KvaliTool.Cli\KvaliTool.Cli.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true -p:DebugType=none --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (CLI) failed.' }

$published = Join-Path $repoRoot 'src\KvaliTool.Cli\bin\Release\net10.0-windows\win-x64\publish\kvalitool.exe'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$exe = Join-Path $OutDir 'kvalitool.exe'
Copy-Item -LiteralPath $published -Destination $exe -Force
$exeHash = Get-Sha256 $exe

# --- Window app ------------------------------------------------------------------------------------------------
Write-Host "Publishing the KvaliTool window app $version (win-x64, self-contained, unpackaged)..." -ForegroundColor Cyan
$appProject = Join-Path $repoRoot 'src\KvaliTool.App\KvaliTool.App.csproj'
$appPublish = Join-Path $repoRoot 'src\KvaliTool.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish'
# Start from an empty publish folder so files from an older build can't end up in the zip.
# (Build output only; nothing outside src\KvaliTool.App\bin is touched.)
if (Test-Path -LiteralPath $appPublish) { Remove-Item -LiteralPath $appPublish -Recurse -Force }
dotnet publish $appProject -c Release -p:DebugType=none --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish (app) failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $appPublish 'KvaliTool.exe'))) { throw "KvaliTool.exe not found in $appPublish." }

# The list the launcher checks every unzipped file against, on every start.
$manifestLines = Get-ChildItem -LiteralPath $appPublish -Recurse -File |
    Where-Object Name -ne 'SHA256SUMS.txt' |
    Sort-Object FullName |
    ForEach-Object { "$(Get-Sha256 $_.FullName)  $([IO.Path]::GetRelativePath($appPublish, $_.FullName))" }
Set-Content -LiteralPath (Join-Path $appPublish 'SHA256SUMS.txt') -Value $manifestLines -Encoding ascii

$zipName = "KvaliTool-$version-win-x64.zip"
$zip = Join-Path $OutDir $zipName
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }   # our own previous build output
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($appPublish, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$zipHash = Get-Sha256 $zip

# --- Launcher and checksums ------------------------------------------------------------------------------------
$launcher = (Get-Content (Join-Path $repoRoot 'install\launcher.ps1') -Raw).
    Replace('__VERSION__', $version).
    Replace('__EXE_URL__', "$base/kvalitool.exe").
    Replace('__EXE_SHA256__', $exeHash).
    Replace('__APP_ZIP_URL__', "$base/$zipName").
    Replace('__APP_ZIP_SHA256__', $zipHash)
if ($launcher -match '[^\x00-\x7F]') { throw 'The launcher must be plain ASCII.' }
if ($launcher -match '__[A-Z0-9_]+__') { throw "Unfilled placeholder in the launcher: $($Matches[0])" }
[IO.File]::WriteAllText((Join-Path $OutDir 'kvalitool.ps1'), $launcher, [Text.Encoding]::ASCII)
"$exeHash  kvalitool.exe", "$zipHash  $zipName", "$(Get-Sha256 (Join-Path $OutDir 'kvalitool.ps1'))  kvalitool.ps1" |
    Set-Content -LiteralPath (Join-Path $OutDir 'SHA256SUMS.txt') -Encoding ascii

Write-Host ''
Write-Host "dist\kvalitool.exe   $([math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)) MB  SHA-256 $exeHash"
Write-Host "dist\$zipName   $([math]::Round((Get-Item -LiteralPath $zip).Length / 1MB, 1)) MB  SHA-256 $zipHash  ($($manifestLines.Count) files)"
Write-Host "dist\kvalitool.ps1   launcher, downloads from $base/"
if ($PSCmdlet.ParameterSetName -eq 'GitHub') {
    Write-Host ''
    Write-Host 'To publish (makes the files PUBLIC):' -ForegroundColor Yellow
    Write-Host "  gh release create v$version dist\kvalitool.exe dist\$zipName dist\kvalitool.ps1 dist\SHA256SUMS.txt --repo $Repo --title `"KvaliTool $version`""
    Write-Host 'Then anyone can run:'
    Write-Host "  irm https://github.com/$Repo/releases/latest/download/kvalitool.ps1 | iex"
}
