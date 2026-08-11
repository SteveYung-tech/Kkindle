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
        Snippet = CreateSnippet(source.Content, query);
    }

    public BookContentChunk Source { get; }

    public string Query { get; }

    public string Title { get; }

    public string Snippet { get; }

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
