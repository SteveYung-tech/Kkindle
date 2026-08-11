using Kkindle.Core;

namespace Kkindle;

/// <summary>
/// A visual section in the reading-materials page. The group itself is an
/// IList so WinUI's grouped ListView can keep the individual records
/// selectable and clickable while rendering one header per book.
/// </summary>
public sealed class ReadingMaterialGroupViewModel : List<ReadingMaterialItemViewModel>
{
    public ReadingMaterialGroupViewModel(
        ReadingMaterialSource source,
        string bookTitle,
        IEnumerable<ReadingMaterialItemViewModel> items)
        : base(items)
    {
        Source = source;
        BookTitle = string.IsNullOrWhiteSpace(bookTitle) ? "未命名书籍" : bookTitle;
    }

    public ReadingMaterialSource Source { get; }
    public string BookTitle { get; }
    public string SourceLabel => Source == ReadingMaterialSource.Local ? "本地书籍" : "Kindle";
    public string CountLabel => $"{Count} 条记录";
}
