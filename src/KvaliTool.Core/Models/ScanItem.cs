namespace KvaliTool.Core.Models;

/// <summary>One row the user can select: a file, a folder, a Recycle Bin entry or a virtual item.</summary>
public sealed record ScanItem
{
    /// <summary>Full path on disk, or a pseudo path such as "restorepoint:42" for <see cref="ItemKind.Virtual"/>.</summary>
    public required string Path { get; init; }

    public required string Name { get; init; }

    public required ItemKind Kind { get; init; }

    /// <summary>Bytes. Null means Windows can't tell us the size (restore points, component store).</summary>
    public long? SizeBytes { get; init; }

    /// <summary>Number of files this row stands for (1 for a file, the total for a folder).</summary>
    public long FileCount { get; init; } = 1;

    public DateTime? LastModifiedUtc { get; init; }

    /// <summary>Plain-language reason the item was flagged.</summary>
    public required string Reason { get; init; }

    /// <summary>Middle level of the results tree, e.g. "npm cache" or "Default\Cache".</summary>
    public required string Group { get; init; }

    /// <summary>The folder <see cref="Group"/> refers to, when there is one.</summary>
    public string? GroupRoot { get; init; }

    /// <summary>Extra caution shown next to the item, e.g. "virtual machine disk".</summary>
    public string? Warning { get; init; }

    /// <summary>Inside Documents, Desktop, Pictures, Videos or Music. Never pre-checked.</summary>
    public bool InUserContentFolder { get; init; }

    /// <summary>Inside OneDrive, Dropbox, iCloud Drive, Box or Google Drive. Removing it also removes it from the cloud.</summary>
    public bool InCloudSyncedFolder { get; init; }

    /// <summary>Shown for information only; KvaliTool will not clean it.</summary>
    public bool ReportOnly { get; init; }

    /// <summary>For Recycle Bin entries: where the item was before you deleted it.</summary>
    public string? OriginalPath { get; init; }

    public string? DuplicateGroupId { get; init; }

    /// <summary>For duplicates: the copy that is kept and is NOT listed.</summary>
    public string? DuplicateKeeperPath { get; init; }
}

/// <summary>Something the scanner saw but did not offer for cleaning, and why.</summary>
public sealed record SkippedEntry(string Path, SkipReason Reason, string Detail, long? SizeBytes);

/// <summary>
/// A scanner tried to list something the safety rules forbid. This should never happen;
/// if it does, it is a bug and the item was dropped.
/// </summary>
public sealed record PolicyViolation(CategoryId Category, string Path, string Rule);
