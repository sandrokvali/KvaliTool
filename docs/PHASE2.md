# Phase 2: results UI (WinUI 3)

Status: built and checked, waiting for your review. **This build contains no code that deletes, moves or modifies your files.** The Clean button is visible but disabled ("Available in Phase 3"). The only files KvaliTool writes are its own: `settings.json`, `hashcache.bin` and scan reports, all in `%LOCALAPPDATA%\KvaliTool`.

## Plan

| Piece | What it does |
|---|---|
| `KvaliTool.Core/Selection` | `ResultTree`: the results as a flat category > group > item list with tri-state checkboxes, sort, filter and the selection rules. No UI code, so it is fully unit-tested. |
| `KvaliTool.Core/Configuration` | `SettingsStore.Save` (atomic), `FileHashCache` (remembers file hashes between scans), `ScanSettings.FindDuplicates` (opt-in). |
| `KvaliTool.App` | The WinUI 3 window: Scan, Results and Settings pages. Unpackaged and self-contained (no MSIX, no .NET install). |
| `tests/KvaliTool.Tests` | MSTest suite (108 tests). |
| Launcher | New menu option "Open KvaliTool (window)": downloads the zip, checks its SHA-256, unzips it, and re-checks every file on every start. |

### Selection rules (enforced in Core, covered by tests)

- "Select safe items" and the initial selection never tick a REVIEW item, an item in a personal or cloud-synced folder, a report-only item or an excluded item.
- A duplicate set can never have every copy ticked. The kept copy counts as ticked if it is listed anywhere (for example under Large files), **or if a folder row that contains it is ticked** (an old Downloads folder offered as a whole). The refused item is reported in the InfoBar.
- A parent shows "mixed" when its children differ.
- Sorting, filtering and expanding never change the selection; parent state and selected size always cover hidden rows too.
- 100,000 items: build about 110 ms, sort about 15-20 ms, filter about 20 ms (limit: 1 s each).

### Duplicate search is opt-in

Off by default (it took 8 of 8.5 minutes on a real PC). Turn on "Find duplicates (slow)" on the Scan page, `--find-duplicates` in the CLI, or `findDuplicates` in settings. Full-file hashes are cached in `%LOCALAPPDATA%\KvaliTool\hashcache.bin`, keyed by path + size + modified time + NTFS change time + file ID, so an unchanged file is never re-read. A changed file is re-hashed even if someone put its modified time back. A corrupt or missing cache just starts empty. Sandbox scans never use it.

### Recycle Bin (decision recorded; implemented in Phase 3)

Phase 3 will quarantine the `$R`/`$I` pairs on the same drive so they stay restorable for 7 days like everything else.

## Files

```
src/KvaliTool.Core/Selection/ResultTree.cs        ResultTree, ResultRow, CheckState, SortKey
src/KvaliTool.Core/Configuration/AppDataFile.cs   the only code that replaces a file (atomic write of KvaliTool's own files)
src/KvaliTool.Core/Configuration/HashCache.cs     IHashCache, FileHashCache
src/KvaliTool.App/                                App, MainWindow, ScanPage, ResultsPage, SettingsPage, ResultRowsAdapter, ...
tests/KvaliTool.Tests/                            108 tests
tools/Publish-Release.ps1                         builds the exe, the app zip, the launcher and SHA256SUMS.txt
tools/Test-Launcher.ps1                           30 launcher checks, including tampering
tools/New-SyntheticReport.ps1                     a 100k-item report for testing the UI
```

NuGet packages: `Microsoft.WindowsAppSDK`, `Microsoft.Windows.SDK.BuildTools` (app only) and `MSTest.Sdk` (tests only). Nothing else.

## Build and run

```powershell
dotnet build KvaliTool.slnx -c Release
dotnet test tests\KvaliTool.Tests -c Release
.\src\KvaliTool.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\KvaliTool.exe
.\src\KvaliTool.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\KvaliTool.exe --report <saved-report.json>   # read-only
.\src\KvaliTool.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\KvaliTool.exe --theme light                  # override light/dark for one run
```

Use the default output folders: MSBuild breaks on `-o <path>` when the path has an apostrophe ("Sandro's files").

## Verified (and how)

| Check | Result |
|---|---|
| `dotnet build KvaliTool.slnx -c Release` | 0 warnings, 0 errors |
| `dotnet test` | 107 passed, 1 skipped (creating a file symlink needs admin or Developer Mode) |
| `tools\Test-Phase1.ps1` | ALL CHECKS PASSED (34 checks) |
| `tools\Test-Launcher.ps1` | ALL LAUNCHER CHECKS PASSED (30 checks) |
| Code audit (command below) | only `AppDataFile.cs`: the atomic replace of KvaliTool's own settings/cache file, and removing its own temp file if that fails |
| Running app, real scan | Scan page, live progress, Results page, expand, tri-state, details pane, image preview, Ctrl+F, filter, Tab order, Space, Shift+F10 menu, Always exclude (saved, then removed in Settings), Settings save |
| Duplicate safety in the running app | ticking the last copy is refused with an explanation |
| Pre-ticking in the running app | REVIEW categories and personal-folder items start unticked |

## Not verified or not done (honest gaps)

- **Light theme and contrast:** the app follows the system theme and I ran it in dark; light mode was only opened, not inspected for 4.5:1 contrast.
- **Smooth scrolling of a group with more than 10,000 rows:** a 100,000-item report opens and expands, but I have not yet measured scrolling. Treat "smooth" as unproven.
- **Text scaling at 200 %** and **Narrator** were not run.
- **Narrow window:** at the 900 px minimum, the Size/Modified/Reason columns need horizontal scrolling (the Details pane can be hidden).
- **Broken-settings flow in the window** (`settings.json` unreadable disables scanning and saving) was not exercised there; the CLI behaviour from Phase 1 still applies.
- **No folder picker** for exclusions: you type the path.
- **Trimming and ReadyToRun are off** for the app (trimming fails on JSON reflection; ReadyToRun only makes it bigger). The zip is about 86 MB.
- **A duplicate scan on a real PC with the cache** was only tested on sandbox folders.
- **The app is not code-signed**, so Windows SmartScreen may warn on first run of a downloaded copy.

## Manual test checklist

**Proof that nothing at all was touched (Phase 2 changes no user files)**

- [ ] `pwsh -File .\tools\Test-Phase1.ps1` ends with **ALL CHECKS PASSED** (compares every file, folder and SHA-256 in a sandbox, plus a folder outside it, before and after the scan).
- [ ] Code audit: this returns only the two lines in `AppDataFile.cs`:
  ```powershell
  Get-ChildItem .\src -Recurse -Filter *.cs | Where-Object FullName -notmatch '\\(bin|obj)\\' | Select-String -Pattern '\.Delete\(|File\.Move|Directory\.Move|MoveTo\(|CopyTo\(|File\.Replace|SHEmptyRecycleBin|SHFileOperation|DeleteFile|RemoveDirectory|MoveFileEx|DeleteOnClose|WriteAllBytes|WriteAllText|SetValue\('
  ```
- [ ] Snapshot your temp folder before and after a scan and compare (see PHASE1.md). Any difference must come from other programs.
- [ ] Tick a few items and use every button: nothing is removed, because the Clean button is disabled.

**The window**

- [ ] Scan page: SAFE categories ticked, REVIEW not; admin-only ones show "Needs admin"; "Find duplicates (slow)" is off.
- [ ] Start a scan: progress updates, Cancel says "Scan cancelled. Nothing was changed."
- [ ] Results: expand a category and group; REVIEW items and anything under Documents, Desktop, OneDrive etc. are **not** ticked; "Select safe items" does not tick them either.
- [ ] Tick a parent: children follow; tick one child: parent shows mixed; header shows selected size.
- [ ] Right-click (or Shift+F10) a file: Open file location selects it in Explorer; Copy path; Always exclude saves to `settings.json` and marks it "Excluded".
- [ ] Select an image: the preview appears.
- [ ] Keyboard only: Tab through the page, Ctrl+F focuses search, Space toggles a row, Left/Right collapse/expand.
- [ ] Settings: change a threshold, Save; remove an exclusion, Save.
- [ ] Turn on "Find duplicates (slow)" on a small test (`--only` is not available in the window; use the CLI with `--only DuplicateFiles --find-duplicates` first). Run it twice: the second run should be much faster.

**The launcher**

- [ ] `irm https://github.com/sandrokvali/KvaliTool/releases/latest/download/kvalitool.ps1 | iex` shows the menu; option 1 downloads, verifies and opens the window.
- [ ] Run it again: "window app (verified)". Edit or add a file in `%LOCALAPPDATA%\KvaliTool\app\0.2.0\` and run it again: it must refuse.
