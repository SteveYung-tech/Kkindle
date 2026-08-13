using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Kkindle;

/// <summary>
/// 简洁的下载按钮可视部分：默认显示“下载”；下载中按进度从左到右用黑色填充，
/// 百分比文字在填充边界处实时反色；完成后整块变黑、白字显示“下载导入完成”。
/// 只负责绘制，不处理点击（由外层 Button 处理）。
/// </summary>
public sealed class TetrisDownloadVisual : Grid
{
    private static readonly SolidColorBrush InkBrush = new(Color.FromArgb(255, 0, 0, 0));
    private static readonly SolidColorBrush PaperBrush = new(Color.FromArgb(255, 255, 255, 255));

    private readonly Rectangle _fillRect;
    private readonly FontIcon _labelIcon;
    private readonly TextBlock _labelText;
    private readonly FontIcon _invertedIcon;
    private readonly TextBlock _invertedText;
    private readonly Grid _invertedLayer;
    private readonly Grid _content;

    public TetrisDownloadVisual()
    {
        IsHitTestVisible = false;

        _fillRect = new Rectangle
        {
            Fill = InkBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch,
            Width = 0
        };

        _labelIcon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Glyph = "\uE896",
            Foreground = InkBrush
        };
        _labelText = new TextBlock
        {
            FontFamily = Application.Current.Resources["DefaultAppFontFamily"] as FontFamily
                ?? new FontFamily("ms-appx:///Assets/Fonts/KingHwaOldSong-v3.0.ttf#KingHwaOldSong"),
            FontSize = 11,
            Foreground = InkBrush,
            Text = "下载",
            VerticalAlignment = VerticalAlignment.Center
        };
        var baseLabel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _labelIcon, _labelText }
        };

        _invertedIcon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 11,
            Glyph = "\uE896",
            Foreground = PaperBrush
        };
        _invertedText = new TextBlock
        {
            FontFamily = Application.Current.Resources["DefaultAppFontFamily"] as FontFamily
                ?? new FontFamily("ms-appx:///Assets/Fonts/KingHwaOldSong-v3.0.ttf#KingHwaOldSong"),
            FontSize = 11,
            Foreground = PaperBrush,
            Text = "下载",
            VerticalAlignment = VerticalAlignment.Center
        };
        var invertedLabel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _invertedIcon, _invertedText }
        };

        // The white copy is clipped to the filled region, so the label text
        // inverts exactly at the progress edge in real time.
        _invertedLayer = new Grid
        {
            IsHitTestVisible = false,
            Children = { invertedLabel }
        };

        _content = new Grid { Children = { _fillRect, baseLabel, _invertedLayer } };
        _content.SizeChanged += (_, _) => UpdateFillWidth();

        Children.Add(new Border
        {
            Background = PaperBrush,
            BorderBrush = InkBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Child = _content
        });

        UpdateLabel();
        UpdateFillWidth();
    }

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(TetrisDownloadVisual),
        new PropertyMetadata(0d, OnStatePropertyChanged));

    public static readonly DependencyProperty IsDownloadingProperty = DependencyProperty.Register(
        nameof(IsDownloading),
        typeof(bool),
        typeof(TetrisDownloadVisual),
        new PropertyMetadata(false, OnStatePropertyChanged));

    public static readonly DependencyProperty IsCompletedProperty = DependencyProperty.Register(
        nameof(IsCompleted),
        typeof(bool),
        typeof(TetrisDownloadVisual),
        new PropertyMetadata(false, OnStatePropertyChanged));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool IsDownloading
    {
        get => (bool)GetValue(IsDownloadingProperty);
        set => SetValue(IsDownloadingProperty, value);
    }

    public bool IsCompleted
    {
        get => (bool)GetValue(IsCompletedProperty);
        set => SetValue(IsCompletedProperty, value);
    }

    private static void OnStatePropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((TetrisDownloadVisual)sender).RefreshState();

    private void RefreshState()
    {
        UpdateFillWidth();
        UpdateLabel();
    }

    private void UpdateFillWidth()
    {
        var ratio = IsCompleted ? 1d : Math.Clamp(Value / 100d, 0d, 1d);
        var width = Math.Max(0, _content.ActualWidth * ratio);
        var height = Math.Max(0, _content.ActualHeight);
        _fillRect.Width = width;
        _invertedLayer.Clip = new RectangleGeometry { Rect = new Rect(0, 0, width, height) };
    }

    private void UpdateLabel()
    {
        var (glyph, text) = IsCompleted
            ? ("\uE8FB", "下载导入完成")
            : IsDownloading
                ? ("\uE896", $"{Math.Clamp((int)Math.Round(Value), 0, 100)}%")
                : ("\uE896", "下载");

        _labelIcon.Glyph = glyph;
        _labelText.Text = text;
        _invertedIcon.Glyph = glyph;
        _invertedText.Text = text;
        _labelIcon.Visibility = IsDownloading ? Visibility.Collapsed : Visibility.Visible;
        _invertedIcon.Visibility = IsDownloading ? Visibility.Collapsed : Visibility.Visible;
    }
}
