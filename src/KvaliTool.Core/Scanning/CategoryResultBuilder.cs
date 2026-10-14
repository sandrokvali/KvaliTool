using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;

namespace KvaliTool.Core.Scanning;

/// <summary>
/// Collects one category's results. <see cref="TryAdd"/> is the gate every item passes through:
///  1. it must be inside a folder the scanner declared up front (<see cref="DeclareRoot"/>);
///  2. it must pass <see cref="Safety.ProtectedPathPolicy"/>;
///  3. it must not be on your exclusion list.
/// Items failing 1 or 2 are dropped and recorded as policy violations (a bug), failing 3 as skipped.
/// </summary>
public sealed class CategoryResultBuilder(ScanContext context, CategoryDefinition definition)
{
    private readonly List<string> _roots = [];

    public CategoryDefinition Definition { get; } = definition;

    public List<ScanItem> Items { get; } = [];

    public List<SkippedEntry> Skipped { get; } = [];

    public List<string> Notes { get; } = [];

    public List<PolicyViolation> Violations { get; } = [];

    public CategoryStatus Status { get; set; } = CategoryStatus.Scanned;

    public void DeclareRoot(string root) => _roots.Add(PathUtil.Normalize(root));

    public bool TryAdd(ScanItem item)
    {
        if (item.Kind != ItemKind.Virtual)
        {
            if (!_roots.Any(root => PathUtil.IsSameOrUnder(item.Path, root)))
            {
                AddViolation(item.Path, "Outside every folder this category is allowed to scan");
                return false;
            }

            var decision = context.Policy.Evaluate(item.Path, Definition);
            if (!decision.Allowed)
            {
                AddViolation(item.Path, decision.Rule!);
                return false;
            }

            if (context.Exclusions.IsExcluded(item.Path))
            {
                Skip(item.Path, SkipReason.ExcludedByUser, "On your exclusion list", item.SizeBytes);
                return false;
            }

            if (decision.InCloudSync)
            {
                item = item with
                {
                    InCloudSyncedFolder = true,
                    Warning = Combine(item.Warning, "In a cloud-synced folder: removing it here also removes it from the cloud and your other devices."),
                };
            }

            if (decision.InUserContent)
            {
                item = item with { InUserContentFolder = true };
            }
        }

        Items.Add(item);
        if (!item.ReportOnly)
        {
            context.Progress.BytesFound(item.SizeBytes ?? 0);
        }

        return true;
    }

    public void Skip(string path, SkipReason reason, string detail, long? sizeBytes = null) =>
        Skipped.Add(new SkippedEntry(path, reason, detail, sizeBytes));

    public void Note(string note) => Notes.Add(note);

    public void AddViolation(string path, string rule) => Violations.Add(new PolicyViolation(Definition.Id, path, rule));

    public CategoryResult Build(TimeSpan duration)
    {
        var status = Status;
        if (status == CategoryStatus.Scanned && Skipped.Any(s => s.Reason is SkipReason.AccessDenied or SkipReason.Unreadable))
        {
            status = CategoryStatus.Partial;
        }

        return new CategoryResult
        {
            Definition = Definition,
            Status = status,
            Items = Items,
            Skipped = Skipped,
            Notes = Notes,
            Duration = duration,
        };
    }

    public static string? Combine(string? first, string? second) =>
        string.IsNullOrEmpty(first) ? second : string.IsNullOrEmpty(second) ? first : first + " " + second;
}
