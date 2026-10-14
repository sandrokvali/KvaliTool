using KvaliTool.Core.Configuration;

namespace KvaliTool.Core.Models;

public sealed record CategoryResult
{
    public required CategoryDefinition Definition { get; init; }

    public required CategoryStatus Status { get; init; }

    public required IReadOnlyList<ScanItem> Items { get; init; }

    public required IReadOnlyList<SkippedEntry> Skipped { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }

    public required TimeSpan Duration { get; init; }

    /// <summary>Total of items KvaliTool could clean. Report-only and unknown-size items are not counted.</summary>
    public long TotalBytes => Items.Where(i => !i.ReportOnly).Sum(i => i.SizeBytes ?? 0);

    public bool HasUnknownSizes => Items.Any(i => i.SizeBytes is null && !i.ReportOnly);
}

public sealed record ScanReport
{
    /// <summary>Always "DryRun" in Phase 1: the engine has no code that changes anything.</summary>
    public string Mode { get; init; } = "DryRun";

    public string EngineVersion { get; init; } = "0.1.0-phase1";

    public required DateTime StartedUtc { get; init; }

    public required TimeSpan Duration { get; init; }

    public required bool IsElevated { get; init; }

    /// <summary>Set when every Windows location was mapped into a test folder.</summary>
    public string? SandboxRoot { get; init; }

    public required ScanSettings Settings { get; init; }

    public required IReadOnlyList<CategoryResult> Categories { get; init; }

    /// <summary>Should always be empty. Anything here is a scanner bug that the safety layer caught.</summary>
    public required IReadOnlyList<PolicyViolation> PolicyViolations { get; init; }

    public long TotalReclaimableBytes => Categories.Sum(c => c.TotalBytes);

    public long DefaultSelectedBytes => Categories.Where(c => c.Definition.CheckedByDefault).Sum(c => c.TotalBytes);
}

/// <summary>Snapshot sent to the UI a few times per second while scanning.</summary>
public sealed record ScanProgress(
    int CategoriesCompleted,
    int CategoriesTotal,
    IReadOnlyList<string> ActiveCategories,
    long FilesExamined,
    long BytesFound,
    string? CurrentPath);
