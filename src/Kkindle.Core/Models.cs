namespace Kkindle.Core;

public sealed class Book
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "未命名书籍";
    public string Authors { get; set; } = "未知作者";
    public string? Series { get; set; }
    public double? SeriesIndex { get; set; }
    public string? Description { get; set; }
    public string Tags { get; set; } = string.Empty;
    public string? CoverPath { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<BookFile> Files { get; set; } = [];

    public string FormatSummary => Files.Count == 0
        ? string.Empty
        : string.Join(" · ", Files.Select(x => x.Format.ToUpperInvariant()).Distinct());

    public string ProgressLabel => Files.Count == 0 ? string.Empty : $"{FormatSummary}  ·  {Files.Count} 个文件";
}

public sealed class BookFile
{
    public Guid Id { get; set; }
    public Guid BookId { get; set; }
    public string Format { get; set; } = string.Empty;
    public string RelativePath { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class KindleDevice
{
    public string RootPath { get; init; } = string.Empty;
    public string VolumeSerial { get; init; } = string.Empty;
    public string Name { get; init; } = "Kindle";
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }
    public bool IsReady { get; init; }

    public string CapacityLabel => $"{FormatBytes(FreeBytes)} 可用 / {FormatBytes(TotalBytes)}";

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / 1024d / 1024 / 1024:0.0} GB";
        return $"{bytes / 1024d / 1024:0} MB";
    }
}

public sealed class KindleBook
{
    public string RelativePath { get; init; } = string.Empty;
    public string FileName => Path.GetFileName(RelativePath);
    public string Format { get; init; } = string.Empty;
    public long Size { get; init; }
    public string Sha256 { get; init; } = string.Empty;
    public bool IsManagedByKkindle { get; init; }
}

public sealed class BookMetadata
{
    public string? Title { get; init; }
    public string? Authors { get; init; }
    public string? Series { get; init; }
    public double? SeriesIndex { get; init; }
    public string? Description { get; init; }
    public byte[]? CoverBytes { get; init; }
    public string CoverExtension { get; init; } = ".jpg";
}

public sealed record ImportItemResult(string SourcePath, bool Succeeded, string? Message, Book? Book);

public sealed class ImportBatchResult
{
    public List<ImportItemResult> Items { get; } = [];
    public int SuccessCount => Items.Count(x => x.Succeeded);
    public int FailureCount => Items.Count(x => !x.Succeeded);
}

public sealed record TransferProgress(long BytesCopied, long TotalBytes, string Message)
{
    public double Percentage => TotalBytes <= 0 ? 0 : BytesCopied * 100d / TotalBytes;
}
