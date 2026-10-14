using System.Security.Cryptography;
using System.Text;

namespace KvaliTool.Core.Configuration;

/// <summary>
/// Identifies one version of one file. A cached hash is reused only if every field matches exactly.
/// <see cref="ChangeTimeUtc"/> (NTFS change time) is included on top of the modified time because tools can
/// rewrite a file and then set its modified time back; they can't set the change time back.
/// </summary>
/// <param name="Path">Full, normalized path.</param>
/// <param name="FileId">Volume serial + file index, as returned by <c>FileSystemInfoNative.TryGetFileIdentity</c>.</param>
public readonly record struct HashCacheKey(string Path, long Size, DateTime LastWriteUtc, DateTime ChangeTimeUtc, string FileId);

/// <summary>
/// Remembers full-content SHA-256 hashes between scans so the duplicate finder doesn't re-read unchanged files.
/// Only FULL-file hashes are cached; a cache hit replaces reading the file, never the full-hash comparison.
/// Implementations must be safe to call from one scanner thread at a time (the duplicate scanner is single-threaded).
/// </summary>
public interface IHashCache
{
    /// <returns>True and the upper-case hex SHA-256 when an entry with exactly this key exists.</returns>
    bool TryGet(in HashCacheKey key, out string sha256Hex);

    /// <summary>Stores or replaces the entry for <paramref name="key"/>'s path.</summary>
    void Set(in HashCacheKey key, string sha256Hex);

    /// <summary>Writes pending changes to disk (no-op for an in-memory cache).</summary>
    void Save();
}

/// <summary>
/// The on-disk cache in %LOCALAPPDATA%\KvaliTool. A missing, unreadable, corrupt or wrong-version file is
/// ignored (the cache starts empty) and replaced on the next <see cref="Save"/>: unlike settings, losing the cache
/// only costs time. Saving goes through <see cref="AppDataFile.WriteAtomic"/>. Entries not seen for
/// <see cref="MaxEntryAgeDays"/> days are dropped on save, and the file holds at most <see cref="MaxEntries"/> entries.
///
/// File layout (little endian): "KVHC", format version (int), entry count (int), the entries, then a SHA-256 of
/// everything before it. Each entry: path (length-prefixed UTF-8), size, last-write ticks, change-time ticks,
/// file id (length-prefixed UTF-8), the 32 raw hash bytes, and the day number the entry was last seen.
/// </summary>
public sealed class FileHashCache : IHashCache
{
    public const int MaxEntries = 500_000;

    public const int MaxEntryAgeDays = 90;

    private const int FormatVersion = 1;
    private const int HashBytes = 32;

    // Smallest possible entry: 1 (empty path prefix) + 3 * 8 + 1 (empty id prefix) + 32 + 4.
    private const int MinEntryBytes = 1 + 24 + 1 + HashBytes + 4;

    private static readonly byte[] Magic = "KVHC"u8.ToArray();

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _clock;
    private readonly int _maxEntries;
    private bool _dirty;

    private FileHashCache(string path, TimeProvider clock, int maxEntries)
    {
        Path = path;
        _clock = clock;
        _maxEntries = maxEntries;
    }

    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KvaliTool", "hashcache.bin");

    public string Path { get; }

    public int Count => _entries.Count;

    /// <summary>Loads the cache at <paramref name="path"/>, or starts empty (see class remarks). Never throws for a bad file.</summary>
    public static FileHashCache Load(string path) => Load(path, TimeProvider.System);

    /// <summary>Same as <see cref="Load(string)"/> with a clock and entry cap, so tests can age and fill the cache without waiting or 500,000 entries.</summary>
    internal static FileHashCache Load(string path, TimeProvider clock, int maxEntries = MaxEntries)
    {
        var cache = new FileHashCache(path, clock, maxEntries);
        if (!File.Exists(path))
        {
            return cache;
        }

        if (!cache.TryRead(path))
        {
            cache._entries.Clear();

            // Make the next Save replace the unusable file even if nothing else changes.
            cache._dirty = true;
        }

        return cache;
    }

    public bool TryGet(in HashCacheKey key, out string sha256Hex)
    {
        if (_entries.TryGetValue(key.Path, out var entry)
            && entry.Size == key.Size
            && entry.LastWriteTicks == key.LastWriteUtc.Ticks
            && entry.ChangeTimeTicks == key.ChangeTimeUtc.Ticks
            && string.Equals(entry.FileId, key.FileId, StringComparison.Ordinal))
        {
            var today = Today();
            if (entry.LastSeenDay != today)
            {
                entry.LastSeenDay = today;
                _dirty = true;
            }

            sha256Hex = Convert.ToHexString(entry.Hash);
            return true;
        }

        sha256Hex = string.Empty;
        return false;
    }

    public void Set(in HashCacheKey key, string sha256Hex)
    {
        var hash = Convert.FromHexString(sha256Hex);
        if (hash.Length != HashBytes)
        {
            throw new ArgumentException("A SHA-256 hash is 64 hex characters.", nameof(sha256Hex));
        }

        _entries[key.Path] = new Entry(key.Size, key.LastWriteUtc.Ticks, key.ChangeTimeUtc.Ticks, key.FileId, hash, Today());
        _dirty = true;
    }

    public void Save()
    {
        if (!_dirty)
        {
            return;
        }

        Prune();

        using var body = new MemoryStream();
        using (var writer = new BinaryWriter(body, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(FormatVersion);
            writer.Write(_entries.Count);
            foreach (var (path, entry) in _entries)
            {
                writer.Write(path);
                writer.Write(entry.Size);
                writer.Write(entry.LastWriteTicks);
                writer.Write(entry.ChangeTimeTicks);
                writer.Write(entry.FileId);
                writer.Write(entry.Hash);
                writer.Write(entry.LastSeenDay);
            }
        }

        var length = (int)body.Length;
        var checksum = SHA256.HashData(body.GetBuffer().AsSpan(0, length));
        body.Write(checksum);

        AppDataFile.WriteAtomic(Path, body.GetBuffer().AsSpan(0, (int)body.Length));
        _dirty = false;
    }

    private int Today() => (int)(_clock.GetUtcNow().UtcDateTime.Ticks / TimeSpan.TicksPerDay);

    /// <summary>Drops entries not seen for <see cref="MaxEntryAgeDays"/> days, then the least recently seen beyond <see cref="MaxEntries"/>.</summary>
    private void Prune()
    {
        var oldest = Today() - MaxEntryAgeDays;
        foreach (var path in _entries.Where(kv => kv.Value.LastSeenDay < oldest).Select(kv => kv.Key).ToList())
        {
            _entries.Remove(path);
        }

        if (_entries.Count > _maxEntries)
        {
            var drop = _entries.OrderBy(kv => kv.Value.LastSeenDay).Take(_entries.Count - _maxEntries).Select(kv => kv.Key).ToList();
            foreach (var path in drop)
            {
                _entries.Remove(path);
            }
        }
    }

    /// <returns>False when the file is missing/unreadable/corrupt/another version; the caller then starts empty.</returns>
    private bool TryRead(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < Magic.Length + 8 + SHA256.HashSizeInBytes)
            {
                return false;
            }

            var bodyLength = bytes.Length - SHA256.HashSizeInBytes;
            if (!SHA256.HashData(bytes.AsSpan(0, bodyLength)).AsSpan().SequenceEqual(bytes.AsSpan(bodyLength)))
            {
                return false;
            }

            using var stream = new MemoryStream(bytes, 0, bodyLength, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || reader.ReadInt32() != FormatVersion)
            {
                return false;
            }

            var count = reader.ReadInt32();
            if (count < 0 || (long)count * MinEntryBytes > stream.Length - stream.Position)
            {
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                var entryPath = reader.ReadString();
                var size = reader.ReadInt64();
                var lastWrite = reader.ReadInt64();
                var changeTime = reader.ReadInt64();
                var fileId = reader.ReadString();
                var hash = reader.ReadBytes(HashBytes);
                var lastSeen = reader.ReadInt32();
                if (entryPath.Length == 0 || hash.Length != HashBytes)
                {
                    return false;
                }

                _entries[entryPath] = new Entry(size, lastWrite, changeTime, fileId, hash, lastSeen);
            }

            // Anything left over means the layout isn't what we think it is.
            return stream.Position == stream.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or FormatException or OverflowException or NotSupportedException)
        {
            return false;
        }
    }

    private sealed class Entry(long size, long lastWriteTicks, long changeTimeTicks, string fileId, byte[] hash, int lastSeenDay)
    {
        public long Size { get; } = size;

        public long LastWriteTicks { get; } = lastWriteTicks;

        public long ChangeTimeTicks { get; } = changeTimeTicks;

        public string FileId { get; } = fileId;

        public byte[] Hash { get; } = hash;

        public int LastSeenDay { get; set; } = lastSeenDay;
    }
}
