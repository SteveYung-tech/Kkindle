using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.Core;
using System.Text.RegularExpressions;

namespace Kkindle;

public sealed record ReaderSearchHighlightRange(int Start, int Length);

public sealed class ReaderSearchResultViewModel
{
    public ReaderSearchResultViewModel(
        string title,
        string excerpt,
        int chapterIndex,
        string chapterPath,
        string? target = null,
        int? pageNumber = null,
        string? query = null)
    {
        Title = title;
        var presentation = string.IsNullOrWhiteSpace(query)
            ? (excerpt, (IReadOnlyList<ReaderSearchHighlightRange>)Array.Empty<ReaderSearchHighlightRange>())
            : ReaderSearchPresentation.CreateSnippet(excerpt, query);
        Excerpt = presentation.Item1;
        ChapterIndex = chapterIndex;
        ChapterPath = chapterPath;
        Target = target;
        PageNumber = pageNumber;
        Query = query;
        ExcerptHighlightRanges = presentation.Item2;
    }

    public ReaderSearchResultViewModel(
        BookContentChunk source,
        string query,
        string? target = null)
    {
        var presentation = ReaderSearchPresentation.CreateSnippet(source.Content, query);
        Title = source.ChapterTitle;
        Excerpt = presentation.Snippet;
        ChapterIndex = source.ChapterIndex;
        ChapterPath = source.ChapterPath;
        Target = target;
        Query = query;
        Source = source;
        ExcerptHighlightRanges = presentation.HighlightRanges;
    }

    public string Title { get; }
    public string Excerpt { get; }
    public int ChapterIndex { get; }
    public string ChapterPath { get; }
    public string? Target { get; }
    public int? PageNumber { get; }
    public string? Query { get; }
    public BookContentChunk? Source { get; }
    public IReadOnlyList<ReaderSearchHighlightRange> ExcerptHighlightRanges { get; }
}

internal static class ReaderSearchPresentation
{
    public static (string Snippet, IReadOnlyList<ReaderSearchHighlightRange> HighlightRanges) CreateSnippet(
        string? content,
        string? query)
    {
        var normalized = Regex.Replace(content ?? string.Empty, @"\s+", " ").Trim();
        var normalizedQuery = Regex.Replace(query?.Trim() ?? string.Empty, @"\s+", " ").Trim();
        if (normalized.Length == 0 || normalizedQuery.Length == 0)
            return (string.Empty, Array.Empty<ReaderSearchHighlightRange>());

        const int maximumLength = 150;
        var match = normalized.IndexOf(normalizedQuery, StringComparison.CurrentCultureIgnoreCase);
        var runs = normalizedQuery.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var useRuns = match < 0 && runs.Length > 0;
        if (useRuns)
        {
            match = runs
                .Select(term => normalized.IndexOf(term, StringComparison.CurrentCultureIgnoreCase))
                .Where(index => index >= 0)
                .DefaultIfEmpty(0)
                .Min();
        }
        if (match < 0) match = 0;

        var start = Math.Max(0, match - 45);
        if (start + maximumLength > normalized.Length)
            start = Math.Max(0, normalized.Length - maximumLength);
        var length = Math.Min(maximumLength, normalized.Length - start);
        var snippet = normalized.Substring(start, length);
        var prefix = start > 0 ? "…" : string.Empty;
        var suffix = start + length < normalized.Length ? "…" : string.Empty;
        var display = prefix + snippet + suffix;

        var rawRanges = new List<ReaderSearchHighlightRange>();
        if (useRuns)
        {
            foreach (var run in runs.Distinct(StringComparer.CurrentCultureIgnoreCase))
                AddSnippetOccurrences(rawRanges, normalized, run, start, length);
        }
        else
        {
            AddSnippetOccurrences(rawRanges, normalized, normalizedQuery, start, length);
        }

        // A query longer than the visible 150-character window still needs a
        // visible highlight. This is also the fallback for a title-only hit
        // where the query is not present in the body chunk itself.
        if (rawRanges.Count == 0 && match >= 0)
        {
            var visibleStart = Math.Max(match, start);
            var visibleEnd = Math.Min(match + normalizedQuery.Length, start + length);
            if (visibleEnd > visibleStart)
                rawRanges.Add(new ReaderSearchHighlightRange(
                    visibleStart - start,
                    visibleEnd - visibleStart));
        }

        var offset = prefix.Length;
        var ranges = MergeRanges(rawRanges.Select(range => new ReaderSearchHighlightRange(
            range.Start + offset,
            range.Length)));
        return (display, ranges);
    }

    public static IReadOnlyList<ReaderSearchHighlightRange> FindTermOccurrences(
        string? text,
        string? query)
    {
        var value = text ?? string.Empty;
        var normalizedQuery = Regex.Replace(query?.Trim() ?? string.Empty, @"\s+", " ").Trim();
        if (value.Length == 0 || normalizedQuery.Length == 0)
            return Array.Empty<ReaderSearchHighlightRange>();

        var ranges = new List<ReaderSearchHighlightRange>();
        foreach (var term in normalizedQuery.Split(
                     ' ',
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                 .Distinct(StringComparer.CurrentCultureIgnoreCase))
        {
            AddOccurrences(ranges, value, term);
        }
        return MergeRanges(ranges);
    }

    private static void AddSnippetOccurrences(
        List<ReaderSearchHighlightRange> ranges,
        string normalized,
        string term,
        int windowStart,
        int windowLength)
    {
        if (term.Length == 0) return;
        var windowEnd = windowStart + windowLength;
        var searchStart = windowStart;
        while (searchStart < windowEnd)
        {
            var index = normalized.IndexOf(
                term,
                searchStart,
                StringComparison.CurrentCultureIgnoreCase);
            if (index < 0 || index >= windowEnd) break;
            var visibleEnd = Math.Min(index + term.Length, windowEnd);
            if (visibleEnd > index)
                ranges.Add(new ReaderSearchHighlightRange(
                    index - windowStart,
                    visibleEnd - index));
            searchStart = index + Math.Max(1, term.Length);
        }
    }

    private static void AddOccurrences(
        List<ReaderSearchHighlightRange> ranges,
        string text,
        string term)
    {
        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(term, start, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0) break;
            ranges.Add(new ReaderSearchHighlightRange(index, term.Length));
            start = index + Math.Max(1, term.Length);
        }
    }

    private static IReadOnlyList<ReaderSearchHighlightRange> MergeRanges(
        IEnumerable<ReaderSearchHighlightRange> ranges)
    {
        var merged = new List<ReaderSearchHighlightRange>();
        foreach (var range in ranges
                     .Where(item => item.Start >= 0 && item.Length > 0)
                     .OrderBy(item => item.Start))
        {
            if (merged.Count > 0
                && range.Start <= merged[^1].Start + merged[^1].Length)
            {
                var previous = merged[^1];
                merged[^1] = new ReaderSearchHighlightRange(
                    previous.Start,
                    Math.Max(
                        previous.Start + previous.Length,
                        range.Start + range.Length) - previous.Start);
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }
}

/// <summary>
/// A TextBlock that paints the query terms of a whole-book search result in
/// black-on-white inverse text, mirroring the WinUI reference's
/// TextHighlighters on the result title and snippet.
/// </summary>
public sealed class ReaderSearchHighlightTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<ReaderSearchHighlightTextBlock, string?>(nameof(Source));

    public static readonly StyledProperty<string?> QueryProperty =
        AvaloniaProperty.Register<ReaderSearchHighlightTextBlock, string?>(nameof(Query));

    public static readonly StyledProperty<IReadOnlyList<ReaderSearchHighlightRange>?> HighlightRangesProperty =
        AvaloniaProperty.Register<ReaderSearchHighlightTextBlock, IReadOnlyList<ReaderSearchHighlightRange>?>(
            nameof(HighlightRanges));

    public ReaderSearchHighlightTextBlock()
    {
        SourceProperty.Changed.AddClassHandler<ReaderSearchHighlightTextBlock>(
            static (control, _) => control.RebuildHighlightedInlines());
        QueryProperty.Changed.AddClassHandler<ReaderSearchHighlightTextBlock>(
            static (control, _) => control.RebuildHighlightedInlines());
        HighlightRangesProperty.Changed.AddClassHandler<ReaderSearchHighlightTextBlock>(
            static (control, _) => control.RebuildHighlightedInlines());
    }

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public string? Query
    {
        get => GetValue(QueryProperty);
        set => SetValue(QueryProperty, value);
    }

    public IReadOnlyList<ReaderSearchHighlightRange>? HighlightRanges
    {
        get => GetValue(HighlightRangesProperty);
        set => SetValue(HighlightRangesProperty, value);
    }

    private void RebuildHighlightedInlines()
    {
        var text = Source ?? string.Empty;
        var query = Query?.Trim() ?? string.Empty;
        if (Inlines is null) return;
        Inlines.Clear();
        if (query.Length == 0)
        {
            Inlines.Add(new Run { Text = text });
            return;
        }

        var highlight = new SolidColorBrush(Color.FromRgb(255, 255, 255));
        var highlightBackground = new SolidColorBrush(Color.FromRgb(0, 0, 0));
        var ranges = HighlightRanges ?? ReaderSearchPresentation.FindTermOccurrences(text, query);
        var cursor = 0;
        foreach (var range in ranges)
        {
            var start = Math.Clamp(range.Start, 0, text.Length);
            var end = Math.Clamp(range.Start + range.Length, start, text.Length);
            if (start > cursor)
                Inlines.Add(new Run { Text = text[cursor..start] });
            if (end <= start) continue;
            Inlines.Add(new Run
            {
                Text = text[start..end],
                Foreground = highlight,
                Background = highlightBackground
            });
            cursor = end;
        }
        if (cursor < text.Length)
            Inlines.Add(new Run { Text = text[cursor..] });
    }
}

public sealed class ReaderAiSourceViewModel
{
    public ReaderAiSourceViewModel(BookContentChunk chunk)
    {
        Chunk = chunk;
        Label = $"{chunk.ChapterTitle} · 片段 {chunk.ChunkIndex + 1}";
    }

    public ReaderAiSourceViewModel(PdfPageText page)
    {
        Page = page;
        Label = $"第 {page.PageNumber} 页 · {CreateExcerpt(page.Text, 100)}";
    }

    public BookContentChunk? Chunk { get; }
    public PdfPageText? Page { get; }
    public string Label { get; }

    private static string CreateExcerpt(string value, int length)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= length ? normalized : normalized[..length] + "…";
    }
}

public sealed class ReaderAiMessageViewModel : ObservableObject
{
    private string _content;
    private string _reasoning;
    private bool _isReasoningVisible;

    public ReaderAiMessageViewModel(string role, string content = "", string reasoning = "")
    {
        Role = role;
        _content = content;
        _reasoning = reasoning;
    }

    public string Role { get; }

    public string RoleLabel => Role.Equals("user", StringComparison.OrdinalIgnoreCase)
        ? "你"
        : "Kreader AI";

    public IBrush BubbleBackground => Role.Equals("user", StringComparison.OrdinalIgnoreCase)
        ? new SolidColorBrush(Color.FromRgb(242, 242, 242))
        : Brushes.White;

    public IBrush BorderBrush => Role.Equals("user", StringComparison.OrdinalIgnoreCase)
        ? new SolidColorBrush(Color.FromRgb(218, 218, 218))
        : new SolidColorBrush(Color.FromRgb(232, 232, 232));

    public IBrush RoleBrush => Role.Equals("user", StringComparison.OrdinalIgnoreCase)
        ? new SolidColorBrush(Color.FromRgb(75, 75, 75))
        : new SolidColorBrush(Color.FromRgb(26, 26, 26));

    public string Content
    {
        get => _content;
        private set => SetProperty(ref _content, value);
    }

    public string Reasoning
    {
        get => _reasoning;
        private set => SetProperty(ref _reasoning, value);
    }

    public bool IsReasoningVisible
    {
        get => _isReasoningVisible;
        private set => SetProperty(ref _isReasoningVisible, value);
    }

    public void Update(string content, string reasoning, bool isStreaming)
    {
        Content = content;
        Reasoning = reasoning;
        IsReasoningVisible = reasoning.Length > 0 && (isStreaming || Content.Length == 0);
    }
}
