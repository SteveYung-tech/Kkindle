using System.Runtime.InteropServices;

namespace Kkindle;

internal sealed class NativeDeviceChangeMonitor : IDisposable
{
    private const int GwlWndProc = -4;
    private const uint WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevnodesChanged = 0x0007;

    private readonly IntPtr _windowHandle;
    private readonly WndProc _newWindowProc;
    private readonly IntPtr _oldWindowProc;
    private bool _disposed;

    public NativeDeviceChangeMonitor(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _newWindowProc = WindowProc;
        _oldWindowProc = SetWindowLongPtr(windowHandle, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_newWindowProc));
    }

    public event EventHandler? DeviceChanged;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_oldWindowProc != IntPtr.Zero)
            SetWindowLongPtr(_windowHandle, GwlWndProc, _oldWindowProc);
        GC.KeepAlive(_newWindowProc);
    }

    private IntPtr WindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == WmDeviceChange && (wParam.ToInt32() == DbtDeviceArrival || wParam.ToInt32() == DbtDeviceRemoveComplete || wParam.ToInt32() == DbtDevnodesChanged))
            DeviceChanged?.Invoke(this, EventArgs.Empty);
        return CallWindowProc(_oldWindowProc, hWnd, message, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr newLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallWindowProc(IntPtr previousWndProc, IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
