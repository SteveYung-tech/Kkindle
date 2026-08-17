using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Kkindle.Core;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Kkindle;

/// <summary>
/// Reader host backed by Avalonia's official NativeWebView control. The
/// control uses the native browser for each platform (WebView2 on Windows),
/// while the rest of the reader only sees the portable IReaderHost contract.
/// </summary>
public sealed class NativeWebViewReaderHost : IReaderHost, IReaderPageSnapshotProvider
{
    private readonly NativeWebView _view = new();
    private readonly Action<IntPtr>? _configureWindowsWebView2;
    private readonly TaskCompletionSource<object?> _ready = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public NativeWebViewReaderHost(Action<IntPtr>? configureWindowsWebView2 = null)
    {
        _configureWindowsWebView2 = configureWindowsWebView2;
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

    public Task<byte[]?> CaptureVisiblePageAsync(CancellationToken cancellationToken)
    {
        if (_disposed || !OperatingSystem.IsWindows())
            return Task.FromResult<byte[]?>(null);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var topLeft = _view.PointToScreen(new Avalonia.Point(0, 0));
            var scaling = TopLevel.GetTopLevel(_view)?.RenderScaling ?? 1d;
            var width = Math.Max(1, (int)Math.Ceiling(_view.Bounds.Width * scaling));
            var height = Math.Max(1, (int)Math.Ceiling(_view.Bounds.Height * scaling));
            var snapshot = CaptureScreenRectangle(topLeft.X, topLeft.Y, width, height);
            return Task.FromResult<byte[]?>(snapshot);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Task.FromResult<byte[]?>(null);
        }
    }

    private static byte[]? CaptureScreenRectangle(int x, int y, int width, int height)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        var memoryDc = IntPtr.Zero;
        var bitmapHandle = IntPtr.Zero;
        var previousObject = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            if (memoryDc == IntPtr.Zero) return null;
            bitmapHandle = CreateCompatibleBitmap(screenDc, width, height);
            if (bitmapHandle == IntPtr.Zero) return null;
            previousObject = SelectObject(memoryDc, bitmapHandle);
            if (!BitBlt(
                    memoryDc,
                    0,
                    0,
                    width,
                    height,
                    screenDc,
                    x,
                    y,
                    SourceCopy | CaptureBlt))
            {
                return null;
            }

            var bitmapInfo = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    Width = width,
                    Height = -height,
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0
                }
            };
            using var bitmap = new SKBitmap(new SKImageInfo(
                width,
                height,
                SKColorType.Bgra8888,
                SKAlphaType.Opaque));
            if (GetDIBits(
                    memoryDc,
                    bitmapHandle,
                    0,
                    (uint)height,
                    bitmap.GetPixels(),
                    ref bitmapInfo,
                    0) != height)
            {
                return null;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data?.ToArray();
        }
        finally
        {
            if (previousObject != IntPtr.Zero && memoryDc != IntPtr.Zero)
                SelectObject(memoryDc, previousObject);
            if (bitmapHandle != IntPtr.Zero)
                DeleteObject(bitmapHandle);
            if (memoryDc != IntPtr.Zero)
                DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private const uint SourceCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr value);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr destination,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint operation);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(
        IntPtr dc,
        IntPtr bitmap,
        uint startScan,
        uint scanLines,
        IntPtr bits,
        ref BitmapInfo bitmapInfo,
        uint usage);

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
    {
        if (OperatingSystem.IsWindows()
            && _configureWindowsWebView2 is not null
            && e.TryGetPlatformHandle() is IWindowsWebView2PlatformHandle platformHandle
            && platformHandle.CoreWebView2 != IntPtr.Zero)
        {
            try { _configureWindowsWebView2(platformHandle.CoreWebView2); }
            catch { }
        }
        _ready.TrySetResult(null);
    }

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
