# KvaliTool

A Windows disk cleaner built for safety and transparency: scan → show exactly what was found and why → you choose → clean (into a restorable quarantine by default).

| Phase | Status |
|---|---|
| 1. Scan engine, dry run only | Done, see [docs/PHASE1.md](docs/PHASE1.md) |
| 2. Results UI (WinUI 3) | Not started |
| 3. Cleanup, quarantine, restore | Not started |
| 4. Tests and packaging | Not started |

## Run it (one line, in PowerShell)

```powershell
irm https://github.com/<owner>/KvaliTool/releases/latest/download/kvalitool.ps1 | iex
```

The launcher downloads one self-contained `kvalitool.exe` (11 MB, no .NET needed) to `%LOCALAPPDATA%\KvaliTool\bin\<version>`. It runs that exe only if its SHA-256 matches the hash pinned inside the launcher, then shows a menu. It never elevates by itself; only menu option 2 asks for administrator rights.

Skip the menu: `$env:KVALITOOL_ACTION = 'scan'; irm <url> | iex` (also `scan-admin`, `categories`).

## Test it

```powershell
pwsh -File .\tools\Test-Phase1.ps1      # sandbox: proves the scan changes nothing (34 checks)
pwsh -File .\tools\Test-Launcher.ps1    # the irm | iex flow end to end on localhost (12 checks)
```

## Release it

```powershell
pwsh -File .\tools\Publish-Release.ps1 -Repo <owner>/KvaliTool   # builds dist\, prints the gh command
```
