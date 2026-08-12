using System.Globalization;
using System.Text.Json;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Kkindle;

public sealed partial class MainWindow
{
    private int _readerInPageSearchSequence;
    private int _readerInPageSearchCount;
    private int _readerInPageSearchIndex = -1;
    private volatile bool _readerInPageSearchVisible;

    private void ShowReaderInPageSearch()
    {
        if (ReaderPane.Visibility != Visibility.Visible || IsPdfReader) return;
        _readerInPageSearchVisible = true;
        ReaderInPageSearchBar.Visibility = Visibility.Visible;
        ReaderInPageSearchBox.Focus(FocusState.Programmatic);
        ReaderInPageSearchBox.SelectAll();
    }

    private async void ReaderInPageSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var sequence = ++_readerInPageSearchSequence;
        var query = ReaderInPageSearchBox.Text.Trim();
        await Task.Delay(120);
        if (sequence != _readerInPageSearchSequence) return;
        await ApplyReaderInPageSearchAsync(query, sequence);
    }

    private async Task ApplyReaderInPageSearchAsync(string query, int sequence)
    {
        if (ReaderWebView.CoreWebView2 is null) return;
        var serializedQuery = JsonSerializer.Serialize(query);
        var script = $$"""
            (() => {
              const oldMarks = Array.from(document.querySelectorAll('mark.kkindle-page-find-hit'));
              for (let index = oldMarks.length - 1; index >= 0; index--) {
                const oldMark = oldMarks[index];
                const parent = oldMark.parentNode;
                if (!parent) continue;
                parent.replaceChild(document.createTextNode(oldMark.textContent || ''), oldMark);
                if (typeof parent.normalize === 'function') parent.normalize();
              }
              const query = ({{serializedQuery}} || '').trim();
              if (!query || !document.body) return 0;
              const foldedQuery = query.toLocaleLowerCase();
              const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
              const matches = [];
              while (walker.nextNode()) {
                const node = walker.currentNode;
                const parent = node.parentElement;
                if (!parent || ['SCRIPT', 'STYLE', 'NOSCRIPT'].includes(parent.tagName)) continue;
                const foldedText = (node.data || '').toLocaleLowerCase();
                let start = foldedText.indexOf(foldedQuery);
                while (start >= 0) {
                  matches.push({ node, start, length: query.length });
                  start = foldedText.indexOf(foldedQuery, start + Math.max(1, foldedQuery.length));
                }
              }
              for (let index = matches.length - 1; index >= 0; index--) {
                const current = matches[index];
                const range = document.createRange();
                range.setStart(current.node, current.start);
                range.setEnd(current.node, current.start + current.length);
                const mark = document.createElement('mark');
                mark.className = 'kkindle-page-find-hit';
                mark.style.setProperty('background', '#D8D8D8', 'important');
                mark.style.setProperty('color', '#000000', 'important');
                mark.style.setProperty('text-decoration', 'none', 'important');
                range.surroundContents(mark);
              }
              return matches.length;
            })();
            """;
        try
        {
            var result = await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script);
            if (sequence != _readerInPageSearchSequence) return;
            _readerInPageSearchCount = int.TryParse(
                result.Trim().Trim('"'),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var count) ? count : 0;
            _readerInPageSearchIndex = _readerInPageSearchCount > 0 ? 0 : -1;
            await NavigateReaderInPageSearchAsync(_readerInPageSearchIndex);
        }
        catch
        {
            _readerInPageSearchCount = 0;
            _readerInPageSearchIndex = -1;
            UpdateReaderInPageSearchCount();
        }
    }

    private async Task NavigateReaderInPageSearchAsync(int index)
    {
        if (ReaderWebView.CoreWebView2 is null || _readerInPageSearchCount <= 0)
        {
            UpdateReaderInPageSearchCount();
            return;
        }
        _readerInPageSearchIndex = (index % _readerInPageSearchCount + _readerInPageSearchCount)
            % _readerInPageSearchCount;
        var pagination = _readerFlowMode == 1 ? "true" : "false";
        var script = $$"""
            (() => {
              const marks = Array.from(document.querySelectorAll('mark.kkindle-page-find-hit'));
              for (let i = 0; i < marks.length; i++) {
                const current = i === {{_readerInPageSearchIndex}};
                marks[i].style.setProperty('background', current ? '#000000' : '#D8D8D8', 'important');
                marks[i].style.setProperty('color', current ? '#FFFFFF' : '#000000', 'important');
              }
              const mark = marks[{{_readerInPageSearchIndex}}];
              if (!mark) return false;
              if ({{pagination}}) {
                const scroller = document.scrollingElement || document.documentElement;
                const step = {{ReaderPaginationScripts.PageStepExpression}};
                // Let Chromium resolve the target through the actual multicolumn
                // layout first. Computing a page from getBoundingClientRect()
                // alone is unreliable for EPUB columns with dynamic gaps,
                // centered max-width content or two-page spreads.
                mark.scrollIntoView({ block: 'nearest', inline: 'center', behavior: 'instant' });
                if (step > 0) {
                  const rawMax = Math.max(0, scroller.scrollWidth - scroller.clientWidth);
                  const trailingInset = parseFloat(getComputedStyle(document.body).paddingRight) || 0;
                  const max = Math.max(0, Math.min(
                    rawMax,
                    Math.round(Math.max(0, rawMax - trailingInset) / step) * step));
                  const targetLeft = Math.max(0, Math.min(max, Math.round(scroller.scrollLeft / step) * step));
                  window.scrollTo({ left: targetLeft, top: 0, behavior: 'instant' });
                }
              } else {
                mark.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'smooth' });
              }
              return true;
            })();
            """;
        try
        {
            await ReaderWebView.CoreWebView2.ExecuteScriptAsync(script);
            if (_readerFlowMode == 1) await SnapReaderPaginationAsync();
        }
        catch { }
        UpdateReaderInPageSearchCount();
    }

    private void UpdateReaderInPageSearchCount() =>
        ReaderInPageSearchCountText.Text = _readerInPageSearchCount <= 0
            ? "0/0"
            : $"{_readerInPageSearchIndex + 1}/{_readerInPageSearchCount}";

    private async void ReaderInPageSearchPreviousButton_Click(object sender, RoutedEventArgs e) =>
        await NavigateReaderInPageSearchAsync(_readerInPageSearchIndex - 1);

    private async void ReaderInPageSearchNextButton_Click(object sender, RoutedEventArgs e) =>
        await NavigateReaderInPageSearchAsync(_readerInPageSearchIndex + 1);

    private async void ReaderInPageSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            await HideReaderInPageSearchAsync();
            return;
        }
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        var shiftState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
        var reverse = (shiftState & CoreVirtualKeyStates.Down) != 0;
        await NavigateReaderInPageSearchAsync(_readerInPageSearchIndex + (reverse ? -1 : 1));
    }

    private async void ReaderInPageSearchCloseButton_Click(object sender, RoutedEventArgs e) =>
        await HideReaderInPageSearchAsync();

    private async Task HideReaderInPageSearchAsync()
    {
        ResetReaderInPageSearchForNavigation();
        if (ReaderWebView.CoreWebView2 is null) return;
        try
        {
            await ReaderWebView.CoreWebView2.ExecuteScriptAsync(
                """
                (() => {
                  const marks = Array.from(document.querySelectorAll('mark.kkindle-page-find-hit'));
                  for (let index = marks.length - 1; index >= 0; index--) {
                    const mark = marks[index];
                    const parent = mark.parentNode;
                    if (!parent) continue;
                    parent.replaceChild(document.createTextNode(mark.textContent || ''), mark);
                    if (typeof parent.normalize === 'function') parent.normalize();
                  }
                })();
                """);
        }
        catch { }
    }

    private void ResetReaderInPageSearchForNavigation()
    {
        _readerInPageSearchSequence++;
        _readerInPageSearchCount = 0;
        _readerInPageSearchIndex = -1;
        _readerInPageSearchVisible = false;
        ReaderInPageSearchBar.Visibility = Visibility.Collapsed;
        ReaderInPageSearchBox.Text = string.Empty;
        UpdateReaderInPageSearchCount();
    }
}
