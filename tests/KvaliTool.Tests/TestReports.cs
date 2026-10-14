using KvaliTool.Core.Configuration;
using KvaliTool.Core.Models;
using KvaliTool.Core.Scanning;

namespace KvaliTool.Tests;

/// <summary>Builds scan reports in memory from hand-made items. Nothing here touches the disk.</summary>
internal static class TestReports
{
    public static ScanItem Item(
        string path,
        long? size = 1000,
        string group = "Group",
        string reason = "Test reason",
        bool reportOnly = false,
        bool personal = false,
        bool cloud = false,
        string? groupId = null,
        string? keeper = null,
        DateTime? modified = null,
        ItemKind kind = ItemKind.File) => new()
    {
        Path = path,
        Name = path[(path.LastIndexOf('\\') + 1)..],
        Kind = kind,
        SizeBytes = size,
        LastModifiedUtc = modified,
        Reason = reason,
        Group = group,
        GroupRoot = path[..Math.Max(0, path.LastIndexOf('\\'))],
        ReportOnly = reportOnly,
        InUserContentFolder = personal,
        InCloudSyncedFolder = cloud,
        DuplicateGroupId = groupId,
        DuplicateKeeperPath = keeper,
    };

    public static CategoryResult Category(CategoryId id, IEnumerable<ScanItem> items, CategoryStatus status = CategoryStatus.Scanned) => new()
    {
        Definition = CategoryCatalog.Get(id),
        Status = status,
        Items = [.. items],
        Skipped = [],
        Notes = [],
        Duration = TimeSpan.Zero,
    };

    public static CategoryResult Category(CategoryId id, params ScanItem[] items) => Category(id, (IEnumerable<ScanItem>)items);

    public static ScanReport Report(params CategoryResult[] categories) => new()
    {
        StartedUtc = DateTime.UtcNow,
        Duration = TimeSpan.Zero,
        IsElevated = false,
        Settings = new ScanSettings(),
        Categories = categories,
        PolicyViolations = [],
    };
}
