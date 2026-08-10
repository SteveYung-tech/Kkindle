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

    public ObservableCollection<ZLibraryBookCardViewModel> ZLibraryBooks { get; } = [];

    private void ShowZLibraryPage()
    {
        SetActiveNavigation(ZLibraryBooksButton);
        LibraryPane.Visibility = Visibility.Collapsed;
        SettingsPane.Visibility = Visibility.Collapsed;
        DevicePage.Visibility = Visibility.Collapsed;
        DeviceResourcePage.Visibility = Visibility.Collapsed;
        ReadingMaterialsPage.Visibility = Visibility.Collapsed;
        DetailPane.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
        ZLibraryPage.Visibility = Visibility.Visible;
        UpdateZLibraryAccountStatus();
    }

    private void ZLibraryBooksButton_Click(object sender, RoutedEventArgs e) => ShowZLibraryPage();

    private void UpdateZLibraryAccountStatus()
    {
        var configured = _zLibrarySettings.IsConfigured;
        ZLibraryStatusText.Text = configured
            ? $"已登录账号：{_zLibrarySettings.Email}"
            : "未配置账号，搜索前请在账号设置中登录 Z-Library。";
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
        if (!_zLibrarySettings.IsConfigured)
        {
            await ShowZLibraryAccountAsync("请先配置 Z-Library 账号。");
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
            if (!_zLibraryService.IsLoggedIn)
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

            item.MarkDownloadCompleted();
            item.SetStatus("已下载并导入书库");
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
        ZLibraryAccountOverlay.Visibility = Visibility.Visible;
    }

    private void ZLibraryAccountCancelButton_Click(object sender, RoutedEventArgs e)
    {
        ZLibraryAccountOverlay.Visibility = Visibility.Collapsed;
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
            await _zLibrarySettingsStore.SaveAsync(normalized);
            _zLibrarySettings = normalized;
            ZLibraryAccountOverlay.Visibility = Visibility.Collapsed;
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
