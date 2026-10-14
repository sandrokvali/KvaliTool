using System.Text.Json;
using System.Text.Json.Serialization;
using KvaliTool.Core.Configuration;
using KvaliTool.Core.Models;

namespace KvaliTool.Core.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    UseStringEnumConverter = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ScanReport))]
[JsonSerializable(typeof(ScanSettings))]
internal sealed partial class KvaliJsonContext : JsonSerializerContext;

public static class ScanReportJson
{
    /// <summary>Writes the report to a NEW file. Refuses to overwrite anything that already exists.</summary>
    public static void WriteNew(ScanReport report, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, report, KvaliJsonContext.Default.ScanReport);
    }

    public static ScanReport Read(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize(stream, KvaliJsonContext.Default.ScanReport)
            ?? throw new InvalidDataException($"'{path}' does not contain a scan report.");
    }

    internal static ScanSettings Clone(ScanSettings settings) =>
        JsonSerializer.Deserialize(
            JsonSerializer.Serialize(settings, KvaliJsonContext.Default.ScanSettings),
            KvaliJsonContext.Default.ScanSettings)!;
}
