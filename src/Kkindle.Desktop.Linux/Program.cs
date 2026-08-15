using Avalonia;
using Kkindle.Infrastructure;
using Kkindle.Platform.Common;
using Kkindle.Platform.Linux;

namespace Kkindle.Desktop.Linux;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("This Kkindle head is for Linux.");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure(() => new Kkindle.App(BuildServices()))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static AppServices BuildServices()
    {
        var configurationDirectory = LinuxAppData.ResolveConfigurationDirectory();
        var paths = new AppPaths(AppRootConfiguration.ResolveRoot(configurationDirectory, LinuxAppData.ResolveRoot()));
        return new AppServices(
            SecretProtector: new LinuxSecretProtector(),
            CreateDeviceChangeNotifier: _ => null,
            KindleDeviceService: new MassStorageKindleDeviceService(
                paths,
                new BookMetadataService(),
                eject: LinuxKindleEjector.EjectAsync),
            Paths: paths,
            RootConfigurationDirectory: configurationDirectory);
    }
}
