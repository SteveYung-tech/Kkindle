using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Kkindle.Infrastructure;

public sealed partial class EpubFootnoteResolver
{
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
                var text = NormalizeText(element.Value);
                if (text.Length > 1200) text = text[..1200] + "…";
                if (text.Length > 0) result[target] = text;
            }
            catch (System.Xml.XmlException)
            {
                // Invalid XHTML remains readable, but unsafe regex-based cross-document extraction is skipped.
            }
        }
        return result;
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
