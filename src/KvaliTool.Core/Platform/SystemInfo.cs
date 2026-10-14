using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32;

namespace KvaliTool.Core.Platform;

public static class SystemInfo
{
    /// <summary>True only when running with an elevated (administrator) token.</summary>
    public static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Windows Update has installed something and is waiting for a restart. Reads the registry; never writes.</summary>
    public static bool IsUpdateRestartPending()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
        return key is not null;
    }

    /// <summary>What Windows itself reports for all Recycle Bins of the current user. Null if unavailable.</summary>
    public static (long Items, long Bytes)? QueryRecycleBin()
    {
        if (!Environment.Is64BitProcess)
        {
            return null;
        }

        var info = new NativeMethods.ShQueryRbInfo { Size = (uint)Marshal.SizeOf<NativeMethods.ShQueryRbInfo>() };
        return NativeMethods.SHQueryRecycleBin(null, ref info) == 0 ? (info.NumItems, info.SizeBytes) : null;
    }

    public static string? GetKnownFolder(Guid folderId)
    {
        var hr = NativeMethods.SHGetKnownFolderPath(in folderId, 0, 0, out var pointer);
        try
        {
            return hr == 0 ? Marshal.PtrToStringUni(pointer) : null;
        }
        finally
        {
            if (pointer != 0)
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }
    }

    public static string? FindOnPath(string executable)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(directory), executable);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry; ignore it.
            }
        }

        return null;
    }
}

public static class KnownFolderIds
{
    public static readonly Guid Downloads = new("374DE290-123F-4565-9164-39C4925E467B");
    public static readonly Guid LocalAppDataLow = new("A520A1A4-1780-4FF6-BD18-167343C5AF16");
}

public interface IProcessDetector
{
    bool IsRunning(string processName);
}

public sealed class SystemProcessDetector : IProcessDetector
{
    public bool IsRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        foreach (var process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }
}

/// <summary>For sandbox runs and tests: pretends exactly these processes are running.</summary>
public sealed class FixedProcessDetector(IEnumerable<string> running) : IProcessDetector
{
    private readonly HashSet<string> _running = new(running, StringComparer.OrdinalIgnoreCase);

    public bool IsRunning(string processName) => _running.Contains(processName);
}
