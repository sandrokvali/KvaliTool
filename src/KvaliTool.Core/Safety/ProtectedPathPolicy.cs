using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;

namespace KvaliTool.Core.Safety;

public readonly record struct PolicyDecision(bool Allowed, bool InUserContent, bool InCloudSync, string? Rule)
{
    public static PolicyDecision Deny(string rule) => new(false, false, false, rule);
}

/// <summary>
/// The NEVER TOUCH rules, applied to every item before it can enter a scan result, regardless of which
/// scanner produced it. Scanners already only look in their own folders; this is the second, independent
/// line of defence that catches a scanner bug.
/// </summary>
public sealed class ProtectedPathPolicy
{
    public const string QuarantineFolderName = "KvaliTool.Quarantine";

    private readonly string[] _alwaysDenied;
    private readonly string[] _systemAreas;
    private readonly string[] _systemExceptions;
    private readonly string[] _userContent;
    private readonly string[] _cloudSync;

    public ProtectedPathPolicy(IKnownPaths paths)
    {
        var windows = paths.WindowsDir;

        // No category may ever list anything in here. WinSxS is only ever handled by DISM, which works on
        // the component store as a whole and never through a file list.
        _alwaysDenied =
        [
            .. paths.ProgramFilesDirs,
            Path.Combine(windows, "System32"),
            Path.Combine(windows, "SysWOW64"),
            Path.Combine(windows, "WinSxS"),
            Path.Combine(windows, "SystemApps"),
            Path.Combine(windows, "servicing"),
            paths.AppDataDir,
            .. paths.FixedDriveRoots.Select(root => Path.Combine(root, QuarantineFolderName)),
        ];

        // Inside the Windows folder and ProgramData, only these exact places are allowed.
        _systemAreas = [windows, paths.ProgramData];
        _systemExceptions =
        [
            Path.Combine(windows, "Temp"),
            Path.Combine(windows, "SoftwareDistribution", "Download"),
            Path.Combine(windows, "Logs"),
            Path.Combine(windows, "Minidump"),
            Path.Combine(windows, "MEMORY.DMP"),
            Path.Combine(windows, "LiveKernelReports"),
            DeliveryOptimizationCachePath(paths),
            Path.Combine(paths.ProgramData, "Microsoft", "Windows", "WER"),
        ];

        _userContent = [.. paths.UserContentFolders];
        _cloudSync = [.. paths.CloudSyncFolders];
    }

    public static string DeliveryOptimizationCachePath(IKnownPaths paths) => Path.Combine(
        paths.WindowsDir, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache");

    public PolicyDecision Evaluate(string path, CategoryDefinition category)
    {
        foreach (var denied in _alwaysDenied)
        {
            if (PathUtil.IsSameOrUnder(path, denied))
            {
                return PolicyDecision.Deny($"Inside a protected location ({denied})");
            }
        }

        if (IsInsideSystemAreaOutsideExceptions(path))
        {
            return PolicyDecision.Deny("Inside the Windows or ProgramData folder, outside the cache folders KvaliTool may clean");
        }

        var inUserContent = _userContent.Any(root => PathUtil.IsSameOrUnder(path, root));
        var inCloud = _cloudSync.Any(root => PathUtil.IsSameOrUnder(path, root));
        if ((inUserContent || inCloud) && !category.AllowsUserContent)
        {
            return PolicyDecision.Deny("Inside a personal or cloud-synced folder; only Large files and Duplicate files may list these");
        }

        return new PolicyDecision(true, inUserContent, inCloud, null);
    }

    /// <summary>
    /// For walks over broad roots (Large files, Duplicates): folders that must not even be entered.
    /// Allowed system exceptions (Windows\Temp etc.) are not entered either; they belong to their own categories.
    /// </summary>
    public bool IsOffLimitsForUserFileWalk(string directory) =>
        _alwaysDenied.Any(denied => PathUtil.IsSameOrUnder(directory, denied))
        || _systemAreas.Any(area => PathUtil.IsSameOrUnder(directory, area));

    public bool IsInCloudSyncFolder(string path) => _cloudSync.Any(root => PathUtil.IsSameOrUnder(path, root));

    public bool IsInUserContentFolder(string path) => _userContent.Any(root => PathUtil.IsSameOrUnder(path, root));

    private bool IsInsideSystemAreaOutsideExceptions(string path) =>
        _systemAreas.Any(area => PathUtil.IsSameOrUnder(path, area))
        && !_systemExceptions.Any(allowed => PathUtil.IsSameOrUnder(path, allowed));
}
