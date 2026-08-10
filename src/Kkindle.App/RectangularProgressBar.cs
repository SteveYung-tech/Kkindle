using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;

namespace Kkindle;

public sealed class RectangularProgressBar : Grid
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(RectangularProgressBar),
        new PropertyMetadata(0d, OnProgressPropertyChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum),
        typeof(double),
        typeof(RectangularProgressBar),
        new PropertyMetadata(0d, OnProgressPropertyChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum),
        typeof(double),
        typeof(RectangularProgressBar),
        new PropertyMetadata(100d, OnProgressPropertyChanged));

    private static readonly SolidColorBrush InkBrush = new(Color.FromArgb(255, 0, 0, 0));
    private readonly Grid _track;
    private readonly Rectangle _indicator;

    public RectangularProgressBar()
    {
        IsHitTestVisible = false;

        _indicator = new Rectangle
        {
            Fill = InkBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _track = new Grid();
        _track.Children.Add(_indicator);
        _track.SizeChanged += (_, _) => UpdateIndicator();

        Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = InkBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(1),
            Child = _track
        });
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    private static void OnProgressPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((RectangularProgressBar)sender).UpdateIndicator();

    private void UpdateIndicator()
    {
        var range = Maximum - Minimum;
        var ratio = range <= 0 ? 0 : Math.Clamp((Value - Minimum) / range, 0, 1);
        _indicator.Width = _track.ActualWidth * ratio;
    }
}
