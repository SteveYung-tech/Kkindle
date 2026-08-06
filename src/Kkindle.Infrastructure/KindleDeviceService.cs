using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class KindleDeviceService : IKindleDeviceService
{
    private const long MaximumMetadataFileSize = 128L * 1024 * 1024;
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".epub", ".pdf", ".mobi", ".azw3", ".azw", ".kfx"
    };
    private readonly IMetadataService _metadata;
    private readonly string? _coverCacheDirectory;

    public KindleDeviceService()
        : this(null, new BookMetadataService())
    {
    }

    public KindleDeviceService(AppPaths? paths, IMetadataService metadata)
    {
        _metadata = metadata;
        _coverCacheDirectory = paths is null ? null : Path.Combine(paths.Covers, "kindle");
        if (_coverCacheDirectory is not null) Directory.CreateDirectory(_coverCacheDirectory);
    }

    public async Task<IReadOnlyList<KindleDevice>> DetectDevicesAsync(CancellationToken cancellationToken = default)
    {
        var devices = new List<KindleDevice>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (drive.DriveType != DriveType.Removable || !drive.IsReady) continue;
                var documents = Path.Combine(drive.RootDirectory.FullName, "documents");
                if (!Directory.Exists(documents)) continue;
                devices.Add(new KindleDevice
                {
                    RootPath = drive.RootDirectory.FullName,
                    VolumeSerial = GetVolumeSerial(drive.RootDirectory.FullName),
                    Name = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Kindle" : drive.VolumeLabel,
                    TotalBytes = drive.TotalSize,
                    FreeBytes = drive.AvailableFreeSpace,
                    IsReady = true
                });
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        var wpdDevices = await Task.Run(() => WpdKindleAccess.DetectDevices(cancellationToken), cancellationToken);
        devices.AddRange(wpdDevices.Where(wpd => devices.All(disk =>
            !string.Equals(disk.Identity, wpd.Identity, StringComparison.OrdinalIgnoreCase))));
        return devices;
    }

    public async Task<IReadOnlyList<KindleBook>> ScanBooksAsync(KindleDevice device, CancellationToken cancellationToken = default)
    {
        if (device.Transport == KindleTransport.Wpd)
        {
            var wpdBooks = await Task.Run(
                () => WpdKindleAccess.ScanBooks(device, SupportedExtensions, cancellationToken),
                cancellationToken);
            foreach (var book in wpdBooks)
                await EnrichWpdBookAsync(device, book, cancellationToken);
            return wpdBooks;
        }

        var documents = GetDocumentsRoot(device);
        if (!Directory.Exists(documents)) return [];
        var books = new List<KindleBook>();
        foreach (var path in Directory.EnumerateFiles(documents, "*.*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SupportedExtensions.Contains(Path.GetExtension(path))) continue;
            try
            {
                var info = new FileInfo(path);
                books.Add(new KindleBook
                {
                    RelativePath = Path.GetRelativePath(device.RootPath, path),
                    Format = Path.GetExtension(path).TrimStart('.').ToLowerInvariant(),
                    Size = info.Length,
                    Sha256 = await Hashing.Sha256Async(path, cancellationToken),
                    IsManagedByKkindle = false
                });
                await EnrichBookAsync(device, books[^1], path, cancellationToken);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return books;
    }

    private async Task EnrichWpdBookAsync(
        KindleDevice device,
        KindleBook book,
        CancellationToken cancellationToken)
    {
        SetFallbackMetadata(book);
        var cachedCover = FindCachedCover(device, book);
        if (cachedCover is not null)
        {
            book.CoverPath = cachedCover;
            return;
        }
        if (_coverCacheDirectory is null || book.Size <= 0 || book.Size > MaximumMetadataFileSize) return;
        if (book.Format is not ("epub" or "mobi" or "azw" or "azw3" or "kfx")) return;

        var stagingDirectory = Path.Combine(Path.GetTempPath(), "Kkindle", "metadata", Guid.NewGuid().ToString("N"));
        try
        {
            var localPath = await Task.Run(
                () => WpdKindleAccess.CopyBookToLocal(device, book, stagingDirectory, cancellationToken),
                cancellationToken);
            await EnrichBookAsync(device, book, localPath, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or TimeoutException)
        {
            // A missing cover must not hide an otherwise readable device book.
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private async Task EnrichBookAsync(
        KindleDevice device,
        KindleBook book,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        SetFallbackMetadata(book);
        try
        {
            var metadata = await _metadata.ReadMetadataAsync(sourcePath, cancellationToken);
            if (!string.IsNullOrWhiteSpace(metadata.Title)) book.Title = metadata.Title.Trim();
            if (!string.IsNullOrWhiteSpace(metadata.Authors)) book.Authors = metadata.Authors.Trim();
            if (metadata.CoverBytes is { Length: > 0 } && _coverCacheDirectory is not null)
            {
                var extension = metadata.CoverExtension.Equals(".png", StringComparison.OrdinalIgnoreCase)
                    ? ".png"
                    : ".jpg";
                var coverPath = Path.Combine(_coverCacheDirectory, GetCoverCacheKey(device, book) + extension);
                await File.WriteAllBytesAsync(coverPath, metadata.CoverBytes, cancellationToken);
                book.CoverPath = coverPath;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // File metadata is best effort; filename, format and size remain available.
        }
    }

    private string? FindCachedCover(KindleDevice device, KindleBook book)
    {
        if (_coverCacheDirectory is null) return null;
        var key = GetCoverCacheKey(device, book);
        foreach (var extension in new[] { ".jpg", ".png" })
        {
            var path = Path.Combine(_coverCacheDirectory, key + extension);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string GetCoverCacheKey(KindleDevice device, KindleBook book)
    {
        var identity = $"{device.Identity}\n{book.RelativePath}\n{book.Size}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    private static void SetFallbackMetadata(KindleBook book)
    {
        var fileName = Path.GetFileNameWithoutExtension(book.RelativePath);
        var identifierSeparator = fileName.LastIndexOf('_');
        if (identifierSeparator > 0)
        {
            var suffix = fileName[(identifierSeparator + 1)..];
            if (suffix.Length == 32 && suffix.All(Uri.IsHexDigit)) fileName = fileName[..identifierSeparator];
        }
        book.Title = fileName.Replace('_', ' ').Trim();
        book.Authors = "未知作者";
    }

    public async Task SendBookAsync(KindleDevice device, BookFile bookFile, string sourcePath, IProgress<TransferProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (device.Transport == KindleTransport.Wpd)
        {
            await Task.Run(() => WpdKindleAccess.SendBook(device, sourcePath, progress, cancellationToken), cancellationToken);
            return;
        }
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("书籍源文件不存在。", sourcePath);
        var documents = GetDocumentsRoot(device);
        Directory.CreateDirectory(documents);
        var fileName = GetSafeFileName(Path.GetFileName(sourcePath));
        var destination = GetUniqueDestination(documents, fileName);
        var temporary = destination + ".kkindle-part";
        try
        {
            var total = new FileInfo(sourcePath).Length;
            await CopyAsync(sourcePath, temporary, total, progress, cancellationToken);
            var hash = await Hashing.Sha256Async(temporary, cancellationToken);
            if (!string.Equals(hash, bookFile.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new IOException("传输校验失败，设备上的文件未被替换。");
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task RemoveBookAsync(KindleDevice device, KindleBook book, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Transport == KindleTransport.Wpd)
        {
            await Task.Run(() => WpdKindleAccess.RemoveBook(device, book, cancellationToken), cancellationToken);
            return;
        }
        var documents = GetDocumentsRoot(device);
        var fullPath = Path.GetFullPath(Path.Combine(device.RootPath, book.RelativePath));
        EnsureUnderRoot(fullPath, documents);
        if (File.Exists(fullPath)) File.Delete(fullPath);
    }

    public Task EjectAsync(KindleDevice device, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (device.Transport == KindleTransport.Wpd)
            throw new NotSupportedException("MTP Kindle 无法通过磁盘弹出接口弹出，请等待传输完成后直接断开连接。");
        return Task.Run(() => EjectDrive(device.RootPath), cancellationToken);
    }

    private static string GetDocumentsRoot(KindleDevice device)
    {
        var root = Path.GetFullPath(device.RootPath);
        var documents = Path.GetFullPath(Path.Combine(root, "documents"));
        EnsureUnderRoot(documents, root);
        return documents;
    }

    private static void EnsureUnderRoot(string path, string root)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        if (!normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("设备路径不在允许的目录范围内。");
    }

    private static async Task CopyAsync(string source, string target, long total, IProgress<TransferProgress>? progress, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            progress?.Report(new TransferProgress(copied, total, $"正在发送 {Path.GetFileName(source)}"));
        }
        await output.FlushAsync(cancellationToken);
    }

    private static string GetUniqueDestination(string directory, string fileName)
    {
        var destination = Path.Combine(directory, fileName);
        if (!File.Exists(destination)) return destination;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var index = 2; ; index++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string GetSafeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var value = new string(fileName.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(value) ? "book.bin" : value;
    }

    private static string GetVolumeSerial(string root)
    {
        return GetVolumeInformation(root, null, 0, out var serial, out _, out _, null, 0)
            ? serial.ToString("X8")
            : root.TrimEnd('\\');
    }

    private static void EjectDrive(string root)
    {
        var driveLetter = root.TrimEnd('\\').TrimEnd('/');
        if (driveLetter.Length < 2) throw new InvalidOperationException("无法确定设备盘符。");
        var handle = CreateFile($@"\\.\{driveLetter}", 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == InvalidHandleValue) throw new IOException("无法打开 Kindle 设备进行安全弹出。");
        try
        {
            DeviceIoControl(handle, FsctlLockVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            DeviceIoControl(handle, FsctlDismountVolume, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
            if (!DeviceIoControl(handle, IoctlStorageEjectMedia, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new IOException("Windows 未允许安全弹出，请使用资源管理器手动弹出 Kindle。");
        }
        finally { CloseHandle(handle); }
    }

    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint FsctlLockVolume = 0x00090018;
    private const uint FsctlDismountVolume = 0x00090020;
    private const uint IoctlStorageEjectMedia = 0x002D4808;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string? rootPathName, StringBuilder? volumeNameBuffer, int volumeNameSize, out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags, StringBuilder? fileSystemNameBuffer, int fileSystemNameSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr device, uint controlCode, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
