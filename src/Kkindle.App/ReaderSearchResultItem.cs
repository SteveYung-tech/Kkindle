using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Kkindle.Core;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Kkindle;

public sealed class ReaderSearchResultItem : INotifyPropertyChanged
{
    private static readonly Brush SelectedBorder = new SolidColorBrush(Color.FromArgb(255, 0, 0, 0));
    private static readonly Brush IdleBorder = new SolidColorBrush(Color.FromArgb(255, 190, 190, 190));
    private bool _isSelected;

    public ReaderSearchResultItem(BookContentChunk source, string query)
    {
        Source = source;
        Query = query;
        Title = source.ChapterTitle;
        (Snippet, HighlightRanges) = CreateSnippet(source.Content, query);
    }

    public BookContentChunk Source { get; }

    public string Query { get; }

    public string Title { get; }

    public string Snippet { get; }

    // Keyword ranges inside Snippet (including the leading/trailing ellipsis).
    // Computed together with the snippet so highlighting survives multi-line
    // selection queries and queries longer than the 150-character window.
    public IReadOnlyList<(int Start, int Length)> HighlightRanges { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ResultBorderBrush));
        }
    }

    public Brush ResultBorderBrush => IsSelected ? SelectedBorder : IdleBorder;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static (string Snippet, IReadOnlyList<(int Start, int Length)> HighlightRanges) CreateSnippet(
        string content,
        string query)
    {
        var normalized = Regex.Replace(content ?? string.Empty, @"\s+", " ").Trim();
        // The query can come straight from a text-selection popup, which keeps
        // the newlines between blocks. The search index normalizes all
        // whitespace, so do the same here before looking for the keyword;
        // otherwise a multi-line selection never appears in the snippet.
        var normalizedQuery = Regex.Replace(query?.Trim() ?? string.Empty, @"\s+", " ").Trim();
        if (normalized.Length == 0 || normalizedQuery.Length == 0)
            return (string.Empty, Array.Empty<(int, int)>());

        const int maximumLength = 150;
        var match = normalized.IndexOf(normalizedQuery, StringComparison.CurrentCultureIgnoreCase);
        var runs = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

        // Collect every keyword occurrence inside the window. A query longer
        // than the window still contributes its visible tail, otherwise the
        // long selection-popup query would highlight nothing.
        var rawRanges = new List<(int Start, int Length)>();
        if (useRuns)
        {
            foreach (var run in runs.Distinct(StringComparer.CurrentCultureIgnoreCase))
                AddSnippetOccurrences(rawRanges, normalized, run, start, length);
        }
        else
        {
            AddSnippetOccurrences(rawRanges, normalized, normalizedQuery, start, length);
        }
        if (rawRanges.Count == 0 && match >= 0)
        {
            var visibleStart = Math.Max(match, start);
            var visibleEnd = Math.Min(match + normalizedQuery.Length, start + length);
            if (visibleEnd > visibleStart)
                rawRanges.Add((visibleStart - start, visibleEnd - visibleStart));
        }

        var offset = prefix.Length;
        var highlightRanges = MergeRanges(rawRanges
            .Select(range => (range.Start + offset, range.Length))
            .Where(range => range.Item1 >= 0 && range.Item1 + range.Length <= display.Length));
        return (display, highlightRanges);
    }

    private static void AddSnippetOccurrences(
        List<(int Start, int Length)> ranges,
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
            var index = normalized.IndexOf(term, searchStart, StringComparison.CurrentCultureIgnoreCase);
            if (index < 0 || index >= windowEnd) break;
            var visibleEnd = Math.Min(index + term.Length, windowEnd);
            if (visibleEnd > index)
                ranges.Add((index - windowStart, visibleEnd - index));
            searchStart = index + Math.Max(1, term.Length);
        }
    }

    private static IReadOnlyList<(int Start, int Length)> MergeRanges(
        IEnumerable<(int Start, int Length)> ranges)
    {
        var merged = new List<(int Start, int Length)>();
        foreach (var range in ranges.OrderBy(range => range.Start))
        {
            if (merged.Count > 0
                && range.Start <= merged[^1].Start + merged[^1].Length)
            {
                merged[^1] = (
                    merged[^1].Start,
                    Math.Max(merged[^1].Start + merged[^1].Length, range.Start + range.Length)
                    - merged[^1].Start);
            }
            else
            {
                merged.Add(range);
            }
        }
        return merged;
    }
}
