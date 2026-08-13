using Kkindle.Core;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Kkindle;

public sealed class ReadingMaterialItemViewModel : INotifyPropertyChanged
{
    private bool _isSelected;

    public ReadingMaterialSource Source { get; set; }
    public string SourceLabel => Source == ReadingMaterialSource.Local ? "本地" : "Kindle";
    public string BookTitle { get; set; } = "未知书籍";
    public string TypeLabel { get; set; } = "划线";
    public string Location { get; set; } = string.Empty;
    public string ChapterLabel { get; set; } = "未指定章节";
    public string Quote { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public DateTimeOffset? UpdatedAt { get; set; }
    public string QuoteLabel => string.IsNullOrWhiteSpace(Quote) ? "无" : Quote;
    public string NoteLabel => string.IsNullOrWhiteSpace(Note) ? "无" : Note;
    public string TooltipLabel => $"划线：{QuoteLabel}\n批注：{NoteLabel}";
    public string DateLabel => UpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "时间未知";
    public ReaderAnnotation? LocalAnnotation { get; set; }
    public KindleClipping? KindleClipping { get; set; }
    public string SearchText => $"{SourceLabel}\n{BookTitle}\n{TypeLabel}\n{ChapterLabel}\n{Location}\n{Quote}\n{Note}";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectionBoxOpacity));
        }
    }

    public double SelectionBoxOpacity => IsSelected ? 1 : 0;

    public ReadingMaterialRecord ToRecord() => new(Source, BookTitle, TypeLabel, Location, Quote, Note, UpdatedAt);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
