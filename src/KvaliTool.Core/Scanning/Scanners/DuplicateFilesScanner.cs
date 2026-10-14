using System.Buffers;
using System.Security.Cryptography;
using KvaliTool.Core.Configuration;
using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;
using Microsoft.Win32.SafeHandles;

namespace KvaliTool.Core.Scanning.Scanners;

/// <summary>
/// Duplicates: group by size, then by SHA-256 of the first 64 KB (a cheap pre-filter), then by full SHA-256.
/// Only a full-content SHA-256 match counts as a duplicate. Hard links to the same file are not duplicates
/// (removing one frees nothing). In each set the oldest copy is the keeper and is not listed.
/// Files are opened read-only and shared, so other programs are never blocked.
///
/// Off unless <see cref="Configuration.ScanSettings.FindDuplicates"/> is on: it reads every candidate file in full,
/// so when it's off nothing is walked or read and the category reports Skipped.
///
/// Full-file hashes can be remembered between scans (<see cref="ScanContext.HashCache"/>). An entry is used only
/// when the file's size, last-write time, NTFS change time and file id all match what was recorded. A file whose
/// full hash is remembered is not read at all (not even the 64 KB pre-filter).
/// </summary>
internal sealed class DuplicateFilesScanner() : ScannerBase(CategoryId.DuplicateFiles)
{
    private const int PrefixBytes = 64 * 1024;

    public override bool IsSlow => true;

    /// <summary>A file that passed the open/hard-link checks; <see cref="CachedFullHash"/> is set on a cache hit.</summary>
    private sealed record Candidate(WalkEntry Entry, string? CachedFullHash);

    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
        if (!context.Settings.FindDuplicates)
        {
            result.Status = CategoryStatus.Skipped;
            result.Note("Duplicate search is off. Turn on \"Find duplicates (slow)\" to include it.");
            return;
        }

        var ignored = new HashSet<string>(context.Settings.DuplicateIgnoredFolderNames, StringComparer.OrdinalIgnoreCase);
        var bySize = new Dictionary<long, List<WalkEntry>>();
        foreach (var root in UserFileRoots.Get(context))
        {
            result.DeclareRoot(root);
            foreach (var entry in context.Walker.EnumerateFiles(root, result, UserFileRoots.DescendFilter(context, ignored)))
            {
                if (entry.Length < context.Settings.DuplicateMinBytes)
                {
                    continue;
                }

                if (!bySize.TryGetValue(entry.Length, out var list))
                {
                    bySize[entry.Length] = list = [];
                }

                list.Add(entry);
            }
        }

        var setNumber = 0;
        foreach (var (size, sameSize) in bySize.Where(kv => kv.Value.Count > 1).OrderByDescending(kv => kv.Key))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            foreach (var set in FindIdenticalSets(context, result, size, sameSize))
            {
                setNumber++;
                EmitSet(result, set, setNumber);
            }
        }

        UserFileRoots.AddRootsNote(context, result);
        if (ignored.Count > 0)
        {
            result.Note($"Folders named {string.Join(", ", ignored.Order())} are not searched either: identical files inside them are normal and needed.");
        }
    }

    private static IEnumerable<List<(WalkEntry Entry, string Hash)>> FindIdenticalSets(
        ScanContext context, CategoryResultBuilder result, long size, List<WalkEntry> sameSize)
    {
        // Pass 1: open each file once (no content read) to drop unreadable files and hard links, and to look up
        // a remembered full hash.
        var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<Candidate>(sameSize.Count);
        foreach (var entry in sameSize)
        {
            if (Inspect(context, result, entry, seenIdentities) is { } candidate)
            {
                candidates.Add(candidate);
            }
        }

        if (candidates.Count < 2)
        {
            yield break;
        }

        // Pass 2: decide which files still need a full read.
        var fullHashes = new string?[candidates.Count];
        var anyRemembered = false;
        for (var i = 0; i < candidates.Count; i++)
        {
            fullHashes[i] = candidates[i].CachedFullHash;
            anyRemembered |= fullHashes[i] is not null;
        }

        var unknown = Enumerable.Range(0, candidates.Count).Where(i => fullHashes[i] is null).ToList();
        List<int> toRead;
        if (anyRemembered)
        {
            // A remembered file has no prefix hash to compare with, so every file without a remembered hash has to
            // be read in full to know whether it matches one. (When ALL files are remembered this list is empty.)
            toRead = unknown;
        }
        else
        {
            // Nothing remembered: the 64 KB pre-filter rules out most files cheaply.
            var byPrefix = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var i in unknown)
            {
                var prefix = ReadHash(context, result, candidates[i].Entry, PrefixBytes, remember: null);
                if (prefix is null)
                {
                    continue;
                }

                if (!byPrefix.TryGetValue(prefix, out var list))
                {
                    byPrefix[prefix] = list = [];
                }

                list.Add(i);
            }

            toRead = [.. byPrefix.Values.Where(l => l.Count > 1).SelectMany(l => l).Order()];
        }

        foreach (var i in toRead)
        {
            // A file of 64 KB or less was hashed completely already, but hash again anyway: one code path.
            fullHashes[i] = ReadHash(context, result, candidates[i].Entry, null, context.HashCache);
        }

        // Group in the order the files were found, so results don't depend on what the cache already knew.
        var byFull = new Dictionary<string, List<(WalkEntry, string)>>(StringComparer.Ordinal);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (fullHashes[i] is not { } full)
            {
                continue;
            }

            if (!byFull.TryGetValue(full, out var list))
            {
                byFull[full] = list = [];
            }

            list.Add((candidates[i].Entry, full));
        }

        foreach (var set in byFull.Values.Where(l => l.Count > 1))
        {
            yield return set;
        }
    }

    private static void EmitSet(CategoryResultBuilder result, List<(WalkEntry Entry, string Hash)> set, int setNumber)
    {
        var ordered = set
            .OrderBy(s => s.Entry.LastWriteUtc)
            .ThenBy(s => s.Entry.FullPath.Length)
            .ThenBy(s => s.Entry.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var keeper = ordered[0].Entry;
        var groupId = ordered[0].Hash[..16];
        var group = $"Set {setNumber}: {keeper.Name} ({set.Count} copies, {Format.Bytes(keeper.Length)} each)";

        foreach (var (entry, hash) in ordered.Skip(1))
        {
            if (!FileScan.ProbeIsMovable(result, entry.FullPath, entry.Length, isDirectory: false))
            {
                continue;
            }

            result.TryAdd(FileScan.ToItem(entry, $"Identical content (SHA-256 {hash[..12]}...) to the kept copy {keeper.FullPath}", group,
                Path.GetDirectoryName(keeper.FullPath) ?? keeper.FullPath, FileWarnings.For(entry.FullPath)) with
            {
                DuplicateGroupId = groupId,
                DuplicateKeeperPath = keeper.FullPath,
            });
        }
    }

    private static SafeFileHandle? TryOpen(CategoryResultBuilder result, in WalkEntry entry)
    {
        try
        {
            return File.OpenHandle(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Skip(entry.FullPath, SkipReason.Unreadable, $"Couldn't read it to compare contents: {ex.Message}", entry.Length);
            return null;
        }
    }

    /// <summary>
    /// Opens the file (reading no content) to skip hard links of a file already seen, and, when a hash cache is
    /// in use, to look up a remembered full hash for exactly this version of the file.
    /// </summary>
    /// <returns>Null if the file is unreadable or is a hard link to a file already seen.</returns>
    private static Candidate? Inspect(ScanContext context, CategoryResultBuilder result, in WalkEntry entry, HashSet<string> seenIdentities)
    {
        context.CancellationToken.ThrowIfCancellationRequested();
        using var handle = TryOpen(result, entry);
        if (handle is null)
        {
            return null;
        }

        var hasIdentity = FileSystemInfoNative.TryGetFileIdentity(handle, out var identity, out var links);
        if (hasIdentity && links > 1 && !seenIdentities.Add(identity))
        {
            result.Skip(entry.FullPath, SkipReason.HardLinkOfSameFile, "Hard link to a file already listed: same data on disk, removing it would free nothing", entry.Length);
            return null;
        }

        if (context.HashCache is { } cache
            && hasIdentity
            && TryBuildKey(handle, entry, identity, out var key)
            && cache.TryGet(key, out var remembered))
        {
            return new Candidate(entry, remembered);
        }

        return new Candidate(entry, null);
    }

    /// <summary>
    /// The cache key for the file as it is right now. Size, times and id all come from the open handle (not from the
    /// directory listing, which can be stale), and the size must match what the walker saw.
    /// </summary>
    private static bool TryBuildKey(SafeFileHandle handle, in WalkEntry entry, string identity, out HashCacheKey key)
    {
        key = default;
        if (RandomAccess.GetLength(handle) != entry.Length
            || !FileSystemInfoNative.TryGetFileTimes(handle, out var lastWriteUtc, out var changeTimeUtc))
        {
            return false;
        }

        key = new HashCacheKey(entry.FullPath, entry.Length, lastWriteUtc, changeTimeUtc, identity);
        return true;
    }

    /// <summary>
    /// Hex SHA-256 of the first <paramref name="limit"/> bytes (or the whole file when null).
    /// For a whole-file hash, <paramref name="remember"/> is told the result, but only if the file's size, times and
    /// id are the same before and after reading (so a file changing mid-read is never remembered).
    /// </summary>
    /// <returns>The hash, or null if the file is unreadable or changed while reading.</returns>
    private static string? ReadHash(ScanContext context, CategoryResultBuilder result, in WalkEntry entry, int? limit, IHashCache? remember)
    {
        using var handle = TryOpen(result, entry);
        if (handle is null)
        {
            return null;
        }

        HashCacheKey? key = null;
        if (remember is not null && limit is null
            && FileSystemInfoNative.TryGetFileIdentity(handle, out var identity, out _)
            && TryBuildKey(handle, entry, identity, out var built))
        {
            key = built;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            var end = limit is { } l ? Math.Min(l, entry.Length) : entry.Length;
            while (offset < end)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                var toRead = (int)Math.Min(buffer.Length, end - offset);
                var read = RandomAccess.Read(handle, buffer.AsSpan(0, toRead), offset);
                if (read == 0)
                {
                    break;
                }

                sha.AppendData(buffer, 0, read);
                offset += read;
            }

            if (offset != end || RandomAccess.GetLength(handle) != entry.Length)
            {
                result.Skip(entry.FullPath, SkipReason.Unreadable, "Changed while it was being compared", entry.Length);
                return null;
            }

            var hash = Convert.ToHexString(sha.GetHashAndReset());
            if (key is { } before
                && remember is not null
                && FileSystemInfoNative.TryGetFileTimes(handle, out var lastWriteAfter, out var changeTimeAfter)
                && lastWriteAfter == before.LastWriteUtc
                && changeTimeAfter == before.ChangeTimeUtc)
            {
                remember.Set(before, hash);
            }

            return hash;
        }
        catch (IOException ex)
        {
            result.Skip(entry.FullPath, SkipReason.Unreadable, $"Couldn't read it to compare contents: {ex.Message}", entry.Length);
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
