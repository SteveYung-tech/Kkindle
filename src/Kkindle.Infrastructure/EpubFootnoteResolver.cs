using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Kkindle.Infrastructure;

public sealed partial class EpubFootnoteResolver
{
    public static string NormalizeTargetKey(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
            || !uri.IsFile
            || string.IsNullOrEmpty(uri.Fragment))
        {
            return target;
        }

        try
        {
            var fileUri = new Uri(Path.GetFullPath(uri.LocalPath)).AbsoluteUri;
            var fragment = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
            // EPUB paths are case-insensitive on Windows, while fragment IDs
            // remain case-sensitive. Keep the two parts separate accordingly.
            return $"{fileUri.ToLowerInvariant()}#{fragment}";
        }
        catch (UriFormatException)
        {
            return target;
        }
        catch (ArgumentException)
        {
            return target;
        }
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        string epubRoot,
        IEnumerable<string> absoluteTargets,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var documents = new Dictionary<string, XDocument>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in absoluteTargets.Distinct(StringComparer.Ordinal).Take(120))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || !uri.IsFile || string.IsNullOrEmpty(uri.Fragment))
                continue;

            var path = Path.GetFullPath(uri.LocalPath);
            if (!IsPathInside(epubRoot, path) || !File.Exists(path)) continue;
            try
            {
                if (!documents.TryGetValue(path, out var document))
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
                    document = await XDocument.LoadAsync(stream, LoadOptions.PreserveWhitespace, cancellationToken);
                    documents[path] = document;
                }

                var fragment = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
                var element = document.Descendants().FirstOrDefault(candidate => candidate.Attributes().Any(attribute =>
                    (attribute.Name.LocalName.Equals("id", StringComparison.OrdinalIgnoreCase)
                     || attribute.Name.LocalName.Equals("name", StringComparison.OrdinalIgnoreCase))
                     && attribute.Value.Equals(fragment, StringComparison.Ordinal)));
                if (element is null) continue;

                // In common EPUB markup the fragment ID is attached to the
                // backlink marker (<a id="note1n">[1]</a>), while the actual
                // footnote text is the rest of that paragraph. Resolve the
                // nearest containing block so the popup shows the note, not
                // just its marker.
                var contentElement = SelectFootnoteContentElement(element);
                var text = NormalizeText(contentElement.Value);
                if (text.Length > 1200) text = text[..1200] + "…";
                if (text.Length > 0) result[NormalizeTargetKey(target)] = text;
            }
            catch (System.Xml.XmlException)
            {
                // Invalid XHTML remains readable, but unsafe regex-based cross-document extraction is skipped.
            }
        }
        return result;
    }

    private static XElement SelectFootnoteContentElement(XElement target)
    {
        var targetText = NormalizeText(target.Value);
        var localName = target.Name.LocalName;
        var isInlineMarker = localName.Equals("a", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("area", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("sup", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("span", StringComparison.OrdinalIgnoreCase);
        if (!isInlineMarker) return target;

        for (var parent = target.Parent;
             parent is not null && !parent.Name.LocalName.Equals("body", StringComparison.OrdinalIgnoreCase);
             parent = parent.Parent)
        {
            if (!IsFootnoteBlock(parent.Name.LocalName)) continue;

            var parentText = NormalizeText(parent.Value);
            if (parentText.Length > targetText.Length + 1)
                return parent;
        }

        return target;
    }

    private static bool IsFootnoteBlock(string localName)
    {
        return localName.Equals("p", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("li", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("dd", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("dt", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("aside", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("blockquote", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("section", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("article", StringComparison.OrdinalIgnoreCase)
            || localName.Equals("div", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeText(string value)
    {
        var text = WhitespaceRegex().Replace(value, " ").Trim();
        return text.Trim('↩', '↵', '↑', ' ');
    }

    private static bool IsPathInside(string root, string path)
    {
        var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(boundary, StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
