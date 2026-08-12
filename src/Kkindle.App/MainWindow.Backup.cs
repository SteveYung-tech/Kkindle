using Kkindle.Infrastructure;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Kkindle;

public sealed partial class MainWindow
{
    private readonly AppBackupService _backupService;
    private bool _backupOperationInProgress;

    private void ShowSettings()
    {
        SetActiveNavigation(SettingsNavigationButton);
        DevicePage.Visibility = Visibility.Collapsed;
        DeviceResourcePage.Visibility = Visibility.Collapsed;
        ReadingMaterialsPage.Visibility = Visibility.Collapsed;
        ReadingDashboardPage.Visibility = Visibility.Collapsed;
        LibraryPane.Visibility = Visibility.Collapsed;
        SettingsPane.Visibility = Visibility.Visible;
        ZLibraryPage.Visibility = Visibility.Collapsed;
        DetailPane.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
        HideSettingsPanel();
        SettingsDataPathText.Text = _paths.Data;
        ShowSettingsSection("General");
    }

    private async void ExportBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_backupOperationInProgress) return;

        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("Kkindle 备份", [AppBackupService.FileExtension]);
        picker.SuggestedFileName = $"Kkindle-备份-{DateTime.Now:yyyyMMdd-HHmmss}";

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await ExportBackupAsync(file.Path);
    }

    private async void ImportBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_backupOperationInProgress) return;
        if (ReaderPane.Visibility == Visibility.Visible)
        {
            await ShowMessageAsync("请先返回书库", "导入备份前请先关闭阅读器，避免正在保存的阅读记录被打断。 ");
            return;
        }

        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.ViewMode = PickerViewMode.List;
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(AppBackupService.FileExtension);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = ((FrameworkElement)Content).XamlRoot,
            Title = "导入 Kkindle 备份？",
            Content = "导入会覆盖当前书库、封面和阅读记录。当前数据会先保留到临时回滚目录；AI API Key 和 SMTP 密码不会被备份覆盖。",
            PrimaryButtonText = "覆盖并导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await ImportBackupAsync(file.Path);
    }

    private async Task ExportBackupAsync(string destinationPath)
    {
        _backupOperationInProgress = true;
        SetBackupStatus("正在导出书库和设置，请稍候…");
        TaskProgress.IsIndeterminate = true;
        ShowTaskProgressPopup();
        try
        {
            var result = await _backupService.ExportAsync(destinationPath);
            SetBackupStatus($"已导出 {result.BookCount} 本书、{result.FileCount} 个文件：{destinationPath}");
        }
        catch (OperationCanceledException)
        {
            SetBackupStatus("备份导出已取消。 ");
        }
        catch (Exception exception)
        {
            SetBackupStatus($"导出失败：{exception.Message}");
            await ShowMessageAsync("导出失败", exception.Message);
        }
        finally
        {
            TaskProgress.IsIndeterminate = false;
            HideTaskProgressPopup();
            _backupOperationInProgress = false;
        }
    }

    private async Task ImportBackupAsync(string sourcePath)
    {
        _backupOperationInProgress = true;
        SetBackupStatus("正在校验并导入备份，请稍候…");
        TaskProgress.IsIndeterminate = true;
        ShowTaskProgressPopup();
        try
        {
            var result = await _backupService.ImportAsync(sourcePath);
            await _library.InitializeAsync();
            await _readerData.InitializeAsync();
            _readerAiSettings = result.AiSettings;
            _kindleEmailSettings = result.KindleEmailSettings;
            UpdateReaderAiHeader();

            _selectedBook = null;
            DetailPane.Visibility = Visibility.Collapsed;
            DetailColumn.Width = new GridLength(0);
            HideSettingsPanel();
            await RefreshLibraryAsync();
            SetBackupStatus($"已导入 {result.BookCount} 本书、{result.FileCount} 个文件：{sourcePath}");
        }
        catch (OperationCanceledException)
        {
            SetBackupStatus("备份导入已取消。 ");
        }
        catch (Exception exception)
        {
            SetBackupStatus($"导入失败：{exception.Message}");
            await ShowMessageAsync("导入失败", exception.Message);
        }
        finally
        {
            TaskProgress.IsIndeterminate = false;
            HideTaskProgressPopup();
            _backupOperationInProgress = false;
        }
    }

    private void SetBackupStatus(string message)
    {
        if (SettingsBackupStatusText is not null)
            SettingsBackupStatusText.Text = message;
        if (TaskStatusText is not null)
            TaskStatusText.Text = message;
    }
}
