using KvaliTool.Core.Platform;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace KvaliTool.App;

public sealed partial class MainWindow : Window
{
    private const int MinWidthDip = 900;
    private const int MinHeightDip = 600;

    public MainWindow()
    {
        InitializeComponent();

        Title = "KvaliTool";
        SystemBackdrop = new MicaBackdrop();

        // The caption bar follows the theme the app actually uses (the system's, or the --theme override).
        AppWindow.TitleBar.PreferredTheme = Application.Current.RequestedTheme == ApplicationTheme.Dark
            ? TitleBarTheme.Dark
            : TitleBarTheme.Light;

        var hwnd = WindowNative.GetWindowHandle(this);
        MinimumWindowSize.Apply(hwnd, MinWidthDip, MinHeightDip);
        var scale = MinimumWindowSize.ScaleFor(hwnd);
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)Math.Min(1400 * scale, work.Width * 0.95),
            (int)Math.Min(880 * scale, work.Height * 0.95)));
        AppWindow.Move(new Windows.Graphics.PointInt32(
            work.X + Math.Max(0, (work.Width - AppWindow.Size.Width) / 2),
            work.Y + Math.Max(0, (work.Height - AppWindow.Size.Height) / 2)));

        if (AppState.SettingsError is { } problem)
        {
            SettingsErrorBar.Message = problem + " Scanning and saving settings are turned off until the file is fixed or moved away.";
            SettingsErrorBar.IsOpen = true;
        }

        AppState.ShowResultsRequested += (_, _) => NavigateTo("results");
    }

    private void OnNavLoaded(object sender, RoutedEventArgs e)
    {
        Nav.SelectedItem = ScanNavItem;

        // "--report <file>" opens a saved report straight into the Results page.
        var args = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(args, a => string.Equals(a, "--report", StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _ = OpenReportAsync(index + 1 < args.Length ? args[index + 1] : null);
        }
    }

    private async Task OpenReportAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowNotice("--report needs the path of a saved report file.");
            return;
        }

        try
        {
            await AppState.OpenSavedReportAsync(path);
            NavigateTo("results");
        }
        catch (Exception ex)
        {
            ShowNotice($"Could not open the report '{path}': {ex.Message}");
        }
    }

    private void ShowNotice(string message)
    {
        NoticeBar.Title = "Report not opened";
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }

    private void NavigateTo(string tag)
    {
        var item = tag switch
        {
            "results" => ResultsNavItem,
            "settings" => SettingsNavItem,
            _ => ScanNavItem,
        };

        if (!ReferenceEquals(Nav.SelectedItem, item))
        {
            Nav.SelectedItem = item;
        }
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        var page = tag switch
        {
            "results" => typeof(ResultsPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(ScanPage),
        };

        if (ContentFrame.CurrentSourcePageType != page)
        {
            ContentFrame.Navigate(page);
        }
    }
}
