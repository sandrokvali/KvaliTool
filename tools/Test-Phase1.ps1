#Requires -Version 7.0
<#
.SYNOPSIS
    Phase 1 safety test: proves the dry-run scan changes nothing and only reports what it should.

.DESCRIPTION
    1. Builds the CLI.
    2. Creates a fake Windows tree (a "sandbox") in a NEW timestamped folder under artifacts\phase1-test, with
       traps: browser cookies/history, a junction from Temp to a folder outside the sandbox, a locked file,
       recent files, a file copied with an old modified date, a hard-link pair, personal files.
    3. Snapshots every file and folder (path, size, modified time, SHA-256).
    4. Runs `kvalitool scan --sandbox` while one file is held open (locked).
    5. Snapshots again and requires both snapshots to be identical.
    6. Checks the JSON report against the expectations below.

    The script never deletes anything. Each run uses a new folder; delete old ones yourself when you like.
#>
[CmdletBinding()]
param(
    # Test a published kvalitool.exe instead of building and running the development build.
    [string]$Exe
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $repo "artifacts\phase1-test\$(Get-Date -Format 'yyyyMMdd-HHmmss')"
$sb = Join-Path $work 'sandbox'
$outside = Join-Path $work 'outside-target'
$report = Join-Path $work 'report.json'
$reportNoDup = Join-Path $work 'report-no-duplicates.json'

$script:failures = 0
function Check([bool]$condition, [string]$what) {
    if ($condition) { Write-Host "  PASS  $what" -ForegroundColor Green }
    else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failures++ }
}

$old = (Get-Date).AddDays(-200)
$recent = Get-Date
function New-TestFile([string]$path, [int]$bytes = 2048, [datetime]$time = $old, [datetime]$created = $time, [byte]$fill = 0) {
    New-Item -ItemType Directory -Force -Path (Split-Path $path) | Out-Null
    $data = [byte[]]::new($bytes)
    if ($fill -ne 0) { [Array]::Fill($data, $fill) } else { [Random]::new($path.GetHashCode()).NextBytes($data) }
    [IO.File]::WriteAllBytes($path, $data)
    $item = Get-Item -LiteralPath $path
    $item.CreationTime = $created
    $item.LastWriteTime = $time
}

if ($Exe) {
    $runCli = { & $Exe @args }
}
else {
    Write-Host "Building..." -ForegroundColor Cyan
    # No -o: MSBuild mishandles output paths that contain an apostrophe (as in "Sandro's files").
    dotnet build (Join-Path $repo 'src\KvaliTool.Cli\KvaliTool.Cli.csproj') -c Release --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $cli = Join-Path $repo 'src\KvaliTool.Cli\bin\Release\net10.0-windows\kvalitool.dll'
    $runCli = { dotnet $cli @args }
}

Write-Host "Creating sandbox in $work" -ForegroundColor Cyan
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$user = Join-Path $sb 'Users\Test'
$local = Join-Path $user 'AppData\Local'
$roaming = Join-Path $user 'AppData\Roaming'
$temp = Join-Path $local 'Temp'

# --- Temp ---
New-TestFile "$temp\old1.tmp"
New-TestFile "$temp\sub\old2.log"
New-TestFile "$temp\fresh.tmp" -time $recent
New-TestFile "$temp\copied-old-date.tmp" -time $old -created $recent    # old modified date, created just now
New-TestFile "$temp\locked.tmp"
New-TestFile "$outside\SECRET-outside.txt"                              # must never be reached
New-Item -ItemType Junction -Path "$temp\link-to-outside" -Target $outside | Out-Null
New-TestFile "$sb\Windows\Temp\old-system.tmp"
New-TestFile "$sb\Windows\System32\drivers\NEVER.sys"
New-TestFile "$sb\Program Files\App\NEVER.dll"

# --- Browsers ---
$chrome = "$local\Google\Chrome\User Data"
New-TestFile "$chrome\Default\Preferences" -bytes 64
New-TestFile "$chrome\Default\Cache\Cache_Data\f_000001"
New-TestFile "$chrome\Default\Code Cache\js\abc123"
New-TestFile "$chrome\Default\Network\Cookies"
New-TestFile "$chrome\Default\History"
New-TestFile "$chrome\Default\Login Data"
New-TestFile "$chrome\Default\Local Storage\leveldb\000003.log"
$edge = "$local\Microsoft\Edge\User Data"
New-TestFile "$edge\Default\Preferences" -bytes 64
New-TestFile "$edge\Default\Cache\Cache_Data\edge_cached"
New-TestFile "$local\Mozilla\Firefox\Profiles\abc.default\cache2\entries\E1"
New-TestFile "$roaming\Mozilla\Firefox\Profiles\abc.default\cookies.sqlite"
New-TestFile "$roaming\Mozilla\Firefox\Profiles\abc.default\places.sqlite"

# --- Caches, reports ---
New-TestFile "$local\npm-cache\_cacache\content-v2\sha512\aa\bb\npmblob"
New-TestFile "$user\.nuget\packages\some.package\1.0.0\some.package.1.0.0.nupkg"
New-TestFile "$local\D3DSCache\shader1"
New-TestFile "$local\Microsoft\Windows\Explorer\thumbcache_256.db"
New-TestFile "$local\Microsoft\Windows\Explorer\NOT-A-CACHE.dat"
New-TestFile "$local\CrashDumps\app.exe.1234.dmp"
New-TestFile "$sb\Windows\Logs\CBS\old.log"
New-TestFile "$sb\Windows\Logs\CBS\new.log" -time $recent

# --- Recycle Bin ($R data + $I record, version 2 format) ---
$recycle = Join-Path $sb "`$Recycle.Bin\$sid"
New-TestFile "$recycle\`$RABC123.txt"
$original = 'C:\Users\Test\Documents\deleted-report.txt'
$ms = [IO.MemoryStream]::new(); $w = [IO.BinaryWriter]::new($ms)
$w.Write([long]2); $w.Write([long]2048); $w.Write($old.ToFileTimeUtc()); $w.Write([int]($original.Length + 1))
$w.Write([Text.Encoding]::Unicode.GetBytes($original + [char]0)); $w.Flush()
[IO.File]::WriteAllBytes("$recycle\`$IABC123.txt", $ms.ToArray())

# --- Windows.old with personal files ---
New-TestFile "$sb\Windows.old\Windows\System32\old.dll"
New-TestFile "$sb\Windows.old\Users\Test\Documents\thesis.docx"

# --- Personal folders (only Large/Duplicate files may list these) ---
New-TestFile "$user\Documents\unique-notes.docx"
New-TestFile "$user\Documents\video-original.bin" -bytes 2MB -fill 7 -time $old
New-TestFile "$user\Desktop\video-copy.bin" -bytes 2MB -fill 7 -time $old.AddDays(10)
New-TestFile "$user\Documents\hardlink-a.bin" -bytes 2MB -fill 9
New-Item -ItemType HardLink -Path "$user\Documents\hardlink-b.bin" -Target "$user\Documents\hardlink-a.bin" | Out-Null
New-TestFile "$user\projects\app\node_modules\lib\same.js" -bytes 2MB -fill 5
New-TestFile "$user\projects\app2\node_modules\lib\same.js" -bytes 2MB -fill 5
New-TestFile "$user\Downloads\old-installer.exe"
New-TestFile "$user\Downloads\new.zip" -time $recent
# Folder untouched for 200 days -> offered as one row.
New-TestFile "$user\Downloads\old-project\a.txt"
New-TestFile "$user\Downloads\old-project\sub\b.txt"
(Get-Item -LiteralPath "$user\Downloads\old-project").CreationTime = $old
(Get-Item -LiteralPath "$user\Downloads\old-project\sub").CreationTime = $old
# Old program that is still used (one fresh output file) -> not offered.
New-TestFile "$user\Downloads\used-app\app.exe"
New-TestFile "$user\Downloads\used-app\output\today.png" -time $recent
(Get-Item -LiteralPath "$user\Downloads\used-app").CreationTime = $old
# Archive extracted today: files keep old dates, folder is new -> not offered.
New-TestFile "$user\Downloads\extracted-today\tool.exe"
# Tool data in dot-folders: identical files there are not duplicates to clean.
New-TestFile "$user\.conda\pkgs\lib\same.dll" -bytes 2MB -fill 4
New-TestFile "$user\.conda\envs\env1\same.dll" -bytes 2MB -fill 4
New-TestFile "$user\OneDrive\synced-big.bin" -bytes 3MB -fill 3

function Get-Snapshot {
    Get-ChildItem -LiteralPath $sb, $outside -Recurse -Force | Sort-Object FullName | ForEach-Object {
        # Directory listings carry NTFS's lazily-updated copy of the timestamps; Refresh() reads the real ones.
        $_.Refresh()
        $hash = if ($_.PSIsContainer) { '' } else { (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
        $len = if ($_.PSIsContainer) { '' } else { $_.Length }
        "$($_.FullName)|$len|$($_.LastWriteTimeUtc.Ticks)|$($_.CreationTimeUtc.Ticks)|$hash"
    }
}

Write-Host "Snapshot before..." -ForegroundColor Cyan
$before = Get-Snapshot

$lock = [IO.File]::Open("$temp\locked.tmp", 'Open', 'ReadWrite', 'None')
try {
    Write-Host "Scanning sandbox (Edge pretended to be running)..." -ForegroundColor Cyan
    & $runCli scan --sandbox $sb --json $report --large-mb 1 --top 0 --pretend-running msedge --find-duplicates | Out-Host
    $exit = $LASTEXITCODE

    # Same sandbox without --find-duplicates: the duplicate search must stay off by default.
    Write-Host "Scanning sandbox again without --find-duplicates..." -ForegroundColor Cyan
    & $runCli scan --sandbox $sb --json $reportNoDup --large-mb 1 --top 0 --pretend-running msedge --only DuplicateFiles | Out-Host
    $exitNoDup = $LASTEXITCODE
}
finally {
    $lock.Dispose()
}

Write-Host "Snapshot after..." -ForegroundColor Cyan
$after = Get-Snapshot
$diff = Compare-Object $before $after

Write-Host "`nResults" -ForegroundColor Cyan
Check ($exit -eq 0) "CLI exit code is 0 (got $exit)"
Check ($exitNoDup -eq 0) "CLI exit code is 0 without --find-duplicates (got $exitNoDup)"
$noDup = (Get-Content $reportNoDup -Raw | ConvertFrom-Json).categories | Where-Object { $_.definition.id -eq 'DuplicateFiles' }
Check ($noDup.status -eq 'Skipped' -and @($noDup.items).Count -eq 0 -and ($noDup.notes -join ' ') -like '*Duplicate search is off*') "Without --find-duplicates, Duplicate files is Skipped with no items and a note saying it's off"
Check ($null -eq $diff) "Nothing in the sandbox or the outside folder changed ($($before.Count) entries compared: size, times, SHA-256)"
if ($diff) { $diff | Format-Table -AutoSize | Out-Host }

$r = Get-Content $report -Raw | ConvertFrom-Json
$items = $r.categories | ForEach-Object { $cat = $_.definition.id; $_.items | ForEach-Object { $_ | Add-Member cat $cat -PassThru } }
$skipped = $r.categories | ForEach-Object { $_.skipped }
function Listed([string]$pattern) { @($items | Where-Object { $_.path -like $pattern }) }
function Get-Category([string]$id) { $r.categories | Where-Object { $_.definition.id -eq $id } }

Check ($r.mode -eq 'DryRun') "Report mode is DryRun"
Check ($r.policyViolations.Count -eq 0) "No safety-policy violations"
Check ((Listed '*SECRET-outside*').Count -eq 0) "Junction target outside the sandbox was never listed"
Check (@($skipped | Where-Object { $_.path -like '*link-to-outside' -and $_.reason -eq 'LinkNotFollowed' }).Count -eq 1) "Junction reported as 'link not followed'"
Check ((Listed '*Cookies*').Count + (Listed '*History*').Count + (Listed '*Login Data*').Count + (Listed '*Local Storage*').Count -eq 0) "No browser cookies, history, logins or local storage listed"
Check ((Listed '*.sqlite').Count -eq 0) "No Firefox profile databases listed"
Check ((Listed '*System32*').Count + (Listed '*Program Files*').Count -eq 0) "Nothing from System32 or Program Files"
Check ((Listed '*NOT-A-CACHE*').Count -eq 0) "Only thumbcache/iconcache files from the Explorer folder"
Check ((Listed '*\Temp\old1.tmp').Count -eq 1 -and (Listed '*\Temp\sub\old2.log').Count -eq 1) "Old temp files listed"
Check (@($skipped | Where-Object { $_.path -like '*fresh.tmp' -and $_.reason -eq 'RecentlyModified' }).Count -eq 1) "Recent temp file skipped as too recent"
Check (@($skipped | Where-Object { $_.path -like '*copied-old-date.tmp' -and $_.reason -eq 'RecentlyModified' }).Count -eq 1) "File with an old date but created just now is skipped"
Check (@($skipped | Where-Object { $_.path -like '*locked.tmp' -and $_.reason -eq 'Locked' }).Count -eq 1) "Locked temp file skipped as in use"
Check ((Listed '*Windows\Temp\old-system.tmp').Count -eq 1) "Windows\Temp file listed"
Check ((Get-Category 'EdgeCache').status -eq 'Skipped' -and (Get-Category 'EdgeCache').items.Count -eq 0) "Edge skipped because it is 'running'"
Check ((Listed '*Cache_Data\f_000001').Count -eq 1 -and (Listed '*cache2\entries\E1').Count -eq 1) "Chrome and Firefox cache files listed"
Check ((Listed '*Logs\CBS\old.log').Count -eq 1 -and (Listed '*Logs\CBS\new.log').Count -eq 0) "Only old Windows logs listed"
$rb = Listed '*$RABC123.txt'
Check ($rb.Count -eq 1 -and $rb[0].originalPath -eq $original) "Recycle Bin entry listed with its original path"
$wold = Listed '*Windows.old'
Check ($wold.Count -eq 1 -and $wold[0].warning -like '*personal file*') "Windows.old listed as one folder with a personal-files warning"
$personal = @($items | Where-Object { $_.path -like '*\Users\Test\Documents\*' -or $_.path -like '*\Users\Test\Desktop\*' -or $_.path -like '*\Users\Test\OneDrive\*' })
Check (@($personal | Where-Object { $_.cat -notin 'LargeFiles', 'DuplicateFiles' }).Count -eq 0) "Personal/cloud files appear only under Large files or Duplicate files"
Check (@($personal | Where-Object { $_.inUserContentFolder -or $_.inCloudSyncedFolder }).Count -eq $personal.Count) "...and every one of them is flagged as personal/cloud"
$dups = @($items | Where-Object cat -eq 'DuplicateFiles')
Check ($dups.Count -eq 1 -and $dups[0].path -like '*video-copy.bin' -and $dups[0].duplicateKeeperPath -like '*video-original.bin') "Duplicate pair: newer copy listed, older copy kept"
Check (@($dups | Where-Object path -like '*hardlink*').Count -eq 0) "Hard links are not reported as duplicates"
Check (@($dups | Where-Object path -like '*node_modules*').Count -eq 0) "node_modules is ignored by the duplicate finder"
Check ((Listed '*old-installer.exe').Count -eq 1 -and (Listed '*new.zip').Count -eq 0) "Only old top-level downloads listed"
$oldProject = Listed '*\Downloads\old-project'
Check ($oldProject.Count -eq 1 -and $oldProject[0].kind -eq 'Folder' -and $oldProject[0].fileCount -eq 2) "Untouched Downloads folder offered as one row (2 files)"
Check ((Listed '*\Downloads\old-project\*').Count -eq 0) "...and its files are not listed again anywhere"
Check ((Listed '*used-app*').Count -eq 0) "Downloads folder with a recent file inside is not offered"
Check ((Listed '*extracted-today*').Count -eq 0) "Freshly extracted folder with old file dates is not offered"
Check ((Listed '*\.conda\*').Count -eq 0) "Nothing from dot-folders (.conda) in Large or Duplicate files"
Check ((Listed '*OneDrive\synced-big.bin')[0].warning -like '*cloud*') "OneDrive file carries the cloud warning"

Write-Host ""
if ($script:failures -eq 0) { Write-Host "ALL CHECKS PASSED  (work folder: $work)" -ForegroundColor Green; exit 0 }
else { Write-Host "$script:failures CHECK(S) FAILED  (work folder: $work)" -ForegroundColor Red; exit 1 }
