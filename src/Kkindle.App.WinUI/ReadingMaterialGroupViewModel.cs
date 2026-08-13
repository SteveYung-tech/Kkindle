using Kkindle.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kkindle;

/// <summary>
/// A visual section in the reading-materials page. The group itself is an
/// IList so WinUI's grouped ListView can keep the individual records
/// selectable and clickable while rendering one header per book.
/// </summary>
public sealed class ReadingMaterialGroupViewModel : List<ReadingMaterialItemViewModel>, INotifyPropertyChanged
{
    public ReadingMaterialGroupViewModel(
        ReadingMaterialSource source,
        string bookTitle,
        IEnumerable<ReadingMaterialItemViewModel> items,
        string? coverPath = null)
        : base(items)
    {
        Source = source;
        BookTitle = string.IsNullOrWhiteSpace(bookTitle) ? "未命名书籍" : bookTitle;
        CoverImage = LoadCover(coverPath);
    }

    public ReadingMaterialSource Source { get; }
    public string BookTitle { get; }
    public string SourceLabel => Source == ReadingMaterialSource.Local ? "本地书籍" : "Kindle";
    public IReadOnlyList<ReadingMaterialItemViewModel> Items => this;
    public string CountLabel => $"{Count} 条批注";
    public ReadingMaterialItemViewModel? FirstItem => this.FirstOrDefault();
    public string PreviewQuoteLabel => FirstItem?.QuoteLabel ?? "无";
    public string PreviewNoteLabel => FirstItem?.NoteLabel ?? "无";
    public string PreviewDateLabel => FirstItem?.DateLabel ?? "时间未知";
    public string PreviewTooltipLabel => FirstItem?.TooltipLabel ?? "划线：无\n批注：无";
    public BitmapImage? CoverImage { get; }
    public Visibility CoverImageVisibility => CoverImage is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CoverPlaceholderVisibility => CoverImage is null ? Visibility.Visible : Visibility.Collapsed;

    private bool _isExpanded;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChildrenVisibility));
            OnPropertyChanged(nameof(PreviewVisibility));
            OnPropertyChanged(nameof(ExpandLabel));
            OnPropertyChanged(nameof(ExpandGlyph));
        }
    }

    public Visibility ChildrenVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PreviewVisibility => IsExpanded ? Visibility.Collapsed : Visibility.Visible;
    public string ExpandLabel => IsExpanded ? "收起子条目" : "展开子条目";
    public string ExpandGlyph => IsExpanded ? "\uE70D" : "\uE76C";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static BitmapImage? LoadCover(string? coverPath)
    {
        if (string.IsNullOrWhiteSpace(coverPath) || !File.Exists(coverPath)) return null;
        try { return new BitmapImage(new Uri(Path.GetFullPath(coverPath))); }
        catch { return null; }
    }
}
