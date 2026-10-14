#Requires -Version 7.0
<#
.SYNOPSIS
    Writes a large scan report for testing the results UI: a real sandbox report from Test-Phase1.ps1 (so the
    REVIEW, personal-folder, report-only and duplicate cases are real) plus synthetic items, including one
    group with more than 10,000 rows.

.DESCRIPTION
    Synthetic items live under X:\KvaliTool-synthetic\, a made-up path, so nothing in the report points at a
    real file. Open the result with:  KvaliTool.exe --report <path>
    The report is written to a NEW file under artifacts\ (never overwrites).

.EXAMPLE
    pwsh -File .\tools\New-SyntheticReport.ps1 -Items 100000
#>
[CmdletBinding()]
param(
    [int]$Items = 100000,
    [int]$BigGroupItems = 15000,
    [string]$BaseReport
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not $BaseReport) {
    $BaseReport = Get-ChildItem (Join-Path $repo 'artifacts\phase1-test') -Recurse -Filter 'report.json' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $BaseReport) { throw 'No sandbox report found. Run tools\Test-Phase1.ps1 first.' }
}

$report = Get-Content -LiteralPath $BaseReport -Raw | ConvertFrom-Json -Depth 20
$rng = [Random]::new(42)
$now = [DateTime]::UtcNow
$exts = '.tmp', '.log', '.dat', '.cache', '.bin', '.etl'

function New-FakeItem([string]$folder, [string]$group, [int]$n, [string]$reason) {
    $name = "file-{0:D6}{1}" -f $n, $exts[$n % $exts.Length]
    [ordered]@{
        path = "X:\KvaliTool-synthetic\$folder\$name"
        name = $name
        kind = 'File'
        sizeBytes = [long]($rng.NextDouble() * 50MB)
        fileCount = 1
        lastModifiedUtc = $now.AddDays(-$rng.Next(2, 900)).ToString('o')
        reason = $reason
        group = $group
        groupRoot = "X:\KvaliTool-synthetic\$folder"
        inUserContentFolder = $false
        inCloudSyncedFolder = $false
        reportOnly = $false
    }
}

$byId = @{}
foreach ($c in $report.categories) { $byId[$c.definition.id] = $c }

# One SAFE group with more than 10k rows.
$temp = $byId['UserTemp']
$list = [Collections.Generic.List[object]]::new()
$list.AddRange([object[]]@($temp.items))
for ($i = 0; $i -lt $BigGroupItems; $i++) { $list.Add((New-FakeItem 'Temp' 'Synthetic temp folder (big group)' $i 'Synthetic temporary file')) }
$temp.items = $list.ToArray()
$temp.status = 'Scanned'

# The rest spread over many groups in a few SAFE and REVIEW categories.
$targets = 'ShaderCache', 'UserErrorReports', 'ChromeCache', 'DeveloperCaches'
$remaining = $Items - $BigGroupItems
$perTarget = [math]::Floor($remaining / $targets.Count)
foreach ($id in $targets) {
    $c = $byId[$id]
    $list = [Collections.Generic.List[object]]::new()
    $list.AddRange([object[]]@($c.items))
    for ($i = 0; $i -lt $perTarget; $i++) {
        $g = $i % 200
        $list.Add((New-FakeItem "$id\group$g" "Synthetic group $g" $i "Synthetic $id item"))
    }
    $c.items = $list.ToArray()
    $c.status = 'Scanned'
}

$report.engineVersion = "$($report.engineVersion)+synthetic"
$out = Join-Path $repo "artifacts\synthetic-report-$Items-$(Get-Date -Format 'yyyyMMdd-HHmmss').json"
New-Item -ItemType Directory -Force (Split-Path $out -Parent) | Out-Null
$json = $report | ConvertTo-Json -Depth 20 -Compress
$stream = [IO.FileStream]::new($out, [IO.FileMode]::CreateNew)   # never overwrites
try { $bytes = [Text.Encoding]::UTF8.GetBytes($json); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
$total = ($report.categories | ForEach-Object { @($_.items).Count } | Measure-Object -Sum).Sum
Write-Host "Wrote $out ($total items, largest group $BigGroupItems+ rows)"
