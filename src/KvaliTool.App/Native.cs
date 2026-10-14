using System.Runtime.InteropServices;

namespace KvaliTool.App;

/// <summary>
/// WinUI windows have no minimum size property. This subclasses the window to answer WM_GETMINMAXINFO, the
/// message Windows asks before it lets the user drag the window smaller. It changes nothing but that answer.
/// </summary>
internal static unsafe partial class MinimumWindowSize
{
    private const uint WmGetMinMaxInfo = 0x0024;
    private const nuint SubclassId = 1;

    private static int _minWidthDip;
    private static int _minHeightDip;
    private static nint _hwnd;

    public static void Apply(nint hwnd, int minWidthDip, int minHeightDip)
    {
        _hwnd = hwnd;
        _minWidthDip = minWidthDip;
        _minHeightDip = minHeightDip;
        SetWindowSubclass(hwnd, &SubclassProc, SubclassId, 0);
    }

    /// <summary>Current display scale of the window (1.0 = 100 %).</summary>
    public static double ScaleFor(nint hwnd) => GetDpiForWindow(hwnd) / 96.0;

    [UnmanagedCallersOnly]
    private static nint SubclassProc(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint refData)
    {
        if (message == WmGetMinMaxInfo)
        {
            var info = (MinMaxInfo*)lParam;
            var scale = ScaleFor(_hwnd);
            info->MinTrackSizeX = (int)Math.Ceiling(_minWidthDip * scale);
            info->MinTrackSizeY = (int)Math.Ceiling(_minHeightDip * scale);
            return 0;
        }

        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public int ReservedX;
        public int ReservedY;
        public int MaxSizeX;
        public int MaxSizeY;
        public int MaxPositionX;
        public int MaxPositionY;
        public int MinTrackSizeX;
        public int MinTrackSizeY;
        public int MaxTrackSizeX;
        public int MaxTrackSizeY;
    }

    [LibraryImport("comctl32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, delegate* unmanaged<nint, uint, nuint, nint, nuint, nuint, nint> proc, nuint id, nuint refData);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
