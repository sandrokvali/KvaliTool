using Microsoft.UI.Xaml;

namespace KvaliTool.App;

public partial class KvaliApp : Application
{
    private MainWindow? _window;

    public KvaliApp()
    {
        InitializeComponent();

        // The app follows the Windows light/dark setting. "--theme light" or "--theme dark" overrides it for this
        // run only (for screenshots and contrast checks without changing the Windows setting). Must be set here,
        // before any window exists.
        var args = Environment.GetCommandLineArgs();
        var index = Array.FindIndex(args, a => string.Equals(a, "--theme", StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length)
        {
            if (string.Equals(args[index + 1], "light", StringComparison.OrdinalIgnoreCase))
            {
                RequestedTheme = ApplicationTheme.Light;
            }
            else if (string.Equals(args[index + 1], "dark", StringComparison.OrdinalIgnoreCase))
            {
                RequestedTheme = ApplicationTheme.Dark;
            }
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppState.LoadSettings();
        _window = new MainWindow();
        _window.Activate();
    }
}
