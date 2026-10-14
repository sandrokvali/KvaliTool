using System.Buffers;
using System.Security.Cryptography;
using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;
using Microsoft.Win32.SafeHandles;

namespace KvaliTool.Core.Scanning.Scanners;

/// <summary>
/// Duplicates: group by size, then by SHA-256 of the first 64 KB (a cheap pre-filter), then by full SHA-256.
/// Only a full-content SHA-256 match counts as a duplicate. Hard links to the same file are not duplicates
/// (removing one frees nothing). In each set the oldest copy is the keeper and is not listed.
/// Files are opened read-only and shared, so other programs are never blocked.
/// </summary>
internal sealed class DuplicateFilesScanner() : ScannerBase(CategoryId.DuplicateFiles)
{
    private const int PrefixBytes = 64 * 1024;

    public override bool IsSlow => true;

    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
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
        var seenIdentities = new HashSet<string>(StringComparer.Ordinal);
        var byPrefix = new Dictionary<string, List<WalkEntry>>(StringComparer.Ordinal);
        foreach (var entry in sameSize)
        {
            var prefix = TryHash(context, result, entry, PrefixBytes, seenIdentities);
            if (prefix is null)
            {
                continue;
            }

            if (!byPrefix.TryGetValue(prefix, out var list))
            {
                byPrefix[prefix] = list = [];
            }

            list.Add(entry);
        }

        foreach (var candidates in byPrefix.Values.Where(l => l.Count > 1))
        {
            var byFull = new Dictionary<string, List<(WalkEntry, string)>>(StringComparer.Ordinal);
            foreach (var entry in candidates)
            {
                // A file of 64 KB or less was hashed completely already, but hash again anyway: one code path.
                var full = TryHash(context, result, entry, null, null);
                if (full is null)
                {
                    continue;
                }

                if (!byFull.TryGetValue(full, out var list))
                {
                    byFull[full] = list = [];
                }

                list.Add((entry, full));
            }

            foreach (var set in byFull.Values.Where(l => l.Count > 1))
            {
                yield return set;
            }
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

    /// <returns>Hex SHA-256 of the first <paramref name="limit"/> bytes (or the whole file), or null if unreadable / a hard link already seen / changed while reading.</returns>
    private static string? TryHash(ScanContext context, CategoryResultBuilder result, in WalkEntry entry, int? limit, HashSet<string>? seenIdentities)
    {
        SafeFileHandle handle;
        try
        {
            handle = File.OpenHandle(entry.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            result.Skip(entry.FullPath, SkipReason.Unreadable, $"Couldn't read it to compare contents: {ex.Message}", entry.Length);
            return null;
        }

        using (handle)
        {
            if (seenIdentities is not null
                && FileSystemInfoNative.TryGetFileIdentity(handle, out var identity, out var links)
                && links > 1
                && !seenIdentities.Add(identity))
            {
                result.Skip(entry.FullPath, SkipReason.HardLinkOfSameFile, "Hard link to a file already listed: same data on disk, removing it would free nothing", entry.Length);
                return null;
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

                return Convert.ToHexString(sha.GetHashAndReset());
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
}
