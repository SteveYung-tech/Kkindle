using Microsoft.UI.Xaml;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var applicationDirectory = AppContext.BaseDirectory;
            var paths = new AppPaths(AppRootConfiguration.ResolveRoot(applicationDirectory));
            var metadata = new BookMetadataService();
            var library = new SqliteBookLibraryService(paths, metadata);
            var formatConverter = new BookFormatConversionService();
            var kindle = new KindleDeviceService(paths, metadata);
            var readerData = new ReaderDataService(paths);
            var deviceModels = new DeviceModelStore(paths);
            await library.InitializeAsync();
            await readerData.InitializeAsync();
            await deviceModels.InitializeAsync();
            var migrationBackup = AppRootConfiguration.MigrationBackupPath(paths.Root);
            if (File.Exists(migrationBackup))
            {
                await new AppBackupService(paths).ImportAsync(migrationBackup);
                File.Delete(migrationBackup);
            }
            _window = new MainWindow(
                paths,
                library,
                formatConverter,
                kindle,
                deviceModels,
                readerData,
                new EpubBookContentService(readerData),
                new EpubFootnoteResolver(),
                new AiSettingsStore(paths),
                new AiChatClient(),
                new ZLibraryService(),
                new ZLibrarySettingsStore(paths));
            _window.Activate();
        }
        catch (Exception exception)
        {
            try
            {
                var logPath = Path.Combine(Path.GetTempPath(), "Kkindle-startup-error.log");
                await File.WriteAllTextAsync(logPath, exception.ToString());
            }
            catch { }
            throw;
        }
    }
}
