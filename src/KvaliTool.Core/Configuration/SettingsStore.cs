using System.Text.Json;
using KvaliTool.Core.Serialization;

namespace KvaliTool.Core.Configuration;

public static class SettingsStore
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KvaliTool", "settings.json");

    /// <summary>
    /// Loads settings. A missing file means defaults. A broken file is an error, never silently replaced by
    /// defaults: that would quietly drop your exclusion list and let excluded files show up again.
    /// </summary>
    public static ScanSettings Load(string path)
    {
        if (!File.Exists(path))
        {
            return new ScanSettings();
        }

        ScanSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize(File.ReadAllText(path), KvaliJsonContext.Default.ScanSettings);
        }
        catch (JsonException ex)
        {
            throw new SettingsException(
                $"The settings file '{path}' is not valid JSON ({ex.Message}). Fix it or move it away. " +
                "KvaliTool won't run with unreadable settings because your exclusion list could be lost.", ex);
        }

        if (settings is null)
        {
            throw new SettingsException($"The settings file '{path}' is empty.");
        }

        settings.Validate();
        return settings;
    }

    /// <summary>
    /// Validates, then saves atomically through <see cref="AppDataFile.WriteAtomic"/>: a crash mid-save leaves the
    /// previous file intact, never a truncated one. Invalid settings throw <see cref="SettingsException"/> and
    /// nothing is written. Callers must not save over a file that failed to <see cref="Load"/>: that would replace
    /// the user's (broken but recoverable) exclusion list with whatever is in memory.
    /// </summary>
    public static void Save(ScanSettings settings, string path)
    {
        settings.Validate();
        var json = JsonSerializer.SerializeToUtf8Bytes(settings, KvaliJsonContext.Default.ScanSettings);
        AppDataFile.WriteAtomic(path, json);
    }
}
