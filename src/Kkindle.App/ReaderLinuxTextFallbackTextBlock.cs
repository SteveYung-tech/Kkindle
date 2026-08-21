using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;

namespace Kkindle;

/// <summary>
/// Selectable fallback text that can keep a footnote marker inside the same
/// paragraph instead of giving it a separate block in the recovery surface.
/// </summary>
public sealed class ReaderLinuxTextFallbackTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<MainWindow.ReaderLinuxTextFallbackBlock?> BlockProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackTextBlock, MainWindow.ReaderLinuxTextFallbackBlock?>(
            nameof(Block));

    public MainWindow.ReaderLinuxTextFallbackBlock? Block
    {
        get => GetValue(BlockProperty);
        set => SetValue(BlockProperty, value);
    }

    public event EventHandler<PointerEventArgs>? FootnotePointerEntered;
    public event EventHandler<PointerEventArgs>? FootnotePointerExited;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BlockProperty)
            RebuildContent();
    }

    private void RebuildContent()
    {
        Inlines?.Clear();
        var block = Block;
        if (block is null)
        {
            Text = string.Empty;
            return;
        }

        if (!block.HasInlineFootnotes)
        {
            Text = block.Text;
            return;
        }

        Text = string.Empty;
        Inlines?.Clear();
        var footnoteIndex = 0;
        var textStart = 0;
        var text = block.Text;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != MainWindow.ReaderLinuxTextFallbackFootnoteMarker)
                continue;

            AddRun(text[textStart..index]);
            if (footnoteIndex < block.InlineFootnotes.Count)
            {
                var footnote = block.InlineFootnotes[footnoteIndex++];
                Inlines?.Add(new InlineUIContainer(CreateFootnoteButton(footnote)));
            }
            else
            {
                AddRun("注");
            }
            textStart = index + 1;
        }

        AddRun(text[textStart..]);
    }

    private void AddRun(string value)
    {
        if (value.Length > 0)
            Inlines?.Add(new Run(value));
    }

    private Button CreateFootnoteButton(MainWindow.ReaderLinuxTextFallbackFootnote footnote)
    {
        var button = new Button
        {
            Content = footnote.Label,
            Tag = footnote.Href,
            Focusable = true,
            Padding = new Thickness(2, 0),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        button.Classes.Add("readerFootnoteMarker");
        button.PointerEntered += HandleFootnotePointerEntered;
        button.PointerExited += HandleFootnotePointerExited;
        return button;
    }

    private void HandleFootnotePointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Button button)
            FootnotePointerEntered?.Invoke(button, e);
    }

    private void HandleFootnotePointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Button button)
            FootnotePointerExited?.Invoke(button, e);
    }

}
