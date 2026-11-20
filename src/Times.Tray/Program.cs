using Avalonia;
using Avalonia.Controls;

namespace Times.Tray;

/// <summary>Starts the tray app: no window, no Dock icon on macOS, running until Quit.</summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);

    // Also used by the Avalonia designer tooling.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TrayApp>()
            .UsePlatformDetect()
            .With(new MacOSPlatformOptions { ShowInDock = false });
}
