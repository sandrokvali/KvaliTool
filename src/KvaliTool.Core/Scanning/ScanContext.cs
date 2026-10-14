using System.Collections.Concurrent;
using KvaliTool.Core.Configuration;
using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;
using KvaliTool.Core.Safety;

namespace KvaliTool.Core.Scanning;

/// <summary>Everything a scanner needs. One instance per scan, shared by all category scanners.</summary>
public sealed class ScanContext
{
    public required ScanSettings Settings { get; init; }

    public required IKnownPaths Paths { get; init; }

    public required ProtectedPathPolicy Policy { get; init; }

    public required ExclusionList Exclusions { get; init; }

    public required IProcessDetector Processes { get; init; }

    public required bool IsElevated { get; init; }

    public required DateTime NowUtc { get; init; }

    public required ProgressTracker Progress { get; init; }

    public required CancellationToken CancellationToken { get; init; }

    /// <summary>Remembered full-file hashes for the duplicate finder; null = none. The engine saves it after a completed scan.</summary>
    public IHashCache? HashCache { get; init; }

    /// <summary>Sandbox folders belong to you, so "admin-only" categories can be exercised there without elevation.</summary>
    public bool HasAdminAccess => IsElevated || Paths.IsSandbox;

    /// <summary>DISM, docker, PowerShell and registry checks look at the real machine, so they are off in sandbox mode.</summary>
    public bool CanQueryRealSystem => !Paths.IsSandbox;

    public SafeFileWalker Walker => new(this);

    /// <summary>
    /// Age measured from the NEWER of creation and last-write time. Copying or extracting a file keeps its old
    /// modified date, so modified time alone would make a file created a minute ago look years old.
    /// </summary>
    public TimeSpan AgeOf(in WalkEntry entry) =>
        NowUtc - (entry.CreationUtc > entry.LastWriteUtc ? entry.CreationUtc : entry.LastWriteUtc);
}

/// <summary>Lock-free counters updated by scanners; the engine samples them a few times per second.</summary>
public sealed class ProgressTracker(int categoriesTotal)
{
    private readonly ConcurrentDictionary<CategoryId, string> _active = new();
    private long _filesExamined;
    private long _bytesFound;
    private int _categoriesCompleted;
    private volatile string? _currentPath;

    public void FileExamined(string path)
    {
        Interlocked.Increment(ref _filesExamined);
        _currentPath = path;
    }

    public void BytesFound(long bytes) => Interlocked.Add(ref _bytesFound, bytes);

    public void Begin(CategoryDefinition category) => _active[category.Id] = category.DisplayName;

    public void End(CategoryDefinition category)
    {
        _active.TryRemove(category.Id, out _);
        Interlocked.Increment(ref _categoriesCompleted);
    }

    public ScanProgress Snapshot() => new(
        Volatile.Read(ref _categoriesCompleted),
        categoriesTotal,
        [.. _active.Values],
        Interlocked.Read(ref _filesExamined),
        Interlocked.Read(ref _bytesFound),
        _currentPath);
}
