using System.Text;
using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;

namespace KvaliTool.Core.Scanning.Scanners;

/// <summary>
/// Lists your own Recycle Bin on every fixed drive (X:\$Recycle.Bin\&lt;your SID&gt;). Each deleted item is a pair:
/// $Rxxxxxx holds the data, $Ixxxxxx records the original path and deletion time. Other users' bins are never read.
/// </summary>
internal sealed class RecycleBinScanner() : ScannerBase(CategoryId.RecycleBin)
{
    private static readonly EnumerationOptions TopLevelOnly = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = 0,
    };

    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
        var anyBin = false;
        foreach (var drive in context.Paths.FixedDriveRoots)
        {
            var bin = Path.Combine(drive, "$Recycle.Bin", context.Paths.CurrentUserSid);
            if (!Directory.Exists(bin))
            {
                continue;
            }

            anyBin = true;
            result.DeclareRoot(bin);
            ScanBin(context, result, bin, drive);
        }

        if (!anyBin)
        {
            result.Status = CategoryStatus.NotFound;
            return;
        }

        if (context.CanQueryRealSystem && SystemInfo.QueryRecycleBin() is { } windows)
        {
            result.Note($"For comparison, Windows reports {windows.Items:N0} item(s), {Format.Bytes(windows.Bytes)} in your Recycle Bins.");
        }
    }

    private static void ScanBin(ScanContext context, CategoryResultBuilder result, string bin, string drive)
    {
        string[] infoFiles;
        string[] dataEntries;
        try
        {
            infoFiles = Directory.GetFiles(bin, "$I*", TopLevelOnly);
            dataEntries = Directory.GetFileSystemEntries(bin, "$R*", TopLevelOnly);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            result.Skip(bin, SkipReason.AccessDenied, $"Couldn't read this Recycle Bin: {ex.Message}");
            return;
        }

        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var infoFile in infoFiles)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var suffix = Path.GetFileName(infoFile)[2..];
            var dataPath = Path.Combine(bin, "$R" + suffix);
            matched.Add(dataPath);

            if (!TryReadInfo(infoFile, out var originalPath, out var deletedUtc))
            {
                result.Skip(infoFile, SkipReason.Unreadable, "Recycle Bin record couldn't be read");
                continue;
            }

            AddEntry(context, result, bin, drive, dataPath, originalPath, deletedUtc);
        }

        foreach (var orphan in dataEntries.Where(e => !matched.Contains(e)))
        {
            result.Skip(orphan, SkipReason.OrphanedEntry, "Recycle Bin data without its record; Explorer doesn't show it either, so it is left alone");
        }
    }

    private static void AddEntry(ScanContext context, CategoryResultBuilder result, string bin, string drive, string dataPath, string originalPath, DateTime deletedUtc)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(dataPath);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return; // Record without data: Windows cleans these up itself.
        }

        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            result.Skip(dataPath, SkipReason.LinkNotFollowed, "Deleted link: not followed");
            return;
        }

        var isDirectory = attributes.HasFlag(FileAttributes.Directory);
        long size;
        long files;
        if (isDirectory)
        {
            var totals = FileScan.Aggregate(context, result, dataPath);
            (size, files) = (totals.Bytes, totals.Files);
        }
        else
        {
            context.Progress.FileExamined(dataPath);
            size = new FileInfo(dataPath).Length;
            files = 1;
        }

        if (!FileScan.ProbeIsMovable(result, dataPath, size, isDirectory))
        {
            return;
        }

        result.TryAdd(new ScanItem
        {
            Path = dataPath,
            Name = Path.GetFileName(originalPath),
            Kind = ItemKind.RecycleBinEntry,
            SizeBytes = size,
            FileCount = files,
            LastModifiedUtc = deletedUtc,
            OriginalPath = originalPath,
            Reason = $"Deleted on {deletedUtc.ToLocalTime():g} from {Path.GetDirectoryName(originalPath)}",
            Group = $"Recycle Bin on {drive.TrimEnd('\\')}",
            GroupRoot = bin,
        });
    }

    /// <summary>
    /// $I file layout. Version 1 (Vista-8.1): 8-byte version, 8-byte size, 8-byte FILETIME, 520-byte path.
    /// Version 2 (Windows 10+): same first 24 bytes, then a 4-byte character count and the path.
    /// </summary>
    internal static bool TryReadInfo(string infoFile, out string originalPath, out DateTime deletedUtc)
    {
        originalPath = string.Empty;
        deletedUtc = default;
        byte[] data;
        try
        {
            data = File.ReadAllBytes(infoFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return TryParseInfo(data, out originalPath, out deletedUtc);
    }

    internal static bool TryParseInfo(ReadOnlySpan<byte> data, out string originalPath, out DateTime deletedUtc)
    {
        originalPath = string.Empty;
        deletedUtc = default;
        if (data.Length < 24)
        {
            return false;
        }

        var version = BitConverter.ToInt64(data[..8]);
        var fileTime = BitConverter.ToInt64(data[16..24]);
        ReadOnlySpan<byte> name;
        switch (version)
        {
            case 1 when data.Length >= 24 + 520:
                name = data.Slice(24, 520);
                break;
            case 2 when data.Length >= 28:
                var chars = BitConverter.ToInt32(data[24..28]);
                if (chars <= 0 || data.Length < 28 + (chars * 2))
                {
                    return false;
                }

                name = data.Slice(28, chars * 2);
                break;
            default:
                return false;
        }

        var text = Encoding.Unicode.GetString(name);
        var nul = text.IndexOf('\0');
        originalPath = nul >= 0 ? text[..nul] : text;
        try
        {
            deletedUtc = DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return originalPath.Length > 0;
    }
}
