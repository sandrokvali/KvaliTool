using System.Diagnostics;
using KvaliTool.Core;
using KvaliTool.Core.Configuration;
using KvaliTool.Core.Models;
using KvaliTool.Core.Safety;
using KvaliTool.Core.Selection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace KvaliTool.App;

/// <summary>One label/value line in the details pane.</summary>
public sealed record DetailField(string Label, string Value);

/// <summary>
/// Shows a <see cref="ResultTree"/>: a flat, virtualized list of category, group and item rows with checkboxes,
/// plus a details pane. This page only changes which rows are ticked in memory. It has no code that deletes,
/// moves or writes files; "Open file location" only starts Explorer with /select, which highlights the file.
/// </summary>
public sealed partial class ResultsPage : Page
{
    private const long MaxPreviewBytes = 50L * 1024 * 1024;

    private static readonly HashSet<string> PreviewExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".ico",
    };

    private readonly DispatcherQueueTimer _searchTimer;
    private ResultTree? _tree;
    private ResultRowsAdapter? _adapter;
    private ResultRow? _contextRow;
    private bool _binding;
    private int _previewVersion;
    private long _lastMenuTick;
    private double? _savedOffset;
    private ResultRow? _selectedBeforeReset;
    private ScrollViewer? _listScroller;

    public ResultsPage()
    {
        InitializeComponent();

        _searchTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(200);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += (_, _) => _tree?.SetFilter(SearchBox.Text);

        // handledEventsToo: the ListView itself handles Space and some arrows, but they still have to reach us.
        RowList.AddHandler(KeyDownEvent, new KeyEventHandler(OnListKeyDown), handledEventsToo: true);

        AppState.ResultsChanged += (_, _) => Bind(AppState.Tree);
        Bind(AppState.Tree);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (!ReferenceEquals(AppState.Tree, _tree))
        {
            Bind(AppState.Tree);
        }
    }

    // ---------------------------------------------------------------- binding the tree

    private void Bind(ResultTree? tree)
    {
        if (_tree is not null)
        {
            _tree.SelectionChanged -= OnTreeSelectionChanged;
        }

        if (_adapter is not null)
        {
            _adapter.Detach();
            _adapter.LargeStructureChangeStarting -= OnLargeChangeStarting;
            _adapter.LargeStructureChangeFinished -= OnLargeChangeFinished;
        }

        _tree = tree;
        _adapter = null;
        _binding = true;
        try
        {
            RowList.ItemsSource = null;
            ShowDetails(null);
            DuplicateBar.IsOpen = false;
            ActionBar.IsOpen = false;
            SearchBox.Text = string.Empty;
            SortBox.SelectedIndex = 0;

            var hasResults = tree is not null;
            EmptyPanel.Visibility = hasResults ? Visibility.Collapsed : Visibility.Visible;
            TopPanel.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
            ListArea.Visibility = hasResults ? Visibility.Visible : Visibility.Collapsed;
            if (tree is null)
            {
                return;
            }

            // The tree arrives sorted by size (largest first), which is the first entry of the sort box.
            _adapter = new ResultRowsAdapter(tree);
            _adapter.LargeStructureChangeStarting += OnLargeChangeStarting;
            _adapter.LargeStructureChangeFinished += OnLargeChangeFinished;
            RowList.ItemsSource = _adapter;
            tree.SelectionChanged += OnTreeSelectionChanged;
            UpdateHeader();

            if (AppState.SavedReportStartedUtc is { } started)
            {
                SavedReportBar.Message = $"Viewing a saved report from {started.ToLocalTime():f}. Nothing can be cleaned from a saved report.";
                SavedReportBar.IsOpen = true;
            }
            else
            {
                SavedReportBar.IsOpen = false;
            }

            var violations = AppState.Report?.PolicyViolations.Count ?? 0;
            PolicyBar.Message = $"{violations} item(s) were dropped by the safety rules. This should never happen; please report it.";
            PolicyBar.IsOpen = violations > 0;
        }
        finally
        {
            _binding = false;
        }
    }

    private void OnTreeSelectionChanged(object? sender, EventArgs e) => UpdateHeader();

    private void UpdateHeader()
    {
        if (_tree is not { } tree)
        {
            return;
        }

        ReclaimableText.Text = $"Reclaimable: {Format.Bytes(tree.ReclaimableBytes)}";
        var count = tree.SelectedItemCount;
        SelectedText.Text =
            $"Selected: {Format.Bytes(tree.SelectedBytes)}{(tree.SelectionHasUnknownSizes ? " + unknown" : string.Empty)} in {count:N0} {(count == 1 ? "item" : "items")}";
    }

    // ---------------------------------------------------------------- toolbar

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_binding || _tree is null)
        {
            return;
        }

        var (key, descending) = SortBox.SelectedIndex switch
        {
            1 => (SortKey.Name, false),
            2 => (SortKey.Modified, true),
            3 => (SortKey.Modified, false),
            4 => (SortKey.ScanOrder, false),
            _ => (SortKey.Size, true),
        };
        _tree.Sort(key, descending);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_binding || _tree is null)
        {
            return;
        }

        // Wait for a pause in typing so a big list isn't re-filtered on every key.
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void OnFindAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
        args.Handled = true;
    }

    /// <summary>
    /// A ScrollViewer lets its content grow without limit, which would stretch the star-sized columns. Pin the list
    /// to the visible width (but never narrower than its minimum) so the columns share the space and only scroll
    /// sideways when the window is really small.
    /// </summary>
    private void OnListScrollSizeChanged(object sender, SizeChangedEventArgs e) =>
        ListContent.Width = Math.Max(ListContent.MinWidth, e.NewSize.Width);

    private void OnDetailsToggleClick(object sender, RoutedEventArgs e)
    {
        var show = DetailsToggle.IsChecked == true;
        DetailsPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DetailsColumn.Width = new GridLength(show ? 300 : 0);
    }

    private void OnSelectSafeClick(object sender, RoutedEventArgs e) => ReportOutcome(_tree?.SelectSafe());

    private void OnSelectAllClick(object sender, RoutedEventArgs e) => ReportOutcome(_tree?.SelectAll());

    private void OnClearAllClick(object sender, RoutedEventArgs e) => ReportOutcome(_tree?.ClearAll());

    /// <summary>Explains anything the tree refused to tick, so the user doesn't wonder why a box stayed empty.</summary>
    private void ReportOutcome(SelectionOutcome? outcome)
    {
        if (outcome is { KeptForDuplicateSafety: > 0 } kept)
        {
            DuplicateBar.Message = $"{kept.KeptForDuplicateSafety:N0} items left unticked so one copy of every duplicate set is kept.";
            DuplicateBar.IsOpen = true;
        }
        else
        {
            DuplicateBar.IsOpen = false;
        }
    }

    // ---------------------------------------------------------------- list rows

    private void OnRowCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is CheckBox box && box.DataContext is ResultRow row && _tree is not null)
        {
            var outcome = _tree.Toggle(row);

            // The checkbox cycles its own states when clicked; show what the tree decided instead.
            box.IsChecked = Fmt.Check(row.State);
            ReportOutcome(outcome);
        }
    }

    private void OnChevronClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ResultRow row })
        {
            _tree?.SetExpanded(row, !row.IsExpanded);
        }
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ResultRow row)
        {
            return;
        }

        // Screen readers read the row's name instead of the class name.
        var kind = row.Kind switch
        {
            RowKind.Category => "Category",
            RowKind.Group => "Group",
            _ => "Item",
        };
        var size = row.SizeBytes is { } bytes ? Format.Bytes(bytes) : "size unknown";
        AutomationProperties.SetName(args.ItemContainer, $"{kind} {row.Name}, {size}");
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_tree is null)
        {
            return;
        }

        // Space/arrows on a button or checkbox inside the row belong to that control.
        if (e.OriginalSource is ButtonBase)
        {
            return;
        }

        var row = RowFor(e.OriginalSource as DependencyObject) ?? RowList.SelectedItem as ResultRow;
        if (row is null)
        {
            return;
        }
        var shiftDown =InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Application:
            case Windows.System.VirtualKey.F10 when shiftDown:
                // The keyboard way to the row menu (menu key or Shift+F10).
                if (RowList.ContainerFromItem(row) is FrameworkElement container)
                {
                    e.Handled = ShowRowMenu(row, container, null);
                }

                break;

            case Windows.System.VirtualKey.Space:
                if (row.IsSelectable && !row.IsExcluded)
                {
                    ReportOutcome(_tree.Toggle(row));
                }

                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Right:
                if (row.Kind != RowKind.Item && !row.IsExpanded)
                {
                    _tree.SetExpanded(row, true);
                    e.Handled = true;
                }

                break;

            case Windows.System.VirtualKey.Left:
                if (row.Kind != RowKind.Item && row.IsExpanded)
                {
                    _tree.SetExpanded(row, false);
                    e.Handled = true;
                }
                else if (row.Parent is { } parent && _adapter?.IndexOf(parent) >= 0)
                {
                    RowList.SelectedItem = parent;
                    RowList.ScrollIntoView(parent);
                    e.Handled = true;
                }

                break;
        }
    }

    // A very large expand or collapse arrives as a Reset, which would throw the list back to the top.
    private void OnLargeChangeStarting(object? sender, EventArgs e)
    {
        _savedOffset = FindListScroller()?.VerticalOffset;
        _selectedBeforeReset = RowList.SelectedItem as ResultRow;
    }

    private void OnLargeChangeFinished(object? sender, EventArgs e)
    {
        var offset = _savedOffset;
        var selected = _selectedBeforeReset;
        _savedOffset = null;
        _selectedBeforeReset = null;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (offset is { } value && FindListScroller() is { } scroller)
            {
                RowList.UpdateLayout();
                scroller.ChangeView(null, value, null, disableAnimation: true);
            }

            if (selected is not null && _adapter?.IndexOf(selected) >= 0)
            {
                RowList.SelectedItem = selected;
            }
        });
    }

    private ScrollViewer? FindListScroller() => _listScroller ??= FindDescendant<ScrollViewer>(RowList);

    private static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    // ---------------------------------------------------------------- context menu

    private void OnRowContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        // The list raises this for the mouse (the element under the pointer) and usually for the keyboard too.
        if (args.OriginalSource is not FrameworkElement element)
        {
            return;
        }

        Windows.Foundation.Point? position = args.TryGetPosition(element, out var point) ? point : null;
        if (RowFor(element) is { } row && ShowRowMenu(row, element, position))
        {
            args.Handled = true;
        }
    }

    /// <summary>
    /// The row a UI element belongs to. Elements inside a row inherit the row as their DataContext, but the
    /// ListViewItem that wraps it doesn't, so walk up to the container and ask the list.
    /// </summary>
    private ResultRow? RowFor(DependencyObject? element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is ListViewItem container)
            {
                return RowList.ItemFromContainer(container) as ResultRow;
            }
        }

        return null;
    }

    /// <summary>Opens the row menu for an item row. Group and category rows have no menu.</summary>
    private bool ShowRowMenu(ResultRow row, FrameworkElement element, Windows.Foundation.Point? position)
    {
        if (row.Kind != RowKind.Item)
        {
            return false;
        }

        // Shift+F10 can arrive both as a key press and as a context request; one menu is enough.
        var now = Environment.TickCount64;
        if (now - _lastMenuTick < 300)
        {
            return true;
        }

        _lastMenuTick = now;
        _contextRow = row;
        var realPath = HasRealPath(row);
        OpenLocationItem.IsEnabled = realPath && (File.Exists(row.Path) || Directory.Exists(row.Path));
        CopyPathItem.IsEnabled = !string.IsNullOrEmpty(row.Path);
        ExcludeItem.IsEnabled = realPath && AppState.SettingsError is null;

        if (position is { } point)
        {
            RowMenu.ShowAt(element, new FlyoutShowOptions { Position = point });
        }
        else
        {
            RowMenu.ShowAt(element);
        }

        return true;
    }

    /// <summary>Virtual items (restore points, the component store) have pseudo paths that don't exist on disk.</summary>
    private static bool HasRealPath(ResultRow row) =>
        row.Item is { Kind: not ItemKind.Virtual } item && !string.IsNullOrEmpty(item.Path) && System.IO.Path.IsPathFullyQualified(item.Path);

    private void OnOpenLocationClick(object sender, RoutedEventArgs e)
    {
        if (_contextRow?.Path is not { } path || !HasRealPath(_contextRow))
        {
            return;
        }

        try
        {
            // Explorer with /select only highlights the file in its folder. The file itself is never opened.
            var start = new ProcessStartInfo("explorer.exe");
            start.ArgumentList.Add("/select," + path);
            Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            ShowAction(InfoBarSeverity.Error, "Could not open Explorer", ex.Message);
        }
    }

    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        if (_contextRow?.Path is not { } path)
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(path);
            Clipboard.SetContent(package);
            ShowAction(InfoBarSeverity.Success, "Path copied", path);
        }
        catch (Exception ex)
        {
            ShowAction(InfoBarSeverity.Error, "Could not copy the path", ex.Message);
        }
    }

    private void OnExcludeClick(object sender, RoutedEventArgs e)
    {
        if (_contextRow?.Path is not { } path || !HasRealPath(_contextRow) || _tree is null)
        {
            return;
        }

        if (AppState.SettingsError is not null)
        {
            ShowAction(InfoBarSeverity.Error, "Exclusions can't be saved", "The settings file can't be read, so it won't be changed.");
            return;
        }

        // Save a modified copy first; only if that works does it become the live settings.
        var updated = AppState.CloneSettings(AppState.Settings);
        if (!updated.Exclusions.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            updated.Exclusions.Add(path);
        }

        try
        {
            SettingsStore.Save(updated, SettingsStore.DefaultPath);
        }
        catch (Exception ex)
        {
            ShowAction(InfoBarSeverity.Error, "Could not save the exclusion", ex.Message);
            return;
        }

        AppState.SettingsSaved(updated);
        var marked = _tree.ApplyExclusions(new ExclusionList(updated.Exclusions));
        ShowAction(
            InfoBarSeverity.Success,
            "Added to the exclusion list",
            $"{path} will never be listed again. {marked:N0} {(marked == 1 ? "item" : "items")} in these results {(marked == 1 ? "is" : "are")} now marked Excluded and unticked.");
    }

    private void ShowAction(InfoBarSeverity severity, string title, string message)
    {
        ActionBar.Severity = severity;
        ActionBar.Title = title;
        ActionBar.Message = message;
        ActionBar.IsOpen = true;
    }

    // ---------------------------------------------------------------- details pane

    private void OnRowSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails(RowList.SelectedItem as ResultRow);

    private void ShowDetails(ResultRow? row)
    {
        _previewVersion++;
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        NothingSelectedText.Visibility = row is null ? Visibility.Visible : Visibility.Collapsed;
        if (row is null)
        {
            DetailsList.ItemsSource = null;
            return;
        }

        var fields = new List<DetailField>();
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                fields.Add(new DetailField(label, value));
            }
        }

        var definition = row.Category.Definition;
        var kindName = row.Kind switch
        {
            RowKind.Category => "Category",
            RowKind.Group => "Group",
            _ => "Item",
        };
        Add(kindName, row.Name);
        Add(row.Kind == RowKind.Group ? "Folder" : "Full path", row.Path);
        Add("Size", Fmt.SizeLong(row.SizeBytes, row.HasUnknownSize));
        if (row.Kind != RowKind.Item)
        {
            Add("Items", row.ItemCount.ToString("N0"));
        }

        if (row.Item is { } item)
        {
            if (item.FileCount > 1)
            {
                Add("Files", item.FileCount.ToString("N0"));
            }

            Add("Type", item.Kind.ToString());
            Add("Modified", Fmt.DateText(item.LastModifiedUtc));
            Add("Reason", item.Reason);
            Add("Group", item.Group);
            Add("Warning", item.Warning);
            Add("Original location", item.OriginalPath);
            Add("Duplicate copy that is kept", item.DuplicateKeeperPath);

            var flags = new List<string>();
            if (item.ReportOnly)
            {
                flags.Add("Report only: KvaliTool will not clean this.");
            }

            if (row.IsPersonal)
            {
                flags.Add("Inside a personal or cloud-synced folder.");
            }

            if (row.IsExcluded)
            {
                flags.Add("Excluded by your exclusion list.");
            }

            Add("Notes", string.Join(" ", flags));
        }
        else
        {
            Add("Modified", Fmt.DateText(row.LastModifiedUtc));
            Add("Warning", row.Warning);
        }

        Add("Category", definition.DisplayName);
        if (row.Kind == RowKind.Category)
        {
            Add("What it is", definition.Description);
        }

        Add("Risk tier", Fmt.TierText(definition.Tier));
        Add("Status", Fmt.StatusLongText(row.Category.Status));
        Add("How it would be cleaned", Fmt.CleanMethodText(definition.CleanMethod));
        Add("Needs administrator", definition.RequiresElevation ? "Yes" : null);
        Add("Category caveat", definition.Caveat);
        if (row.Kind == RowKind.Category)
        {
            Add("Scanner notes", string.Join(Environment.NewLine, row.Category.Notes));
            if (row.Category.Skipped.Count > 0)
            {
                Add("Left alone", $"{row.Category.Skipped.Count:N0} entries were seen but not offered (in use, too recent, links, ...).");
            }
        }

        DetailsList.ItemsSource = fields;
        _ = LoadPreviewAsync(row, _previewVersion);
    }

    /// <summary>Shows a picture for common image files up to 50 MB. The file is opened read-only.</summary>
    private async Task LoadPreviewAsync(ResultRow row, int version)
    {
        if (row.Item is not { Kind: ItemKind.File } item || !PreviewExtensions.Contains(System.IO.Path.GetExtension(item.Path)))
        {
            return;
        }

        PreviewPanel.Visibility = Visibility.Visible;
        PreviewText.Text = "Loading preview...";
        FileStream? stream = null;
        try
        {
            var path = item.Path;
            stream = await Task.Run(() =>
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxPreviewBytes)
                {
                    throw new IOException("Missing or too large to preview.");
                }

                return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            });

            using var randomAccess = stream.AsRandomAccessStream();
            var bitmap = new BitmapImage { DecodePixelWidth = 400 };
            await bitmap.SetSourceAsync(randomAccess);
            if (version != _previewVersion)
            {
                return;
            }

            PreviewImage.Source = bitmap;
            AutomationProperties.SetName(PreviewImage, $"Preview of {row.Name}");
            PreviewText.Text = "Preview";
        }
        catch (Exception)
        {
            if (version == _previewVersion)
            {
                PreviewImage.Source = null;
                PreviewText.Text = "No preview";
            }
        }
        finally
        {
            stream?.Dispose();
        }
    }
}
