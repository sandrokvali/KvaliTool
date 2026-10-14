namespace KvaliTool.Core;

public static class Format
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>Binary units with Windows' labels (1 KB = 1024 bytes), like Explorer shows them.</summary>
    public static string Bytes(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {Units[unit]}";
    }

    public static string Age(TimeSpan age) =>
        age.TotalDays >= 2 ? $"{(int)age.TotalDays} days"
        : age.TotalHours >= 2 ? $"{(int)age.TotalHours} hours"
        : $"{Math.Max(0, (int)age.TotalMinutes)} minutes";
}
