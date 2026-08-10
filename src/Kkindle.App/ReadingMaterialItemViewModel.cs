using Kkindle.Core;

namespace Kkindle;

public sealed class ReadingMaterialItemViewModel
{
    public ReadingMaterialSource Source { get; set; }
    public string SourceLabel => Source == ReadingMaterialSource.Local ? "本地" : "Kindle";
    public string BookTitle { get; set; } = "未知书籍";
    public string TypeLabel { get; set; } = "划线";
    public string Location { get; set; } = string.Empty;
    public string Quote { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string DateLabel => UpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? string.Empty;
    public ReaderAnnotation? LocalAnnotation { get; set; }
    public KindleClipping? KindleClipping { get; set; }
    public string SearchText => $"{SourceLabel}\n{BookTitle}\n{TypeLabel}\n{Location}\n{Quote}\n{Note}";

    public ReadingMaterialRecord ToRecord() => new(Source, BookTitle, TypeLabel, Location, Quote, Note, UpdatedAt);
}
