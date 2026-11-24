using Microsoft.UI.Xaml;
using ScrcpyLauncher.Interop;

namespace ScrcpyLauncher;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Allow hosting scrcpy's (SDL) window as a child even if its DPI awareness differs from ours.
        NativeMethods.SetThreadDpiHostingBehavior(NativeMethods.DPI_HOSTING_BEHAVIOR_MIXED);

        _window = new MainWindow();
        _window.Activate();
    }
}
