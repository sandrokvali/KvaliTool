using Microsoft.UI.Xaml;

namespace KvaliTool.App;

public partial class KvaliApp : Application
{
    private MainWindow? _window;

    public KvaliApp()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppState.LoadSettings();
        _window = new MainWindow();
        _window.Activate();
    }
}
