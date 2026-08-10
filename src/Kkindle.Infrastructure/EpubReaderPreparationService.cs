using System.IO.Compression;
using System.Xml.Linq;

namespace Kkindle.Infrastructure;

public sealed record EpubReaderNavigationItem(string Title, string Target, int ChapterIndex);

public sealed record EpubReaderDocument(
    string RootPath,
    IReadOnlyList<string> Chapters,
    IReadOnlyList<EpubReaderNavigationItem> Navigation);

public sealed class EpubReaderPreparationService
{
    private const string ExtractionReadyFileName = ".kkindle-extracted";
    private readonly AppPaths _paths;

    public EpubReaderPreparationService(AppPaths paths)
    {
        _paths = paths;
    }

    public async Task<EpubReaderDocument> PrepareAsync(
        string epubPath,
        string sha256,
        CancellationToken cancellationToken = default)
    {
        var cacheKey = string.Concat(sha256.Where(Uri.IsHexDigit)).ToLowerInvariant();
        if (cacheKey.Length != 64)
            throw new InvalidDataException("书籍校验值无效。");

        var cacheRoot = Path.GetFullPath(Path.Combine(_paths.ReaderCache, cacheKey));
        EnsureContainedPath(_paths.ReaderCache, cacheRoot);
        Directory.CreateDirectory(cacheRoot);

        var extractionReadyPath = Path.Combine(cacheRoot, ExtractionReadyFileName);
        if (!File.Exists(extractionReadyPath))
        {
            await ExtractSafelyAsync(epubPath, cacheRoot, cancellationToken);
            await File.WriteAllTextAsync(extractionReadyPath, cacheKey, cancellationToken);
        }

        var containerPath = Path.Combine(cacheRoot, "META-INF", "container.xml");
        if (!File.Exists(containerPath))
            throw new InvalidDataException("EPUB 缺少 META-INF/container.xml。");

        var container = await LoadXmlAsync(containerPath, cancellationToken);
        var packageRelativePath = container
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "rootfile")?
            .Attribute("full-path")?.Value;
        if (string.IsNullOrWhiteSpace(packageRelativePath))
            throw new InvalidDataException("EPUB 没有声明内容清单。");

        var packagePath = ResolveContainedPath(cacheRoot, packageRelativePath);
        if (!File.Exists(packagePath))
            throw new InvalidDataException("EPUB 内容清单不存在。");

        var package = await LoadXmlAsync(packagePath, cancellationToken);
        var manifest = package.Descendants()
            .Where(element => element.Name.LocalName == "item")
            .Select(element => new ManifestItem(
                element.Attribute("id")?.Value,
                element.Attribute("href")?.Value,
                element.Attribute("media-type")?.Value,
                element.Attribute("properties")?.Value))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Href))
            .ToDictionary(item => item.Id!, item => item, StringComparer.Ordinal);

        var packageDirectory = Path.GetDirectoryName(packagePath)!;
        var chapters = new List<string>();
        foreach (var itemRef in package.Descendants().Where(element => element.Name.LocalName == "itemref"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var idRef = itemRef.Attribute("idref")?.Value;
            if (idRef is null || !manifest.TryGetValue(idRef, out var item)) continue;
            if (item.MediaType is not ("application/xhtml+xml" or "text/html")) continue;

            var href = Uri.UnescapeDataString(item.Href!.Split('#')[0]);
            var chapterPath = ResolveContainedPath(packageDirectory, href);
            EnsureContainedPath(cacheRoot, chapterPath);
            if (File.Exists(chapterPath)) chapters.Add(chapterPath);
        }

        if (chapters.Count == 0)
            throw new InvalidDataException("EPUB 没有可阅读的章节。");

        var navigation = await ReadNavigationAsync(
            package,
            manifest,
            packageDirectory,
            cacheRoot,
            chapters,
            cancellationToken);
        if (navigation.Count == 0)
        {
            navigation = chapters
                .Select((chapter, index) => new EpubReaderNavigationItem(
                    $"第 {index + 1} 章",
                    new Uri(chapter).AbsoluteUri,
                    index))
                .ToList();
        }

        return new EpubReaderDocument(cacheRoot, chapters, navigation);
    }

    private static async Task<List<EpubReaderNavigationItem>> ReadNavigationAsync(
        XDocument package,
        IReadOnlyDictionary<string, ManifestItem> manifest,
        string packageDirectory,
        string cacheRoot,
        IReadOnlyList<string> chapters,
        CancellationToken cancellationToken)
    {
        var navItem = manifest.Values.FirstOrDefault(item =>
            item.Properties?.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("nav") == true);
        if (navItem is not null)
        {
            var navPath = ResolveContainedPath(packageDirectory, Uri.UnescapeDataString(navItem.Href!.Split('#')[0]));
            EnsureContainedPath(cacheRoot, navPath);
            if (File.Exists(navPath))
            {
                var navDocument = await LoadXmlAsync(navPath, cancellationToken);
                var toc = navDocument.Descendants().FirstOrDefault(element =>
                    element.Name.LocalName == "nav"
                    && element.Attributes().Any(attribute =>
                        attribute.Name.LocalName == "type" && attribute.Value.Split(' ').Contains("toc")))
                    ?? navDocument.Descendants().FirstOrDefault(element => element.Name.LocalName == "nav");
                if (toc is not null)
                {
                    var result = CreateNavigationItems(
                        toc.Descendants().Where(element => element.Name.LocalName == "a")
                            .Select(element => (Title: NormalizeTitle(element.Value), Href: element.Attribute("href")?.Value)),
                        navPath,
                        cacheRoot,
                        chapters);
                    if (result.Count > 0) return result;
                }
            }
        }

        var spineTocId = package.Descendants().FirstOrDefault(element => element.Name.LocalName == "spine")?
            .Attribute("toc")?.Value;
        if (spineTocId is null || !manifest.TryGetValue(spineTocId, out var ncxItem)) return [];

        var ncxPath = ResolveContainedPath(packageDirectory, Uri.UnescapeDataString(ncxItem.Href!.Split('#')[0]));
        EnsureContainedPath(cacheRoot, ncxPath);
        if (!File.Exists(ncxPath)) return [];

        var ncx = await LoadXmlAsync(ncxPath, cancellationToken);
        return CreateNavigationItems(
            ncx.Descendants().Where(element => element.Name.LocalName == "navPoint")
                .Select(element =>
                {
                    var title = element.Descendants().FirstOrDefault(descendant => descendant.Name.LocalName == "navLabel")?
                        .Descendants().FirstOrDefault(descendant => descendant.Name.LocalName == "text")?.Value;
                    var href = element.Elements().FirstOrDefault(child => child.Name.LocalName == "content")?
                        .Attribute("src")?.Value;
                    return (Title: NormalizeTitle(title), Href: href);
                }),
            ncxPath,
            cacheRoot,
            chapters);
    }

    private static List<EpubReaderNavigationItem> CreateNavigationItems(
        IEnumerable<(string Title, string? Href)> source,
        string navigationDocumentPath,
        string cacheRoot,
        IReadOnlyList<string> chapters)
    {
        var result = new List<EpubReaderNavigationItem>();
        foreach (var (title, href) in source)
        {
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(href)) continue;
            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute) && !absolute.IsFile) continue;

            var parts = href.Split('#', 2);
            var targetPath = parts[0].Length == 0
                ? navigationDocumentPath
                : ResolveContainedPath(Path.GetDirectoryName(navigationDocumentPath)!, Uri.UnescapeDataString(parts[0]));
            EnsureContainedPath(cacheRoot, targetPath);
            var chapterIndex = chapters.ToList().FindIndex(chapter =>
                Path.GetFullPath(chapter).Equals(Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase));
            if (chapterIndex < 0 || !File.Exists(targetPath)) continue;

            var target = new Uri(targetPath).AbsoluteUri;
            if (parts.Length == 2 && parts[1].Length > 0) target += $"#{parts[1]}";
            result.Add(new EpubReaderNavigationItem(title, target, chapterIndex));
        }
        return result;
    }

    private static string NormalizeTitle(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed record ManifestItem(string? Id, string? Href, string? MediaType, string? Properties);

    private static async Task ExtractSafelyAsync(
        string epubPath,
        string destinationRoot,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(epubPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.FullName)) continue;

            var destination = ResolveContainedPath(destinationRoot, entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var source = entry.Open();
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
            await source.CopyToAsync(output, cancellationToken);
        }
    }

    private static async Task<XDocument> LoadXmlAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.GetFullPath(Path.Combine(root, normalized));
        EnsureContainedPath(root, fullPath);
        return fullPath;
    }

    private static void EnsureContainedPath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EPUB 包含不安全的文件路径。");
    }
}
