using System.Runtime.InteropServices;
using Kkindle.Infrastructure;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinRT.Interop;

namespace Kkindle;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, string> _readerFootnotes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _readerFootnoteResolutionAttempts = new(StringComparer.Ordinal);
    private DispatcherQueueTimer? _readerFootnoteHoverTimer;
    private Popup? _readerFootnotePopup;
    private bool _readerFootnotePollRunning;
    private int _readerFootnoteConfigurationVersion;

    private sealed class ReaderFootnoteHoverInfo
    {
        public string? Href { get; set; }
        public double Left { get; set; }
        public double Top { get; set; }
        public double Right { get; set; }
        public double Bottom { get; set; }
        public double Vw { get; set; }
        public double Vh { get; set; }
    }

    private void ResetReaderFootnoteSession()
    {
        ClearReaderFootnotePage();
    }

    private void ClearReaderFootnotePage()
    {
        _readerFootnoteConfigurationVersion++;
        _readerFootnotes.Clear();
        _readerFootnoteResolutionAttempts.Clear();
        HideReaderFootnotePopup();
    }

    private async Task ConfigureReaderFootnoteHoverAsync()
    {
        if (_readerAllowedRoot is not { } root
            || ReaderWebView.CoreWebView2 is null
            || _readerFeatureCancellation is not { } cancellation)
        {
            ClearReaderFootnotePage();
            return;
        }

        var chapterPath = GetCurrentReaderChapterPath();
        if (chapterPath is null)
        {
            ClearReaderFootnotePage();
            return;
        }

        ClearReaderFootnotePage();
        var requestVersion = _readerFootnoteConfigurationVersion;
        var token = cancellation.Token;

        string[] targets;
        try
        {
            targets = await ExecuteReaderJsonScriptAsync<string[]>(
                "Array.from(document.querySelectorAll('a[href]')).map(a => { try { const url = new URL(a.getAttribute('href') || '', location.href); return url.hash ? url.href : ''; } catch { return ''; } }).filter(Boolean);")
                ?? [];
        }
        catch
        {
            return;
        }

        if (token.IsCancellationRequested || requestVersion != _readerFootnoteConfigurationVersion)
            return;

        IReadOnlyDictionary<string, string> footnotes;
        try
        {
            footnotes = await _footnotes.ResolveAsync(root, targets, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            return;
        }

        if (token.IsCancellationRequested
            || requestVersion != _readerFootnoteConfigurationVersion
            || !string.Equals(_readerAllowedRoot, root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(GetCurrentReaderChapterPath(), chapterPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        StoreReaderFootnotes(footnotes);
    }

    private void StartReaderFootnoteHoverPoll()
    {
        if (_readerFootnoteHoverTimer is null)
        {
            _readerFootnoteHoverTimer = DispatcherQueue.CreateTimer();
            _readerFootnoteHoverTimer.Interval = TimeSpan.FromMilliseconds(120);
            _readerFootnoteHoverTimer.Tick += async (_, _) => await PollReaderFootnoteHoverAsync();
        }
        _readerFootnoteHoverTimer.Start();
    }

    private void StopReaderFootnoteHoverPoll()
    {
        _readerFootnoteHoverTimer?.Stop();
        HideReaderFootnotePopup();
    }

    private async Task PollReaderFootnoteHoverAsync()
    {
        if (_readerFootnotePollRunning) return;
        if (ReaderPane.Visibility != Visibility.Visible
            || _readerCloseRequested
            || _readerTransitionActive
            || ReaderWebView.CoreWebView2 is null
            || _readerAllowedRoot is null)
        {
            HideReaderFootnotePopup();
            return;
        }

        if (_readerLayoutPopup?.IsOpen == true
            || _readerSettingsPopup?.IsOpen == true
            || _readerSearchVisible)
        {
            HideReaderFootnotePopup();
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            HideReaderFootnotePopup();
            return;
        }

        Windows.Foundation.Rect hostRect;
        try
        {
            hostRect = GetReaderWebViewScreenRect();
        }
        catch
        {
            HideReaderFootnotePopup();
            return;
        }
        if (hostRect.Width <= 0
            || hostRect.Height <= 0
            || cursor.X < hostRect.Left
            || cursor.X > hostRect.Right
            || cursor.Y < hostRect.Top
            || cursor.Y > hostRect.Bottom)
        {
            HideReaderFootnotePopup();
            return;
        }

        var relativeX = Math.Clamp(cursor.X - hostRect.Left, 0, hostRect.Width);
        var relativeY = Math.Clamp(cursor.Y - hostRect.Top, 0, hostRect.Height);
        _readerFootnotePollRunning = true;
        try
        {
            var info = await ExecuteReaderJsonScriptAsync<ReaderFootnoteHoverInfo>(
                GetReaderFootnoteHoverScript(relativeX, relativeY, hostRect));
            if (info is null
                || string.IsNullOrWhiteSpace(info.Href)
                || info.Vw <= 0
                || info.Vh <= 0)
            {
                HideReaderFootnotePopup();
                return;
            }

            if (!TryGetReaderFootnoteText(info.Href, out var text))
                text = await ResolveReaderFootnoteOnDemandAsync(info.Href);
            if (string.IsNullOrWhiteSpace(text))
            {
                HideReaderFootnotePopup();
                return;
            }

            ShowReaderFootnotePopup(info, text, hostRect);
        }
        catch
        {
            HideReaderFootnotePopup();
        }
        finally
        {
            _readerFootnotePollRunning = false;
        }
    }

    private static string GetReaderFootnoteHoverScript(
        double relativeX,
        double relativeY,
        Windows.Foundation.Rect hostRect)
    {
        var x = (int)Math.Max(0, relativeX);
        var y = (int)Math.Max(0, relativeY);
        var width = Math.Max(1, hostRect.Width);
        var height = Math.Max(1, hostRect.Height);
        var widthText = width.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        var heightText = height.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        return $$"""
            (() => {
              const root = document.documentElement;
              const vw = root.clientWidth || document.body?.clientWidth || window.innerWidth || 0;
              const vh = root.clientHeight || document.body?.clientHeight || window.innerHeight || 0;
              if (!vw || !vh) return null;
              const x = Math.max(0, Math.min(vw - 1, Math.round({{x}} * vw / {{widthText}})));
              const y = Math.max(0, Math.min(vh - 1, Math.round({{y}} * vh / {{heightText}})));
              const element = document.elementFromPoint(x, y);
              const anchor = element && element.closest ? element.closest('a[href]') : null;
              if (!anchor) return null;
              let url;
              try { url = new URL(anchor.getAttribute('href') || '', location.href); } catch { return null; }
              if (!url.hash) return null;
              const rect = anchor.getBoundingClientRect();
              return {
                href: url.href,
                left: rect.left,
                top: rect.top,
                right: rect.right,
                bottom: rect.bottom,
                vw,
                vh
              };
            })()
            """;
    }

    private void StoreReaderFootnotes(IReadOnlyDictionary<string, string> footnotes)
    {
        foreach (var pair in footnotes)
        {
            _readerFootnotes[pair.Key] = pair.Value;
            _readerFootnotes[EpubFootnoteResolver.NormalizeTargetKey(pair.Key)] = pair.Value;
        }
    }

    private bool TryGetReaderFootnoteText(string href, out string text)
    {
        if (_readerFootnotes.TryGetValue(href, out text!)) return true;

        var normalized = EpubFootnoteResolver.NormalizeTargetKey(href);
        if (_readerFootnotes.TryGetValue(normalized, out text!)) return true;

        text = string.Empty;
        return false;
    }

    private async Task<string?> ResolveReaderFootnoteOnDemandAsync(string href)
    {
        if (_readerAllowedRoot is not { } root
            || _readerFeatureCancellation is not { } cancellation)
        {
            return null;
        }

        var normalized = EpubFootnoteResolver.NormalizeTargetKey(href);
        if (!_readerFootnoteResolutionAttempts.Add(normalized)) return null;

        var requestVersion = _readerFootnoteConfigurationVersion;
        try
        {
            var footnotes = await _footnotes.ResolveAsync(root, [href], cancellation.Token);
            if (cancellation.IsCancellationRequested
                || requestVersion != _readerFootnoteConfigurationVersion
                || !string.Equals(_readerAllowedRoot, root, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            StoreReaderFootnotes(footnotes);
            return TryGetReaderFootnoteText(href, out var text) ? text : null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return null;
        }
        catch
        {
            return null;
        }
    }

    private void ShowReaderFootnotePopup(
        ReaderFootnoteHoverInfo info,
        string text,
        Windows.Foundation.Rect hostRect)
    {
        if (_readerFootnotePopup is null || RootGrid.XamlRoot is null) return;
        if (info.Vw <= 0 || info.Vh <= 0) return;

        var scale = RootGrid.XamlRoot.RasterizationScale;
        var hwnd = WindowNative.GetWindowHandle(this);
        var origin = new POINT { X = 0, Y = 0 };
        _ = ClientToScreen(hwnd, ref origin);

        var screenLeft = hostRect.Left + info.Left / info.Vw * hostRect.Width;
        var screenRight = hostRect.Left + info.Right / info.Vw * hostRect.Width;
        var screenTop = hostRect.Top + info.Top / info.Vh * hostRect.Height;
        var screenBottom = hostRect.Top + info.Bottom / info.Vh * hostRect.Height;
        var left = (screenLeft - origin.X) / scale;
        var right = (screenRight - origin.X) / scale;
        var top = (screenTop - origin.Y) / scale;
        var bottom = (screenBottom - origin.Y) / scale;

        // Popup offsets are relative to the window XamlRoot, but the footnote
        // itself belongs to the reading surface. Use the WebView host bounds
        // as the placement viewport so it can never spill into the TOC pane.
        var bodyBounds = new Windows.Foundation.Rect(
            (hostRect.Left - origin.X) / scale,
            (hostRect.Top - origin.Y) / scale,
            hostRect.Width / scale,
            hostRect.Height / scale);
        if (bodyBounds.Width <= 16 || bodyBounds.Height <= 16) return;

        var popupWidth = Math.Min(360, bodyBounds.Width - 16);
        var popupMaxHeight = Math.Min(260, bodyBounds.Height - 16);
        if (popupWidth <= 0 || popupMaxHeight <= 0) return;

        ReaderFootnoteText.Text = text;
        ReaderFootnotePopup.Width = popupWidth;
        ReaderFootnotePopup.MaxHeight = popupMaxHeight;
        ReaderFootnotePopup.Visibility = Visibility.Visible;
        _readerFootnotePopup.XamlRoot = RootGrid.XamlRoot;
        ReaderFootnotePopup.Measure(new Windows.Foundation.Size(popupWidth, popupMaxHeight));
        ReaderFootnotePopup.UpdateLayout();

        var popupHeight = Math.Clamp(ReaderFootnotePopup.DesiredSize.Height, 1, popupMaxHeight);
        var anchorCenter = (left + right) / 2;
        var popupLeft = Math.Clamp(
            anchorCenter - popupWidth / 2,
            bodyBounds.Left + 8,
            Math.Max(bodyBounds.Left + 8, bodyBounds.Right - popupWidth - 8));
        var below = bottom + 10;
        var above = top - popupHeight - 10;
        var popupTop = below + popupHeight <= bodyBounds.Bottom - 8 ? below : above;
        popupTop = Math.Clamp(
            popupTop,
            bodyBounds.Top + 8,
            Math.Max(bodyBounds.Top + 8, bodyBounds.Bottom - popupHeight - 8));

        _readerFootnotePopup.HorizontalOffset = popupLeft;
        _readerFootnotePopup.VerticalOffset = popupTop;
        if (!_readerFootnotePopup.IsOpen) _readerFootnotePopup.IsOpen = true;
    }

    private void HideReaderFootnotePopup()
    {
        if (_readerFootnotePopup is not null) _readerFootnotePopup.IsOpen = false;
        if (ReaderFootnotePopup is not null)
        {
            ReaderFootnotePopup.Visibility = Visibility.Collapsed;
            ReaderFootnoteText.Text = string.Empty;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);
}
