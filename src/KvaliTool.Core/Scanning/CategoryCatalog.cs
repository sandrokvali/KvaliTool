using KvaliTool.Core.Models;
using KvaliTool.Core.Scanning.Scanners;

namespace KvaliTool.Core.Scanning;

/// <summary>
/// The single list of categories. Order = display order = claim order (a file matching two categories is
/// listed only under the first). There is deliberately no registry category.
/// </summary>
public static class CategoryCatalog
{
    private const string BrowserDescription =
        "Cached pages, scripts and images only. Cookies, history, saved passwords and open sessions are never touched. Skipped while the browser is running.";

    public static IReadOnlyList<CategoryDefinition> All { get; } =
    [
        // ---------------- SAFE ----------------
        new(CategoryId.UserTemp, "Temporary files (your account)",
            "Files programs left in your temp folder. Files changed in the last 24 hours (or your setting) and files in use are left alone.",
            RiskTier.Safe, CleanMethod.QuarantineFiles, RequiresElevation: false, AllowsUserContent: false, Caveat: null),

        new(CategoryId.WindowsTemp, "Temporary files (Windows)",
            @"Files in C:\Windows\Temp. Same rules: recent and in-use files are left alone.",
            RiskTier.Safe, CleanMethod.QuarantineFiles, RequiresElevation: true, AllowsUserContent: false, Caveat: null),

        new(CategoryId.WindowsUpdateCache, "Windows Update downloads",
            "Update packages Windows has already downloaded. Windows downloads them again if it still needs them. Skipped while an update is waiting for a restart.",
            RiskTier.Safe, CleanMethod.QuarantineFiles, RequiresElevation: true, AllowsUserContent: false,
            Caveat: "Cleaning briefly stops the Windows Update service and starts it again afterwards."),

        new(CategoryId.DeliveryOptimizationCache, "Delivery Optimization cache",
            "Update pieces Windows keeps so it can share them with other PCs.",
            RiskTier.Safe, CleanMethod.WindowsTool, RequiresElevation: true, AllowsUserContent: false,
            Caveat: "Removed with Windows' own Delete-DeliveryOptimizationCache command, so it can't go to quarantine. It is only a download cache."),

        new(CategoryId.ThumbnailCache, "Thumbnail and icon cache",
            "Explorer's thumbnail and icon databases. Windows rebuilds them automatically.",
            RiskTier.Safe, CleanMethod.QuarantineFiles, RequiresElevation: false, AllowsUserContent: false,
            Caveat: "Files Explorer has open are skipped as \"in use\". KvaliTool will not restart Explorer to free them."),

        new(CategoryId.ShaderCache, "DirectX shader cache",
            "Compiled graphics shaders (DirectX, NVIDIA, AMD, Intel). Rebuilt automatically; the first game launch afterwards may stutter briefly.",
            RiskTier.Safe, CleanMethod.QuarantineFiles, RequiresElevation: false, AllowsUserContent: false, Caveat: null),

        new(CategoryId.UserErrorReports, "Error reports and crash dumps (your account)",
            "Windows Error Reporting files and application crash dumps.",
            RiskTier.Safe, CleanMethod.QuarantineFiles, RequiresElevation: false, AllowsUserContent: false, Caveat: null),

        new(CategoryId.SystemErrorReportsAndLogs, "System error reports, memory dumps and old logs",
            "System-wide error reports, kernel memory dumps, and Windows log files older than your log-age setting (30 days by default).",
            RiskTier.Safe, CleanMethod.QuarantineFiles, RequiresElevation: true, AllowsUserContent: false, Caveat: null),

        new(CategoryId.RecycleBin, "Recycle Bin",
            "Items you have already deleted, on every fixed drive. Each entry shows where it came from.",
            RiskTier.Safe, CleanMethod.RecycleBinEntries, RequiresElevation: false, AllowsUserContent: false, Caveat: null),

        new(CategoryId.ChromeCache, "Google Chrome cache", BrowserDescription,
            RiskTier.Safe, CleanMethod.QuarantineFiles, RequiresElevation: false, AllowsUserContent: false, Caveat: null),

        new(CategoryId.EdgeCache, "Microsoft Edge cache", BrowserDescription,
            RiskTier.Safe, CleanMethod.QuarantineFiles, RequiresElevation: false, AllowsUserContent: false,
            Caveat: "Edge often keeps running in the background (Startup boost). Turn that off in Edge settings if this category is always skipped."),

        new(CategoryId.BraveCache, "Brave cache", BrowserDescription,
            RiskTier.Safe, CleanMethod.QuarantineFiles, RequiresElevation: false, AllowsUserContent: false, Caveat: null),

        new(CategoryId.FirefoxCache, "Mozilla Firefox cache", BrowserDescription,
            RiskTier.Safe, CleanMethod.QuarantineFiles, RequiresElevation: false, AllowsUserContent: false, Caveat: null),

        // ---------------- REVIEW ----------------
        new(CategoryId.DeveloperCaches, "Developer caches",
            "npm, NuGet, pip and Gradle download caches and Visual Studio cache folders. Re-downloaded or rebuilt when needed (which needs internet).",
            RiskTier.Review, CleanMethod.QuarantineFiles, RequiresElevation: false, AllowsUserContent: false,
            Caveat: "Your next build or install will be slower and needs a connection. Offline work may fail until the caches are filled again."),

        new(CategoryId.DockerBuildCache, "Docker build cache",
            "Docker's build cache. It lives inside Docker's own virtual disk, not as normal files.",
            RiskTier.Review, CleanMethod.ExternalTool, RequiresElevation: false, AllowsUserContent: false,
            Caveat: "Cleaned with 'docker builder prune': permanent, no quarantine. Docker's virtual disk doesn't shrink by itself, so free space on your drive may not grow until that disk is compacted."),

        new(CategoryId.PreviousWindowsInstallation, "Previous Windows installation",
            "Windows.old and leftover Windows setup folders.",
            RiskTier.Review, CleanMethod.WindowsTool, RequiresElevation: true, AllowsUserContent: false,
            Caveat: "Removing Windows.old ends your ability to go back to the previous Windows version. Permanent: it is too large and too locked-down to quarantine."),

        new(CategoryId.ComponentStore, "Component store cleanup (DISM)",
            @"Superseded Windows components in C:\Windows\WinSxS. Only ever cleaned through DISM /StartComponentCleanup, never file by file.",
            RiskTier.Review, CleanMethod.WindowsTool, RequiresElevation: true, AllowsUserContent: false,
            Caveat: "Permanent. Old component versions are removed immediately instead of after Windows' usual grace period. KvaliTool never uses /ResetBase."),

        new(CategoryId.OldRestorePoints, "Older System Restore points",
            "Every System Restore point except the newest.",
            RiskTier.Review, CleanMethod.WindowsTool, RequiresElevation: true, AllowsUserContent: false,
            Caveat: "Permanent. Windows doesn't report how much space each restore point uses, so the size is shown as unknown."),

        new(CategoryId.DuplicateFiles, "Duplicate files",
            "Files with identical content (same size, then same SHA-256) in the same places Large files looks. One copy of each set (the oldest) is kept and not listed.",
            RiskTier.Review, CleanMethod.QuarantineFiles, RequiresElevation: false, AllowsUserContent: true,
            Caveat: "A copy may be needed where it is, for example by a program or project that expects it there."),

        new(CategoryId.LargeFiles, "Large files",
            "Files over your size threshold (1 GB by default) in your user folder and your Documents, Desktop, Pictures, Videos, Music, Downloads and cloud folders. AppData, hidden folders and folders starting with \".\" are skipped.",
            RiskTier.Review, CleanMethod.QuarantineFiles, RequiresElevation: false, AllowsUserContent: true,
            Caveat: "Being large says nothing about whether you still need a file. Look at each one."),

        new(CategoryId.OldDownloads, "Old downloads",
            "Files at the top of Downloads that haven't changed for your age threshold (90 days by default), and folders in Downloads where nothing inside has changed for that long (offered as a whole).",
            RiskTier.Review, CleanMethod.QuarantineFiles, RequiresElevation: false, AllowsUserContent: false,
            Caveat: "Installers and documents you saved here may be the only copy you have."),
    ];

    private static readonly Dictionary<CategoryId, CategoryDefinition> ById = All.ToDictionary(d => d.Id);

    public static CategoryDefinition Get(CategoryId id) => ById[id];

    public static IReadOnlyList<ICategoryScanner> CreateScanners() =>
    [
        new UserTempScanner(),
        new WindowsTempScanner(),
        new WindowsUpdateCacheScanner(),
        new DeliveryOptimizationScanner(),
        new ThumbnailCacheScanner(),
        new ShaderCacheScanner(),
        new UserErrorReportsScanner(),
        new SystemErrorReportsScanner(),
        new RecycleBinScanner(),
        BrowserCacheScanner.Chrome(),
        BrowserCacheScanner.Edge(),
        BrowserCacheScanner.Brave(),
        BrowserCacheScanner.Firefox(),
        new DeveloperCacheScanner(),
        new DockerBuildCacheScanner(),
        new PreviousWindowsScanner(),
        new ComponentStoreScanner(),
        new RestorePointScanner(),
        new DuplicateFilesScanner(),
        new LargeFilesScanner(),
        new OldDownloadsScanner(),
    ];
}
