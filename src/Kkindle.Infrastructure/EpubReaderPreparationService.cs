using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
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
    private const string ExtractionFormatVersion = "3";
    private const string ContentSecurityPolicyBase =
        "default-src 'none'; base-uri 'none'; object-src 'none'; frame-src 'none'; " +
        "connect-src 'none'; form-action 'none'; img-src 'self' file:; " +
        "font-src 'self' file:; style-src 'self' 'unsafe-inline' file:; " +
        "media-src 'none'; worker-src 'none'; frame-ancestors 'none';";
    private const string ReaderBridgeScript = """
        (() => {
          if (window.__kkindleReaderBridgeInstalled) return;
          window.__kkindleReaderBridgeInstalled = true;

          const send = value => {
            try {
              const body = JSON.stringify(value);
              const webview = window.chrome && window.chrome.webview;
              if (webview && typeof webview.postMessage === "function")
                webview.postMessage(body);
              else if (typeof window.invokeCSharpAction === "function")
                window.invokeCSharpAction(body);
            } catch (_) { }
          };

          const reportScroll = () => {
            const element = document.scrollingElement || document.documentElement;
            if (!element) return;
            send({
              type: "scroll",
              top: element.scrollTop || 0,
              left: element.scrollLeft || 0,
              scrollWidth: element.scrollWidth || 0,
              scrollHeight: element.scrollHeight || 0,
              clientWidth: element.clientWidth || 0,
              clientHeight: element.clientHeight || 0
            });
          };
          let scrollQueued = false;
          const queueScrollReport = () => {
            if (scrollQueued) return;
            scrollQueued = true;
            requestAnimationFrame(() => {
              scrollQueued = false;
              reportScroll();
            });
          };

          const reportSelection = () => {
            try {
              const selection = window.getSelection();
              if (!selection || selection.rangeCount === 0 || selection.isCollapsed || !document.body) {
                send({ type: "selection", text: "" });
                return;
              }
              const range = selection.getRangeAt(0);
              if (!document.body.contains(range.commonAncestorContainer)) return;
              const pointOffset = (container, offset) => {
                const before = document.createRange();
                before.selectNodeContents(document.body);
                before.setEnd(container, offset);
                return (before.cloneContents().textContent || "").length;
              };
              const rawText = selection.toString() || "";
              const leading = rawText.length - rawText.trimStart().length;
              const trailing = rawText.length - rawText.trimEnd().length;
              const text = rawText.trim();
              const startOffset = pointOffset(range.startContainer, range.startOffset) + leading;
              const endOffset = pointOffset(range.endContainer, range.endOffset) - trailing;
              const fullText = document.body.textContent || "";
              send({
                type: "selection",
                text: text.slice(0, 12000),
                startOffset,
                endOffset,
                prefix: fullText.slice(Math.max(0, startOffset - 72), startOffset),
                suffix: fullText.slice(endOffset, Math.min(fullText.length, endOffset + 72))
              });
            } catch (_) { }
          };

          document.addEventListener("click", event => {
            try {
              const element = event.target instanceof Element
                ? event.target.closest("a")
                : null;
              if (element && element.href) {
                event.preventDefault();
                send({ type: "link", href: element.href, target: element.target || "" });
              }
            } catch (_) { }
          }, true);
          document.addEventListener("mouseup", reportSelection, true);
          document.addEventListener("keyup", event => {
            if (["ArrowLeft", "ArrowRight", "PageUp", "PageDown"].includes(event.key))
              send({ type: "key", key: event.key });
          }, true);
          document.addEventListener("scroll", queueScrollReport, { passive: true });
          window.addEventListener("resize", () => {
            send({ type: "resize" });
            queueScrollReport();
          }, { passive: true });

          const ready = () => {
            send({ type: "ready" });
            queueScrollReport();
          };
          if (document.readyState === "loading")
            document.addEventListener("DOMContentLoaded", ready, { once: true });
          else
            ready();
        })();
        """;
    private static readonly Regex CssUrlPattern = new(
        """url\s*\(\s*(?<quote>['"]?)(?<value>[^)'"]+)\k<quote>\s*\)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex CssImportPattern = new(
        "@import\\s+[^;]+;?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
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
        var extractionReady = await IsExtractionReadyAsync(
            extractionReadyPath,
            cacheKey,
            cancellationToken);
        if (!extractionReady)
        {
            if (!File.Exists(extractionReadyPath))
                await ExtractSafelyAsync(epubPath, cacheRoot, cancellationToken);

            await SanitizeExtractedResourcesAsync(cacheRoot, cancellationToken);
            await File.WriteAllTextAsync(
                extractionReadyPath,
                $"{cacheKey}\n{ExtractionFormatVersion}",
                Encoding.UTF8,
                cancellationToken);
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
        var settings = new XmlReaderSettings
        {
            Async = true,
            // Standard EPUB XHTML commonly carries a DOCTYPE. Ignore it
            // without resolving entities; the null resolver keeps external
            // DTDs and entities out of the reader process.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null
        };
        using var reader = XmlReader.Create(stream, settings);
        return await XDocument.LoadAsync(reader, LoadOptions.PreserveWhitespace, cancellationToken);
    }

    private static async Task<bool> IsExtractionReadyAsync(
        string markerPath,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(markerPath)) return false;
        var marker = await File.ReadAllTextAsync(markerPath, cancellationToken);
        return string.Equals(
            marker.Trim(),
            $"{cacheKey}\n{ExtractionFormatVersion}",
            StringComparison.OrdinalIgnoreCase);
    }

    private static async Task SanitizeExtractedResourcesAsync(
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var htmlFiles = Directory.EnumerateFiles(cacheRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path).Equals(".xhtml", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(path).Equals(".htm", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var path in htmlFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SanitizeHtmlFileAsync(path, cacheRoot, cancellationToken);
        }

        var cssFiles = Directory.EnumerateFiles(cacheRoot, "*.css", SearchOption.AllDirectories).ToArray();
        foreach (var path in cssFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await SanitizeCssFileAsync(path, cacheRoot, cancellationToken);
        }
    }

    private static async Task SanitizeHtmlFileAsync(
        string path,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var document = await LoadXmlAsync(path, cancellationToken);
        var root = document.Root ?? throw new InvalidDataException("EPUB HTML 缺少根元素。");
        var namespaceName = root.Name.Namespace;
        var elements = root.DescendantsAndSelf().ToArray();
        foreach (var element in elements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localName = element.Name.LocalName;
            if (localName is "script" or "object" or "iframe" or "frame" or "embed" or "applet" or "base")
            {
                element.Remove();
                continue;
            }

            if (localName == "meta"
                && string.Equals(
                    element.Attribute("http-equiv")?.Value,
                    "refresh",
                    StringComparison.OrdinalIgnoreCase))
            {
                element.Remove();
                continue;
            }

            foreach (var attribute in element.Attributes().ToArray())
            {
                var attributeName = attribute.Name.LocalName;
                if (attributeName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                    || attributeName is "srcset" or "background")
                {
                    attribute.Remove();
                    continue;
                }

                if (attributeName is "src" or "href" or "action" or "poster" or "data"
                    or "cite" or "formaction" or "xlink:href")
                {
                    if (!IsSafeLocalReference(attribute.Value, path, cacheRoot))
                        attribute.Remove();
                }
                else if (attributeName == "style")
                {
                    var css = SanitizeCss(attribute.Value, path, cacheRoot);
                    if (string.IsNullOrWhiteSpace(css)) attribute.Remove();
                    else attribute.Value = css;
                }
            }

            var styleText = element.Name.LocalName == "style" ? element.Value : null;
            if (styleText is not null)
                element.Value = SanitizeCss(styleText, path, cacheRoot);
        }

        var head = root.Elements().FirstOrDefault(element => element.Name.LocalName == "head");
        if (head is null)
        {
            head = new XElement(namespaceName + "head");
            root.AddFirst(head);
        }

        head.Elements()
            .Where(element => element.Name.LocalName == "meta"
                && string.Equals(
                    element.Attribute("http-equiv")?.Value,
                    "Content-Security-Policy",
                    StringComparison.OrdinalIgnoreCase))
            .Remove();

        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var policy = $"{ContentSecurityPolicyBase} script-src 'nonce-{nonce}';";
        head.AddFirst(
            new XElement(
                namespaceName + "meta",
                new XAttribute("http-equiv", "Content-Security-Policy"),
                new XAttribute("content", policy)));
        head.Add(
            new XElement(
                namespaceName + "script",
                new XAttribute("nonce", nonce),
                new XCData(ReaderBridgeScript)));

        await WriteXmlAsync(document, path, cancellationToken);
    }

    private static async Task SanitizeCssFileAsync(
        string path,
        string cacheRoot,
        CancellationToken cancellationToken)
    {
        var css = await File.ReadAllTextAsync(path, cancellationToken);
        var sanitized = SanitizeCss(css, path, cacheRoot);
        if (!string.Equals(css, sanitized, StringComparison.Ordinal))
            await File.WriteAllTextAsync(path, sanitized, Encoding.UTF8, cancellationToken);
    }

    private static string SanitizeCss(string css, string sourcePath, string cacheRoot)
    {
        var sanitized = CssImportPattern.Replace(css, string.Empty);
        return CssUrlPattern.Replace(sanitized, match =>
        {
            var value = match.Groups["value"].Value.Trim();
            return IsSafeLocalReference(value, sourcePath, cacheRoot) ? match.Value : string.Empty;
        });
    }

    private static bool IsSafeLocalReference(string value, string sourcePath, string cacheRoot)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#')) return true;
        if (trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("//", StringComparison.Ordinal)) return false;

        if (!Uri.TryCreate(new Uri(sourcePath), trimmed, out var resolved) || !resolved.IsFile)
            return false;
        try
        {
            EnsureContainedPath(cacheRoot, resolved.LocalPath);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static async Task WriteXmlAsync(
        XDocument document,
        string path,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        using (var writer = new Utf8StringWriter(builder))
            document.Save(writer, SaveOptions.DisableFormatting);
        await File.WriteAllTextAsync(path, builder.ToString(), Encoding.UTF8, cancellationToken);
    }

    private sealed class Utf8StringWriter(StringBuilder builder) : StringWriter(builder, System.Globalization.CultureInfo.InvariantCulture)
    {
        public override Encoding Encoding => Encoding.UTF8;
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
