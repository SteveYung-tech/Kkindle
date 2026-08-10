using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed record ReaderFormatCacheResult(string EpubPath, string CacheKey, bool CacheHit);

/// <summary>
/// Keeps the expensive Calibre reader conversion for a source file so opening
/// the same AZW3/MOBI again does not start another conversion process.
/// </summary>
public sealed class ReaderFormatCacheService
{
    private const string CacheVersion = "v2";
    private readonly string _cacheDirectory;
    private readonly IBookFormatConverter _converter;
    private readonly SemaphoreSlim _conversionGate = new(1, 1);

    public ReaderFormatCacheService(AppPaths paths, IBookFormatConverter converter)
    {
        _cacheDirectory = Path.Combine(paths.ReaderCache, "format-conversions");
        _converter = converter;
    }

    public async Task<ReaderFormatCacheResult> PrepareEpubAsync(
        string sourcePath,
        string sourceHash,
        string sourceFormat,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = NormalizeHash(sourceHash);
        var format = sourceFormat.Trim().TrimStart('.').ToLowerInvariant();
        if (format is not ("azw3" or "mobi"))
            throw new NotSupportedException($"不支持为 {sourceFormat} 创建阅读缓存。");

        Directory.CreateDirectory(_cacheDirectory);
        var destination = Path.GetFullPath(Path.Combine(_cacheDirectory, $"{CacheVersion}-{format}-{cacheKey}.epub"));
        EnsureContainedPath(destination);
        if (IsUsable(destination))
            return new ReaderFormatCacheResult(destination, cacheKey, CacheHit: true);

        await _conversionGate.WaitAsync(cancellationToken);
        try
        {
            if (IsUsable(destination))
                return new ReaderFormatCacheResult(destination, cacheKey, CacheHit: true);

            if (File.Exists(destination)) File.Delete(destination);
            var temporary = Path.Combine(
                _cacheDirectory,
                $".{CacheVersion}-{format}-{cacheKey}-{Guid.NewGuid():N}.tmp.epub");
            EnsureContainedPath(temporary);
            try
            {
                await _converter.ConvertAsync(sourcePath, temporary, cancellationToken: cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, destination, overwrite: false);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch { }
            }

            return new ReaderFormatCacheResult(destination, cacheKey, CacheHit: false);
        }
        finally
        {
            _conversionGate.Release();
        }
    }

    private static string NormalizeHash(string hash)
    {
        var normalized = string.Concat(hash.Where(Uri.IsHexDigit)).ToLowerInvariant();
        if (normalized.Length != 64)
            throw new InvalidDataException("书籍校验值无效。");
        return normalized;
    }

    private static bool IsUsable(string path)
    {
        try { return new FileInfo(path) is { Exists: true, Length: > 0 }; }
        catch { return false; }
    }

    private void EnsureContainedPath(string path)
    {
        var root = Path.GetFullPath(_cacheDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("阅读转换缓存路径无效。");
    }
}
