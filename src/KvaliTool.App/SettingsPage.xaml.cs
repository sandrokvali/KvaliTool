using System.Collections.ObjectModel;
using KvaliTool.Core.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace KvaliTool.App;

/// <summary>One line in the exclusion list.</summary>
public sealed record ExclusionEntry(string Value)
{
    public string RemoveName => "Remove exclusion " + Value;
}

/// <summary>
/// Edits the settings. Changes stay in this page until "Save settings"; saving goes through
/// <see cref="SettingsStore.Save"/> and is switched off entirely when the settings file was unreadable at start-up.
/// </summary>
public sealed partial class SettingsPage : Page
{
    private readonly ObservableCollection<ExclusionEntry> _exclusions = [];

    public SettingsPage()
    {
        InitializeComponent();

        var settings = AppState.Settings;
        PathRun.Text = SettingsStore.DefaultPath;
        foreach (var entry in settings.Exclusions)
        {
            _exclusions.Add(new ExclusionEntry(entry));
        }

        ExclusionList.ItemsSource = _exclusions;
        _exclusions.CollectionChanged += (_, _) => UpdateExclusionHint();
        UpdateExclusionHint();

        TempHoursBox.Value = settings.TempMinAgeHours;
        DownloadsDaysBox.Value = settings.DownloadsMinAgeDays;
        LargeFileMbBox.Value = (double)settings.LargeFileThresholdBytes / ScanSettings.OneMiB;
        DuplicateKbBox.Value = (double)settings.DuplicateMinBytes / 1024;
        LogDaysBox.Value = settings.LogMinAgeDays;
        RetentionDaysBox.Value = settings.QuarantineRetentionDays;
        FindDuplicatesSwitch.IsOn = settings.FindDuplicates;
        ComponentStoreSwitch.IsOn = settings.AnalyzeComponentStore;

        if (AppState.SettingsError is not null)
        {
            SaveButton.IsEnabled = false;
            SaveBlockedText.Text = "Saving is turned off because the settings file can't be read. Saving now would overwrite your exclusion list.";
        }
    }

    private void UpdateExclusionHint() =>
        NoExclusionsText.Visibility = _exclusions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnExclusionKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            AddExclusion();
            e.Handled = true;
        }
    }

    private void OnAddExclusionClick(object sender, RoutedEventArgs e) => AddExclusion();

    private void AddExclusion()
    {
        var text = ExclusionBox.Text.Trim();
        if (text.Length == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "Nothing to add", "Type a folder, file or pattern first.");
            return;
        }

        if (_exclusions.Any(x => string.Equals(x.Value, text, StringComparison.OrdinalIgnoreCase)))
        {
            ShowStatus(InfoBarSeverity.Informational, "Already in the list", text);
            return;
        }

        _exclusions.Add(new ExclusionEntry(text));
        ExclusionBox.Text = string.Empty;
        StatusBar.IsOpen = false;
    }

    private void OnRemoveExclusionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ExclusionEntry entry })
        {
            _exclusions.Remove(entry);
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (AppState.SettingsError is not null)
        {
            return;
        }

        try
        {
            var settings = AppState.CloneSettings(AppState.Settings);
            settings.TempMinAgeHours = ReadInt(TempHoursBox, "Temporary files age");
            settings.DownloadsMinAgeDays = ReadInt(DownloadsDaysBox, "Old downloads age");
            settings.LargeFileThresholdBytes = ReadBytes(LargeFileMbBox, ScanSettings.OneMiB, "Large file size");
            settings.DuplicateMinBytes = ReadBytes(DuplicateKbBox, 1024, "Duplicate minimum size");
            settings.LogMinAgeDays = ReadInt(LogDaysBox, "Log age");
            settings.QuarantineRetentionDays = ReadInt(RetentionDaysBox, "Quarantine retention");
            settings.FindDuplicates = FindDuplicatesSwitch.IsOn;
            settings.AnalyzeComponentStore = ComponentStoreSwitch.IsOn;
            settings.Exclusions = _exclusions.Select(x => x.Value).ToList();

            settings.Validate();
            SettingsStore.Save(settings, SettingsStore.DefaultPath);
            AppState.SettingsSaved(settings);
            ShowStatus(InfoBarSeverity.Success, "Settings saved", $"Saved to {SettingsStore.DefaultPath}.");
        }
        catch (SettingsException ex)
        {
            ShowStatus(InfoBarSeverity.Error, "These settings can't be used", ex.Message);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Settings were not saved", ex.Message);
        }
    }

    private static int ReadInt(NumberBox box, string what)
    {
        var value = Math.Round(box.Value);
        if (double.IsNaN(value) || value < 0 || value > int.MaxValue)
        {
            throw new SettingsException($"{what}: enter a whole number.");
        }

        return (int)value;
    }

    private static long ReadBytes(NumberBox box, long unit, string what)
    {
        var value = Math.Round(box.Value);
        if (double.IsNaN(value) || value < 0 || value > long.MaxValue / unit / 2)
        {
            throw new SettingsException($"{what}: enter a whole number.");
        }

        return (long)value * unit;
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
