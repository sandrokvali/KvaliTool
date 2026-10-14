namespace KvaliTool.Core.Configuration;

/// <summary>User-adjustable thresholds. Stored as JSON in %LOCALAPPDATA%\KvaliTool\settings.json.</summary>
public sealed class ScanSettings
{
    public const long OneMiB = 1024L * 1024;
    public const long OneGiB = 1024L * OneMiB;

    /// <summary>Temp and update-cache files changed more recently than this are never offered.</summary>
    public int TempMinAgeHours { get; set; } = 24;

    public int DownloadsMinAgeDays { get; set; } = 90;

    public long LargeFileThresholdBytes { get; set; } = OneGiB;

    /// <summary>Smaller files are ignored by the duplicate finder (hashing thousands of tiny files is slow and frees little).</summary>
    public long DuplicateMinBytes { get; set; } = OneMiB;

    public int LogMinAgeDays { get; set; } = 30;

    /// <summary>Used from Phase 3 on.</summary>
    public int QuarantineRetentionDays { get; set; } = 7;

    /// <summary>
    /// Search for duplicate files. Off by default because it reads every candidate file in full (it took 8 of
    /// 8.5 minutes on a real PC). This is the default for the "Find duplicates (slow)" switch; the UI and CLI can
    /// turn it on for a single scan. When off, the Duplicate files category reports Skipped with a note.
    /// </summary>
    public bool FindDuplicates { get; set; }

    /// <summary>Run DISM /AnalyzeComponentStore when elevated. It is read-only but can take several minutes.</summary>
    public bool AnalyzeComponentStore { get; set; } = true;

    /// <summary>
    /// Paths or wildcard patterns that are never listed. A plain path excludes that file or folder and
    /// everything under it. Patterns use * and ? and are matched against the full path, e.g. "*.psd".
    /// Environment variables such as %USERPROFILE% are expanded.
    /// </summary>
    public List<string> Exclusions { get; set; } = [];

    /// <summary>Where Large files and Duplicate files look. Empty means your user folder (AppData is always skipped).</summary>
    public List<string> UserFileScanRoots { get; set; } = [];

    /// <summary>Folder names the duplicate finder never enters: identical files inside them are normal and needed.</summary>
    public List<string> DuplicateIgnoredFolderNames { get; set; } =
        ["node_modules", ".git", ".svn", ".hg", ".vs", "__pycache__", ".venv", "venv", "site-packages"];

    /// <summary>An independent deep copy (through the same JSON the settings file uses, so every property is copied).</summary>
    public ScanSettings Clone() => Serialization.ScanReportJson.Clone(this);

    /// <summary>Rejects values that would make the cleaner reckless (e.g. a 0-hour temp age).</summary>
    public void Validate()
    {
        Require(TempMinAgeHours >= 1, "tempMinAgeHours must be at least 1.");
        Require(DownloadsMinAgeDays >= 1, "downloadsMinAgeDays must be at least 1.");
        Require(LogMinAgeDays >= 1, "logMinAgeDays must be at least 1.");
        Require(LargeFileThresholdBytes >= OneMiB, "largeFileThresholdBytes must be at least 1 MB.");
        Require(DuplicateMinBytes >= 1, "duplicateMinBytes must be at least 1 byte.");
        Require(QuarantineRetentionDays >= 1, "quarantineRetentionDays must be at least 1.");

        static void Require(bool ok, string message)
        {
            if (!ok)
            {
                throw new SettingsException(message);
            }
        }
    }
}

public sealed class SettingsException(string message, Exception? inner = null) : Exception(message, inner);
