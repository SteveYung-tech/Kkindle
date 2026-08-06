using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public sealed partial class MainWindow
{
    private void ReaderAiTabButton_Click(object sender, RoutedEventArgs e) => ShowReaderAiTab();

    private void ReaderNotesTabButton_Click(object sender, RoutedEventArgs e) => ShowReaderNotesTab();

    private void ShowReaderAiTab()
    {
        ReaderAiView.Visibility = Visibility.Visible;
        ReaderAiComposer.Visibility = Visibility.Visible;
        ReaderNotesView.Visibility = Visibility.Collapsed;
        SetReaderAssistantTabState(ReaderAiTabButton, selected: true);
        SetReaderAssistantTabState(ReaderNotesTabButton, selected: false);
    }

    private void ShowReaderNotesTab()
    {
        ReaderAiView.Visibility = Visibility.Collapsed;
        ReaderAiComposer.Visibility = Visibility.Collapsed;
        ReaderNotesView.Visibility = Visibility.Visible;
        SetReaderAssistantTabState(ReaderAiTabButton, selected: false);
        SetReaderAssistantTabState(ReaderNotesTabButton, selected: true);
    }

    private static void SetReaderAssistantTabState(Button button, bool selected)
    {
        button.Background = new SolidColorBrush(selected ? Colors.Black : Colors.Transparent);
        button.Foreground = new SolidColorBrush(selected ? Colors.White : ColorHelper.FromArgb(255, 36, 36, 36));
        button.BorderBrush = new SolidColorBrush(selected
            ? Colors.Black
            : Colors.Black);
    }

    private void UpdateReaderAiHeader()
    {
        if (ReaderAiProviderText is null) return;
        var configured = _readerAiSettings.IsConfigured ? _readerAiSettings.Model : "未配置 API Key";
        ReaderAiProviderText.Text = $"{_readerAiSettings.ProviderDisplayName} · {configured}";
    }

    private void ReaderAiSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        _suppressAiProviderChange = true;
        var provider = _readerAiSettings.Provider.Trim().ToLowerInvariant();
        ReaderAiProviderBox.SelectedItem = ReaderAiProviderBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => (item.Tag as string)?.Equals(provider, StringComparison.OrdinalIgnoreCase) == true)
            ?? ReaderAiProviderBox.Items[0];
        ReaderAiBaseUrlBox.Text = _readerAiSettings.BaseUrl;
        ReaderAiModelBox.Text = _readerAiSettings.Model;
        ReaderAiApiKeyBox.Password = _readerAiSettings.ApiKey;
        ReaderAiSettingsStatusText.Text = string.Empty;
        _suppressAiProviderChange = false;
        SetReaderAiSettingsVisible(true);
    }

    private void ReaderAiProviderBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAiProviderChange || ReaderAiProviderBox.SelectedItem is not ComboBoxItem { Tag: string provider })
            return;
        var defaults = AiConnectionSettings.GetDefaults(provider);
        ReaderAiBaseUrlBox.Text = defaults.BaseUrl;
        ReaderAiModelBox.Text = defaults.Model;
        ReaderAiSettingsStatusText.Text = provider == "custom"
            ? "自定义服务使用 OpenAI-compatible Chat Completions。"
            : string.Empty;
    }

    private void ReaderAiSettingsCancelButton_Click(object sender, RoutedEventArgs e)
    {
        SetReaderAiSettingsVisible(false);
        ReaderAiSettingsStatusText.Text = string.Empty;
    }

    private async void ReaderAiSettingsSaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ReaderAiProviderBox.SelectedItem is not ComboBoxItem { Tag: string provider }) return;
        var baseUrl = ReaderAiBaseUrlBox.Text.Trim();
        var model = ReaderAiModelBox.Text.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https"))
        {
            ReaderAiSettingsStatusText.Text = "请输入有效的 HTTP 或 HTTPS Base URL。";
            return;
        }
        if (string.IsNullOrWhiteSpace(model))
        {
            ReaderAiSettingsStatusText.Text = "请输入模型名称。";
            return;
        }

        var settings = new AiConnectionSettings
        {
            Provider = provider,
            BaseUrl = baseUrl,
            Model = model,
            ApiKey = ReaderAiApiKeyBox.Password.Trim()
        };
        try
        {
            ReaderAiSettingsStatusText.Text = "正在安全保存…";
            await _aiSettingsStore.SaveAsync(settings);
            _readerAiSettings = settings;
            UpdateReaderAiHeader();
            SetReaderAiSettingsVisible(false);
            ReaderAiStatusText.Text = settings.IsConfigured
                ? $"已连接配置：{settings.ProviderDisplayName} · {settings.Model}"
                : "设置已保存；发送问题前还需要填写 API Key。";
        }
        catch (Exception exception)
        {
            ReaderAiSettingsStatusText.Text = $"保存失败：{exception.Message}";
        }
    }

    private async void ReaderAiSendButton_Click(object sender, RoutedEventArgs e)
    {
        var question = ReaderAiQuestionBox.Text.Trim();
        if (question.Length == 0) return;
        await SendReaderAiQuestionAsync(question, useBookOverview: false);
    }

    private async void ReaderAiQuestionBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        var controlState = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Control);
        if ((controlState & Windows.UI.Core.CoreVirtualKeyStates.Down) == 0) return;
        e.Handled = true;
        var question = ReaderAiQuestionBox.Text.Trim();
        if (question.Length > 0) await SendReaderAiQuestionAsync(question, useBookOverview: false);
    }

    private async void ReaderAiSummarizeChapterButton_Click(object sender, RoutedEventArgs e)
    {
        await SendReaderAiQuestionAsync("请总结当前章节的核心观点、关键论据和需要记住的结论。", useBookOverview: false);
    }

    private async void ReaderAiExplainSelectionButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = (await ExecuteReaderStringScriptAsync(
            "window.getSelection ? window.getSelection().toString() : ''")).Trim();
        if (selected.Length == 0)
        {
            ReaderAiStatusText.Text = "请先在正文中选择一段文字。";
            return;
        }
        await SendReaderAiQuestionAsync($"请解释这段选文的含义、上下文和重要性：\n\n{LimitReaderText(selected, 2200)}", useBookOverview: false);
    }

    private async void ReaderAiSummarizeBookButton_Click(object sender, RoutedEventArgs e)
    {
        await SendReaderAiQuestionAsync(
            "请根据全书各章节的代表性片段，给出结构化全书概览：主题、章节脉络、核心观点、重要概念和阅读建议。",
            useBookOverview: true);
    }

    private void ReaderAiClearButton_Click(object sender, RoutedEventArgs e)
    {
        _readerAiCancellation?.Cancel();
        _readerAiConversation.Clear();
        ReaderChatMessagesPanel.Children.Clear();
        ReaderAiEmptyState.Visibility = Visibility.Visible;
        ResetReaderAiSources();
        ReaderAiStatusText.Text = _readerIndexAvailable
            ? "本地索引已就绪"
            : "等待本地索引";
    }

    private async Task SendReaderAiQuestionAsync(string question, bool useBookOverview)
    {
        ShowReaderAiTab();
        if (_readerAiBusy) return;
        if (_readerBook is null || _readerBookFile is null || _readerDocument is null)
        {
            ReaderAiStatusText.Text = "目前仅支持对 EPUB 建立全文索引并进行书籍问答。";
            return;
        }
        if (!_readerAiSettings.IsConfigured)
        {
            ReaderAiStatusText.Text = "请先点击右上角设置，配置 DeepSeek、OpenAI 或自定义 API。";
            ReaderAiSettingsButton_Click(ReaderAiSettingsButton, new RoutedEventArgs());
            return;
        }
        if (_readerFeatureCancellation is null) return;

        _readerAiBusy = true;
        ReaderAiSendButton.IsEnabled = false;
        _readerAiCancellation?.Cancel();
        _readerAiCancellation?.Dispose();
        _readerAiCancellation = CancellationTokenSource.CreateLinkedTokenSource(_readerFeatureCancellation.Token);
        var cancellationToken = _readerAiCancellation.Token;
        var history = _readerAiConversation.ToArray();
        ReaderAiQuestionBox.Text = string.Empty;
        AddReaderChatMessage("user", question);
        ResetReaderAiSources();

        try
        {
            ReaderAiStatusText.Text = "正在准备本地书籍上下文…";
            if (_readerIndexTask is not null) await _readerIndexTask;
            if (!_readerIndexAvailable)
                throw new InvalidOperationException("本地书籍索引尚未准备好。请稍后重试。");

            var selectedText = (await ExecuteReaderStringScriptAsync(
                "window.getSelection ? window.getSelection().toString() : ''")).Trim();
            var currentSection = await ExecuteReaderStringScriptAsync(GetReaderSectionTextScript());
            ReaderAiStatusText.Text = useBookOverview ? "正在抽取全书代表片段…" : "正在本地检索相关章节…";
            var sources = useBookOverview
                ? await _readerData.GetBookOverviewChunksAsync(_readerBook.Id, 12, cancellationToken)
                : await _readerData.SearchBookAsync(
                    _readerBook.Id,
                    question + " " + LimitReaderText(selectedText, 800),
                    7,
                    cancellationToken);
            var instructions = BuildReaderAiInstructions(
                _readerBook,
                GetCurrentReaderChapterTitle(),
                selectedText,
                currentSection,
                sources);

            ReaderAiStatusText.Text = $"正在请求 {_readerAiSettings.ProviderDisplayName} · {_readerAiSettings.Model}…";
            var answer = await _aiChatClient.CompleteAsync(
                _readerAiSettings,
                instructions,
                question,
                history,
                cancellationToken);
            _readerAiConversation.Add(new AiConversationTurn("user", question));
            _readerAiConversation.Add(new AiConversationTurn("assistant", answer));
            AddReaderChatMessage("assistant", answer);
            ShowReaderAiSources(sources);
            ReaderAiStatusText.Text = sources.Count > 0
                ? $"回答完成 · 本地检索 {sources.Count} 个相关片段"
                : "回答完成 · 使用当前章节上下文";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReaderAiStatusText.Text = "请求已取消。";
        }
        catch (Exception exception)
        {
            AddReaderChatMessage("assistant", $"请求失败：{exception.Message}");
            ReaderAiStatusText.Text = "AI 请求失败；书籍和批注数据仍只保存在本机。";
        }
        finally
        {
            _readerAiBusy = false;
            ReaderAiSendButton.IsEnabled = true;
        }
    }

    private static string BuildReaderAiInstructions(
        Book book,
        string chapterTitle,
        string selectedText,
        string currentSection,
        IReadOnlyList<BookContentChunk> sources)
    {
        var builder = new StringBuilder();
        builder.AppendLine("你是 Kkindle 的阅读助手。请使用简体中文回答，并严格依据下面提供的书籍上下文。")
            .AppendLine("如果上下文不足以确定答案，请明确说明，不要虚构书中观点、引文或页码。")
            .AppendLine("引用检索片段时使用 [1]、[2] 这样的编号；回答应先给结论，再给依据。")
            .AppendLine($"书名：{book.Title}")
            .AppendLine($"作者：{book.Authors}")
            .AppendLine($"当前章节：{chapterTitle}");

        if (!string.IsNullOrWhiteSpace(selectedText))
            builder.AppendLine().AppendLine("【当前选文】").AppendLine(LimitReaderText(selectedText, 2200));
        if (!string.IsNullOrWhiteSpace(currentSection))
            builder.AppendLine().AppendLine("【当前章节上下文】").AppendLine(LimitReaderText(currentSection, 5200));

        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            builder.AppendLine()
                .Append('[').Append(index + 1).Append("] ").AppendLine(source.ChapterTitle)
                .AppendLine(LimitReaderText(source.Content, 1700));
        }
        return builder.ToString();
    }

    private string GetCurrentReaderChapterTitle() =>
        (ReaderTocList.SelectedItem as EpubReaderNavigationItem)?.Title
        ?? _readerNavigation.FirstOrDefault(item => item.ChapterIndex == _readerChapterIndex)?.Title
        ?? (_readerChapterIndex >= 0 ? $"第 {_readerChapterIndex + 1} 章" : "当前章节");

    private void AddReaderChatMessage(string role, string content)
    {
        var isUser = role.Equals("user", StringComparison.OrdinalIgnoreCase);
        var foreground = new SolidColorBrush(isUser ? Colors.White : ColorHelper.FromArgb(255, 24, 24, 24));
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = isUser ? "你" : _readerAiSettings.ProviderDisplayName,
            FontSize = 9,
            Foreground = foreground,
            Opacity = 0.72
        });
        stack.Children.Add(new TextBlock
        {
            Text = content,
            FontSize = 12,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = foreground
        });
        var bubble = new Border
        {
            MaxWidth = 306,
            Padding = new Thickness(11, 9, 11, 10),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Background = new SolidColorBrush(isUser ? Colors.Black : Colors.White),
            BorderBrush = new SolidColorBrush(isUser ? Colors.Black : ColorHelper.FromArgb(255, 218, 218, 214)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(0),
            Child = stack
        };
        ReaderChatMessagesPanel.Children.Add(bubble);
        ReaderAiEmptyState.Visibility = Visibility.Collapsed;
        DispatcherQueue.TryEnqueue(() =>
        {
            ReaderAiScrollViewer.UpdateLayout();
            ReaderAiScrollViewer.ChangeView(null, ReaderAiScrollViewer.ScrollableHeight, null, disableAnimation: false);
        });
    }

    private void ResetReaderAiSources()
    {
        ReaderAiSourcesPanel.Children.Clear();
        ReaderAiSourcesPanel.Children.Add(new TextBlock
        {
            Text = "本次参考",
            FontSize = 10,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 119, 119, 119))
        });
        ReaderAiSourcesPanel.Visibility = Visibility.Collapsed;
    }

    private void ShowReaderAiSources(IReadOnlyList<BookContentChunk> sources)
    {
        ResetReaderAiSources();
        if (sources.Count == 0) return;
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var button = new Button
            {
                Content = $"[{index + 1}] {source.ChapterTitle}",
                Tag = source,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 10,
                Padding = new Thickness(8, 5, 8, 5),
                Background = new SolidColorBrush(Colors.Transparent),
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 45, 45, 45)),
                BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 218, 218, 214)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(0)
            };
            button.Click += ReaderAiSourceButton_Click;
            ReaderAiSourcesPanel.Children.Add(button);
        }
        ReaderAiSourcesPanel.Visibility = Visibility.Visible;
    }

    private void ReaderAiSourceButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: BookContentChunk source }
            || _readerAllowedRoot is null
            || _readerDocument is null) return;
        var targetPath = Path.GetFullPath(Path.Combine(
            _readerAllowedRoot,
            source.ChapterPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsPathInside(_readerAllowedRoot, targetPath) || !File.Exists(targetPath)) return;
        _readerChapterIndex = Math.Clamp(source.ChapterIndex, 0, _readerChapters.Count - 1);
        _pendingReaderChunkOffset = source.StartOffset;
        UpdateReaderChapterControls();
        var target = new Uri(targetPath);
        if (ReaderWebView.Source?.LocalPath.Equals(target.LocalPath, StringComparison.OrdinalIgnoreCase) == true)
            _ = ScrollToPendingReaderChunkAsync();
        else
            ReaderWebView.Source = target;
    }

    private async Task ScrollToPendingReaderChunkAsync()
    {
        if (_pendingReaderChunkOffset is not int offset || ReaderWebView.CoreWebView2 is null) return;
        _pendingReaderChunkOffset = null;
        var script = $$"""
            (() => {
              const root = document.body;
              if (!root) return;
              const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT, {
                acceptNode(node) {
                  const parent = node.parentElement;
                  return !parent || ['SCRIPT', 'STYLE', 'NOSCRIPT'].includes(parent.tagName)
                    ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_ACCEPT;
                }
              });
              let cursor = 0;
              while (walker.nextNode()) {
                const node = walker.currentNode;
                if (cursor + node.data.length >= {{offset}}) {
                  (node.parentElement || root).scrollIntoView({ block: 'center', behavior: 'smooth' });
                  return;
                }
                cursor += node.data.length;
              }
            })();
            """;
        try { await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script); }
        catch { }
    }

    private static string LimitReaderText(string value, int maximum)
    {
        var normalized = value.Trim();
        return normalized.Length <= maximum ? normalized : normalized[..maximum] + "…";
    }
}
