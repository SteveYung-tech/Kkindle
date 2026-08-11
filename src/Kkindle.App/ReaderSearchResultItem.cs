using System.Text.RegularExpressions;
using Kkindle.Core;

namespace Kkindle;

public sealed class ReaderSearchResultItem
{
    public ReaderSearchResultItem(BookContentChunk source, string query)
    {
        Source = source;
        Query = query;
        Title = source.ChapterTitle;
        Snippet = CreateSnippet(source.Content, query);
    }

    public BookContentChunk Source { get; }

    public string Query { get; }

    public string Title { get; }

    public string Snippet { get; }

    private static string CreateSnippet(string content, string query)
    {
        var normalized = Regex.Replace(content ?? string.Empty, @"\s+", " ").Trim();
        if (normalized.Length == 0) return string.Empty;

        const int maximumLength = 150;
        var match = normalized.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        if (match < 0)
        {
            match = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(term => normalized.IndexOf(term, StringComparison.CurrentCultureIgnoreCase))
                .Where(index => index >= 0)
                .DefaultIfEmpty(0)
                .Min();
        }

        var start = Math.Max(0, match - 45);
        if (start + maximumLength > normalized.Length)
            start = Math.Max(0, normalized.Length - maximumLength);
        var length = Math.Min(maximumLength, normalized.Length - start);
        var snippet = normalized.Substring(start, length);
        return (start > 0 ? "…" : string.Empty)
            + snippet
            + (start + length < normalized.Length ? "…" : string.Empty);
    }
}
