# Phase 1: scan engine (dry run only)

Status: complete, waiting for your review. **This build contains no code that deletes, moves or modifies your files.**

## Plan

| Piece | What it does |
|---|---|
| `KvaliTool.Core` | Class library holding the scan engine. The WinUI app (Phase 2) and the cleaner (Phase 3) will use the same library. |
| `KvaliTool.Cli` | `kvalitool scan`: runs the engine and prints what it found and why. It writes a JSON report to a **new** file (never overwrites). |
| `tools/Test-Phase1.ps1` | Builds a fake Windows tree full of traps, snapshots it, scans, snapshots again and checks 34 expectations. |

How a scan works:

1. Each category has its own scanner and only looks in its own allow-listed folders (`CategoryCatalog.cs`).
2. `SafeFileWalker` reads directory listings only. It never follows junctions or symlinks; it does enter OneDrive's "cloud files" folders, because those are real folders in place. It skips cloud-only placeholders (reading one would download it) and anything on your exclusion list.
3. Before a file is listed, `FileSystemInfoNative.ProbeMovable` asks Windows whether it could be moved right now. Locked files are reported as **skipped: in use**, not hidden.
4. Every item goes through `CategoryResultBuilder.TryAdd`, which checks three things independently of the scanner:
   - The item is inside a folder that scanner declared.
   - `ProtectedPathPolicy` allows it. That means no Program Files, System32, SysWOW64, WinSxS, nothing in `C:\Windows` or ProgramData outside the exact cache folders, and no personal or cloud folders except for Large files and Duplicate files.
   - It's not on your exclusion list.

   Anything that fails the first two is dropped and reported as a **policy violation** (a bug), and the CLI exits with code 2.
5. Overlaps: each path is offered once. A file inside a folder row (an old Downloads folder, Windows.old) isn't listed again elsewhere.
6. Cancel (Ctrl+C) throws the partial result away. A half-finished list could pass for a complete one.

## Files

```
Directory.Build.props                      net10.0-windows, nullable, warnings-as-errors
KvaliTool.slnx
src/KvaliTool.Core/
  Models/            Enums.cs, CategoryDefinition.cs, ScanItem.cs, ScanReport.cs
  Configuration/     ScanSettings.cs (thresholds + validation), SettingsStore.cs (load only)
  Serialization/     KvaliJsonContext.cs (source-generated JSON, CreateNew-only report writer)
  Platform/          NativeMethods.cs (read-only Win32), FileSystemInfoNative.cs (lock probe,
                     reparse tags, hard-link identity), PathUtil.cs (8.3 expansion, containment),
                     KnownPaths.cs (real + sandbox locations), SystemInfo.cs, CommandRunner.cs
  Safety/            ProtectedPathPolicy.cs, ExclusionList.cs
  Scanning/          ScanEngine.cs, ScanContext.cs, CategoryResultBuilder.cs, SafeFileWalker.cs,
                     FileScan.cs, ICategoryScanner.cs, CategoryCatalog.cs
  Scanning/Scanners/ TempScanners.cs, SystemCacheScanners.cs, RecycleBinScanner.cs,
                     BrowserCacheScanner.cs, DeveloperCacheScanner.cs, ToolBasedScanners.cs,
                     UserFileScanners.cs, DuplicateFilesScanner.cs
src/KvaliTool.Cli/   Program.cs, ReportPrinter.cs
tools/Test-Phase1.ps1
```

No NuGet packages. Everything uses the .NET 10 SDK and Windows' own DLLs.

## Build and run

Requirements: .NET 10 SDK, Windows 10 2004+ or Windows 11, PowerShell 7 for the test script.

```powershell
dotnet build KvaliTool.slnx -c Release
$kt = ".\src\KvaliTool.Cli\bin\Release\net10.0-windows\kvalitool.dll"

dotnet $kt categories                     # every category, tier, clean method, caveats
dotnet $kt scan                           # full dry run, not elevated
dotnet $kt scan --only UserTemp,ChromeCache --top 20 --show-skipped
pwsh -File .\tools\Test-Phase1.ps1        # sandbox safety test
```

To include the admin-only categories (Windows Temp, Windows Update, Delivery Optimization, system error reports, Windows.old, DISM, restore points), run the same `scan` command from an **elevated** terminal. DISM's analysis is read-only but can take several minutes; add `--skip-dism` to leave it out.

Reports go to `%LOCALAPPDATA%\KvaliTool\reports\scan-<time>.json` unless you pass `--json`.

## Manual test checklist

**Proof that nothing was touched**

- [ ] `pwsh -File .\tools\Test-Phase1.ps1` ends with **ALL CHECKS PASSED**. It compares every file and folder in the sandbox, plus a folder *outside* it that a junction points to, before and after the scan: path, size, created and modified time, SHA-256. One file is held locked during the scan. Proven checks include:
  - nothing changed;
  - the junction target is never listed;
  - cookies, history, logins and local storage are never listed;
  - nothing from System32 or Program Files is listed;
  - personal and cloud files appear only under Large files or Duplicate files.
- [ ] Code audit: this search over `src` returns nothing:
  ```powershell
  Get-ChildItem .\src -Recurse -Filter *.cs | Select-String -Pattern '\.Delete\(|File\.Move|Directory\.Move|MoveTo\(|CopyTo\(|File\.Replace|SHEmptyRecycleBin|SHFileOperation|DeleteFile|RemoveDirectory|MoveFileEx|DeleteOnClose|WriteAllBytes|WriteAllText|SetValue\('
  ```
- [ ] On your real machine, before and after `dotnet $kt scan`, snapshot a folder that the scan lists, e.g. your temp folder:
  ```powershell
  Get-ChildItem $env:TEMP -Recurse -Force -File | % { $_.Refresh(); "$($_.FullName)|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" } > before.txt
  ```
  Run the scan, take `after.txt` the same way, then `Compare-Object (gc before.txt) (gc after.txt)`. Any differences must come from other programs writing temp files, never from removed files. The scan removes nothing.

**Correctness**

- [ ] Open Chrome, run `dotnet $kt scan --only ChromeCache`, and confirm it says **Skipped: Chrome is running**. Close Chrome completely and run it again; only `Cache`, `Code Cache`, `GPUCache` and `Dawn*` paths appear.
- [ ] `--show-skipped --only UserTemp`: files changed in the last 24 h show as *too recent*; a file you hold open (e.g. a document open in Word inside `%TEMP%`) shows as *in use*.
- [ ] Not elevated: the 7 admin categories show `[NeedsAdmin]` and nothing else asks for elevation. Elevated: they're scanned.
- [ ] Add an exclusion to `%LOCALAPPDATA%\KvaliTool\settings.json`, e.g. `{ "exclusions": ["*.iso"] }`. Rescan: no `.iso` appears and the skip summary shows "excluded by you". Then break the JSON on purpose: the CLI must refuse to run (exit 78) instead of ignoring your exclusions.
- [ ] Every Duplicate files row names a **kept copy** that exists and isn't listed itself.
- [ ] Cancel a scan with Ctrl+C: "Scan cancelled. Nothing was changed and no report was written."

## Things I won't fake (and what Phase 3 will do instead)

| Category | Honest limitation |
|---|---|
| Thumbnail and icon cache | Files Explorer has open are reported as in use. KvaliTool won't kill or restart Explorer. |
| Delivery Optimization | Only Windows' `Delete-DeliveryOptimizationCache` can clean it. Permanent, so no quarantine. |
| Docker build cache | Only `docker builder prune` can clean it. Permanent. Docker's virtual disk (`ext4.vhdx`) doesn't shrink by itself, so your drive may not gain free space until it's compacted. |
| Windows.old | Planned method: `DISM /Online /Remove-OSUninstall`. **I still need to verify on a test machine that it removes the folder**; if it doesn't, this becomes report-only. `$Windows.~BT` / `~WS` are already report-only ("use Disk Cleanup"). Too big and too locked-down to quarantine, so it's permanent. Each row warns if it contains personal files under `Windows.old\Users`. |
| Component store | Size can't be known in advance, so it shows as "size unknown". `/StartComponentCleanup` only, never `/ResetBase`. Permanent. |
| Restore points | Windows doesn't report a size per restore point. Also, Windows **refuses to create a new restore point if one was made in the last 24 h**, so Phase 3 will tell you when the "restore point before system cleanup" rule is covered by an existing recent point rather than a new one. |
| Locked files in general | Re-checked again right before acting in Phase 3 (exists, same size, same modified time, not locked). |
| Sizes | Logical file sizes. Compressed or sparse files may free less than shown. |

## Decisions I need from you before Phase 3

1. **Recycle Bin vs. "permanent deletion is opt-in".** Emptying the Recycle Bin is permanent by nature, but you made it SAFE and checked by default. Options:
   - **(a) Recommended:** move each `$R`/`$I` pair into quarantine on the same drive, so it's restorable for 7 days like everything else.
   - (b) Treat it as permanent, which means it's only cleaned when you tick "delete permanently" for that run.
2. **Duplicate search speed.** On this PC it took 8 min 23 s of an 8 min 24 s scan: about 24 GB of identical files on your D: hard drive had to be fully hashed. Everything else took about 6 s. Options:
   - (a) Make "Duplicate files" an opt-in per scan.
   - (b) Cache hashes in `%LOCALAPPDATA%\KvaliTool` (keyed by path, size, modified time and file ID), so rescans are fast.
   - (c) Both. I'd do both.
