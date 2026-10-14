using System.ComponentModel;
using KvaliTool.Core;
using KvaliTool.Core.Configuration;
using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;
using KvaliTool.Core.Scanning;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace KvaliTool.App;

/// <summary>One category checkbox on the Scan page.</summary>
public sealed class CategoryChoice(CategoryDefinition definition, bool needsAdminBadge) : INotifyPropertyChanged
{
    private bool _isSelected = definition.CheckedByDefault;

    public event PropertyChangedEventHandler? PropertyChanged;

    public CategoryDefinition Definition { get; } = definition;

    public string DisplayName => Definition.DisplayName;

    public string Description => Definition.Description;

    /// <summary>True when the category needs administrator rights and this app isn't running as administrator.</summary>
    public bool NeedsAdminBadge { get; } = needsAdminBadge;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }
}

/// <summary>Keeps only the newest progress snapshot; the page's timer reads it ten times a second.</summary>
internal sealed class LatestProgress : IProgress<ScanProgress>
{
    private ScanProgress? _latest;

    public ScanProgress? Latest => Volatile.Read(ref _latest);

    public void Report(ScanProgress value) => Volatile.Write(ref _latest, value);
}

public sealed partial class ScanPage : Page
{
    private readonly List<CategoryChoice> _choices = [];
    private readonly DispatcherQueueTimer _progressTimer;
    private CancellationTokenSource? _cancel;
    private LatestProgress? _progress;
    private ScanProgress? _shownProgress;
    private bool _running;
    private int _settingsVersionSeen = -1;

    public ScanPage()
    {
        InitializeComponent();

        _progressTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _progressTimer.Interval = TimeSpan.FromMilliseconds(100);
        _progressTimer.Tick += (_, _) => ShowProgress();

        var elevated = SystemInfo.IsElevated();
        ElevationText.Text = elevated
            ? "KvaliTool is running as administrator."
            : "KvaliTool is not running as administrator, so categories marked \"Needs admin\" will be reported as not scanned. This version never asks for administrator rights.";

        foreach (var definition in CategoryCatalog.All)
        {
            // Duplicate files is controlled by the "Find duplicates" switch, not by a checkbox of its own.
            if (definition.Id == CategoryId.DuplicateFiles)
            {
                continue;
            }

            _choices.Add(new CategoryChoice(definition, definition.RequiresElevation && !elevated));
        }

        SafeList.ItemsSource = _choices.Where(c => c.Definition.Tier == RiskTier.Safe).ToList();
        ReviewList.ItemsSource = _choices.Where(c => c.Definition.Tier == RiskTier.Review).ToList();
        DuplicatesSwitch.IsOn = AppState.Settings.FindDuplicates;
        _settingsVersionSeen = AppState.SettingsVersion;

        UpdateControls();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // Settings saved on the Settings page change the default of the duplicates switch.
        if (!_running && _settingsVersionSeen != AppState.SettingsVersion)
        {
            DuplicatesSwitch.IsOn = AppState.Settings.FindDuplicates;
            _settingsVersionSeen = AppState.SettingsVersion;
        }
    }

    private async void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (_running || AppState.SettingsError is not null)
        {
            return;
        }

        var categories = _choices.Where(c => c.IsSelected).Select(c => c.Definition.Id).ToHashSet();
        var findDuplicates = DuplicatesSwitch.IsOn;
        if (findDuplicates)
        {
            categories.Add(CategoryId.DuplicateFiles);
        }

        if (categories.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "Nothing to scan", "Tick at least one category, or turn on \"Find duplicates\".");
            return;
        }

        // The scan gets its own copy of the settings, so the switch only affects this scan.
        var settings = AppState.CloneSettings(AppState.Settings);
        settings.FindDuplicates = findDuplicates;

        StatusBar.IsOpen = false;
        _cancel = new CancellationTokenSource();
        _progress = new LatestProgress();
        _shownProgress = null;
        SetRunning(true);
        ScanProgressBar.IsIndeterminate = true;
        ProgressCategoriesText.Text = "Starting...";
        ProgressFilesText.Text = string.Empty;
        ProgressActiveText.Text = string.Empty;
        ProgressPathText.Text = string.Empty;
        _progressTimer.Start();

        var token = _cancel.Token;
        try
        {
            IHashCache? hashCache = null;
            string? cacheProblem = null;
            if (findDuplicates)
            {
                (hashCache, cacheProblem) = await AppState.GetHashCacheAsync();
            }

            var request = new ScanRequest
            {
                Settings = settings,
                Paths = new WindowsKnownPaths(),
                Processes = new SystemProcessDetector(),
                Categories = categories,
                HashCache = hashCache,
            };
            var progress = _progress;
            var report = await Task.Run(() => ScanEngine.CreateDefault().ScanAsync(request, progress, token), token);

            _progressTimer.Stop();
            ShowProgress();
            ProgressCategoriesText.Text = "Preparing the results...";
            ScanProgressBar.IsIndeterminate = true;

            var tree = await AppState.BuildTreeAsync(report);
            AppState.SetResults(tree, report, fromSavedReport: false);
            SetRunning(false);
            if (cacheProblem is not null)
            {
                ShowStatus(InfoBarSeverity.Warning, "Duplicate hash cache", cacheProblem);
            }

            AppState.RequestShowResults();
        }
        catch (OperationCanceledException)
        {
            ShowStatus(InfoBarSeverity.Informational, "Scan cancelled", "Scan cancelled. Nothing was changed.");
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "The scan failed", $"{ex.Message} Nothing was changed.");
        }
        finally
        {
            _progressTimer.Stop();
            _cancel?.Dispose();
            _cancel = null;
            SetRunning(false);
        }
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        if (_cancel is { } cancel && !cancel.IsCancellationRequested)
        {
            cancel.Cancel();
            CancelButton.IsEnabled = false;
            ProgressCategoriesText.Text = "Cancelling...";
        }
    }

    /// <summary>Called by the 100 ms timer: shows the newest snapshot if there is one we haven't shown yet.</summary>
    private void ShowProgress()
    {
        var latest = _progress?.Latest;
        if (latest is null || ReferenceEquals(latest, _shownProgress))
        {
            return;
        }

        _shownProgress = latest;
        ScanProgressBar.IsIndeterminate = latest.CategoriesTotal == 0;
        ScanProgressBar.Maximum = Math.Max(1, latest.CategoriesTotal);
        ScanProgressBar.Value = latest.CategoriesCompleted;
        ProgressCategoriesText.Text = $"Categories done: {latest.CategoriesCompleted} of {latest.CategoriesTotal}";
        ProgressFilesText.Text = $"Files examined: {latest.FilesExamined:N0}   |   Found so far: {Format.Bytes(latest.BytesFound)}";
        ProgressActiveText.Text = latest.ActiveCategories.Count == 0 ? string.Empty : "Scanning: " + string.Join(", ", latest.ActiveCategories);
        ProgressPathText.Text = latest.CurrentPath ?? string.Empty;
    }

    private void SetRunning(bool running)
    {
        _running = running;
        ProgressPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        CategoryScroll.IsEnabled = !running;
        DuplicatesSwitch.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        UpdateControls();
    }

    private void UpdateControls()
    {
        var blocked = AppState.SettingsError is not null;
        StartButton.IsEnabled = !_running && !blocked;
        if (blocked && !_running)
        {
            ShowStatus(
                InfoBarSeverity.Error,
                "Scanning is turned off",
                "The settings file can't be read (see the message at the top). Fix or move the file, then restart KvaliTool.");
        }
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
