using System.Diagnostics;
using KvaliTool.Core.Configuration;
using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;
using KvaliTool.Core.Safety;
using KvaliTool.Core.Serialization;

namespace KvaliTool.Core.Scanning;

public sealed class ScanRequest
{
    public required ScanSettings Settings { get; init; }

    public required IKnownPaths Paths { get; init; }

    public required IProcessDetector Processes { get; init; }

    /// <summary>Null scans every category.</summary>
    public IReadOnlySet<CategoryId>? Categories { get; init; }

    /// <summary>
    /// Full-file hashes remembered between scans (used only when <see cref="ScanSettings.FindDuplicates"/> is on).
    /// Null = no cache. The engine calls <see cref="IHashCache.Save"/> after a completed scan, not after a cancelled one.
    /// </summary>
    public IHashCache? HashCache { get; init; }
}

/// <summary>
/// Runs the category scanners in parallel and merges their results. Read-only by construction: this
/// assembly contains no code that deletes, moves or writes your files (the only file it writes is the
/// report, and only to a new file name).
/// </summary>
public sealed class ScanEngine(IReadOnlyList<ICategoryScanner> scanners)
{
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(150);

    public static ScanEngine CreateDefault() => new(CategoryCatalog.CreateScanners());

    /// <summary>
    /// Scans and returns a report. Throws <see cref="OperationCanceledException"/> when cancelled: a half-finished
    /// scan is thrown away rather than shown, because an incomplete list could be mistaken for a complete one.
    /// </summary>
    public async Task<ScanReport> ScanAsync(ScanRequest request, IProgress<ScanProgress>? progress, CancellationToken cancellationToken)
    {
        request.Settings.Validate();
        var started = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        var selected = scanners
            .Where(s => request.Categories is null || request.Categories.Contains(s.Definition.Id))
            .ToList();

        var tracker = new ProgressTracker(selected.Count);
        var context = new ScanContext
        {
            Settings = request.Settings,
            Paths = request.Paths,
            Policy = new ProtectedPathPolicy(request.Paths),
            Exclusions = new ExclusionList(request.Settings.Exclusions),
            Processes = request.Processes,
            IsElevated = SystemInfo.IsElevated(),
            NowUtc = started,
            Progress = tracker,
            CancellationToken = cancellationToken,
        };

        var results = new Dictionary<CategoryId, CategoryResultBuilder>();
        var durations = new Dictionary<CategoryId, TimeSpan>();
        foreach (var scanner in selected)
        {
            results[scanner.Definition.Id] = new CategoryResultBuilder(context, scanner.Definition);
        }

        using var pumpStop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var pump = PumpProgressAsync(tracker, progress, pumpStop.Token);

        try
        {
            var options = new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken };
            await Parallel.ForEachAsync(selected.OrderByDescending(s => s.IsSlow), options, async (scanner, token) =>
            {
                var builder = results[scanner.Definition.Id];
                var timer = Stopwatch.StartNew();
                tracker.Begin(scanner.Definition);
                try
                {
                    await RunOneAsync(scanner, context, builder, token).ConfigureAwait(false);
                }
                finally
                {
                    tracker.End(scanner.Definition);
                    lock (durations)
                    {
                        durations[scanner.Definition.Id] = timer.Elapsed;
                    }
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await pumpStop.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(false);
        }

        progress?.Report(tracker.Snapshot());

        var ordered = CategoryCatalog.All
            .Where(d => results.ContainsKey(d.Id))
            .Select(d => results[d.Id])
            .ToList();

        ResolveOverlaps(ordered);

        return new ScanReport
        {
            StartedUtc = started,
            Duration = stopwatch.Elapsed,
            IsElevated = context.IsElevated,
            SandboxRoot = request.Paths is SandboxKnownPaths sandbox ? sandbox.Root : null,
            Settings = ScanReportJson.Clone(request.Settings),
            Categories = ordered.Select(b => b.Build(durations.GetValueOrDefault(b.Definition.Id))).ToList(),
            PolicyViolations = ordered.SelectMany(b => b.Violations).ToList(),
        };
    }

    private static async Task RunOneAsync(ICategoryScanner scanner, ScanContext context, CategoryResultBuilder builder, CancellationToken token)
    {
        if (scanner.Definition.RequiresElevation && !context.HasAdminAccess)
        {
            builder.Status = CategoryStatus.NeedsAdmin;
            builder.Note("Needs administrator rights to scan. KvaliTool asks for elevation only if you select this category.");
            return;
        }

        try
        {
            // Scanners do blocking file I/O; keep them off the caller's (UI) thread.
            await Task.Run(() => scanner.ScanAsync(context, builder), token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            builder.Status = CategoryStatus.Failed;
            builder.Note($"The scan of this category failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Every path is offered once. A file matching several categories is listed under the first in catalog
    /// order. A file inside a folder row (an old Downloads folder, Windows.old) is not listed again elsewhere:
    /// the folder row already covers it, and selecting both would hide what is really being removed.
    /// </summary>
    private static void ResolveOverlaps(List<CategoryResultBuilder> ordered)
    {
        var folderRows = ordered
            .SelectMany(b => b.Items.Where(i => i.Kind is ItemKind.Folder or ItemKind.RecycleBinEntry).Select(i => (Builder: b, i.Path)))
            .ToList();

        var claimedBy = new Dictionary<string, CategoryDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var builder in ordered)
        {
            var kept = new List<ScanItem>(builder.Items.Count);
            foreach (var item in builder.Items)
            {
                if (item.Kind == ItemKind.Virtual)
                {
                    kept.Add(item);
                    continue;
                }

                if (claimedBy.TryGetValue(item.Path, out var owner))
                {
                    builder.Skip(item.Path, SkipReason.AlreadyListed, $"Already listed under \"{owner.DisplayName}\"", item.SizeBytes);
                    continue;
                }

                var container = folderRows.FirstOrDefault(f =>
                    !string.Equals(f.Path, item.Path, StringComparison.OrdinalIgnoreCase) && PathUtil.IsSameOrUnder(item.Path, f.Path));
                if (container.Builder is not null)
                {
                    builder.Skip(item.Path, SkipReason.AlreadyListed,
                        $"Inside \"{Path.GetFileName(container.Path)}\", which is listed as a whole under \"{container.Builder.Definition.DisplayName}\"", item.SizeBytes);
                    continue;
                }

                claimedBy[item.Path] = builder.Definition;
                kept.Add(item);
            }

            builder.Items.Clear();
            builder.Items.AddRange(kept);
        }
    }

    private static async Task PumpProgressAsync(ProgressTracker tracker, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        if (progress is null)
        {
            return;
        }

        using var timer = new PeriodicTimer(ProgressInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                progress.Report(tracker.Snapshot());
            }
        }
        catch (OperationCanceledException)
        {
            // Scan finished or was cancelled.
        }
    }
}
