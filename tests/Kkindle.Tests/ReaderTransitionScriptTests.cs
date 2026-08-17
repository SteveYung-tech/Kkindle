using Kkindle;

namespace Kkindle.Tests;

public sealed class ReaderTransitionScriptTests
{
    [Theory]
    [InlineData(true, "translate3d(100%,0,0)", "-22px")]
    [InlineData(false, "translate3d(-100%,0,0)", "22px")]
    public void SlideMovesTheNewSnapshotOverAStationaryOldPage(
        bool forward,
        string expectedOrigin,
        string expectedShadow)
    {
        var script = ReaderWaveScripts.CreateSlideViewTransitionStartScript(
            forward,
            durationMs: 430);

        Assert.Contains("::view-transition-old(root)", script, StringComparison.Ordinal);
        Assert.Contains("animation: none", script, StringComparison.Ordinal);
        Assert.Contains("::view-transition-new(root)", script, StringComparison.Ordinal);
        Assert.Contains(expectedOrigin, script, StringComparison.Ordinal);
        Assert.Contains(expectedShadow, script, StringComparison.Ordinal);
        Assert.DoesNotContain("body.style.transform", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WaveUsesAWavyHighContrastRefreshFront(bool forward)
    {
        var script = ReaderWaveScripts.CreateWaveViewTransitionStartScript(
            forward,
            durationMs: 760);

        Assert.Contains("kk-kindle-new", script, StringComparison.Ordinal);
        Assert.Contains("clip-path: polygon(", script, StringComparison.Ordinal);
        Assert.Contains("grayscale(1)", script, StringComparison.Ordinal);
        Assert.Contains("drop-shadow(", script, StringComparison.Ordinal);
        Assert.Contains("brightness(.86) contrast(1.24)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("body.style.transform", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "inset(0 100% 0 0)")]
    [InlineData(false, "inset(0 0 0 100%)")]
    public void ChapterSlideFallbackWipesTheOldSnapshotInTheTurnDirection(
        bool forward,
        string expectedClip)
    {
        var script = ReaderWaveScripts.CreateSlideOverlayScript(
            "data:image/png;base64,AA==",
            width: 982,
            height: 720,
            forward,
            durationMs: 430,
            startPaused: true);

        Assert.Contains(expectedClip, script, StringComparison.Ordinal);
        Assert.Contains("#kk-slide-edge", script, StringComparison.Ordinal);
        Assert.Contains("document.createElement('canvas')", script, StringComparison.Ordinal);
        Assert.Contains("createImageBitmap(new Blob", script, StringComparison.Ordinal);
        Assert.DoesNotContain("document.createElement('img')", script, StringComparison.Ordinal);
        Assert.DoesNotContain("kk-slide-away", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ChapterWaveFallbackDecodesTheSnapshotWithoutCspBlockedImageSources()
    {
        var script = ReaderWaveScripts.CreateWaveOverlayScript(
            "data:image/png;base64,AA==",
            width: 982,
            height: 720,
            forward: true,
            startPaused: true);

        Assert.Contains("document.createElement('canvas')", script, StringComparison.Ordinal);
        Assert.Contains("createImageBitmap(new Blob", script, StringComparison.Ordinal);
        Assert.Contains("__kkindleStartWaveOverlay", script, StringComparison.Ordinal);
        Assert.DoesNotContain("document.createElement('img')", script, StringComparison.Ordinal);
    }
}
