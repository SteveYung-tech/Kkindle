using Avalonia.Controls;
using Avalonia.Platform;
using Kkindle.Core;
using System.Runtime.InteropServices;

namespace Kkindle;

/// <summary>
/// Reader host backed by Avalonia's official NativeWebView control. The
/// control uses the native browser for each platform (WebView2 on Windows),
/// while the rest of the reader only sees the portable IReaderHost contract.
/// </summary>
public sealed class NativeWebViewReaderHost : IReaderHost
{
    private readonly NativeWebView _view = new();
    private readonly TaskCompletionSource<object?> _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public NativeWebViewReaderHost()
    {
        _view.AdapterCreated += View_AdapterCreated;
        _view.EnvironmentRequested += View_EnvironmentRequested;
        _view.NavigationStarted += View_NavigationStarted;
        _view.NavigationCompleted += View_NavigationCompleted;
        _view.WebMessageReceived += View_WebMessageReceived;
        _view.NewWindowRequested += View_NewWindowRequested;
    }

    public object View => _view;

    public Uri? Source => _view.Source;

    public Task ReadyTask => _ready.Task;

    public event EventHandler<ReaderNavigationStartingEventArgs>? NavigationStarting;
    public event EventHandler<ReaderNavigationCompletedEventArgs>? NavigationCompleted;
    public event EventHandler<ReaderWebMessageReceivedEventArgs>? WebMessageReceived;

    public void Navigate(Uri uri)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _view.Navigate(uri);
    }

    public async Task<string?> InvokeScriptAsync(string script)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await ReadyTask.ConfigureAwait(true);
        return await _view.InvokeScript(script).ConfigureAwait(true);
    }

    public void Stop()
    {
        if (_disposed) return;
        _view.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _view.AdapterCreated -= View_AdapterCreated;
        _view.EnvironmentRequested -= View_EnvironmentRequested;
        _view.NavigationStarted -= View_NavigationStarted;
        _view.NavigationCompleted -= View_NavigationCompleted;
        _view.WebMessageReceived -= View_WebMessageReceived;
        _view.NewWindowRequested -= View_NewWindowRequested;
        try { _view.Stop(); } catch { }
        _ready.TrySetCanceled();
    }

    private void View_AdapterCreated(object? sender, WebViewAdapterEventArgs e)
        => _ready.TrySetResult(null);

    private static void View_EnvironmentRequested(
        object? sender,
        WebViewEnvironmentRequestedEventArgs e)
    {
        e.EnableDevTools = false;

        if (e is LinuxWpeWebViewEnvironmentRequestedEventArgs linux)
        {
            // Ubuntu 24.04 ships WebKitGTK 4.1 but not the WPE WebKit 2.0 ABI
            // used by Avalonia's preferred Linux adapter. Keep WPE where it is
            // available (for example on Debian 13), otherwise select the GTK
            // adapter that is installed by the Kkindle .deb package.
            linux.PreferWebKitGtkInstead = !CanLoadLinuxWpeWebKit();
        }
    }

    private static bool CanLoadLinuxWpeWebKit()
    {
        if (!OperatingSystem.IsLinux()) return false;
        if (!NativeLibrary.TryLoad("libWPEWebKit-2.0.so.1", out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }

    private void View_NavigationStarted(
        object? sender,
        WebViewNavigationStartingEventArgs e)
    {
        var translated = new ReaderNavigationStartingEventArgs(e.Request);
        NavigationStarting?.Invoke(this, translated);
        e.Cancel = translated.Cancel;
    }

    private void View_NavigationCompleted(
        object? sender,
        WebViewNavigationCompletedEventArgs e)
        => NavigationCompleted?.Invoke(
            this,
            new ReaderNavigationCompletedEventArgs(e.Request, e.IsSuccess));

    private void View_WebMessageReceived(
        object? sender,
        WebMessageReceivedEventArgs e)
        => WebMessageReceived?.Invoke(this, new ReaderWebMessageReceivedEventArgs(e.Body));

    private static void View_NewWindowRequested(
        object? sender,
        WebViewNewWindowRequestedEventArgs e)
        => e.Handled = true;
}
