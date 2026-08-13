using System.ComponentModel;
using System.Runtime.InteropServices;
using Kkindle.Core;

namespace Kkindle.Platform.Windows;

/// <summary>
/// Subclasses the host window procedure and reports WM_DEVICECHANGE, so the
/// Kindle device list refreshes the moment a device is plugged or unplugged.
///
/// Windows delivers this message only to windows, hence the handle: callers
/// pass their own HWND and must dispose the notifier before the window is
/// destroyed, otherwise the original window procedure is never restored.
/// </summary>
public sealed class WindowsDeviceChangeNotifier : IDeviceChangeNotifier
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

    public WindowsDeviceChangeNotifier(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _newWindowProc = WindowProc;
        _oldWindowProc = SetWindowLongPtr(windowHandle, GwlWndProc, Marshal.GetFunctionPointerForDelegate(_newWindowProc));
        if (_oldWindowProc == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法监听设备插拔事件。");
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
