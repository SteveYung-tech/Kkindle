using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Kkindle;

public partial class MainWindow : Window
{
    // Square outline for "maximise", two offset outlines for "restore". Drawn
    // rather than taken from Segoe MDL2 Assets, which exists only on Windows.
    private const string MaximizeGlyphData = "M 0.5,0.5 H 9.5 V 9.5 H 0.5 Z";
    private const string RestoreGlyphData = "M 2.5,0.5 H 9.5 V 7.5 M 0.5,2.5 H 7.5 V 9.5 H 0.5 Z";

    public MainWindow()
    {
        InitializeComponent();
        UpdateMaximizeGlyph();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty
            && MaximizeWindowGlyph is not null
            && MaximizeWindowButton is not null)
        {
            UpdateMaximizeGlyph();
        }
    }

    private void TitleBarDragRegion_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        // The extended client area takes the system title bar away, and with it
        // the double-click-to-maximise gesture users expect.
        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }

        BeginMoveDrag(e);
    }

    private void MinimizeWindowButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeWindowButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ToggleMaximized();

    private void CloseWindowButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => Close();

    private void ToggleMaximized() => WindowState = WindowState == WindowState.Maximized
        ? WindowState.Normal
        : WindowState.Maximized;

    private void UpdateMaximizeGlyph()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeWindowGlyph.Data = Geometry.Parse(isMaximized ? RestoreGlyphData : MaximizeGlyphData);
        AutomationProperties.SetName(MaximizeWindowButton, isMaximized ? "还原" : "最大化");
    }
}
