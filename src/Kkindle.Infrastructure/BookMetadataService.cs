using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class BookMetadataService : IMetadataService
{
    public async Task<BookMetadata> ReadMetadataAsync(string path, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension == ".epub"
            ? await ReadEpubAsync(path, cancellationToken)
            : await ReadFallbackAsync(path, extension, cancellationToken);
    }

    private static async Task<BookMetadata> ReadFallbackAsync(
        string path,
        string extension,
        CancellationToken cancellationToken)
    {
        var title = CleanFileTitle(Path.GetFileNameWithoutExtension(path));
        byte[]? coverBytes = null;
        if (extension is ".mobi" or ".azw" or ".azw3" or ".kfx")
            coverBytes = await ReadLargestEmbeddedJpegAsync(path, cancellationToken);
        return new BookMetadata
        {
            Title = title,
            Authors = "未知作者",
            CoverBytes = coverBytes,
            CoverExtension = ".jpg"
        };
    }

    private static async Task<BookMetadata> ReadEpubAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var container = archive.GetEntry("META-INF/container.xml");
            if (container is null) return await ReadFallbackAsync(path, ".epub", cancellationToken);

            await using var containerStream = container.Open();
            var containerXml = await XDocument.LoadAsync(containerStream, LoadOptions.None, cancellationToken);
            var rootFile = containerXml.Descendants().FirstOrDefault(x => x.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
            if (string.IsNullOrWhiteSpace(rootFile)) return await ReadFallbackAsync(path, ".epub", cancellationToken);

            var opfEntry = archive.GetEntry(rootFile.Replace('\\', '/'));
            if (opfEntry is null) return await ReadFallbackAsync(path, ".epub", cancellationToken);
            await using var opfStream = opfEntry.Open();
            var opf = await XDocument.LoadAsync(opfStream, LoadOptions.None, cancellationToken);
            var metadata = opf.Descendants().FirstOrDefault(x => x.Name.LocalName == "metadata");
            if (metadata is null) return await ReadFallbackAsync(path, ".epub", cancellationToken);

            var title = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "title")?.Value?.Trim();
            var creators = metadata.Elements().Where(x => x.Name.LocalName == "creator").Select(x => x.Value.Trim()).Where(x => x.Length > 0).ToList();
            var description = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "description")?.Value?.Trim();
            var series = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "meta" && string.Equals((string?)x.Attribute("name"), "calibre:series", StringComparison.OrdinalIgnoreCase))?.Attribute("content")?.Value;
            var seriesIndexText = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "meta" && string.Equals((string?)x.Attribute("name"), "calibre:series_index", StringComparison.OrdinalIgnoreCase))?.Attribute("content")?.Value;
            _ = double.TryParse(seriesIndexText, out var seriesIndex);

            var manifest = opf.Descendants().FirstOrDefault(x => x.Name.LocalName == "manifest")?.Elements()
                .Where(x => x.Name.LocalName == "item")
                .Select(x => new ManifestItem(
                    (string?)x.Attribute("id") ?? string.Empty,
                    (string?)x.Attribute("href") ?? string.Empty,
                    (string?)x.Attribute("media-type") ?? string.Empty,
                    (string?)x.Attribute("properties") ?? string.Empty))
                .ToList() ?? [];

            var coverId = metadata.Elements().FirstOrDefault(x => x.Name.LocalName == "meta" && string.Equals((string?)x.Attribute("name"), "cover", StringComparison.OrdinalIgnoreCase))?.Attribute("content")?.Value;
            var coverItem = manifest.FirstOrDefault(x => x.Properties.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("cover-image", StringComparer.OrdinalIgnoreCase))
                ?? manifest.FirstOrDefault(x => x.Id == coverId)
                ?? manifest.FirstOrDefault(x => x.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase));

            byte[]? coverBytes = null;
            var coverExtension = ".jpg";
            if (coverItem is not null)
            {
                var baseDirectory = Path.GetDirectoryName(rootFile)?.Replace('\\', '/') ?? string.Empty;
                var coverPath = CombineZipPath(baseDirectory, Uri.UnescapeDataString(coverItem.Href));
                var coverEntry = archive.GetEntry(coverPath);
                if (coverEntry is not null)
                {
                    await using var coverStream = coverEntry.Open();
                    await using var buffer = new MemoryStream();
                    await coverStream.CopyToAsync(buffer, cancellationToken);
                    coverBytes = buffer.ToArray();
                    coverExtension = coverItem.MediaType switch
                    {
                        "image/png" => ".png",
                        "image/webp" => ".webp",
                        _ => ".jpg"
                    };
                }
            }

            return new BookMetadata
            {
                Title = title,
                Authors = creators.Count == 0 ? "未知作者" : string.Join(", ", creators),
                Series = string.IsNullOrWhiteSpace(series) ? null : series,
                SeriesIndex = double.TryParse(seriesIndexText, out _) ? seriesIndex : null,
                Description = description,
                CoverBytes = coverBytes,
                CoverExtension = coverExtension
            };
        }
        catch (InvalidDataException)
        {
            return await ReadFallbackAsync(path, ".epub", cancellationToken);
        }
        catch (XmlException)
        {
            return await ReadFallbackAsync(path, ".epub", cancellationToken);
        }
    }

    private static string CleanFileTitle(string fileName)
    {
        var title = Regex.Replace(fileName, @"_?[0-9A-F]{32}$", string.Empty, RegexOptions.IgnoreCase);
        title = Regex.Replace(title, @"\s*\([^)]*(?:z-library|z-lib|1lib)[^)]*\)\s*", " ", RegexOptions.IgnoreCase);
        return title.Replace('_', ' ').Trim();
    }

    private static async Task<byte[]?> ReadLargestEmbeddedJpegAsync(
        string path,
        CancellationToken cancellationToken)
    {
        const long maximumContainerSize = 128L * 1024 * 1024;
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= 0 || file.Length > maximumContainerSize) return null;

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        byte[]? largest = null;
        for (var index = 0; index < bytes.Length - 3; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes[index] != 0xFF || bytes[index + 1] != 0xD8 || bytes[index + 2] != 0xFF) continue;

            for (var end = index + 3; end < bytes.Length - 1; end++)
            {
                if (bytes[end] != 0xFF || bytes[end + 1] != 0xD9) continue;
                var length = end + 2 - index;
                if (length >= 8 * 1024 && (largest is null || length > largest.Length))
                {
                    largest = new byte[length];
                    Buffer.BlockCopy(bytes, index, largest, 0, length);
                }
                index = end + 1;
                break;
            }
        }
        return largest;
    }

    private static string CombineZipPath(string directory, string relative)
    {
        var combined = string.IsNullOrEmpty(directory) ? relative : $"{directory.TrimEnd('/')}/{relative.TrimStart('/')}";
        var parts = new List<string>();
        foreach (var part in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == ".." && parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            else if (part != "..") parts.Add(part);
        }
        return string.Join('/', parts);
    }

    private sealed record ManifestItem(string Id, string Href, string MediaType, string Properties);
}
