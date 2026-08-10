using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public static partial class KindleClippingsParser
{
    public static IReadOnlyList<KindleClipping> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var normalized = text.ReplaceLineEndings("\n");
        var result = new List<KindleClipping>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var value in DelimiterPattern().Split(normalized))
        {
            var raw = value.Trim('\n', '\r', ' ', '\t');
            if (raw.Length == 0) continue;
            var lines = raw.Split('\n');
            var heading = lines[0].Trim();
            if (heading.Length == 0) continue;
            var metadataIndex = Array.FindIndex(lines, 1, line => line.TrimStart().StartsWith('-'));
            var metadata = metadataIndex >= 0 ? lines[metadataIndex].Trim() : string.Empty;
            var content = metadataIndex >= 0
                ? string.Join("\n", lines.Skip(metadataIndex + 1)).Trim()
                : string.Join("\n", lines.Skip(1)).Trim();
            var (title, author) = ParseHeading(heading);
            var occurrence = occurrences.TryGetValue(raw, out var count) ? count + 1 : 1;
            occurrences[raw] = occurrence;
            result.Add(new KindleClipping
            {
                Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{raw}\n#{occurrence}"))).ToLowerInvariant(),
                BookTitle = title,
                Author = author,
                Type = ParseType(metadata),
                Metadata = metadata,
                Content = content,
                RawBlock = raw
            });
        }
        return result;
    }

    public static string BuildDocument(IEnumerable<KindleClipping> clippings)
    {
        var blocks = clippings.Select(item => item.RawBlock.Trim().ReplaceLineEndings("\r\n")).Where(value => value.Length > 0).ToArray();
        return blocks.Length == 0 ? string.Empty : string.Join("\r\n==========\r\n", blocks) + "\r\n==========\r\n";
    }

    private static (string Title, string Author) ParseHeading(string heading)
    {
        if (heading.EndsWith('）'))
        {
            var fullWidthOpening = heading.LastIndexOf('（');
            if (fullWidthOpening > 0)
                return (heading[..fullWidthOpening].Trim(), heading[(fullWidthOpening + 1)..^1].Trim());
        }
        if (!heading.EndsWith(')')) return (heading, string.Empty);
        var opening = heading.LastIndexOf(" (", StringComparison.Ordinal);
        if (opening <= 0) return (heading, string.Empty);
        return (heading[..opening].Trim(), heading[(opening + 2)..^1].Trim());
    }

    private static KindleClippingType ParseType(string metadata)
    {
        if (ContainsAny(metadata, "highlight", "划线", "标注")) return KindleClippingType.Highlight;
        if (ContainsAny(metadata, "note", "笔记")) return KindleClippingType.Note;
        if (ContainsAny(metadata, "bookmark", "书签")) return KindleClippingType.Bookmark;
        return KindleClippingType.Unknown;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex(@"(?m)^\s*={10}\s*$")]
    private static partial Regex DelimiterPattern();
}
