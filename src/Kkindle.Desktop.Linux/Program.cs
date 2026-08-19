using System.IO;
using Avalonia;
using Kkindle.Core;
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
        ConfigureLinuxWebViewRendering();
        var configurationDirectory = LinuxAppData.ResolveConfigurationDirectory();
        var paths = new AppPaths(AppRootConfiguration.ResolveRoot(configurationDirectory, LinuxAppData.ResolveRoot()));
        BuildAvaloniaApp(BuildServices(paths, configurationDirectory)).StartWithClassicDesktopLifetime(args);
    }

    private static void ConfigureLinuxWebViewRendering()
    {
        SetDefaultEnvironment("GDK_BACKEND", "x11");
        // WebKitGTK 2.42+ shares its rendered frames through DMA-BUF. That
        // path silently produces empty frames when the GL driver cannot export
        // buffers — virtual GPUs (VMware SVGA3D, QEMU, many VDI sessions) and
        // software GL both hit this — so the reader painted a blank page while
        // the DOM was fully loaded and scriptable. Falling back to the shared
        // memory renderer costs a little scrolling smoothness and makes the
        // chapter actually reach the screen. Overridable: export the variable
        // yourself to keep DMA-BUF on hardware where it works.
        SetDefaultEnvironment("WEBKIT_DISABLE_DMABUF_RENDERER", "1");
        if (IsLikelyVirtualMachine())
        {
            // VMware and similar virtual GPUs can still blank the page even
            // with DMA-BUF disabled. Software GL is slower but reliable for
            // reader content.
            SetDefaultEnvironment("LIBGL_ALWAYS_SOFTWARE", "1");
            SetDefaultEnvironment("MESA_LOADER_DRIVER_OVERRIDE", "llvmpipe");
            SetDefaultEnvironment("KKINDLE_LINUX_TEXT_RECOVERY", "1");
        }
    }

    private static void SetDefaultEnvironment(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
            Environment.SetEnvironmentVariable(name, value);
    }

    private static bool IsLikelyVirtualMachine()
    {
        foreach (var path in new[] { "/sys/class/dmi/id/product_name", "/sys/class/dmi/id/sys_vendor" })
        {
            try
            {
                if (!File.Exists(path)) continue;
                var text = File.ReadAllText(path);
                if (text.Contains("vmware", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("virtualbox", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("qemu", StringComparison.OrdinalIgnoreCase)
                    || text.Contains("kvm", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
            }
        }

        return false;
    }

    public static AppBuilder BuildAvaloniaApp(AppServices? services = null) =>
        AppBuilder.Configure(() => new Kkindle.App(services ?? BuildServices()))
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = "kkindle" })
            .WithInterFont()
            .LogToTrace();

    private static AppServices BuildServices()
    {
        var configurationDirectory = LinuxAppData.ResolveConfigurationDirectory();
        var paths = new AppPaths(AppRootConfiguration.ResolveRoot(configurationDirectory, LinuxAppData.ResolveRoot()));
        return BuildServices(paths, configurationDirectory);
    }

    private static AppServices BuildServices(AppPaths paths, string configurationDirectory)
    {
        return new AppServices(
            SecretProtector: new LinuxSecretProtector(),
            CreateDeviceChangeNotifier: _ => null,
            KindleDeviceService: new MassStorageKindleDeviceService(
                paths,
                new BookMetadataService(),
                eject: LinuxKindleEjector.EjectAsync),
            ReaderHostFactory: () => new NativeWebViewReaderHost(),
            Paths: paths,
            RootConfigurationDirectory: configurationDirectory);
    }
}
