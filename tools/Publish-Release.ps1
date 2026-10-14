#Requires -Version 7.0
<#
.SYNOPSIS
    Builds a release: a self-contained single-file kvalitool.exe plus a launcher with that exe's SHA-256
    pinned inside it. Output goes to dist\.

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

Write-Host "Publishing kvalitool.exe $version (win-x64, self-contained, single file)..." -ForegroundColor Cyan
# No -o: MSBuild mishandles output paths containing an apostrophe (as in "Sandro's files").
dotnet publish (Join-Path $repoRoot 'src\KvaliTool.Cli\KvaliTool.Cli.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true -p:DebugType=none --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

$published = Join-Path $repoRoot 'src\KvaliTool.Cli\bin\Release\net10.0-windows\win-x64\publish\kvalitool.exe'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$exe = Join-Path $OutDir 'kvalitool.exe'
Copy-Item -LiteralPath $published -Destination $exe -Force
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToUpperInvariant()

$launcher = (Get-Content (Join-Path $repoRoot 'install\launcher.ps1') -Raw).
    Replace('__VERSION__', $version).
    Replace('__EXE_URL__', "$base/kvalitool.exe").
    Replace('__EXE_SHA256__', $hash)
if ($launcher -match '[^\x00-\x7F]') { throw 'The launcher must be plain ASCII.' }
[IO.File]::WriteAllText((Join-Path $OutDir 'kvalitool.ps1'), $launcher, [Text.Encoding]::ASCII)
"$hash  kvalitool.exe" | Set-Content -LiteralPath (Join-Path $OutDir 'SHA256SUMS.txt') -Encoding ascii

$size = (Get-Item -LiteralPath $exe).Length / 1MB
Write-Host ''
Write-Host "dist\kvalitool.exe   $([math]::Round($size, 1)) MB  SHA-256 $hash"
Write-Host "dist\kvalitool.ps1   launcher, downloads from $base/kvalitool.exe"
if ($PSCmdlet.ParameterSetName -eq 'GitHub') {
    Write-Host ''
    Write-Host 'To publish (makes the files PUBLIC):' -ForegroundColor Yellow
    Write-Host "  gh release create v$version dist\kvalitool.exe dist\kvalitool.ps1 dist\SHA256SUMS.txt --repo $Repo --title `"KvaliTool $version`""
    Write-Host 'Then anyone can run:'
    Write-Host "  irm https://github.com/$Repo/releases/latest/download/kvalitool.ps1 | iex"
}
