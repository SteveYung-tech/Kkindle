using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed class DoubanCandidateViewModel : INotifyPropertyChanged
{
    private static readonly Brush SelectedBorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Black);
    private static readonly Brush UnselectedBorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 190, 190, 190));

    public DoubanCandidateViewModel(DoubanBookCandidate candidate)
    {
        Candidate = candidate;
        Title = candidate.Title;
        Abstract = string.IsNullOrWhiteSpace(candidate.Abstract) ? "豆瓣未提供简要出版信息" : candidate.Abstract;
        SubjectText = candidate.SubjectId > 0 ? $"豆瓣条目 #{candidate.SubjectId}" : "豆瓣图书条目";
        RatingText = candidate.Rating is null
            ? "暂无评分"
            : $"{candidate.Rating:0.0}  /  {candidate.RatingCount} 人";
    }

    public DoubanBookCandidate Candidate { get; }
    public string Title { get; }
    public string Abstract { get; }
    public string SubjectText { get; }
    public string RatingText { get; }
    private BitmapImage? _coverImage;
    public BitmapImage? CoverImage
    {
        get => _coverImage;
        set
        {
            if (ReferenceEquals(_coverImage, value)) return;
            _coverImage = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverImage)));
        }
    }

    private bool _isSelected;
    public Brush CandidateBorderBrush => _isSelected ? SelectedBorderBrush : UnselectedBorderBrush;
    public Thickness CandidateBorderThickness => _isSelected ? new Thickness(3) : new Thickness(1);

    public void SetSelected(bool isSelected)
    {
        if (_isSelected == isSelected) return;
        _isSelected = isSelected;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CandidateBorderBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CandidateBorderThickness)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class MainWindow
{
    private readonly DoubanMetadataService _doubanMetadataService = new();
    private CancellationTokenSource? _doubanMatchCancellation;
    private TaskCompletionSource<DoubanBookCandidate?>? _doubanCandidateCompletion;
    private TaskCompletionSource<DoubanUpdateChoices?>? _doubanApplyCompletion;
    private DoubanBookCandidate? _doubanSelectedCandidate;
    private DoubanBookMetadata? _doubanPreviewMetadata;

    public ObservableCollection<DoubanCandidateViewModel> DoubanCandidates { get; } = [];

    private async void DoubanMatchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBook is null || _doubanMatchCancellation is not null) return;
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync("网络功能已关闭", "请先在应用设置中允许网络功能，再使用豆瓣匹配。");
            return;
        }

        var book = _selectedBook;
        var cancellation = new CancellationTokenSource();
        _doubanMatchCancellation = cancellation;
        DoubanMatchButton.IsEnabled = false;
        TaskProgress.IsIndeterminate = true;
        TaskProgress.Visibility = Visibility.Visible;
        TaskStatusText.Text = $"正在豆瓣匹配《{book.Title}》…";
        OpenDoubanOverlay();
        SetDoubanBusy(true, "正在搜索豆瓣图书…");
        try
        {
            var candidates = await _doubanMetadataService.SearchAsync(book.Title, book.Authors, cancellation.Token);
            if (candidates.Count == 0)
            {
                TaskStatusText.Text = "豆瓣未找到匹配条目";
                CloseDoubanOverlay();
                await ShowMessageAsync("没有找到", "豆瓣没有返回匹配条目。可以先修正本地书名或作者后再试。");
                return;
            }

            DoubanCandidates.Clear();
            foreach (var candidate in candidates) DoubanCandidates.Add(new DoubanCandidateViewModel(candidate));
            SetDoubanBusy(true, "正在加载豆瓣封面…");
            await LoadDoubanCandidateCoversAsync(cancellation.Token);

            while (true)
            {
                var candidate = await ChooseDoubanCandidateAsync();
                if (candidate is null)
                {
                    TaskStatusText.Text = "已取消豆瓣匹配";
                    return;
                }

                SetDoubanBusy(true, $"正在读取《{candidate.Title}》的豆瓣详情…");
                TaskStatusText.Text = $"正在读取《{candidate.Title}》的豆瓣详情…";
                var metadata = await _doubanMetadataService.GetDetailsAsync(candidate, cancellation.Token);
                var choices = await ConfirmDoubanMetadataAsync(metadata);
                if (choices?.GoBack == true) continue;
                if (choices is null)
                {
                    TaskStatusText.Text = "已取消豆瓣匹配";
                    return;
                }

                if (choices.UpdateTitle && !string.IsNullOrWhiteSpace(metadata.Title)) book.Title = metadata.Title.Trim();
                if (choices.UpdateAuthors && !string.IsNullOrWhiteSpace(metadata.Authors)) book.Authors = metadata.Authors.Trim();
                if (choices.UpdateSeries && !string.IsNullOrWhiteSpace(metadata.Series)) book.Series = metadata.Series.Trim();
                if (choices.UpdateDescription && !string.IsNullOrWhiteSpace(metadata.Description)) book.Description = metadata.Description.Trim();
                if (choices.UpdatePublication)
                {
                    if (!string.IsNullOrWhiteSpace(metadata.Publisher)) book.Publisher = metadata.Publisher.Trim();
                    if (!string.IsNullOrWhiteSpace(metadata.PublishDate)) book.PublishDate = metadata.PublishDate.Trim();
                    if (!string.IsNullOrWhiteSpace(metadata.Isbn)) book.Isbn = metadata.Isbn.Trim();
                    if (!string.IsNullOrWhiteSpace(metadata.Pages)) book.PageCount = metadata.Pages.Trim();
                    if (!string.IsNullOrWhiteSpace(metadata.Binding)) book.Binding = metadata.Binding.Trim();
                    if (metadata.Rating is not null) book.DoubanRating = metadata.Rating;
                    book.DoubanRatingCount = metadata.RatingCount;
                }

                if (choices.UpdateCover && !string.IsNullOrWhiteSpace(metadata.CoverUrl))
                {
                    SetDoubanBusy(true, "正在下载并保存豆瓣封面…");
                    TaskStatusText.Text = "正在下载豆瓣封面…";
                    var bytes = await _doubanMetadataService.DownloadCoverAsync(metadata.CoverUrl, cancellation.Token);
                    var coverName = $"{book.Id:N}-douban.jpg";
                    var coverPath = Path.Combine(_paths.Covers, coverName);
                    var temporaryPath = coverPath + ".tmp";
                    await File.WriteAllBytesAsync(temporaryPath, bytes, cancellation.Token);
                    File.Move(temporaryPath, coverPath, overwrite: true);
                    book.CoverPath = Path.GetRelativePath(_paths.Data, coverPath);
                }

                await _library.UpdateMetadataAsync(book);
                await RefreshLibraryAsync();
                SelectBook(book);
                TaskStatusText.Text = $"已用豆瓣信息更新《{book.Title}》";
                return;
            }
        }
        catch (OperationCanceledException)
        {
            TaskStatusText.Text = "豆瓣匹配已取消";
        }
        catch (Exception exception)
        {
            TaskStatusText.Text = "豆瓣匹配失败";
            CloseDoubanOverlay();
            await ShowMessageAsync("豆瓣匹配失败", exception.Message);
        }
        finally
        {
            CloseDoubanOverlay();
            if (ReferenceEquals(_doubanMatchCancellation, cancellation)) _doubanMatchCancellation = null;
            cancellation.Dispose();
            DoubanMatchButton.IsEnabled = _selectedBook is not null;
            TaskProgress.IsIndeterminate = false;
            TaskProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void OpenDoubanOverlay()
    {
        DoubanMatchOverlay.Visibility = Visibility.Visible;
        DoubanMatchOverlay.Focus(FocusState.Programmatic);
        DoubanMatchStatusText.Text = string.Empty;
    }

    private void CloseDoubanOverlay()
    {
        DoubanMatchOverlay.Visibility = Visibility.Collapsed;
        DoubanMatchBusyLayer.Visibility = Visibility.Collapsed;
        _doubanCandidateCompletion?.TrySetResult(null);
        _doubanApplyCompletion?.TrySetResult(null);
        _doubanCandidateCompletion = null;
        _doubanApplyCompletion = null;
        _doubanSelectedCandidate = null;
        _doubanPreviewMetadata = null;
        DoubanPreviewCoverImage.Source = null;
    }

    private void SetDoubanBusy(bool isBusy, string? message = null)
    {
        DoubanMatchBusyLayer.Visibility = isBusy ? Visibility.Visible : Visibility.Collapsed;
        if (!string.IsNullOrWhiteSpace(message)) DoubanMatchBusyText.Text = message;
        DoubanMatchPrimaryButton.IsEnabled = !isBusy &&
            (DoubanPreviewView.Visibility == Visibility.Visible || _doubanSelectedCandidate is not null);
    }

    private Task<DoubanBookCandidate?> ChooseDoubanCandidateAsync()
    {
        DoubanCandidateView.Visibility = Visibility.Visible;
        DoubanPreviewView.Visibility = Visibility.Collapsed;
        DoubanMatchBackButton.Visibility = Visibility.Collapsed;
        DoubanMatchTitleText.Text = "选择豆瓣条目";
        DoubanMatchSubtitleText.Text = $"找到 {DoubanCandidates.Count} 个候选结果，请根据封面与出版信息确认";
        DoubanMatchPrimaryButton.Content = "查看详情";
        DoubanMatchStatusText.Text = "选择候选条目后可查看完整元数据";
        _doubanSelectedCandidate = null;
        DoubanCandidateList.SelectedItem = null;
        foreach (var item in DoubanCandidates) item.SetSelected(false);
        DoubanMatchPrimaryButton.IsEnabled = false;
        SetDoubanBusy(false);
        _doubanCandidateCompletion = new TaskCompletionSource<DoubanBookCandidate?>();
        return _doubanCandidateCompletion.Task;
    }

    private Task<DoubanUpdateChoices?> ConfirmDoubanMetadataAsync(DoubanBookMetadata metadata)
    {
        _doubanPreviewMetadata = metadata;
        DoubanCandidateView.Visibility = Visibility.Collapsed;
        DoubanPreviewView.Visibility = Visibility.Visible;
        DoubanMatchBackButton.Visibility = Visibility.Visible;
        DoubanMatchTitleText.Text = "确认豆瓣匹配结果";
        DoubanMatchSubtitleText.Text = "核对书籍信息并选择需要写入本地书库的字段";
        DoubanMatchPrimaryButton.Content = "应用所选字段";
        DoubanMatchPrimaryButton.IsEnabled = true;
        DoubanMatchStatusText.Text = "未勾选的本地字段不会被修改";
        DoubanPreviewSummaryText.Text = BuildDoubanSummary(metadata);
        DoubanPreviewCoverImage.Source = DoubanCandidates
            .FirstOrDefault(item => item.Candidate.SubjectId == _doubanSelectedCandidate?.SubjectId)?.CoverImage;

        DoubanUpdateTitleCheck.IsChecked = !string.IsNullOrWhiteSpace(metadata.Title);
        DoubanUpdateAuthorsCheck.IsChecked = !string.IsNullOrWhiteSpace(metadata.Authors);
        DoubanUpdateSeriesCheck.IsChecked = !string.IsNullOrWhiteSpace(metadata.Series);
        DoubanUpdateSeriesCheck.IsEnabled = !string.IsNullOrWhiteSpace(metadata.Series);
        DoubanUpdateDescriptionCheck.IsChecked = !string.IsNullOrWhiteSpace(metadata.Description);
        DoubanUpdateDescriptionCheck.IsEnabled = !string.IsNullOrWhiteSpace(metadata.Description);
        DoubanUpdateCoverCheck.IsChecked = !string.IsNullOrWhiteSpace(metadata.CoverUrl);
        DoubanUpdateCoverCheck.IsEnabled = !string.IsNullOrWhiteSpace(metadata.CoverUrl);
        var hasPublicationData = !string.IsNullOrWhiteSpace(metadata.Publisher)
            || !string.IsNullOrWhiteSpace(metadata.PublishDate)
            || !string.IsNullOrWhiteSpace(metadata.Isbn)
            || !string.IsNullOrWhiteSpace(metadata.Pages)
            || !string.IsNullOrWhiteSpace(metadata.Binding)
            || metadata.Rating is not null;
        DoubanUpdatePublicationCheck.IsChecked = hasPublicationData;
        DoubanUpdatePublicationCheck.IsEnabled = hasPublicationData;
        SetDoubanBusy(false);

        _doubanApplyCompletion = new TaskCompletionSource<DoubanUpdateChoices?>();
        return _doubanApplyCompletion.Task;
    }

    private void DoubanCandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedItem = DoubanCandidateList.SelectedItem as DoubanCandidateViewModel;
        foreach (var item in DoubanCandidates) item.SetSelected(ReferenceEquals(item, selectedItem));
        _doubanSelectedCandidate = selectedItem?.Candidate;
        DoubanMatchPrimaryButton.IsEnabled = _doubanSelectedCandidate is not null;
        DoubanMatchStatusText.Text = _doubanSelectedCandidate is null
            ? "选择候选条目后可查看完整元数据"
            : $"已选择《{_doubanSelectedCandidate.Title}》";
    }

    private void DoubanCandidateList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (_doubanSelectedCandidate is not null) CompleteDoubanCandidateSelection();
    }

    private void DoubanMatchPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (DoubanCandidateView.Visibility == Visibility.Visible)
        {
            CompleteDoubanCandidateSelection();
            return;
        }

        _doubanApplyCompletion?.TrySetResult(new DoubanUpdateChoices(
            false,
            DoubanUpdateTitleCheck.IsChecked == true,
            DoubanUpdateAuthorsCheck.IsChecked == true,
            DoubanUpdateSeriesCheck.IsChecked == true,
            DoubanUpdateDescriptionCheck.IsChecked == true,
            DoubanUpdateCoverCheck.IsChecked == true,
            DoubanUpdatePublicationCheck.IsChecked == true));
        _doubanApplyCompletion = null;
    }

    private void CompleteDoubanCandidateSelection()
    {
        if (_doubanSelectedCandidate is null) return;
        SetDoubanBusy(true, "正在读取豆瓣详情…");
        _doubanCandidateCompletion?.TrySetResult(_doubanSelectedCandidate);
        _doubanCandidateCompletion = null;
    }

    private void DoubanMatchBackButton_Click(object sender, RoutedEventArgs e)
    {
        _doubanApplyCompletion?.TrySetResult(new DoubanUpdateChoices(true, false, false, false, false, false, false));
        _doubanApplyCompletion = null;
    }

    private void DoubanMatchCancelButton_Click(object sender, RoutedEventArgs e)
    {
        _doubanCandidateCompletion?.TrySetResult(null);
        _doubanApplyCompletion?.TrySetResult(null);
        _doubanMatchCancellation?.Cancel();
    }

    private void DoubanMatchOverlay_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape) return;
        e.Handled = true;
        DoubanMatchCancelButton_Click(sender, new RoutedEventArgs());
    }

    private async void DoubanOpenSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_doubanPreviewMetadata is null || !Uri.TryCreate(_doubanPreviewMetadata.Url, UriKind.Absolute, out var uri)) return;
        if (!await Windows.System.Launcher.LaunchUriAsync(uri))
            DoubanMatchStatusText.Text = "无法打开豆瓣详情页";
    }

    private async Task LoadDoubanCandidateCoversAsync(CancellationToken cancellationToken)
    {
        var downloads = DoubanCandidates.Select(async item =>
        {
            if (string.IsNullOrWhiteSpace(item.Candidate.CoverUrl)) return (item, Bytes: (byte[]?)null);
            try
            {
                var bytes = await _doubanMetadataService.DownloadCoverAsync(item.Candidate.CoverUrl, cancellationToken);
                return (item, Bytes: (byte[]?)bytes);
            }
            catch when (!cancellationToken.IsCancellationRequested)
            {
                return (item, Bytes: (byte[]?)null);
            }
        });

        var covers = await Task.WhenAll(downloads);
        foreach (var (item, bytes) in covers)
        {
            if (bytes is null) continue;
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            var image = new BitmapImage { DecodePixelWidth = 180 };
            await image.SetSourceAsync(stream);
            item.CoverImage = image;
        }
    }

    private static string BuildDoubanSummary(DoubanBookMetadata metadata)
    {
        var rows = new List<string>
        {
            $"书名：{metadata.Title}",
            $"作者：{Fallback(metadata.Authors)}",
            $"译者：{Fallback(metadata.Translators)}",
            $"出版社：{Fallback(metadata.Publisher)}",
            $"出版年：{Fallback(metadata.PublishDate)}",
            $"ISBN：{Fallback(metadata.Isbn)}",
            $"页数 / 装帧：{Fallback(metadata.Pages)} / {Fallback(metadata.Binding)}",
            $"定价：{Fallback(metadata.Price)}",
            $"系列：{Fallback(metadata.Series)}",
            metadata.Rating is null ? "豆瓣评分：暂无" : $"豆瓣评分：{metadata.Rating:0.0}（{metadata.RatingCount} 人评价）",
            string.Empty,
            $"简介：{Fallback(metadata.Description)}"
        };
        return string.Join(Environment.NewLine, rows);
    }

    private static string Fallback(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private sealed record DoubanUpdateChoices(
        bool GoBack,
        bool UpdateTitle,
        bool UpdateAuthors,
        bool UpdateSeries,
        bool UpdateDescription,
        bool UpdateCover,
        bool UpdatePublication);
}
