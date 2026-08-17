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

        Assert.Contains(expected, css, StringComparison.Ordinal);
        Assert.Contains("column-width: auto !important", css, StringComparison.Ordinal);
    }

    [Fact]
    public void TurnScriptDefaultsToInstantScrolling()
    {
        var script = ReaderPaginationScripts.CreateTurnScript(direction: 1);

        Assert.Contains("behavior: 'instant'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("behavior: 'smooth'", script, StringComparison.Ordinal);
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
