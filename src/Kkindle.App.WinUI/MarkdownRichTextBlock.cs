using System.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Kkindle;

public sealed class MarkdownRichTextBlock : Grid
{
    private static readonly SolidColorBrush InkBrush = new(Colors.Black);
    private static readonly SolidColorBrush MutedBrush = new(ColorHelper.FromArgb(255, 92, 92, 92));
    private static readonly SolidColorBrush CodeBackgroundBrush = new(ColorHelper.FromArgb(255, 244, 244, 241));
    private string _text = string.Empty;
    private bool _renderMarkdown;
    private readonly RichTextBlock _view;

    public MarkdownRichTextBlock()
    {
        _view = new RichTextBlock
        {
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap
        };
        Children.Add(_view);
    }

    public void SetContent(string? text, bool renderMarkdown)
    {
        text ??= string.Empty;
        if (_text == text && _renderMarkdown == renderMarkdown) return;
        _text = text;
        _renderMarkdown = renderMarkdown;
        Render();
    }

    private void Render()
    {
        _view.Blocks.Clear();
        if (!_renderMarkdown)
        {
            AddParagraph(_text, 12, FontWeights.Normal);
            return;
        }

        var lines = _text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var paragraph = new StringBuilder();
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph(paragraph);
                var code = new StringBuilder();
                index++;
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    if (code.Length > 0) code.AppendLine();
                    code.Append(lines[index]);
                    index++;
                }
                AddCodeBlock(code.ToString());
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph(paragraph);
                continue;
            }

            var headingLevel = CountHeadingMarkers(trimmed);
            if (headingLevel > 0)
            {
                FlushParagraph(paragraph);
                AddParagraph(
                    trimmed[(headingLevel + 1)..],
                    Math.Max(13, 20 - headingLevel),
                    FontWeights.SemiBold,
                    new Thickness(0, _view.Blocks.Count == 0 ? 0 : 5, 0, 2));
                continue;
            }

            if (IsHorizontalRule(trimmed))
            {
                FlushParagraph(paragraph);
                AddParagraph("────────────────", 10, FontWeights.Normal, new Thickness(0, 3, 0, 3), MutedBrush);
                continue;
            }

            if (trimmed.StartsWith("> ", StringComparison.Ordinal) || trimmed == ">")
            {
                FlushParagraph(paragraph);
                AddParagraph($"│ {trimmed.TrimStart('>').TrimStart()}", 11, FontWeights.Normal, new Thickness(3, 2, 0, 3), MutedBrush);
                continue;
            }

            if (TryGetListItem(trimmed, out var prefix, out var itemText))
            {
                FlushParagraph(paragraph);
                AddParagraph(prefix + itemText, 12, FontWeights.Normal, new Thickness(8, 1, 0, 2));
                continue;
            }

            if (paragraph.Length > 0) paragraph.Append(' ');
            paragraph.Append(trimmed);
        }
        FlushParagraph(paragraph);
    }

    private void FlushParagraph(StringBuilder text)
    {
        if (text.Length == 0) return;
        AddParagraph(text.ToString(), 12, FontWeights.Normal, new Thickness(0, 0, 0, 4));
        text.Clear();
    }

    private void AddParagraph(
        string text,
        double fontSize,
        FontWeight fontWeight,
        Thickness? margin = null,
        Brush? foreground = null)
    {
        var paragraph = new Paragraph
        {
            FontSize = fontSize,
            FontWeight = fontWeight,
            Foreground = foreground ?? InkBrush,
            LineHeight = Math.Max(18, fontSize + 8),
            Margin = margin ?? new Thickness(0)
        };
        AddInlineMarkdown(paragraph.Inlines, text);
        _view.Blocks.Add(paragraph);
    }

    private void AddCodeBlock(string code)
    {
        var codeText = new TextBlock
        {
            Text = code,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 10,
            LineHeight = 17,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = InkBrush
        };
        var paragraph = new Paragraph { Margin = new Thickness(0, 3, 0, 6) };
        paragraph.Inlines.Add(new InlineUIContainer
        {
            Child = new Border
            {
                Padding = new Thickness(8, 6, 8, 7),
                Background = CodeBackgroundBrush,
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 218, 218, 214)),
                BorderThickness = new Thickness(1),
                Child = codeText
            }
        });
        _view.Blocks.Add(paragraph);
    }

    private static void AddInlineMarkdown(InlineCollection target, string text)
    {
        var cursor = 0;
        while (cursor < text.Length)
        {
            var match = FindNextInline(text, cursor);
            if (match.Start < 0)
            {
                target.Add(new Run { Text = text[cursor..] });
                break;
            }
            if (match.Start > cursor) target.Add(new Run { Text = text[cursor..match.Start] });

            var inner = text.Substring(match.ContentStart, match.ContentLength);
            switch (match.Kind)
            {
                case InlineKind.Bold:
                    var bold = new Bold();
                    AddInlineMarkdown(bold.Inlines, inner);
                    target.Add(bold);
                    break;
                case InlineKind.Italic:
                    var italic = new Italic();
                    AddInlineMarkdown(italic.Inlines, inner);
                    target.Add(italic);
                    break;
                case InlineKind.Code:
                    target.Add(new Run
                    {
                        Text = inner,
                        FontFamily = new FontFamily("Consolas"),
                        Foreground = InkBrush
                    });
                    break;
                case InlineKind.Link:
                    var link = new Hyperlink();
                    AddInlineMarkdown(link.Inlines, inner);
                    if (Uri.TryCreate(match.Link, UriKind.Absolute, out var uri)
                        && uri.Scheme is "http" or "https" or "mailto")
                        link.NavigateUri = uri;
                    target.Add(link);
                    break;
            }
            cursor = match.End;
        }
    }

    private static InlineMatch FindNextInline(string text, int start)
    {
        var best = InlineMatch.None;
        Consider("**", "**", InlineKind.Bold);
        Consider("__", "__", InlineKind.Bold);
        Consider("`", "`", InlineKind.Code);
        Consider("*", "*", InlineKind.Italic);
        Consider("_", "_", InlineKind.Italic);

        var linkStart = text.IndexOf('[', start);
        if (linkStart >= 0)
        {
            var labelEnd = text.IndexOf("](", linkStart + 1, StringComparison.Ordinal);
            var linkEnd = labelEnd >= 0 ? text.IndexOf(')', labelEnd + 2) : -1;
            if (labelEnd > linkStart + 1 && linkEnd > labelEnd + 2 && (best.Start < 0 || linkStart < best.Start))
            {
                best = new InlineMatch(
                    InlineKind.Link,
                    linkStart,
                    linkEnd + 1,
                    linkStart + 1,
                    labelEnd - linkStart - 1,
                    text[(labelEnd + 2)..linkEnd]);
            }
        }
        return best;

        void Consider(string open, string close, InlineKind kind)
        {
            var openIndex = text.IndexOf(open, start, StringComparison.Ordinal);
            if (openIndex < 0 || (best.Start >= 0 && openIndex >= best.Start)) return;
            var contentStart = openIndex + open.Length;
            var closeIndex = text.IndexOf(close, contentStart, StringComparison.Ordinal);
            if (closeIndex <= contentStart) return;
            best = new InlineMatch(kind, openIndex, closeIndex + close.Length, contentStart, closeIndex - contentStart, null);
        }
    }

    private static int CountHeadingMarkers(string text)
    {
        var count = 0;
        while (count < text.Length && count < 6 && text[count] == '#') count++;
        return count > 0 && count < text.Length && text[count] == ' ' ? count : 0;
    }

    private static bool IsHorizontalRule(string text)
    {
        var compact = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        return compact.Length >= 3 && (compact.All(character => character == '-')
            || compact.All(character => character == '*')
            || compact.All(character => character == '_'));
    }

    private static bool TryGetListItem(string text, out string prefix, out string item)
    {
        if (text.Length > 2 && text[1] == ' ' && text[0] is '-' or '*' or '+')
        {
            prefix = "•  ";
            item = text[2..];
            return true;
        }
        var digits = 0;
        while (digits < text.Length && char.IsDigit(text[digits])) digits++;
        if (digits > 0 && digits + 1 < text.Length && text[digits] == '.' && text[digits + 1] == ' ')
        {
            prefix = text[..(digits + 1)] + "  ";
            item = text[(digits + 2)..];
            return true;
        }
        prefix = string.Empty;
        item = string.Empty;
        return false;
    }

    private enum InlineKind { Bold, Italic, Code, Link }

    private readonly record struct InlineMatch(
        InlineKind Kind,
        int Start,
        int End,
        int ContentStart,
        int ContentLength,
        string? Link)
    {
        public static InlineMatch None => new(default, -1, -1, -1, 0, null);
    }
}
