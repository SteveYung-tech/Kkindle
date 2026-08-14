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
    public static void Main(string[] args)
    {
        // Any unhandled exception is written to a crash log next to the exe
        // (and in the data logs directory) before the process dies, so remote
        // startup failures can be diagnosed from the user machine.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            WriteCrashLog("UnhandledException", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    private static void WriteCrashLog(string kind, Exception? exception)
    {
        var payload = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {kind}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
        var targets = new List<string>();
        try
        {
            targets.Add(Path.Combine(AppContext.BaseDirectory, "kkindle-crash.log"));
        }
        catch
        {
        }
        try
        {
            var paths = new AppPaths(AppRootConfiguration.ResolveRoot(AppContext.BaseDirectory));
            targets.Add(Path.Combine(paths.Logs, "crash.log"));
        }
        catch
        {
        }

        foreach (var target in targets.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
                File.AppendAllText(target, payload, new System.Text.UTF8Encoding(false));
            }
            catch
            {
                // Logging must never mask the original failure.
            }
        }
    }

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
