namespace KvaliTool.Core.Platform;

public static class PathUtil
{
    /// <summary>
    /// Full, long-name path without a trailing separator (except for drive roots like "C:\").
    /// Expanding 8.3 short names matters: %TEMP% is often "C:\Users\ABCDEF~1\..." and a short-name root
    /// would not match the long-name paths used everywhere else, which would defeat the safety checks.
    /// </summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        full = ToLongPath(full);
        return Path.TrimEndingDirectorySeparator(full);
    }

    public static unsafe string ToLongPath(string path)
    {
        if (!path.Contains('~'))
        {
            return path;
        }

        var needed = NativeMethods.GetLongPathName(path, null, 0);
        if (needed == 0)
        {
            return path;
        }

        var buffer = new char[needed];
        fixed (char* p = buffer)
        {
            var written = NativeMethods.GetLongPathName(path, p, needed);
            return written == 0 || written >= needed ? path : new string(p, 0, (int)written);
        }
    }

    /// <summary>True when <paramref name="path"/> equals <paramref name="root"/> or is inside it. Both must be normalized.</summary>
    public static bool IsSameOrUnder(string path, string root)
    {
        if (path.Length < root.Length || !path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Length == root.Length
            || path[root.Length] == Path.DirectorySeparatorChar
            || root[^1] == Path.DirectorySeparatorChar;
    }

    public static bool IsDriveRoot(string path) =>
        string.Equals(Path.GetPathRoot(path)?.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
