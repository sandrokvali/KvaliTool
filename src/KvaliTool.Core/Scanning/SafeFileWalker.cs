using System.IO.Enumeration;
using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;

namespace KvaliTool.Core.Scanning;

public readonly record struct WalkEntry(
    string FullPath,
    string Name,
    long Length,
    DateTime LastWriteUtc,
    DateTime CreationUtc,
    FileAttributes Attributes);

/// <summary>
/// Enumerates files under a folder, one directory at a time, with these guarantees:
///  - Junctions and symbolic links (folders or files) are never followed or listed. They are reported as
///    skipped, so a link inside %TEMP% that points at Documents can't drag Documents into the results.
///    The only reparse points entered are OneDrive-style "cloud files" folders, which are real folders in place.
///  - Cloud-only placeholders are skipped: they take no disk space and reading one would download it.
///  - Folders and files on your exclusion list are skipped (and not entered at all).
///  - A folder that can't be read is reported, not silently ignored.
/// It only reads directory listings; it never opens file contents.
/// </summary>
public sealed class SafeFileWalker(ScanContext context)
{
    private const FileAttributes RecallOnOpen = (FileAttributes)0x0004_0000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x0040_0000;

    private static readonly EnumerationOptions OneLevel = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    /// <param name="descendInto">Optional filter (folder path, folder attributes): return false to not enter a sub-folder.</param>
    public IEnumerable<WalkEntry> EnumerateFiles(string root, CategoryResultBuilder sink, Func<string, FileAttributes, bool>? descendInto = null)
    {
        FileAttributes rootAttributes;
        try
        {
            rootAttributes = File.GetAttributes(root);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            yield break;
        }
        catch (UnauthorizedAccessException)
        {
            sink.Skip(root, SkipReason.AccessDenied, "Couldn't open this folder (access denied)");
            yield break;
        }

        if (!rootAttributes.HasFlag(FileAttributes.Directory))
        {
            yield break;
        }

        if (rootAttributes.HasFlag(FileAttributes.ReparsePoint) && !IsCloudReparsePoint(root))
        {
            sink.Skip(root, SkipReason.LinkNotFollowed, "This folder is a junction or symbolic link, so it was not followed");
            yield break;
        }

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var entries = ReadDirectory(directory, sink);
            if (entries is null)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (entry.IsDirectory)
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && !IsCloudReparsePoint(entry.FullPath))
                    {
                        sink.Skip(entry.FullPath, SkipReason.LinkNotFollowed, "Junction or symbolic link: not followed");
                        continue;
                    }

                    if (context.Exclusions.IsExcluded(entry.FullPath))
                    {
                        sink.Skip(entry.FullPath, SkipReason.ExcludedByUser, "Folder is on your exclusion list");
                        continue;
                    }

                    if (descendInto is null || descendInto(entry.FullPath, entry.Attributes))
                    {
                        pending.Push(entry.FullPath);
                    }

                    continue;
                }

                context.Progress.FileExamined(entry.FullPath);

                if ((entry.Attributes & (FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
                {
                    sink.Skip(entry.FullPath, SkipReason.CloudOnlyPlaceholder, "Cloud-only file: it isn't stored on this PC, and reading it would download it");
                    continue;
                }

                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && !IsCloudReparsePoint(entry.FullPath))
                {
                    sink.Skip(entry.FullPath, SkipReason.LinkNotFollowed, "Symbolic link or other reparse point: not followed");
                    continue;
                }

                if (context.Exclusions.IsExcluded(entry.FullPath))
                {
                    sink.Skip(entry.FullPath, SkipReason.ExcludedByUser, "On your exclusion list", entry.Length);
                    continue;
                }

                yield return new WalkEntry(entry.FullPath, entry.Name, entry.Length, entry.LastWriteUtc, entry.CreationUtc, entry.Attributes);
            }
        }
    }

    private static List<RawEntry>? ReadDirectory(string directory, CategoryResultBuilder sink)
    {
        try
        {
            return new FileSystemEnumerable<RawEntry>(directory, Transform, OneLevel).ToList();
        }
        catch (UnauthorizedAccessException)
        {
            sink.Skip(directory, SkipReason.AccessDenied, "Couldn't open this folder (access denied)");
        }
        catch (DirectoryNotFoundException)
        {
            // Removed while we were scanning; nothing to report.
        }
        catch (IOException ex)
        {
            sink.Skip(directory, SkipReason.Unreadable, $"Couldn't read this folder: {ex.Message}");
        }

        return null;

        static RawEntry Transform(ref FileSystemEntry e) => new(
            e.ToFullPath(),
            e.FileName.ToString(),
            e.IsDirectory,
            e.Attributes,
            e.Length,
            e.LastWriteTimeUtc.UtcDateTime,
            e.CreationTimeUtc.UtcDateTime);
    }

    internal static bool IsCloudReparsePoint(string path) =>
        FileSystemInfoNative.GetReparseTag(path) is { } tag && FileSystemInfoNative.IsCloudFilesTag(tag);

    private readonly record struct RawEntry(
        string FullPath,
        string Name,
        bool IsDirectory,
        FileAttributes Attributes,
        long Length,
        DateTime LastWriteUtc,
        DateTime CreationUtc);
}
