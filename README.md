# KvaliTool

A Windows disk cleaner built for safety and transparency: scan → show exactly what was found and why → you choose → clean (into a restorable quarantine by default).

| Phase | Status |
|---|---|
| 1. Scan engine, dry run only | Done, see [docs/PHASE1.md](docs/PHASE1.md) |
| 2. Results UI (WinUI 3) | Done, see [docs/PHASE2.md](docs/PHASE2.md). Shows and selects; **cleaning is disabled** |
| 3. Cleanup, quarantine, restore | Not started |
| 4. Tests and packaging | Not started |

## Run it (one line, in PowerShell)

```powershell
irm https://github.com/sandrokvali/KvaliTool/releases/latest/download/kvalitool.ps1 | iex
```

The launcher shows a menu and downloads only what the option you pick needs, into `%LOCALAPPDATA%\KvaliTool`:

- **1 Open KvaliTool (window)**: a self-contained WinUI 3 app (zip, no .NET or Windows App SDK install needed). The launcher checks the zip's SHA-256 against the hash pinned inside the launcher, unzips it, and on **every** start checks every unzipped file against the list inside that verified zip. A changed, missing or extra file (for example a planted DLL) means it refuses to start.
- **2–4**: the console scanner from Phase 1 (`kvalitool.exe`, single file, also hash-pinned).

It never elevates by itself; only the "system locations" scan asks for administrator rights. It never deletes anything except its own half-finished download.

Skip the menu: `$env:KVALITOOL_ACTION = 'app'; irm <url> | iex` (also `app-install`, `scan`, `scan-admin`, `categories`).

## Build and test it

```powershell
dotnet build KvaliTool.slnx -c Release
dotnet test tests\KvaliTool.Tests -c Release   # selection rules, settings, hash cache, 100k-row timings
pwsh -File .\tools\Test-Phase1.ps1              # sandbox: proves the scan changes nothing
pwsh -File .\tools\Test-Launcher.ps1            # the irm | iex flow end to end on localhost, incl. tampering
```

Run the app from a build: `src\KvaliTool.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\KvaliTool.exe`.
Open a saved report read-only: `KvaliTool.exe --report <report.json>`.

## Release it

```powershell
pwsh -File .\tools\Publish-Release.ps1 -Repo sandrokvali/KvaliTool   # builds dist\, prints the gh command
```
