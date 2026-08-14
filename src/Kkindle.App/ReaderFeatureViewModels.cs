using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Kkindle.Core;

namespace Kkindle;

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
        Excerpt = excerpt;
        ChapterIndex = chapterIndex;
        ChapterPath = chapterPath;
        Target = target;
        PageNumber = pageNumber;
        Query = query;
    }

    public string Title { get; }
    public string Excerpt { get; }
    public int ChapterIndex { get; }
    public string ChapterPath { get; }
    public string? Target { get; }
    public int? PageNumber { get; }
    public string? Query { get; }
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

    public ReaderSearchHighlightTextBlock()
    {
        SourceProperty.Changed.AddClassHandler<ReaderSearchHighlightTextBlock>(
            static (control, _) => control.RebuildHighlightedInlines());
        QueryProperty.Changed.AddClassHandler<ReaderSearchHighlightTextBlock>(
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

        var plain = new SolidColorBrush(Color.FromRgb(0, 0, 0));
        var highlight = new SolidColorBrush(Color.FromRgb(255, 255, 255));
        var highlightBackground = new SolidColorBrush(Color.FromRgb(0, 0, 0));
        var start = 0;
        var index = text.IndexOf(query, StringComparison.CurrentCultureIgnoreCase);
        while (index >= 0)
        {
            if (index > start)
                Inlines.Add(new Run { Text = text[start..index], Foreground = plain });
            Inlines.Add(new Run
            {
                Text = text[index..(index + query.Length)],
                Foreground = highlight,
                Background = highlightBackground
            });
            start = index + Math.Max(1, query.Length);
            index = text.IndexOf(query, start, StringComparison.CurrentCultureIgnoreCase);
        }
        if (start < text.Length)
            Inlines.Add(new Run { Text = text[start..], Foreground = plain });
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
