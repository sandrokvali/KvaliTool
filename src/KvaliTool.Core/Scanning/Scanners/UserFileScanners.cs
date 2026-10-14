using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;

namespace KvaliTool.Core.Scanning.Scanners;

/// <summary>
/// Old downloads, judged at the top level of Downloads only:
///  - a top-level file is offered if it hasn't changed for the threshold;
///  - a top-level folder is offered as ONE row, and only if NOTHING inside it changed for the threshold.
/// Judging files inside folders one by one would be dangerous: extracting an archive (a portable app, a game,
/// a toolchain) keeps the archive's old dates, so a program you use every day would look "old" file by file.
/// A folder you actually use almost always has something recent in it (logs, outputs, caches).
/// </summary>
internal sealed class OldDownloadsScanner() : ScannerBase(CategoryId.OldDownloads)
{
    private static readonly EnumerationOptions TopLevel = new() { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };

    public override bool IsSlow => true;

    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
        var downloads = context.Paths.Downloads;
        if (!Directory.Exists(downloads))
        {
            result.Status = CategoryStatus.NotFound;
            return;
        }

        // Personal and cloud folders may only appear as Large/Duplicate files. A Downloads folder redirected
        // into one of them (e.g. OneDrive) is therefore not scanned here at all.
        if (context.Policy.IsInCloudSyncFolder(downloads) || context.Policy.IsInUserContentFolder(downloads))
        {
            result.Status = CategoryStatus.Skipped;
            result.Note($"Your Downloads folder ({downloads}) is inside a personal or cloud-synced folder, so it is only checked for large and duplicate files.");
            return;
        }

        var days = context.Settings.DownloadsMinAgeDays;
        var threshold = TimeSpan.FromDays(days);

        // Top-level files (the walker applies the link, placeholder and exclusion rules).
        FileScan.Candidates(context, result, downloads, "Files in Downloads", entry =>
        {
            var age = context.AgeOf(entry);
            return age >= threshold ? $"Not changed for {Format.Age(age)} (your threshold: {days} days)" : null;
        }, descendInto: (_, _) => false);

        // Top-level folders, each as a whole.
        DirectoryInfo[] folders;
        try
        {
            folders = new DirectoryInfo(downloads).GetDirectories("*", TopLevel);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Skip(downloads, SkipReason.AccessDenied, $"Couldn't list the folders in Downloads: {ex.Message}");
            return;
        }

        var recentFolders = 0;
        foreach (var folder in folders)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (folder.Attributes.HasFlag(FileAttributes.ReparsePoint) && !SafeFileWalker.IsCloudReparsePoint(folder.FullName))
            {
                result.Skip(folder.FullName, SkipReason.LinkNotFollowed, "Junction or symbolic link: not followed");
                continue;
            }

            if (context.Exclusions.IsExcluded(folder.FullName))
            {
                result.Skip(folder.FullName, SkipReason.ExcludedByUser, "Folder is on your exclusion list");
                continue;
            }

            long programs = 0;
            var totals = FileScan.Aggregate(context, result, folder.FullName, e =>
            {
                if (e.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    programs++;
                }
            });
            if (totals.Files == 0)
            {
                continue;
            }

            var newest = totals.NewestUtc!.Value > folder.CreationTimeUtc ? totals.NewestUtc.Value : folder.CreationTimeUtc;
            var age = context.NowUtc - newest;
            if (age < threshold)
            {
                recentFolders++;
                continue;
            }

            string? warning = null;
            if (programs > 0)
            {
                warning = $"Contains {programs:N0} program(s) (.exe). If you run anything from this folder, keep it.";
            }

            if (totals.UnreadableFolders > 0)
            {
                warning = CategoryResultBuilder.Combine(warning, $"{totals.UnreadableFolders:N0} sub-folder(s) couldn't be read.");
            }

            var excludedInside = totals.ExcludedEntries > 0;
            if (excludedInside)
            {
                warning = CategoryResultBuilder.Combine(warning, $"{totals.ExcludedEntries:N0} item(s) inside are on your exclusion list, so this folder is shown for information only.");
            }

            if (!excludedInside && !FileScan.ProbeIsMovable(result, folder.FullName, totals.Bytes, isDirectory: true))
            {
                continue;
            }

            result.TryAdd(new ScanItem
            {
                Path = folder.FullName,
                Name = folder.Name,
                Kind = ItemKind.Folder,
                SizeBytes = totals.Bytes,
                FileCount = totals.Files,
                LastModifiedUtc = newest,
                Reason = $"Nothing in this folder ({totals.Files:N0} files) has changed for {Format.Age(age)} (your threshold: {days} days)",
                Group = "Folders in Downloads",
                GroupRoot = downloads,
                Warning = warning,
                ReportOnly = excludedInside,
            });
        }

        if (recentFolders > 0)
        {
            result.Note($"{recentFolders:N0} folder(s) in Downloads contain something changed in the last {days} days, so they are not offered.");
        }

        AddWarnings(result);
    }

    internal static void AddWarnings(CategoryResultBuilder result)
    {
        for (var i = 0; i < result.Items.Count; i++)
        {
            if (result.Items[i].Kind == ItemKind.File && FileWarnings.For(result.Items[i].Path) is { } warning)
            {
                result.Items[i] = result.Items[i] with { Warning = CategoryResultBuilder.Combine(result.Items[i].Warning, warning) };
            }
        }
    }
}

internal sealed class LargeFilesScanner() : ScannerBase(CategoryId.LargeFiles)
{
    public override bool IsSlow => true;

    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
        var threshold = context.Settings.LargeFileThresholdBytes;
        foreach (var root in UserFileRoots.Get(context))
        {
            result.DeclareRoot(root);
            foreach (var entry in context.Walker.EnumerateFiles(root, result, UserFileRoots.DescendFilter(context)))
            {
                if (entry.Length < threshold || !FileScan.ProbeIsMovable(result, entry.FullPath, entry.Length, isDirectory: false))
                {
                    continue;
                }

                var folder = Path.GetDirectoryName(entry.FullPath) ?? root;
                result.TryAdd(FileScan.ToItem(entry, $"{Format.Bytes(entry.Length)}, over your {Format.Bytes(threshold)} threshold", folder, folder));
            }
        }

        UserFileRoots.AddRootsNote(context, result);
        OldDownloadsScanner.AddWarnings(result);
    }
}

/// <summary>Where Large files and Duplicate files look, and which folders they never enter.</summary>
internal static class UserFileRoots
{
    /// <summary>
    /// Your settings' list if you set one; otherwise your user folder plus wherever Documents, Desktop,
    /// Pictures, Videos, Music, Downloads and cloud folders really are (they are often moved to another drive).
    /// Roots inside another root are dropped so nothing is walked twice.
    /// </summary>
    public static List<string> Get(ScanContext context)
    {
        var p = context.Paths;
        var configured = context.Settings.UserFileScanRoots;
        IEnumerable<string> candidates = configured.Count > 0
            ? configured.Select(r => PathUtil.Normalize(Environment.ExpandEnvironmentVariables(r)))
            : [p.UserProfile, .. p.UserContentFolders, p.Downloads, .. p.CloudSyncFolders];

        var existing = candidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return existing
            .Where(root => !existing.Any(other => !ReferenceEquals(other, root)
                && !string.Equals(other, root, StringComparison.OrdinalIgnoreCase)
                && PathUtil.IsSameOrUnder(root, other)))
            .ToList();
    }

    /// <summary>
    /// Never enters: AppData; hidden folders and folders whose name starts with "." (.conda, .vscode, .local,
    /// .gradle ... are program data just like AppData, and deleting a "duplicate" or "large file" there breaks
    /// the tool); Windows, Program Files, ProgramData and KvaliTool's own folders; the extra names given.
    /// </summary>
    public static Func<string, FileAttributes, bool> DescendFilter(ScanContext context, IReadOnlySet<string>? ignoredNames = null)
    {
        var appData = Path.Combine(context.Paths.UserProfile, "AppData");
        return (directory, attributes) =>
        {
            var name = Path.GetFileName(directory);
            return !attributes.HasFlag(FileAttributes.Hidden)
                && !name.StartsWith('.')
                && !PathUtil.IsSameOrUnder(directory, appData)
                && !context.Policy.IsOffLimitsForUserFileWalk(directory)
                && (ignoredNames is null || !ignoredNames.Contains(name));
        };
    }

    public static void AddRootsNote(ScanContext context, CategoryResultBuilder result) =>
        result.Note($"Searched: {string.Join("; ", Get(context))}. Hidden folders, folders starting with \".\" and AppData are skipped (program data).");
}
