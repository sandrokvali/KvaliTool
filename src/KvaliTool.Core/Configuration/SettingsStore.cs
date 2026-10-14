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
}
