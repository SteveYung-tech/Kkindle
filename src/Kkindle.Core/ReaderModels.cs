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
