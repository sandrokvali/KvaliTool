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
/// </summary>
public sealed class FileHashCache : IHashCache
{
    public const int MaxEntries = 500_000;

    public const int MaxEntryAgeDays = 90;

    private FileHashCache()
    {
    }

    public static string DefaultPath { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KvaliTool", "hashcache.bin");

    public string Path => throw new NotImplementedException();

    public int Count => throw new NotImplementedException();

    /// <summary>Loads the cache at <paramref name="path"/>, or starts empty (see class remarks). Never throws for a bad file.</summary>
    public static FileHashCache Load(string path) => throw new NotImplementedException();

    public bool TryGet(in HashCacheKey key, out string sha256Hex) => throw new NotImplementedException();

    public void Set(in HashCacheKey key, string sha256Hex) => throw new NotImplementedException();

    public void Save() => throw new NotImplementedException();
}
