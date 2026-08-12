using System.Collections.ObjectModel;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Kkindle;

public sealed partial class MainWindow
{
    private readonly IZLibraryService _zLibraryService;
    private readonly ZLibrarySettingsStore _zLibrarySettingsStore;
    private ZLibrarySettings _zLibrarySettings = new();
    private CancellationTokenSource? _zLibrarySearchCancellation;
    private bool _zLibrarySearching;
    private int _zLibraryPageIndex = 1;
    private int _zLibraryPageCount;
    private ZLibraryBookCardViewModel? _selectedZLibraryBook;

    public ObservableCollection<ZLibraryBookCardViewModel> ZLibraryBooks { get; } = [];

    private void ShowZLibraryPage()
    {
        SetActiveNavigation(ZLibraryBooksButton);
        LibraryPane.Visibility = Visibility.Collapsed;
        SettingsPane.Visibility = Visibility.Collapsed;
        DevicePage.Visibility = Visibility.Collapsed;
        DeviceResourcePage.Visibility = Visibility.Collapsed;
        ReadingMaterialsPage.Visibility = Visibility.Collapsed;
        ReadingDashboardPage.Visibility = Visibility.Collapsed;
        DetailPane.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
        HideSettingsPanel();
        ZLibraryPage.Visibility = Visibility.Visible;
        UpdateZLibraryAccountStatus();
    }

    private void ZLibraryBooksButton_Click(object sender, RoutedEventArgs e) => ShowZLibraryPage();

    private void UpdateZLibraryAccountStatus()
    {
        var configured = _zLibrarySettings.IsConfigured;
        ZLibraryStatusText.Text = configured
            ? $"已配置账号：{_zLibrarySettings.Email}"
            : "未配置账号，可搜索书籍；下载前需要登录 Z-Library。";
    }

    private async void ZLibrarySearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
            await StartZLibrarySearchAsync();
    }

    private async void ZLibrarySearchButton_Click(object sender, RoutedEventArgs e)
    {
        await StartZLibrarySearchAsync();
    }

    private async Task StartZLibrarySearchAsync()
    {
        if (_zLibrarySearching) return;
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync("网络功能已关闭", "请在应用设置中允许网络功能后使用在线书库。");
            return;
        }
        if (string.IsNullOrWhiteSpace(ZLibrarySearchBox.Text))
        {
            ZLibraryResultText.Text = "请输入书名或作者后搜索。";
            return;
        }
        _zLibraryPageIndex = 1;
        await PerformZLibrarySearchAsync(ZLibrarySearchBox.Text.Trim(), 1);
    }

    private async Task PerformZLibrarySearchAsync(string query, int page)
    {
        _zLibrarySearchCancellation?.Cancel();
        _zLibrarySearchCancellation?.Dispose();
        _zLibrarySearchCancellation = new CancellationTokenSource();
        var cancellationToken = _zLibrarySearchCancellation.Token;

        _zLibrarySearching = true;
        ZLibrarySearchButton.IsEnabled = false;
        ZLibraryPrevPageButton.IsEnabled = false;
        ZLibraryNextPageButton.IsEnabled = false;
        ZLibraryResultText.Text = $"正在搜索《{query}》…";
        try
        {
            if (_zLibrarySettings.IsConfigured && !_zLibraryService.IsLoggedIn)
                await _zLibraryService.LoginAsync(
                    _zLibrarySettings.Email,
                    _zLibrarySettings.Password,
                    _zLibrarySettings.BaseUrl,
                    cancellationToken);

            var extensions = ZLibraryExtensionBox.SelectedItem is ComboBoxItem { Tag: string extension } && extension.Length > 0
                ? new[] { extension }
                : null;
            var languages = ZLibraryLanguageBox.SelectedItem is ComboBoxItem { Tag: string language } && language.Length > 0
                ? new[] { language }
                : null;

            var result = await _zLibraryService.SearchAsync(
                query,
                page,
                extensions: extensions,
                languages: languages,
                cancellationToken: cancellationToken);

            _zLibraryPageIndex = result.Page;
            _zLibraryPageCount = result.PageCount;
            CloseZLibraryDetailPanel();
            ZLibraryBooks.Clear();
            foreach (var book in result.Books)
            {
                var item = new ZLibraryBookCardViewModel(book);
                ZLibraryBooks.Add(item);
                _ = item.LoadCoverAsync(cancellationToken);
            }

            ZLibraryResultText.Text = result.Books.Count == 0
                ? "没有找到匹配的书籍，试试更换关键词或放宽筛选。"
                : $"共找到 {result.Total} 本相关书籍";
            ZLibraryPageText.Text = _zLibraryPageCount <= 0
                ? "第 1 / 1 页"
                : $"第 {_zLibraryPageIndex} / {_zLibraryPageCount} 页";
            ZLibraryPrevPageButton.IsEnabled = _zLibraryPageIndex > 1;
            ZLibraryNextPageButton.IsEnabled = _zLibraryPageIndex < _zLibraryPageCount;
        }
        catch (OperationCanceledException)
        {
            // A newer search superseded this one.
        }
        catch (Exception exception)
        {
            ZLibraryResultText.Text = $"搜索失败：{exception.Message}";
            ZLibraryPageText.Text = string.Empty;
        }
        finally
        {
            _zLibrarySearching = false;
            ZLibrarySearchButton.IsEnabled = true;
        }
    }

    private async void ZLibraryPrevPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_zLibrarySearching || _zLibraryPageIndex <= 1) return;
        await PerformZLibrarySearchAsync(ZLibrarySearchBox.Text.Trim(), _zLibraryPageIndex - 1);
    }

    private async void ZLibraryNextPageButton_Click(object sender, RoutedEventArgs e)
    {
        if (_zLibrarySearching || _zLibraryPageIndex >= _zLibraryPageCount) return;
        await PerformZLibrarySearchAsync(ZLibrarySearchBox.Text.Trim(), _zLibraryPageIndex + 1);
    }

    private async void ZLibraryDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ZLibraryBookCardViewModel item }) return;
        if (item.IsDownloading) return;
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync("网络功能已关闭", "请在应用设置中允许网络功能后下载书籍。");
            return;
        }
        if (!_zLibrarySettings.IsConfigured)
        {
            await ShowZLibraryAccountAsync("请先配置 Z-Library 账号。");
            return;
        }

        item.IsDownloading = true;
        item.SetStatus("正在准备…");
        try
        {
            if (!_zLibraryService.IsLoggedIn)
                await _zLibraryService.LoginAsync(
                    _zLibrarySettings.Email,
                    _zLibrarySettings.Password,
                    _zLibrarySettings.BaseUrl);

            var progress = new Progress<TransferProgress>(item.SetDownloadProgress);
            var downloadsDirectory = Path.Combine(_paths.Data, "downloads");
            var downloadedPath = await _zLibraryService.DownloadAsync(
                item.Book,
                downloadsDirectory,
                progress);

            item.SetStatus("正在导入书库…");
            var import = await _library.ImportAsync([downloadedPath]);
            var entry = import.Items.FirstOrDefault(result => result.Succeeded);
            if (entry is null)
                throw new InvalidOperationException(import.Items.FirstOrDefault()?.Message ?? "导入书库失败。");

            var automaticFormats = await AutoGenerateReaderFormatsForImportsAsync(import);
            item.MarkDownloadCompleted();
            item.SetStatus(automaticFormats.GeneratedCount > 0
                ? "已下载、导入并补齐 EPUB/AZW3"
                : automaticFormats.Failures.Count > 0
                    ? "已导入，EPUB/AZW3 补齐失败"
                    : "已下载并导入书库");
            TaskStatusText.Text = $"《{item.Title}》已下载并导入电脑书库";
            await RefreshLibraryAsync();
            try { File.Delete(downloadedPath); } catch { /* The library copy is authoritative. */ }
        }
        catch (Exception exception)
        {
            item.SetStatus("下载失败");
            await ShowMessageAsync("下载失败", exception.Message);
        }
        finally
        {
            item.IsDownloading = false;
        }
    }

    private void ZLibraryBookList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ZLibraryBookList.SelectedItem is not ZLibraryBookCardViewModel item)
        {
            CloseZLibraryDetailPanel();
            return;
        }

        _selectedZLibraryBook = item;
        ZLibraryDetailPanel.DataContext = item;
        ZLibraryDetailPanel.Visibility = Visibility.Visible;
    }

    private void ZLibraryDetailCloseButton_Click(object sender, RoutedEventArgs e)
    {
        ZLibraryBookList.SelectedItem = null;
        CloseZLibraryDetailPanel();
    }

    private void CloseZLibraryDetailPanel()
    {
        _selectedZLibraryBook = null;
        ZLibraryDetailPanel.DataContext = null;
        ZLibraryDetailPanel.Visibility = Visibility.Collapsed;
    }

    private async void ZLibraryOfficialDetailButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenZLibraryUrlAsync(_selectedZLibraryBook?.Book.OfficialDetailUrl, "官网详情");
    }

    private async void ZLibraryReadOnlineButton_Click(object sender, RoutedEventArgs e)
    {
        await OpenZLibraryUrlAsync(_selectedZLibraryBook?.Book.ReadOnlineUrl, "在线阅读");
    }

    private async Task OpenZLibraryUrlAsync(string? value, string actionName)
    {
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync("网络功能已关闭", $"请在应用设置中允许网络功能后使用{actionName}。");
            return;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            await ShowMessageAsync($"无法打开{actionName}", "这本书没有提供有效链接。");
            return;
        }
        if (!await Windows.System.Launcher.LaunchUriAsync(uri))
            await ShowMessageAsync($"无法打开{actionName}", "系统没有可用于打开该链接的浏览器。");
    }

    private async void ZLibrarySendEmailButton_Click(object sender, RoutedEventArgs e)
    {
        var item = _selectedZLibraryBook;
        if (item is null || item.IsDownloading || _kindleEmailSending) return;
        if (!item.CanSendToEmail)
        {
            await ShowMessageAsync("无法发送", "该书当前不支持邮件发送，或文件不是 EPUB/PDF 格式。");
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync("网络功能已关闭", "请在应用设置中允许网络功能后再发送邮件。");
            return;
        }
        if (!_zLibrarySettings.IsConfigured)
        {
            await ShowZLibraryAccountAsync("下载并发送前请先配置 Z-Library 账号。");
            return;
        }

        var emailSettings = await _kindleEmailSettingsStore.LoadAsync();
        _kindleEmailSettings = emailSettings;
        var validationError = emailSettings.Validate();
        if (validationError is not null)
        {
            await ShowKindleEmailSettingsAsync($"请先完成设置：{validationError}");
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "发送到 Kindle 邮箱",
            Content = $"将下载《{item.Title}》并发送到 {emailSettings.KindleEmailAddress}。此操作会消耗一次 Z-Library 下载额度，是否继续？",
            PrimaryButtonText = "下载并发送",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        using var cancellation = new CancellationTokenSource();
        _kindleEmailSendCancellation = cancellation;
        _kindleEmailSending = true;
        item.IsDownloading = true;
        item.SetStatus("正在准备邮件…");
        TaskProgress.IsIndeterminate = true;
        TaskProgress.Visibility = Visibility.Visible;
        string? downloadedPath = null;
        try
        {
            if (!_zLibraryService.IsLoggedIn)
                await _zLibraryService.LoginAsync(
                    _zLibrarySettings.Email,
                    _zLibrarySettings.Password,
                    _zLibrarySettings.BaseUrl,
                    cancellation.Token);

            var downloadsDirectory = Path.Combine(_paths.Data, "downloads");
            downloadedPath = await _zLibraryService.DownloadAsync(
                item.Book,
                downloadsDirectory,
                new Progress<TransferProgress>(item.SetDownloadProgress),
                cancellation.Token);
            item.SetStatus("正在发送邮件…");
            TaskStatusText.Text = $"正在发送《{item.Title}》到 Kindle 邮箱…";
            await _kindleEmailSender.SendAsync(
                emailSettings,
                downloadedPath,
                $"Send to Kindle: {item.Title}",
                cancellation.Token);
            item.MarkDownloadCompleted();
            item.SetStatus("已发送到 Kindle 邮箱");
            TaskStatusText.Text = $"《{item.Title}》已提交到 Kindle 邮箱";
            await ShowMessageAsync("发送成功", "邮件已发送。Amazon 完成转换后，书籍会出现在 Kindle 或 Kindle 应用中。");
        }
        catch (OperationCanceledException)
        {
            item.SetStatus("邮件发送已取消");
            TaskStatusText.Text = "Kindle 邮箱发送已取消";
        }
        catch (Exception exception)
        {
            item.SetStatus("邮件发送失败");
            TaskStatusText.Text = "Kindle 邮箱发送失败";
            await ShowMessageAsync("发送失败", exception.Message);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(downloadedPath))
                try { File.Delete(downloadedPath); } catch { /* The temporary attachment is best-effort cleanup. */ }
            item.IsDownloading = false;
            _kindleEmailSending = false;
            if (ReferenceEquals(_kindleEmailSendCancellation, cancellation))
                _kindleEmailSendCancellation = null;
            TaskProgress.IsIndeterminate = false;
            TaskProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void ZLibraryAccountButton_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(ZLibraryAccountNavigationButton);
        _ = ShowZLibraryAccountAsync();
    }

    private async Task ShowZLibraryAccountAsync(string? status = null)
    {
        _zLibrarySettings = await _zLibrarySettingsStore.LoadAsync();
        ZLibraryEmailBox.Text = _zLibrarySettings.Email;
        ZLibraryPasswordBox.Password = _zLibrarySettings.Password;
        ZLibraryBaseUrlBox.Text = _zLibrarySettings.BaseUrl;
        ZLibraryAccountStatusText.Text = status ?? string.Empty;
        ShowSettingsPanel(ZLibraryAccountPane);
    }

    private void ZLibraryAccountCancelButton_Click(object sender, RoutedEventArgs e)
    {
        HideSettingsPanel();
        ZLibraryAccountStatusText.Text = string.Empty;
    }

    private async void ZLibraryAccountSaveButton_Click(object sender, RoutedEventArgs e)
    {
        var settings = new ZLibrarySettings
        {
            Email = ZLibraryEmailBox.Text,
            Password = ZLibraryPasswordBox.Password,
            BaseUrl = ZLibraryBaseUrlBox.Text
        };
        var validationError = settings.Validate();
        if (validationError is not null)
        {
            ZLibraryAccountStatusText.Text = validationError;
            return;
        }

        ZLibraryAccountStatusText.Text = "正在验证账号…";
        try
        {
            var normalized = ZLibrarySettings.Normalize(settings);
            await _zLibraryService.LoginAsync(
                normalized.Email,
                normalized.Password,
                normalized.BaseUrl);
            normalized.BaseUrl = _zLibraryService.ActiveBaseUrl;
            await _zLibrarySettingsStore.SaveAsync(normalized);
            _zLibrarySettings = normalized;
            HideSettingsPanel();
            ZLibraryAccountStatusText.Text = string.Empty;
            TaskStatusText.Text = "Z-Library 账号已保存并验证";
            UpdateZLibraryAccountStatus();
        }
        catch (Exception exception)
        {
            ZLibraryAccountStatusText.Text = $"登录失败：{exception.Message}";
        }
    }
}
