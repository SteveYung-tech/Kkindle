using Avalonia;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Platform.Windows;

namespace Kkindle.Desktop.Windows;

internal static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things
    // aren't initialized yet and stuff might break.
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    // Also used by the Avalonia visual designer, which calls it without ever
    // reaching Main — so the App must stay constructible from here alone.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure(() => new Kkindle.App(BuildServices()))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static AppServices BuildServices()
    {
        var paths = new AppPaths(AppRootConfiguration.ResolveRoot(AppContext.BaseDirectory));
        return new AppServices(
            SecretProtector: new WindowsSecretProtector(),
            CreateDeviceChangeNotifier: handle => new WindowsDeviceChangeNotifier(handle),
            KindleDeviceService: new KindleDeviceService(paths, new BookMetadataService()));
    }
}
