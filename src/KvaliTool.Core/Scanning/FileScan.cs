using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;

namespace KvaliTool.Core.Scanning;

/// <param name="NewestUtc">Newest creation or modified time of any file inside (null if empty).</param>
/// <param name="ExcludedEntries">Files or folders inside that are on your exclusion list.</param>
public readonly record struct AggregateTotals(long Bytes, long Files, int UnreadableFolders, int ExcludedEntries, DateTime? NewestUtc);

/// <summary>Building blocks shared by the scanners.</summary>
internal static class FileScan
{
    /// <summary>
    /// Walks <paramref name="root"/> and lists every file <paramref name="reasonFor"/> accepts (returns a reason)
    /// that is old enough and can be moved right now. Files that are too new or locked are reported as skipped.
    /// </summary>
    public static void Candidates(
        ScanContext context,
        CategoryResultBuilder result,
        string root,
        string group,
        Func<WalkEntry, string?> reasonFor,
        TimeSpan? minimumAge = null,
        Func<string, FileAttributes, bool>? descendInto = null)
    {
        if (!Directory.Exists(root) || !IsSafeCacheRoot(context, result, root))
        {
            return;
        }

        result.DeclareRoot(root);
        foreach (var entry in context.Walker.EnumerateFiles(root, result, descendInto))
        {
            var reason = reasonFor(entry);
            if (reason is null)
            {
                continue;
            }

            if (minimumAge is { } minAge && context.AgeOf(entry) < minAge)
            {
                result.Skip(entry.FullPath, SkipReason.RecentlyModified,
                    $"Changed {Format.Age(context.AgeOf(entry))} ago; files newer than {Format.Age(minAge)} are left alone", entry.Length);
                continue;
            }

            if (!ProbeIsMovable(result, entry.FullPath, entry.Length, isDirectory: false))
            {
                continue;
            }

            result.TryAdd(ToItem(entry, reason, group, root));
        }
    }

    /// <summary>Lists one specific file (e.g. C:\Windows\MEMORY.DMP).</summary>
    public static void SingleFile(ScanContext context, CategoryResultBuilder result, string path, string group, string reason)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            return;
        }

        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            result.Skip(path, SkipReason.LinkNotFollowed, "Symbolic link: not followed");
            return;
        }

        context.Progress.FileExamined(path);
        result.DeclareRoot(path);
        if (ProbeIsMovable(result, path, info.Length, isDirectory: false))
        {
            result.TryAdd(new ScanItem
            {
                Path = info.FullName,
                Name = info.Name,
                Kind = ItemKind.File,
                SizeBytes = info.Length,
                LastModifiedUtc = info.LastWriteTimeUtc,
                Reason = reason,
                Group = group,
                GroupRoot = info.DirectoryName,
            });
        }
    }

    /// <summary>Totals a whole folder (for items reported as one row, like Windows.old).</summary>
    public static AggregateTotals Aggregate(ScanContext context, CategoryResultBuilder result, string root, Action<WalkEntry>? observe = null)
    {
        int Count(params SkipReason[] reasons) => result.Skipped.Count(s => reasons.Contains(s.Reason));

        var unreadableBefore = Count(SkipReason.AccessDenied, SkipReason.Unreadable);
        var excludedBefore = Count(SkipReason.ExcludedByUser);
        long bytes = 0, files = 0;
        DateTime? newest = null;
        foreach (var entry in context.Walker.EnumerateFiles(root, result))
        {
            bytes += entry.Length;
            files++;
            var stamp = entry.CreationUtc > entry.LastWriteUtc ? entry.CreationUtc : entry.LastWriteUtc;
            if (newest is null || stamp > newest)
            {
                newest = stamp;
            }

            observe?.Invoke(entry);
        }

        return new AggregateTotals(
            bytes,
            files,
            Count(SkipReason.AccessDenied, SkipReason.Unreadable) - unreadableBefore,
            Count(SkipReason.ExcludedByUser) - excludedBefore,
            newest);
    }

    /// <summary>
    /// Re-checks right before listing: is the file still there, and could it be moved now?
    /// (Phase 3 repeats this check, plus size and modified time, immediately before acting.)
    /// </summary>
    public static bool ProbeIsMovable(CategoryResultBuilder result, string path, long? size, bool isDirectory)
    {
        switch (FileSystemInfoNative.ProbeMovable(path, isDirectory))
        {
            case ProbeResult.Movable:
                return true;
            case ProbeResult.Locked:
                result.Skip(path, SkipReason.Locked, "In use by another program", size);
                return false;
            case ProbeResult.AccessDenied:
                result.Skip(path, SkipReason.AccessDenied, "No permission (needs administrator, or belongs to another account)", size);
                return false;
            case ProbeResult.Missing:
                return false;
            default:
                result.Skip(path, SkipReason.Unreadable, "Windows returned an unexpected error for this file", size);
                return false;
        }
    }

    public static ScanItem ToItem(in WalkEntry entry, string reason, string group, string groupRoot, string? warning = null) => new()
    {
        Path = entry.FullPath,
        Name = entry.Name,
        Kind = ItemKind.File,
        SizeBytes = entry.Length,
        LastModifiedUtc = entry.LastWriteUtc,
        Reason = reason,
        Group = group,
        GroupRoot = groupRoot,
        Warning = warning,
    };

    /// <summary>
    /// Cache locations can come from environment variables (npm_config_cache, NUGET_PACKAGES, ...). If one of
    /// them points somewhere absurd, such as C:\ or your user folder, refuse instead of "cleaning" it as a cache.
    /// </summary>
    private static bool IsSafeCacheRoot(ScanContext context, CategoryResultBuilder result, string root)
    {
        var normalized = PathUtil.Normalize(root);
        var paths = context.Paths;
        string[] tooBroad = [paths.UserProfile, paths.LocalAppData, paths.RoamingAppData, paths.LocalLowAppData, paths.WindowsDir, paths.ProgramData, .. paths.UserContentFolders, .. paths.CloudSyncFolders];

        if (PathUtil.IsDriveRoot(normalized) || tooBroad.Any(broad => PathUtil.IsSameOrUnder(broad, normalized)))
        {
            result.Note($"Refused to scan '{normalized}': it is far too broad to be a cache folder (check your environment variables).");
            return false;
        }

        return true;
    }
}

/// <summary>File types that deserve an extra warning wherever they show up.</summary>
internal static class FileWarnings
{
    public static string? For(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".vhd" or ".vhdx" or ".avhdx" or ".vmdk" or ".vdi" or ".qcow2" => "Virtual machine disk: removing it destroys that virtual machine.",
        ".pst" or ".ost" => "Outlook mailbox data file.",
        ".kdbx" or ".kdb" or ".1pux" or ".opvault" => "Password database.",
        ".wallet" => "Possibly a cryptocurrency wallet.",
        ".exe" or ".dll" or ".sys" or ".pyd" or ".so" or ".node" or ".jar" or ".pdb" => "Program file: software may depend on this exact copy in this exact place.",
        _ => null,
    };
}
