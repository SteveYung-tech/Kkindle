using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Kkindle;

public enum MonochromeBarChartOrientation
{
    Columns,
    Horizontal
}

public sealed record MonochromeChartValue(string Label, double Value, string ValueLabel);

public sealed class MonochromeBarChart : Grid
{
    private static readonly SolidColorBrush Ink = new(Color.FromArgb(255, 0, 0, 0));
    private static readonly SolidColorBrush Muted = new(Color.FromArgb(255, 92, 92, 92));
    private static readonly SolidColorBrush Track = new(Color.FromArgb(255, 224, 224, 224));
    private readonly Canvas _canvas = new();
    private IReadOnlyList<MonochromeChartValue> _values = [];
    private MonochromeBarChartOrientation _orientation;

    public MonochromeBarChart()
    {
        Children.Add(_canvas);
        SizeChanged += (_, _) => Redraw();
    }

    public void SetData(
        IEnumerable<MonochromeChartValue> values,
        MonochromeBarChartOrientation orientation = MonochromeBarChartOrientation.Columns,
        string? accessibleName = null)
    {
        _values = values.Where(value => double.IsFinite(value.Value) && value.Value >= 0).ToArray();
        _orientation = orientation;
        if (!string.IsNullOrWhiteSpace(accessibleName))
            AutomationProperties.SetName(this, accessibleName);
        Redraw();
    }

    private void Redraw()
    {
        _canvas.Children.Clear();
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 40 || height < 40) return;
        if (_values.Count == 0 || _values.All(value => value.Value <= 0))
        {
            AddText("暂无数据", width / 2, height / 2, centerX: true, centerY: true, Muted);
            return;
        }

        if (_orientation == MonochromeBarChartOrientation.Horizontal)
            DrawHorizontal(width, height);
        else
            DrawColumns(width, height);
    }

    private void DrawColumns(double width, double height)
    {
        const double left = 8;
        const double right = 8;
        const double top = 22;
        const double bottom = 30;
        var plotWidth = Math.Max(1, width - left - right);
        var plotHeight = Math.Max(1, height - top - bottom);
        var maximum = Math.Max(1, _values.Max(value => value.Value));

        for (var line = 0; line <= 3; line++)
        {
            var y = top + plotHeight * line / 3d;
            AddLine(left, y, width - right, y, line == 3 ? Ink : Track, 1);
        }

        var slot = plotWidth / _values.Count;
        var barWidth = Math.Clamp(slot * 0.58, 5, 34);
        for (var index = 0; index < _values.Count; index++)
        {
            var item = _values[index];
            var barHeight = item.Value <= 0 ? 0 : Math.Max(2, plotHeight * item.Value / maximum);
            var x = left + slot * index + (slot - barWidth) / 2;
            var y = top + plotHeight - barHeight;
            if (barHeight > 0) AddRectangle(x, y, barWidth, barHeight, Ink);

            var showLabel = _values.Count <= 8 || index % 2 == 0 || index == _values.Count - 1;
            if (showLabel) AddText(item.Label, left + slot * (index + 0.5), height - 15, centerX: true, centerY: true, Muted, 10);
            if (barHeight > 0 && (_values.Count <= 8 || item.Value == maximum))
                AddText(item.ValueLabel, left + slot * (index + 0.5), Math.Max(7, y - 8), centerX: true, centerY: true, Ink, 10);
        }
    }

    private void DrawHorizontal(double width, double height)
    {
        var values = _values.Take(8).ToArray();
        var maximum = Math.Max(1, values.Max(value => value.Value));
        var labelWidth = Math.Clamp(width * 0.31, 72, 130);
        const double valueWidth = 54;
        const double gap = 8;
        var trackLeft = labelWidth + gap;
        var trackWidth = Math.Max(20, width - trackLeft - valueWidth - gap);
        var rowHeight = height / values.Length;
        var barHeight = Math.Clamp(rowHeight * 0.34, 7, 14);

        for (var index = 0; index < values.Length; index++)
        {
            var item = values[index];
            var centerY = rowHeight * (index + 0.5);
            AddTrimmedText(item.Label, 0, centerY, labelWidth, Muted);
            AddRectangle(trackLeft, centerY - barHeight / 2, trackWidth, barHeight, Track);
            AddRectangle(trackLeft, centerY - barHeight / 2, trackWidth * item.Value / maximum, barHeight, Ink);
            AddText(item.ValueLabel, width - valueWidth, centerY, centerY: true, brush: Ink, fontSize: 10);
        }
    }

    private void AddRectangle(double x, double y, double width, double height, Brush fill)
    {
        var rectangle = new Rectangle { Width = Math.Max(0, width), Height = Math.Max(0, height), Fill = fill };
        Canvas.SetLeft(rectangle, x);
        Canvas.SetTop(rectangle, y);
        _canvas.Children.Add(rectangle);
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush stroke, double thickness)
    {
        _canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = stroke, StrokeThickness = thickness });
    }

    private void AddTrimmedText(string text, double x, double centerY, double width, Brush brush)
    {
        var label = new TextBlock
        {
            Text = text,
            Width = width,
            FontSize = 10,
            Foreground = brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = FontWeights.Normal
        };
        label.Measure(new Size(width, double.PositiveInfinity));
        Canvas.SetLeft(label, x);
        Canvas.SetTop(label, centerY - label.DesiredSize.Height / 2);
        _canvas.Children.Add(label);
    }

    private void AddText(
        string text,
        double x,
        double y,
        bool centerX = false,
        bool centerY = false,
        Brush? brush = null,
        double fontSize = 11)
    {
        var label = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            Foreground = brush ?? Ink,
            FontWeight = FontWeights.Normal
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(label, centerX ? x - label.DesiredSize.Width / 2 : x);
        Canvas.SetTop(label, centerY ? y - label.DesiredSize.Height / 2 : y - label.DesiredSize.Height);
        _canvas.Children.Add(label);
    }
}
