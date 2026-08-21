using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

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

    /// <summary>
    /// Foreground used to repaint the selected range. Selection colors must
    /// stay out of layout: base SelectableTextBlock feeds this brush into the
    /// TextLayout as a per-run style override, and those overrides split the
    /// text runs exactly at the selection boundaries. When such a boundary
    /// lands on a soft line break, the reshaped runs measure slightly
    /// differently and the paragraph re-wraps mid-gesture. This control keeps
    /// the layout free of overrides and paints the inversion on top of the
    /// finished text lines instead.
    /// </summary>
    public static readonly StyledProperty<IBrush?> InvertedSelectionForegroundProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackTextBlock, IBrush?>(
            nameof(InvertedSelectionForeground));

    /// <summary>
    /// Backing painted under the inverted glyphs. Paragraphs that carry inline
    /// footnote markers keep the plain SelectionBrush highlight instead: their
    /// runs are shaped around embedded controls, so repainting them from a
    /// separate layout cannot be guaranteed to line up.
    /// </summary>
    public static readonly StyledProperty<IBrush?> InvertedSelectionBackgroundProperty =
        AvaloniaProperty.Register<ReaderLinuxTextFallbackTextBlock, IBrush?>(
            nameof(InvertedSelectionBackground));

    public MainWindow.ReaderLinuxTextFallbackBlock? Block
    {
        get => GetValue(BlockProperty);
        set => SetValue(BlockProperty, value);
    }

    public IBrush? InvertedSelectionForeground
    {
        get => GetValue(InvertedSelectionForegroundProperty);
        set => SetValue(InvertedSelectionForegroundProperty, value);
    }

    public IBrush? InvertedSelectionBackground
    {
        get => GetValue(InvertedSelectionBackgroundProperty);
        set => SetValue(InvertedSelectionBackgroundProperty, value);
    }

    public event EventHandler<PointerEventArgs>? FootnotePointerEntered;
    public event EventHandler<PointerEventArgs>? FootnotePointerExited;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BlockProperty)
            RebuildContent();
    }

    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        // Highlight rectangles plus every glyph in the normal ink color.
        base.RenderTextLayout(context, origin);

        var inverted = InvertedSelectionForeground;
        var backing = InvertedSelectionBackground ?? SelectionBrush;
        if (inverted is null || backing is null || Inlines is { Count: > 0 })
            return;

        var selectionStart = Math.Min(SelectionStart, SelectionEnd);
        var selectionLength = Math.Abs(SelectionEnd - SelectionStart);
        if (selectionLength <= 0)
            return;

        var layout = TextLayout;
        if (layout is null)
            return;

        var swapped = CreateInvertedLayout(inverted);
        foreach (var rect in layout.HitTestTextRange(selectionStart, selectionLength))
        {
            var snapped = PixelRect.FromRect(rect, 1).ToRect(1);
            using (context.PushTransform(Matrix.CreateTranslation(origin)))
            {
                context.FillRectangle(backing, snapped);
                using (context.PushClip(snapped))
                {
                    swapped.Draw(context, new Point(0, 0));
                }
            }
        }
    }

    // Same shaping inputs as the regular layout with one exception: the
    // foreground brush. No style overrides are attached, so both layouts wrap
    // identically and the inverted glyphs land exactly on the inked ones.
    private TextLayout CreateInvertedLayout(IBrush foreground)
    {
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var defaultProperties = new GenericTextRunProperties(
            typeface,
            FontSize,
            TextDecorations,
            foreground,
            fontFeatures: FontFeatures);
        var paragraphProperties = new GenericTextParagraphProperties(
            FlowDirection,
            TextAlignment,
            true,
            false,
            defaultProperties,
            TextWrapping,
            LineHeight,
            0,
            LetterSpacing);
        var maxSize = GetMaxSizeFromConstraint();
        return new TextLayout(
            new SimpleTextSource(Text ?? string.Empty, defaultProperties),
            paragraphProperties,
            TextTrimming,
            maxSize.Width,
            maxSize.Height,
            MaxLines);
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
