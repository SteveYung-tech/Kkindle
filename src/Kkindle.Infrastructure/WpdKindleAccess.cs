using System.Runtime.InteropServices;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

internal static class WpdKindleAccess
{
    private const int MyComputerShellFolder = 17;
    private const int CopyWithoutUi = 4 | 16 | 1024;

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

    public static void SendBook(
        KindleDevice device,
        string sourcePath,
        IProgress<TransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("书籍源文件不存在。", sourcePath);
        var sourceInfo = new FileInfo(sourcePath);
        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Kkindle", "transfer", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        dynamic? shell = null;
        try
        {
            shell = CreateShell();
            dynamic? kindle = FindDevice(shell, device.RootPath)
                ?? throw new IOException("Kindle 已断开连接。");
            dynamic? storage = FindFirstStorage(kindle)
                ?? throw new IOException("无法读取 Kindle 内部存储。");
            dynamic? documents = FindChild(storage, "documents")
                ?? throw new IOException("Kindle 上不存在 documents 目录。");

            var finalName = GetUniqueFileName(documents, sourceInfo.Name);
            var temporaryName = finalName + ".kkindle-part";
            var stagedPath = Path.Combine(stagingDirectory, temporaryName);
            File.Copy(sourcePath, stagedPath, overwrite: false);
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new TransferProgress(0, sourceInfo.Length, $"正在发送 {sourceInfo.Name}"));
            dynamic targetFolder = documents.GetFolder;
            targetFolder.CopyHere(stagedPath, CopyWithoutUi);

            var timeout = TimeSpan.FromMinutes(Math.Clamp(2 + sourceInfo.Length / (100d * 1024 * 1024), 2, 30));
            dynamic temporaryItem = WaitForItem(
                documents,
                temporaryName,
                sourceInfo.Length,
                timeout,
                progress,
                sourceInfo.Name,
                cancellationToken);
            temporaryItem.Name = finalName;

            dynamic finalItem = WaitForItem(
                documents,
                finalName,
                sourceInfo.Length,
                TimeSpan.FromSeconds(15),
                progress,
                sourceInfo.Name,
                cancellationToken);
            if (ConvertToInt64(finalItem.Size) != sourceInfo.Length)
                throw new IOException("设备文件大小校验失败。");
            progress?.Report(new TransferProgress(sourceInfo.Length, sourceInfo.Length, $"已发送 {finalName}"));
        }
        catch (COMException exception)
        {
            throw new IOException("MTP 传输失败，请确认 Kindle 仍保持连接。", exception);
        }
        finally
        {
            Release(shell);
            try { Directory.Delete(stagingDirectory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
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

    private static string GetUniqueFileName(dynamic folderItem, string fileName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        dynamic children = folderItem.GetFolder.Items();
        for (var index = 0; index < (int)children.Count; index++)
            names.Add(Convert.ToString(children.Item(index).Name) ?? string.Empty);

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 1; ; index++)
        {
            var candidate = index == 1 ? fileName : $"{stem} ({index}){extension}";
            if (!names.Contains(candidate) && !names.Contains(candidate + ".kkindle-part"))
                return candidate;
        }
    }

    private static dynamic WaitForItem(
        dynamic folderItem,
        string name,
        long expectedSize,
        TimeSpan timeout,
        IProgress<TransferProgress>? progress,
        string displayName,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dynamic? item = FindChild(folderItem, name);
            if (item is not null)
            {
                var size = ConvertToInt64(item.Size);
                progress?.Report(new TransferProgress(Math.Min(size, expectedSize), expectedSize, $"正在发送 {displayName}"));
                if (size == expectedSize) return item;
            }
            Thread.Sleep(250);
        }
        throw new TimeoutException("等待 Kindle 完成文件写入超时。");
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
