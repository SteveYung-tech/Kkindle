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
        var paths = new AppPaths(AppContext.BaseDirectory);
        var metadata = new BookMetadataService();
        var library = new SqliteBookLibraryService(paths, metadata);
        var kindle = new KindleDeviceService(paths, metadata);
        var readerData = new ReaderDataService(paths);
        await library.InitializeAsync();
        await readerData.InitializeAsync();
        _window = new MainWindow(
            paths,
            library,
            kindle,
            readerData,
            new EpubBookContentService(readerData),
            new EpubFootnoteResolver(),
            new AiSettingsStore(paths),
            new AiChatClient());
        _window.Activate();
    }
}
