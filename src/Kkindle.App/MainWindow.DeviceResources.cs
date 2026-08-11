using Kkindle.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Kkindle;

public sealed partial class MainWindow
{
    private KindleResourceKind _deviceResourceKind = KindleResourceKind.Font;
    private KindleDeviceResource? _selectedDeviceResource;
    private bool _deviceResourceOperationInProgress;
    private CancellationTokenSource? _deviceResourceCancellation;
    private CancellationTokenSource? _deviceResourceScanCancellation;
    private string? _scannedResourceDeviceId;
    private KindleResourceKind? _scannedResourceKind;

    private async Task OpenDeviceResourcePageAsync(KindleResourceKind kind)
    {
        _deviceResourceKind = kind;
        _scannedResourceKind = null;
        SetActiveNavigation(kind == KindleResourceKind.Font
            ? FontManagementNavigationButton
            : DictionaryManagementNavigationButton);
        LibraryPane.Visibility = Visibility.Collapsed;
        SettingsPane.Visibility = Visibility.Collapsed;
        DevicePage.Visibility = Visibility.Collapsed;
        ReadingMaterialsPage.Visibility = Visibility.Collapsed;
        ReadingDashboardPage.Visibility = Visibility.Collapsed;
        ZLibraryPage.Visibility = Visibility.Collapsed;
        DetailPane.Visibility = Visibility.Collapsed;
        DetailColumn.Width = new GridLength(0);
        DeviceResourcePage.Visibility = Visibility.Visible;
        DeviceResourcePageTitle.Text = kind == KindleResourceKind.Font ? "Kindle 字体" : "Kindle 字典";
        DeviceResourcePathText.Text = kind == KindleResourceKind.Font ? @"Kindle\fonts" : @"Kindle\documents\dictionaries";
        DeviceResourceSafetyText.Text = kind == KindleResourceKind.Font
            ? "仅读写 Kindle 的 fonts 目录；支持 TTF、OTF。导入或删除后建议断开设备并重启 Kindle。"
            : "仅读写 Kindle 的 documents\\dictionaries 目录；支持 AZW、AZW3、MOBI、KFX。删除前请确认不是当前正在使用的主词典。";
        await RefreshDeviceResourcesAsync();
    }

    private Task RefreshDeviceResourcesAsync() =>
        TrackDeviceOperationAsync(RefreshDeviceResourcesCoreAsync);

    private async Task RefreshDeviceResourcesCoreAsync()
    {
        _selectedDeviceResource = null;
        DeviceResourceList.SelectedItem = null;
        ExportDeviceResourceButton.IsEnabled = false;
        DeleteDeviceResourceButton.IsEnabled = false;
        DeviceResources.Clear();
        if (_devices.Count == 0)
        {
            DeviceResourceDeviceText.Text = "未检测到设备";
            DeviceResourceStatusText.Text = "请先连接并允许 Kkindle 访问 Kindle";
            DeviceResourceCountText.Text = "0 个文件";
            DeviceResourceEmptyText.Visibility = Visibility.Visible;
            ImportDeviceResourceButton.IsEnabled = false;
            return;
        }

        _deviceResourceScanCancellation?.Cancel();
        _deviceResourceScanCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _deviceResourceScanCancellation = cancellation;
        var device = _devices[0];
        ImportDeviceResourceButton.IsEnabled = !_deviceResourceOperationInProgress;
        DeviceResourceDeviceText.Text = $"{device.Name} · {device.ConnectionLabel}";
        DeviceResourceStatusText.Text = "正在读取设备目录…";
        try
        {
            var resources = await _kindle.ScanResourcesAsync(device, _deviceResourceKind, cancellation.Token);
            foreach (var resource in resources) DeviceResources.Add(resource);
            DeviceResourceCountText.Text = $"{resources.Count} 个文件";
            DeviceResourceEmptyText.Visibility = resources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            DeviceResourceStatusText.Text = resources.Count == 0
                ? "目录可访问，目前没有匹配的文件"
                : $"已读取 {resources.Count} 个文件";
            _scannedResourceDeviceId = device.Identity;
            _scannedResourceKind = _deviceResourceKind;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            DeviceResourceEmptyText.Visibility = Visibility.Visible;
            DeviceResourceStatusText.Text = $"读取失败：{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_deviceResourceScanCancellation, cancellation))
            {
                _deviceResourceScanCancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private void DeviceResourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedDeviceResource = DeviceResourceList.SelectedItem as KindleDeviceResource;
        var enabled = _selectedDeviceResource is not null && !_deviceResourceOperationInProgress;
        ExportDeviceResourceButton.IsEnabled = enabled;
        DeleteDeviceResourceButton.IsEnabled = enabled;
    }

    private async void RefreshDeviceResourcesButton_Click(object sender, RoutedEventArgs e)
    {
        if (_deviceResourceOperationInProgress) return;
        _ignoredDeviceId = null;
        _scannedResourceDeviceId = null;
        await RefreshDevicesAsync();
        if (_scannedResourceDeviceId is null) await RefreshDeviceResourcesAsync();
    }

    private async void ImportDeviceResourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.Count == 0 || _deviceResourceOperationInProgress) return;
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var extensions = _deviceResourceKind == KindleResourceKind.Font
            ? new[] { ".ttf", ".otf" }
            : new[] { ".azw", ".azw3", ".mobi", ".kfx" };
        foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;

        await RunDeviceResourceOperationAsync(async (device, cancellationToken) =>
        {
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                DeviceResourceStatusText.Text = $"正在导入 {index + 1}/{files.Count}：{file.Name}";
                var progress = new Progress<TransferProgress>(value =>
                    DeviceResourceStatusText.Text = $"{value.Message} · {value.Percentage:0}%");
                await _kindle.SendResourceAsync(device, _deviceResourceKind, file.Path, progress, cancellationToken);
            }
            DeviceResourceStatusText.Text = $"已导入 {files.Count} 个文件";
        });
    }

    private async void ExportDeviceResourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.Count == 0 || _selectedDeviceResource is null || _deviceResourceOperationInProgress) return;
        var selected = _selectedDeviceResource;
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var extension = Path.GetExtension(selected.FileName);
        picker.FileTypeChoices.Add(selected.Kind == KindleResourceKind.Font ? "字体" : "Kindle 字典", [extension]);
        picker.SuggestedFileName = Path.GetFileNameWithoutExtension(selected.FileName);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await RunDeviceResourceOperationAsync(async (device, cancellationToken) =>
        {
            DeviceResourceStatusText.Text = $"正在导出 {selected.FileName}…";
            await _kindle.ExportResourceAsync(device, selected, file.Path, cancellationToken);
            DeviceResourceStatusText.Text = $"已导出到 {file.Path}";
        }, refreshAfter: false);
    }

    private async void DeleteDeviceResourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (_devices.Count == 0 || _selectedDeviceResource is null || _deviceResourceOperationInProgress) return;
        var selected = _selectedDeviceResource;
        if (!await ShowDevicePromptAsync(
                selected.Kind == KindleResourceKind.Font ? "删除 Kindle 字体？" : "删除 Kindle 字典？",
                $"将永久删除设备文件：\n{selected.RelativePath}\n\n操作范围仅限对应的字体或字典目录。",
                "删除",
                "取消")) return;
        await RunDeviceResourceOperationAsync(async (device, cancellationToken) =>
        {
            await _kindle.RemoveResourceAsync(device, selected, cancellationToken);
            DeviceResourceStatusText.Text = $"已删除 {selected.FileName}";
        });
    }

    private Task RunDeviceResourceOperationAsync(
        Func<KindleDevice, CancellationToken, Task> operation,
        bool refreshAfter = true)
        => TrackDeviceOperationAsync(
            () => RunDeviceResourceOperationCoreAsync(operation, refreshAfter));

    private async Task RunDeviceResourceOperationCoreAsync(
        Func<KindleDevice, CancellationToken, Task> operation,
        bool refreshAfter)
    {
        if (_devices.Count == 0 || _deviceResourceOperationInProgress) return;
        _deviceResourceOperationInProgress = true;
        _deviceResourceCancellation?.Cancel();
        _deviceResourceCancellation?.Dispose();
        _deviceResourceCancellation = new CancellationTokenSource();
        ImportDeviceResourceButton.IsEnabled = false;
        ExportDeviceResourceButton.IsEnabled = false;
        DeleteDeviceResourceButton.IsEnabled = false;
        try
        {
            await operation(_devices[0], _deviceResourceCancellation.Token);
            if (refreshAfter) await RefreshDeviceResourcesAsync();
        }
        catch (Exception exception)
        {
            DeviceResourceStatusText.Text = $"操作失败：{exception.Message}";
        }
        finally
        {
            _deviceResourceCancellation?.Dispose();
            _deviceResourceCancellation = null;
            _deviceResourceOperationInProgress = false;
            ImportDeviceResourceButton.IsEnabled = _devices.Count > 0;
            var selected = DeviceResourceList.SelectedItem is not null;
            ExportDeviceResourceButton.IsEnabled = selected;
            DeleteDeviceResourceButton.IsEnabled = selected;
        }
    }
}
