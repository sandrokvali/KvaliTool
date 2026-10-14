using System.Security.Cryptography;

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
    public static void WriteAtomic(string path, ReadOnlySpan<byte> contents)
    {
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target)
            ?? throw new IOException($"'{path}' has no containing folder.");
        RefuseFolderOrLink(target);
        Directory.CreateDirectory(directory);

        var temp = Path.Combine(directory, $"{Path.GetFileName(target)}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(8))}.tmp");

        // Set only after CreateNew succeeds, so a failure to create can never lead to removing a file we didn't make.
        var created = false;
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, target, overwrite: true);
        }
        catch
        {
            if (created)
            {
                RemoveTemp(temp);
            }

            throw;
        }
    }

    private static void RefuseFolderOrLink(string target)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(target);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        if (attributes.HasFlag(FileAttributes.Directory))
        {
            throw new IOException($"'{target}' is a folder, not a file. Nothing was written.");
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new IOException($"'{target}' is a symbolic link or junction. KvaliTool won't write through links. Nothing was written.");
        }
    }

    /// <summary>Best effort: a leftover temp file is harmless, and the original error is the one worth reporting.</summary>
    private static void RemoveTemp(string temp)
    {
        try
        {
            File.Delete(temp);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing more we can safely do.
        }
    }
}
