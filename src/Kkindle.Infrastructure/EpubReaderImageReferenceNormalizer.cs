using System.Xml.Linq;

namespace Kkindle.Infrastructure;

internal static class EpubReaderImageReferenceNormalizer
{
    private static readonly string[] LazySourceAttributes =
    [
        "data-src",
        "data-original",
        "data-lazy-src",
        "data-actualsrc",
        "data-url"
    ];

    private static readonly string[] LazySourceSetAttributes =
    [
        "data-srcset",
        "data-lazy-srcset",
        "data-original-set"
    ];

    internal static void NormalizeHtmlImageReferences(
        XElement element,
        string sourcePath,
        string cacheRoot)
    {
        var localName = element.Name.LocalName;
        if (localName.Equals("img", StringComparison.OrdinalIgnoreCase))
        {
            PromoteSafeLocalReference(element, "src", sourcePath, cacheRoot, LazySourceAttributes);
            var srcSet = NormalizeSrcSetAttribute(element, sourcePath, cacheRoot, LazySourceSetAttributes);
            if (string.IsNullOrWhiteSpace(srcSet))
                element.Attributes().FirstOrDefault(attribute =>
                    attribute.Name.LocalName.Equals("srcset", StringComparison.OrdinalIgnoreCase))?.Remove();
            else
                element.SetAttributeValue("srcset", srcSet);
            return;
        }

        if (localName.Equals("source", StringComparison.OrdinalIgnoreCase))
        {
            var srcSet = NormalizeSrcSetAttribute(element, sourcePath, cacheRoot, LazySourceSetAttributes);
            if (string.IsNullOrWhiteSpace(srcSet))
                element.Attributes().FirstOrDefault(attribute =>
                    attribute.Name.LocalName.Equals("srcset", StringComparison.OrdinalIgnoreCase))?.Remove();
            else
                element.SetAttributeValue("srcset", srcSet);
            return;
        }

        if (localName.Equals("image", StringComparison.OrdinalIgnoreCase))
            PromoteSafeLocalReference(element, "href", sourcePath, cacheRoot, LazySourceAttributes);
    }

    internal static IReadOnlyList<string> ExtractLocalImagePaths(string path)
    {
        try
        {
            var document = XDocument.Load(path, LoadOptions.PreserveWhitespace);
            var body = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "body");
            if (body is null) return [];

            var chapterDirectory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(chapterDirectory)) return [];
            var epubRoot = Path.GetFullPath(Path.Combine(chapterDirectory, ".."));
            var imagePaths = new List<string>();

            foreach (var element in body.Descendants())
            {
                foreach (var source in EnumerateImageReferences(element))
                {
                    var resolved = ResolveReaderRelativeResourcePath(epubRoot, chapterDirectory, source);
                    if (resolved is null || !File.Exists(resolved)) continue;
                    if (imagePaths.Contains(resolved, StringComparer.OrdinalIgnoreCase)) continue;
                    imagePaths.Add(resolved);
                }
            }

            return imagePaths;
        }
        catch
        {
            return [];
        }
    }

    internal static string? ResolveFirstLocalImagePath(XElement element, string path)
    {
        try
        {
            var chapterDirectory = Path.GetDirectoryName(path);
            if (string.IsNullOrWhiteSpace(chapterDirectory)) return null;
            var epubRoot = Path.GetFullPath(Path.Combine(chapterDirectory, ".."));

            foreach (var source in EnumerateImageReferences(element))
            {
                var resolved = ResolveReaderRelativeResourcePath(epubRoot, chapterDirectory, source);
                if (resolved is not null && File.Exists(resolved))
                    return resolved;
            }

            foreach (var child in element.Descendants())
            {
                foreach (var source in EnumerateImageReferences(child))
                {
                    var resolved = ResolveReaderRelativeResourcePath(epubRoot, chapterDirectory, source);
                    if (resolved is not null && File.Exists(resolved))
                        return resolved;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    internal static string? NormalizeSrcSetAttribute(
        XElement element,
        string sourcePath,
        string cacheRoot,
        params string[] fallbackAttributes)
    {
        var current = GetAttributeValue(element, "srcset");
        var sanitized = SanitizeSrcSet(current, sourcePath, cacheRoot);
        if (!string.IsNullOrWhiteSpace(sanitized))
        {
            RemoveAttributes(element, fallbackAttributes);
            return sanitized;
        }

        foreach (var fallbackAttribute in fallbackAttributes)
        {
            sanitized = SanitizeSrcSet(GetAttributeValue(element, fallbackAttribute), sourcePath, cacheRoot);
            if (string.IsNullOrWhiteSpace(sanitized)) continue;
            RemoveAttributes(element, fallbackAttributes);
            return sanitized;
        }

        return null;
    }

    private static void PromoteSafeLocalReference(
        XElement element,
        string targetAttributeName,
        string sourcePath,
        string cacheRoot,
        params string[] fallbackAttributes)
    {
        var current = GetAttributeValue(element, targetAttributeName);
        if (!string.IsNullOrWhiteSpace(current)
            && IsSafeLocalReference(current, sourcePath, cacheRoot))
        {
            return;
        }

        foreach (var fallbackAttribute in fallbackAttributes)
        {
            var value = GetAttributeValue(element, fallbackAttribute);
            if (!IsSafeLocalReference(value, sourcePath, cacheRoot)) continue;
            element.SetAttributeValue(targetAttributeName, value);
            RemoveAttributes(element, fallbackAttributes);
            return;
        }
    }

    private static void RemoveAttributes(XElement element, IEnumerable<string> attributeNames)
    {
        foreach (var attributeName in attributeNames)
        {
            element.Attributes()
                .FirstOrDefault(attribute =>
                    attribute.Name.LocalName.Equals(attributeName, StringComparison.OrdinalIgnoreCase))
                ?.Remove();
        }
    }

    private static IEnumerable<string> EnumerateImageReferences(XElement element)
    {
        var localName = element.Name.LocalName;
        if (!localName.Equals("img", StringComparison.OrdinalIgnoreCase)
            && !localName.Equals("source", StringComparison.OrdinalIgnoreCase)
            && !localName.Equals("image", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        foreach (var attributeName in new[]
                 {
                     "src",
                     "href",
                     "xlink:href",
                     "data-src",
                     "data-original",
                     "data-lazy-src",
                     "data-actualsrc",
                     "data-url"
                 })
        {
            var value = GetAttributeValue(element, attributeName);
            if (!string.IsNullOrWhiteSpace(value))
                yield return value;
        }

        foreach (var attributeName in new[] { "srcset", "data-srcset", "data-lazy-srcset", "data-original-set" })
        {
            var value = GetAttributeValue(element, attributeName);
            foreach (var candidate in EnumerateSrcSetCandidates(value))
                yield return candidate;
        }
    }

    private static IEnumerable<string> EnumerateSrcSetCandidates(string? srcSet)
    {
        if (string.IsNullOrWhiteSpace(srcSet)) yield break;

        foreach (var rawCandidate in srcSet.Split(','))
        {
            var candidate = rawCandidate.Trim();
            if (candidate.Length == 0) continue;

            var separator = candidate.IndexOfAny([' ', '\t', '\r', '\n', '\f']);
            yield return separator < 0 ? candidate : candidate[..separator];
        }
    }

    private static string? GetAttributeValue(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute =>
            attribute.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string SanitizeSrcSet(
        string? value,
        string sourcePath,
        string cacheRoot)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sanitized = new List<string>();
        foreach (var rawCandidate in value.Split(','))
        {
            var candidate = rawCandidate.Trim();
            if (candidate.Length == 0) continue;

            var separator = candidate.IndexOfAny([' ', '\t', '\r', '\n', '\f']);
            var url = separator < 0 ? candidate : candidate[..separator];
            var descriptors = separator < 0 ? string.Empty : candidate[separator..].Trim();
            if (!IsSafeLocalReference(url, sourcePath, cacheRoot)) continue;
            sanitized.Add(descriptors.Length == 0 ? url : $"{url} {descriptors}");
        }

        return string.Join(", ", sanitized);
    }

    internal static bool IsSafeLocalReference(string? value, string sourcePath, string cacheRoot)
    {
        var trimmed = value?.Trim() ?? string.Empty;
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

    private static string? ResolveReaderRelativeResourcePath(
        string epubRoot,
        string chapterDirectory,
        string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        source = source.Trim();
        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Uri.TryCreate(source, UriKind.RelativeOrAbsolute, out var uri)) return null;

        string candidatePath;
        if (uri.IsAbsoluteUri)
        {
            if (!uri.IsFile) return null;
            candidatePath = uri.LocalPath;
        }
        else
        {
            candidatePath = Path.GetFullPath(
                Path.Combine(chapterDirectory, Uri.UnescapeDataString(source)));
        }

        var root = Path.GetFullPath(epubRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(candidatePath);
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
    }

    private static void EnsureContainedPath(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EPUB 包含不安全的文件路径。");
    }
}
