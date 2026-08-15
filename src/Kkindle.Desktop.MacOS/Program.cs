using Avalonia;
using Kkindle.Infrastructure;
using Kkindle.Platform.Common;
using Kkindle.Platform.MacOS;

namespace Kkindle.Desktop.MacOS;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("This Kkindle head is for macOS.");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure(() => new Kkindle.App(BuildServices()))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static AppServices BuildServices()
    {
        var configurationDirectory = MacOSAppData.ResolveConfigurationDirectory();
        var paths = new AppPaths(AppRootConfiguration.ResolveRoot(configurationDirectory, MacOSAppData.ResolveRoot()));
        return new AppServices(
            SecretProtector: new MacOSSecretProtector(),
            CreateDeviceChangeNotifier: _ => null,
            KindleDeviceService: new MassStorageKindleDeviceService(
                paths,
                new BookMetadataService(),
                eject: MacOSKindleEjector.EjectAsync),
            Paths: paths,
            RootConfigurationDirectory: configurationDirectory);
    }
}
