namespace KvaliTool.Core.Configuration;

/// <summary>
/// The ONLY code in KvaliTool allowed to replace an existing file, and it only ever replaces KvaliTool's own
/// files (settings.json, hashcache.bin). This file is the single, named exception in the Phase 2 code audit.
/// </summary>
public static class AppDataFile
{
    /// <summary>
    /// Writes <paramref name="contents"/> to <paramref name="path"/> so that readers see either the old file or the
    /// complete new one, never a half-written file:
    /// <list type="number">
    /// <item>Refuses (throws <see cref="IOException"/>) if <paramref name="path"/> exists and is a folder or a
    /// reparse point (symlink/junction): replacing a link could write somewhere else.</item>
    /// <item>Writes a new temp file next to it, named "&lt;file name&gt;.&lt;random&gt;.tmp", with FileMode.CreateNew,
    /// and flushes it to disk.</item>
    /// <item>Moves the temp file over the target (same folder, so the replace is atomic on NTFS).</item>
    /// <item>If any step fails, removes only that temp file, then rethrows. Never touches any other file.</item>
    /// </list>
    /// Creates the parent folder if needed.
    /// </summary>
    public static void WriteAtomic(string path, ReadOnlySpan<byte> contents) => throw new NotImplementedException();
}
