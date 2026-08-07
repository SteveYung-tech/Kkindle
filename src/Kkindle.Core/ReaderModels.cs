namespace Kkindle.Core;

public sealed class ReaderAnnotation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookId { get; set; }
    public Guid BookFileId { get; set; }
    public string ChapterPath { get; set; } = string.Empty;
    public string? Fragment { get; set; }
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public string SelectedText { get; set; } = string.Empty;
    public string Prefix { get; set; } = string.Empty;
    public string Suffix { get; set; } = string.Empty;
    public string Color { get; set; } = "#000000";
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string DisplayQuote => string.IsNullOrWhiteSpace(SelectedText) ? "未命名批注" : SelectedText;
    public string DisplayNote => string.IsNullOrWhiteSpace(Note) ? "仅划线" : Note;
}

public sealed record BookContentChunk(
    long Id,
    Guid BookId,
    Guid BookFileId,
    string SourceHash,
    int ChapterIndex,
    int ChunkIndex,
    string ChapterTitle,
    string ChapterPath,
    int StartOffset,
    int EndOffset,
    string Content,
    double Rank = 0);

public sealed record BookContentChunkDraft(
    int ChapterIndex,
    int ChunkIndex,
    string ChapterTitle,
    string ChapterPath,
    int StartOffset,
    int EndOffset,
    string Content);

// ------------------------------------------------------------------
// Reader persistence: progress restore, bookmarks, per-book layout
// settings and cumulative reading stats. All rows are keyed by the
// BookFile so every format of the same book keeps its own position.
// ------------------------------------------------------------------

public sealed record ReaderProgressRow(
    Guid BookId,
    Guid BookFileId,
    string ChapterPath,
    string? Fragment,
    int ChapterIndex,
    int ScrollPosition,
    double ProgressPercent,
    int FlowMode,
    DateTimeOffset UpdatedAt);

public sealed class ReaderBookmark
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid BookId { get; set; }
    public Guid BookFileId { get; set; }
    public string ChapterPath { get; set; } = string.Empty;
    public string? Fragment { get; set; }
    public int ChapterIndex { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Quote { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "未命名书签" : Title;
    public string DisplayQuote => string.IsNullOrWhiteSpace(Quote) ? "当前阅读位置" : Quote;
    public string DisplayTime => CreatedAt.ToLocalTime().ToString("MM-dd HH:mm");
}

public sealed record ReaderLayoutSettings(
    double FontScale = 1.0,
    double LineHeight = 1.88,
    double MaxWidth = 800,
    double BodyPadding = 68,
    string FontFamily = "",
    int FlowMode = 0,
    bool VerticalWriting = false);

// ------------------------------------------------------------------
// Layout settings safety net. Persisted per-book settings can carry
// stale or corrupted values (NaN, out-of-range widths, invalid flow
// modes) from older builds. Clamp every field to the supported ranges
// so a bad row can never force an EPUB into an unreadable layout;
// the rest of the user's reading data is never touched.
// ------------------------------------------------------------------

public static class ReaderLayoutDefaults
{
    public const double DefaultFontScale = 1.0;
    public const double DefaultLineHeight = 1.88;
    public const double DefaultMaxWidth = 800;
    public const double DefaultBodyPadding = 68;
    public const double MinFontScale = 0.8;
    public const double MaxFontScale = 1.8;
    public const double MinLineHeight = 1.3;
    public const double MaxLineHeight = 2.6;
    public const double MinMaxWidth = 480;
    public const double MaxMaxWidth = 1200;
    public const double MinBodyPadding = 24;
    public const double MaxBodyPadding = 160;

    public static ReaderLayoutSettings Normalize(ReaderLayoutSettings settings)
    {
        var fontScale = double.IsFinite(settings.FontScale)
            ? Math.Clamp(settings.FontScale, MinFontScale, MaxFontScale)
            : DefaultFontScale;
        var lineHeight = double.IsFinite(settings.LineHeight)
            ? Math.Clamp(settings.LineHeight, MinLineHeight, MaxLineHeight)
            : DefaultLineHeight;
        var maxWidth = double.IsFinite(settings.MaxWidth)
            ? Math.Clamp(settings.MaxWidth, MinMaxWidth, MaxMaxWidth)
            : DefaultMaxWidth;
        var bodyPadding = double.IsFinite(settings.BodyPadding)
            ? Math.Clamp(settings.BodyPadding, MinBodyPadding, MaxBodyPadding)
            : DefaultBodyPadding;
        return settings with
        {
            FontScale = fontScale,
            LineHeight = lineHeight,
            MaxWidth = maxWidth,
            BodyPadding = bodyPadding,
            FlowMode = settings.FlowMode == 1 ? 1 : 0,
            VerticalWriting = settings.VerticalWriting
        };
    }
}

public sealed class ReaderReadingStats
{
    public Guid BookId { get; set; }
    public Guid BookFileId { get; set; }
    public long CumulativeSeconds { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public double ProgressPercent { get; set; }
    public int CompletedChapters { get; set; }
    public int TotalChapters { get; set; }

    public string DurationLabel
    {
        get
        {
            var seconds = CumulativeSeconds;
            if (seconds < 60) return $"{seconds} 秒";
            if (seconds < 3600) return $"{seconds / 60} 分钟";
            return $"{seconds / 3600.0:0.0} 小时";
        }
    }
}

public static class ReaderFormatting
{
    public static string FormatPercent(double percent) =>
        $"{Math.Clamp((int)Math.Round(percent), 0, 100)}%";
}
