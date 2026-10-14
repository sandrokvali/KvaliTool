namespace KvaliTool.Core.Models;

/// <summary>How much trust a category deserves. SAFE is pre-checked; REVIEW never is.</summary>
public enum RiskTier
{
    Safe,
    Review,
}

/// <summary>
/// Every category the scanner knows about. The order here is also the "claim order":
/// if a file matches several categories, it is listed only under the first one.
/// </summary>
public enum CategoryId
{
    UserTemp,
    WindowsTemp,
    WindowsUpdateCache,
    DeliveryOptimizationCache,
    ThumbnailCache,
    ShaderCache,
    UserErrorReports,
    SystemErrorReportsAndLogs,
    RecycleBin,
    ChromeCache,
    EdgeCache,
    BraveCache,
    FirefoxCache,
    DeveloperCaches,
    DockerBuildCache,
    PreviousWindowsInstallation,
    ComponentStore,
    OldRestorePoints,
    DuplicateFiles,
    LargeFiles,
    OldDownloads,
}

/// <summary>How a category will be cleaned in Phase 3. Decides whether it can be restored.</summary>
public enum CleanMethod
{
    /// <summary>Files are moved to a quarantine folder on the same drive and can be restored.</summary>
    QuarantineFiles,

    /// <summary>Recycle Bin entries ($R data + $I metadata pairs).</summary>
    RecycleBinEntries,

    /// <summary>Only Windows' own tool can clean it (DISM, Delivery Optimization, System Restore). Permanent.</summary>
    WindowsTool,

    /// <summary>Only the owning program's command can clean it (docker builder prune). Permanent.</summary>
    ExternalTool,
}

public enum CategoryStatus
{
    /// <summary>Scanned completely.</summary>
    Scanned,

    /// <summary>Scanned, but some folders could not be read (see Skipped).</summary>
    Partial,

    /// <summary>Not scanned because it needs administrator rights.</summary>
    NeedsAdmin,

    /// <summary>Deliberately not scanned (browser running, update pending, sandbox mode, ...). See Notes.</summary>
    Skipped,

    /// <summary>Nothing to scan: the folder or program does not exist on this PC.</summary>
    NotFound,

    /// <summary>The scanner hit an unexpected error. See Notes.</summary>
    Failed,
}

public enum ItemKind
{
    File,

    /// <summary>A whole folder reported as one row (e.g. Windows.old). Size is the total of its files.</summary>
    Folder,

    /// <summary>One Recycle Bin entry (may be a file or a folder).</summary>
    RecycleBinEntry,

    /// <summary>Not a path on disk: a restore point, the Docker build cache, the component store.</summary>
    Virtual,
}

/// <summary>Why something the scanner saw is NOT offered for cleaning.</summary>
public enum SkipReason
{
    RecentlyModified,
    Locked,
    AccessDenied,
    CloudOnlyPlaceholder,
    LinkNotFollowed,
    ExcludedByUser,
    Unreadable,
    AlreadyListed,
    HardLinkOfSameFile,
    OrphanedEntry,
}
