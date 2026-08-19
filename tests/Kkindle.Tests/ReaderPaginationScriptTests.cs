using Kkindle;

namespace Kkindle.Tests;

public sealed class ReaderPaginationScriptTests
{
    [Theory]
    [InlineData(false, "column-count: 1 !important")]
    [InlineData(true, "column-count: 2 !important")]
    public void FlowCssPinsTheVisibleColumnCount(bool twoPage, string expected)
    {
        var css = ReaderPaginationScripts.CreateFlowCss(
            pagination: true,
            vertical: false,
            twoPage: twoPage);

        if (OperatingSystem.IsLinux())
        {
            Assert.Contains("column-count: auto !important", css, StringComparison.Ordinal);
            Assert.Contains("-webkit-column-width:", css, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains(expected, css, StringComparison.Ordinal);
            Assert.Contains("column-width: auto !important", css, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void FlowCssReducesConfiguredMarginsForNarrowReaderViewports()
    {
        var css = ReaderPaginationScripts.CreateFlowCss(
            pagination: true,
            vertical: false,
            horizontalPadding: 68);

        Assert.Contains("min(68px, max(24px, 5vw))", css, StringComparison.Ordinal);
        Assert.Contains(
            "calc(min(68px, max(24px, 5vw)) + min(68px, max(24px, 5vw)))",
            css,
            StringComparison.Ordinal);
        Assert.DoesNotContain(")px", css, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlowCssUsesTheLiveCssViewportForColumnGeometry(bool twoPage)
    {
        var css = ReaderPaginationScripts.CreateFlowCss(
            pagination: true,
            vertical: false,
            twoPage: twoPage);

        Assert.Contains("100vw", css, StringComparison.Ordinal);
        Assert.DoesNotContain("--kkindle-reader-page-viewport-width", css, StringComparison.Ordinal);
    }

    [Fact]
    public void PageStepPrioritizesTheLiveScrollingViewport()
    {
        Assert.StartsWith(
            "document.scrollingElement?.clientWidth",
            ReaderPaginationScripts.PageStepExpression,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "--kkindle-reader-page-viewport-width",
            ReaderPaginationScripts.PageStepExpression,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TurnScriptDefaultsToInstantScrolling()
    {
        var script = ReaderPaginationScripts.CreateTurnScript(direction: 1);

        Assert.Contains("behavior: 'instant'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("behavior: 'smooth'", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PaginationScriptsKeepTheSnappedViewportBoundary()
    {
        Assert.DoesNotContain("AlignPaginatedPage", ReaderPaginationScripts.Snap, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AlignPaginatedPage",
            ReaderPaginationScripts.CreateTurnScript(direction: 1),
            StringComparison.Ordinal);

        var fragmentScript = ReaderNavigationScripts.CreateFragmentScroll(
            needle: "section",
            flowMode: 1,
            vertical: false);
        Assert.Contains("const pageLeft = pageIndex * step", fragmentScript, StringComparison.Ordinal);
        Assert.DoesNotContain("horizontalError", fragmentScript, StringComparison.Ordinal);
        Assert.DoesNotContain("scroller.scrollLeft +", fragmentScript, StringComparison.Ordinal);
    }

    [Fact]
    public void PaginationScriptsDoNotClampLogicalBoundaryToIntegerRawMaximum()
    {
        var scripts = new[]
        {
            ReaderPaginationScripts.Snap,
            ReaderPaginationScripts.CreateTurnScript(direction: 1),
            ReaderPaginationScripts.CreateCanTurnScript(direction: 1),
            ReaderPaginationScripts.CreateRestorePositionScript(982, 0, pagination: true),
            ReaderNavigationScripts.CreateFragmentScroll("section", flowMode: 1, vertical: false)
        };

        foreach (var script in scripts)
        {
            Assert.Contains("Math.round(Math.max(0, rawMax -", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Math.min(rawMax", script, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestorePositionScriptSnapsOnlyPaginatedLayouts(bool pagination)
    {
        var script = ReaderPaginationScripts.CreateRestorePositionScript(
            left: 1234,
            top: 56,
            pagination);

        if (pagination)
        {
            Assert.Contains("const requested = 1234", script, StringComparison.Ordinal);
            Assert.Contains("const pageIndex = Math.round(requested / step)", script, StringComparison.Ordinal);
            Assert.Contains("left: target, top: 0", script, StringComparison.Ordinal);
            Assert.DoesNotContain("left: 1234", script, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("left: 1234, top: 56", script, StringComparison.Ordinal);
            Assert.DoesNotContain("pageIndex", script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PaginationRestoreDoesNotInstallReactiveScrollGuards()
    {
        var script = ReaderPaginationScripts.CreateRestorePositionScript(
            left: 957,
            top: 0,
            pagination: true);

        Assert.DoesNotContain("addEventListener", script, StringComparison.Ordinal);
        Assert.DoesNotContain("setTimeout", script, StringComparison.Ordinal);
        Assert.DoesNotContain("scrollend", script, StringComparison.Ordinal);
        Assert.DoesNotContain("__kkindlePaginationDiagnostics", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, "el.scrollWidth")]
    [InlineData(true, false, "el.scrollHeight")]
    [InlineData(false, true, "left: moveToEnd ? el.scrollWidth : 0")]
    public void ChapterBoundaryScriptTargetsTheRequestedEdge(
        bool moveToEnd,
        bool horizontal,
        string expected)
    {
        var script = ReaderPaginationScripts.CreateChapterBoundaryScript(moveToEnd, horizontal);

        Assert.Contains($"const moveToEnd = {moveToEnd.ToString().ToLowerInvariant()}", script, StringComparison.Ordinal);
        Assert.Contains($"const horizontal = {horizontal.ToString().ToLowerInvariant()}", script, StringComparison.Ordinal);
        Assert.Contains(expected, script, StringComparison.Ordinal);
        Assert.Contains("behavior: 'instant'", script, StringComparison.Ordinal);
    }
}
