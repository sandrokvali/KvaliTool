using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static KvaliTool.Core.Platform.NativeMethods;

namespace KvaliTool.Core.Platform;

public enum ProbeResult
{
    /// <summary>Nothing has the file open in a way that would stop it being moved.</summary>
    Movable,

    /// <summary>Another program has it open (sharing violation).</summary>
    Locked,

    /// <summary>You don't have permission (needs admin, or belongs to another account).</summary>
    AccessDenied,

    /// <summary>It disappeared since it was listed.</summary>
    Missing,

    Error,
}

/// <summary>Read-only questions about files that .NET can't answer directly.</summary>
public static class FileSystemInfoNative
{
    /// <summary>
    /// Asks Windows whether the file could be moved right now, without moving it.
    /// It opens a handle that requests the DELETE right (required for a move) while sharing read, write and
    /// delete with everyone else, then closes it. Opening the handle changes nothing on disk; the flag that
    /// would delete on close is never passed. If another program has the file open without allowing deletion,
    /// Windows answers "sharing violation" and we report it as locked.
    /// FILE_FLAG_OPEN_REPARSE_POINT makes sure a symlink is never followed to its target.
    /// </summary>
    public static ProbeResult ProbeMovable(string path, bool isDirectory = false)
    {
        var flags = FILE_FLAG_OPEN_REPARSE_POINT | (isDirectory ? FILE_FLAG_BACKUP_SEMANTICS : 0);
        using var handle = CreateFile(path, DELETE, FILE_SHARE_READ_WRITE_DELETE, 0, OPEN_EXISTING, flags, 0);
        if (!handle.IsInvalid)
        {
            return ProbeResult.Movable;
        }

        return Marshal.GetLastPInvokeError() switch
        {
            ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND => ProbeResult.Missing,
            ERROR_ACCESS_DENIED => ProbeResult.AccessDenied,
            ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION => ProbeResult.Locked,
            _ => ProbeResult.Error,
        };
    }

    /// <summary>Returns the reparse tag of a file or folder (without following it), or null if it can't be read.</summary>
    public static uint? GetReparseTag(string path)
    {
        using var handle = CreateFile(
            path, FILE_READ_ATTRIBUTES, FILE_SHARE_READ_WRITE_DELETE, 0, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        return GetFileInformationByHandleEx(handle, FileAttributeTagInfoClass, out FileAttributeTagInfo info, (uint)Marshal.SizeOf<FileAttributeTagInfo>())
            ? info.ReparseTag
            : null;
    }

    /// <summary>
    /// OneDrive and other sync clients mark their folders and hydrated files with "cloud files" reparse tags
    /// (IO_REPARSE_TAG_CLOUD_*). They are real folders and files in place, not links to somewhere else.
    /// </summary>
    public static bool IsCloudFilesTag(uint tag) => (tag & 0xFFFF0FFF) == 0x9000001A;

    /// <summary>Volume + file index: identical for every hard link to the same file.</summary>
    public static bool TryGetFileIdentity(SafeFileHandle handle, out string identity, out uint linkCount)
    {
        if (!GetFileInformationByHandle(handle, out var info))
        {
            identity = string.Empty;
            linkCount = 0;
            return false;
        }

        identity = $"{info.VolumeSerialNumber:X8}-{info.FileIndexHigh:X8}{info.FileIndexLow:X8}";
        linkCount = info.NumberOfLinks;
        return true;
    }

    /// <summary>
    /// Last-write time and NTFS change time of an open file. The change time moves whenever the file's data or
    /// metadata is written, even if a tool sets the last-write time back afterwards, so together they tell
    /// "same content as last time" far more reliably than the modified time alone. Read-only query.
    /// </summary>
    public static bool TryGetFileTimes(SafeFileHandle handle, out DateTime lastWriteUtc, out DateTime changeTimeUtc)
    {
        lastWriteUtc = default;
        changeTimeUtc = default;
        if (!GetFileInformationByHandleEx(handle, FileBasicInfoClass, out FileBasicInfo info, (uint)Marshal.SizeOf<FileBasicInfo>())
            || info.LastWriteTime <= 0
            || info.ChangeTime <= 0)
        {
            return false;
        }

        lastWriteUtc = DateTime.FromFileTimeUtc(info.LastWriteTime);
        changeTimeUtc = DateTime.FromFileTimeUtc(info.ChangeTime);
        return true;
    }
}
