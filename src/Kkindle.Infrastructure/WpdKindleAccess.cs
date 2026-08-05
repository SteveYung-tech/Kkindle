using System.Runtime.InteropServices;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

internal static class WpdKindleAccess
{
    private const int MyComputerShellFolder = 17;

    public static IReadOnlyList<KindleDevice> DetectDevices(CancellationToken cancellationToken)
    {
        var devices = new List<KindleDevice>();
        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic computer = shell.NameSpace(MyComputerShellFolder);
            dynamic items = computer.Items();
            for (var index = 0; index < (int)items.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                dynamic item = items.Item(index);
                var name = Convert.ToString(item.Name) ?? string.Empty;
                var shellPath = Convert.ToString(item.Path) ?? string.Empty;
                if (!name.Contains("Kindle", StringComparison.OrdinalIgnoreCase)
                    && !shellPath.Contains("vid_1949", StringComparison.OrdinalIgnoreCase)) continue;

                dynamic? storage = FindFirstStorage(item);
                if (storage is null || FindChild(storage, "documents") is null) continue;
                devices.Add(new KindleDevice
                {
                    RootPath = shellPath,
                    VolumeSerial = shellPath,
                    Name = string.IsNullOrWhiteSpace(name) ? "Kindle" : name,
                    TotalBytes = ReadInt64Property(storage, "System.Capacity"),
                    FreeBytes = ReadInt64Property(storage, "System.FreeSpace"),
                    IsReady = true,
                    Transport = KindleTransport.Wpd
                });
            }
        }
        catch (COMException)
        {
            return devices;
        }
        finally
        {
            Release(shell);
        }
        return devices;
    }

    public static IReadOnlyList<KindleBook> ScanBooks(
        KindleDevice device,
        IReadOnlySet<string> supportedExtensions,
        CancellationToken cancellationToken)
    {
        var books = new List<KindleBook>();
        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath);
            dynamic? storage = kindle is null ? null : FindFirstStorage(kindle);
            dynamic? documents = storage is null ? null : FindChild(storage, "documents");
            if (documents is null) return books;

            var folders = new Stack<(object Folder, string RelativePath)>();
            folders.Push((documents.GetFolder, string.Empty));
            while (folders.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = folders.Pop();
                dynamic folder = entry.Folder;
                dynamic children = folder.Items();
                for (var index = 0; index < (int)children.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    dynamic child = children.Item(index);
                    var name = Convert.ToString(child.Name) ?? string.Empty;
                    var relativePath = string.IsNullOrEmpty(entry.RelativePath)
                        ? name
                        : $"{entry.RelativePath}\\{name}";
                    if ((bool)child.IsFolder)
                    {
                        if (!name.Equals(".cache", StringComparison.OrdinalIgnoreCase))
                            folders.Push((child.GetFolder, relativePath));
                        continue;
                    }

                    var extension = Path.GetExtension(name);
                    if (!supportedExtensions.Contains(extension)) continue;
                    books.Add(new KindleBook
                    {
                        RelativePath = relativePath,
                        Format = extension.TrimStart('.').ToLowerInvariant(),
                        Size = ConvertToInt64(child.Size),
                        Sha256 = string.Empty,
                        IsManagedByKkindle = false
                    });
                }
            }
        }
        catch (COMException)
        {
            return books;
        }
        finally
        {
            Release(shell);
        }
        return books.OrderBy(book => book.RelativePath, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static dynamic CreateShell()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new PlatformNotSupportedException("Windows Shell 不可用。");
        return Activator.CreateInstance(shellType)
            ?? throw new InvalidOperationException("无法启动 Windows Shell。");
    }

    private static dynamic? FindDevice(dynamic shell, string shellPath)
    {
        dynamic computer = shell.NameSpace(MyComputerShellFolder);
        dynamic items = computer.Items();
        for (var index = 0; index < (int)items.Count; index++)
        {
            dynamic item = items.Item(index);
            if (string.Equals(Convert.ToString(item.Path), shellPath, StringComparison.OrdinalIgnoreCase))
                return item;
        }
        return null;
    }

    private static dynamic? FindFirstStorage(dynamic device)
    {
        dynamic children = device.GetFolder.Items();
        for (var index = 0; index < (int)children.Count; index++)
        {
            dynamic child = children.Item(index);
            if ((bool)child.IsFolder) return child;
        }
        return null;
    }

    private static dynamic? FindChild(dynamic parent, string name)
    {
        dynamic children = parent.GetFolder.Items();
        for (var index = 0; index < (int)children.Count; index++)
        {
            dynamic child = children.Item(index);
            if (string.Equals(Convert.ToString(child.Name), name, StringComparison.OrdinalIgnoreCase))
                return child;
        }
        return null;
    }

    private static long ReadInt64Property(dynamic item, string propertyName)
    {
        try { return ConvertToInt64(item.ExtendedProperty(propertyName)); }
        catch (COMException) { return 0; }
    }

    private static long ConvertToInt64(object? value)
    {
        try { return Convert.ToInt64(value); }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return 0;
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.FinalReleaseComObject(value);
    }
}
