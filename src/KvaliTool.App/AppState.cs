using KvaliTool.Core.Configuration;
using KvaliTool.Core.Models;
using KvaliTool.Core.Selection;
using KvaliTool.Core.Serialization;

namespace KvaliTool.App;

/// <summary>
/// State shared by the pages: the loaded settings, the current results and the hash cache. Everything here is
/// touched from the UI thread only. Nothing in this app deletes or moves files; the only things it writes are
/// the settings file and the hash cache, and both go through Core.
/// </summary>
internal static class AppState
{
    private static FileHashCache? _hashCache;

    /// <summary>The settings as last loaded or saved. Treat as read-only; save a modified copy instead.</summary>
    public static ScanSettings Settings { get; private set; } = new();

    /// <summary>
    /// Set when the settings file exists but can't be used. While set, scanning and saving settings are disabled:
    /// saving would overwrite the broken file, which still holds the user's exclusion list.
    /// </summary>
    public static string? SettingsError { get; private set; }

    /// <summary>Goes up on every successful save, so a page can tell whether its copy is stale.</summary>
    public static int SettingsVersion { get; private set; }

    public static ResultTree? Tree { get; private set; }

    public static ScanReport? Report { get; private set; }

    /// <summary>Set (to the report's start time) when the results come from a saved report file rather than a live scan.</summary>
    public static DateTime? SavedReportStartedUtc { get; private set; }

    /// <summary>Raised when <see cref="Tree"/> is replaced.</summary>
    public static event EventHandler? ResultsChanged;

    /// <summary>Raised when a page wants the window to show the Results page.</summary>
    public static event EventHandler? ShowResultsRequested;

    public static void LoadSettings()
    {
        try
        {
            Settings = SettingsStore.Load(SettingsStore.DefaultPath);
            SettingsError = null;
        }
        catch (SettingsException ex)
        {
            SettingsError = ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SettingsError = $"The settings file '{SettingsStore.DefaultPath}' could not be read: {ex.Message}";
        }
    }

    /// <summary>Remembers settings that were just saved successfully.</summary>
    public static void SettingsSaved(ScanSettings saved)
    {
        Settings = saved;
        SettingsVersion++;
    }

    /// <summary>
    /// Loads the hash cache once (on a background thread). A failure only costs speed, so it is reported to the
    /// caller as text and the scan goes on without a cache.
    /// </summary>
    public static async Task<(FileHashCache? Cache, string? Problem)> GetHashCacheAsync()
    {
        if (_hashCache is not null)
        {
            return (_hashCache, null);
        }

        try
        {
            _hashCache = await Task.Run(() => FileHashCache.Load(FileHashCache.DefaultPath));
            return (_hashCache, null);
        }
        catch (Exception ex)
        {
            return (null, $"The duplicate-file hash cache could not be loaded ({ex.Message}). Hashes will be recomputed.");
        }
    }

    /// <summary>Builds the result tree off the UI thread, sorted largest first.</summary>
    public static Task<ResultTree> BuildTreeAsync(ScanReport report) =>
        Task.Run(() =>
        {
            var tree = ResultTree.Build(report);
            tree.Sort(SortKey.Size, descending: true);
            return tree;
        });

    public static void SetResults(ResultTree tree, ScanReport report, bool fromSavedReport)
    {
        Tree = tree;
        Report = report;
        SavedReportStartedUtc = fromSavedReport ? report.StartedUtc : null;
        ResultsChanged?.Invoke(null, EventArgs.Empty);
    }

    public static void RequestShowResults() => ShowResultsRequested?.Invoke(null, EventArgs.Empty);

    /// <summary>Reads a saved JSON report and builds its tree. Used by the --report command-line option.</summary>
    public static async Task OpenSavedReportAsync(string path)
    {
        var report = await Task.Run(() => ScanReportJson.Read(path));
        var tree = await BuildTreeAsync(report);
        SetResults(tree, report, fromSavedReport: true);
    }

    /// <summary>An independent copy of <paramref name="source"/>, so a scan or an edit never changes the loaded settings by accident.</summary>
    public static ScanSettings CloneSettings(ScanSettings source) => source.Clone();
}
