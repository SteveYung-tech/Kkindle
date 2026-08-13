using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Kkindle;

public sealed partial class MainWindow
{
    private void QueueInteractiveControlToolTipRefresh()
    {
        DispatcherQueue.TryEnqueue(() => EnsureInteractiveControlToolTips(RootGrid));
    }

    private static void EnsureInteractiveControlToolTips(DependencyObject root)
    {
        if (root is FrameworkElement element && ToolTipService.GetToolTip(element) is null)
        {
            var text = BuildControlToolTip(element);
            if (!string.IsNullOrWhiteSpace(text))
            {
                ToolTipService.SetToolTip(element, text);
                if (string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(element)))
                    AutomationProperties.SetHelpText(element, text);
            }
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            EnsureInteractiveControlToolTips(VisualTreeHelper.GetChild(root, index));
    }

    private static string? BuildControlToolTip(FrameworkElement element)
    {
        var helpText = AutomationProperties.GetHelpText(element);
        if (!string.IsNullOrWhiteSpace(helpText)) return helpText.Trim();
        var accessibleName = AutomationProperties.GetName(element);

        return element switch
        {
            ButtonBase button => FirstNonEmpty(
                accessibleName,
                ReadContentText(button.Content)),
            ComboBox comboBox => DescribeField(accessibleName, ReadContentText(comboBox.Header), "选择选项"),
            NumberBox numberBox => DescribeField(accessibleName, ReadContentText(numberBox.Header), "输入或调整数值"),
            TextBox textBox => DescribeField(
                accessibleName,
                ReadContentText(textBox.Header),
                string.IsNullOrWhiteSpace(textBox.PlaceholderText) ? "输入文本" : textBox.PlaceholderText),
            PasswordBox passwordBox => DescribeField(
                accessibleName,
                ReadContentText(passwordBox.Header),
                string.IsNullOrWhiteSpace(passwordBox.PlaceholderText) ? "输入密码" : passwordBox.PlaceholderText),
            Slider slider => DescribeField(accessibleName, ReadContentText(slider.Header), "拖动以调整数值"),
            ToggleSwitch toggleSwitch => DescribeField(accessibleName, ReadContentText(toggleSwitch.Header), "切换开关"),
            DatePicker datePicker => DescribeField(accessibleName, ReadContentText(datePicker.Header), "选择日期"),
            TimePicker timePicker => DescribeField(accessibleName, ReadContentText(timePicker.Header), "选择时间"),
            AutoSuggestBox suggestBox => DescribeField(
                accessibleName,
                ReadContentText(suggestBox.Header),
                string.IsNullOrWhiteSpace(suggestBox.PlaceholderText) ? "输入并选择建议" : suggestBox.PlaceholderText),
            _ => null
        };
    }

    private static string? DescribeField(string? accessibleName, string? header, string action)
    {
        var label = FirstNonEmpty(accessibleName, header);
        return string.IsNullOrWhiteSpace(label) ? action : $"{label}：{action}";
    }

    private static string? ReadContentText(object? content) => content switch
    {
        string text => text.Trim(),
        TextBlock textBlock => textBlock.Text?.Trim(),
        _ => null
    };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
