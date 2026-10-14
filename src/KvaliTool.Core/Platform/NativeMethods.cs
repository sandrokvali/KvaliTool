using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KvaliTool.Core.Platform;

/// <summary>
/// The only Win32 functions the scanner uses. None of them modifies a file.
/// CreateFile is called with OPEN_EXISTING only (it never creates or truncates) and without
/// FILE_FLAG_DELETE_ON_CLOSE.
/// </summary>
internal static partial class NativeMethods
{
    /// <summary>The "DELETE" access right. Requesting it is how Windows tells us whether a file could be moved; it does not delete anything.</summary>
    internal const uint DELETE = 0x0001_0000;
    internal const uint FILE_READ_ATTRIBUTES = 0x0080;
    internal const uint FILE_SHARE_READ_WRITE_DELETE = 0x7;
    internal const uint OPEN_EXISTING = 3;
    internal const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x0020_0000;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x0200_0000;

    internal const int ERROR_FILE_NOT_FOUND = 2;
    internal const int ERROR_PATH_NOT_FOUND = 3;
    internal const int ERROR_ACCESS_DENIED = 5;
    internal const int ERROR_SHARING_VIOLATION = 32;
    internal const int ERROR_LOCK_VIOLATION = 33;

    internal const int FileBasicInfoClass = 0;
    internal const int FileAttributeTagInfoClass = 9;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetLongPathNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static unsafe partial uint GetLongPathName(string shortPath, char* buffer, uint bufferLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileAttributeTagInfo info, uint size);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, out FileBasicInfo info, uint size);

    [LibraryImport("shell32.dll")]
    internal static partial int SHGetKnownFolderPath(in Guid folderId, uint flags, nint token, out nint path);

    [LibraryImport("shell32.dll", EntryPoint = "SHQueryRecycleBinW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SHQueryRecycleBin(string? rootPath, ref ShQueryRbInfo info);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    /// <summary>FILE_BASIC_INFO: four 64-bit FILETIME values, then the attributes (40 bytes with padding).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileBasicInfo
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAttributeTagInfo
    {
        public uint FileAttributes;
        public uint ReparseTag;
    }

    /// <summary>64-bit layout (cbSize = 24). Only used from a 64-bit process.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct ShQueryRbInfo
    {
        public uint Size;
        public long SizeBytes;
        public long NumItems;
    }
}
