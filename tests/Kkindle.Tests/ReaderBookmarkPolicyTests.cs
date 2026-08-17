using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class ReaderBookmarkPolicyTests
{
    [Fact]
    public void SameChapterDifferentPageDoesNotMatchJustBecauseFragmentIsShared()
    {
        Assert.False(ReaderBookmarkPolicy.MatchesVisiblePosition(
            "text/chapter.xhtml", 0, 1200,
            "text/chapter.xhtml", 0, 1800, 4));
    }

    [Fact]
    public void MatchingChapterModeAndScrollPositionMatches()
    {
        Assert.True(ReaderBookmarkPolicy.MatchesVisiblePosition(
            "text/chapter.xhtml", 1, 2000,
            "TEXT/CHAPTER.XHTML", 1, 2007, 8));
    }

    [Fact]
    public void MissingSavedPositionDoesNotMatchVisiblePosition()
    {
        Assert.False(ReaderBookmarkPolicy.MatchesVisiblePosition(
            "text/chapter.xhtml", 0, null,
            "text/chapter.xhtml", 0, 0, 4));
    }

    [Fact]
    public void LegacyPdfPagePathStillMatchesCurrentFormat()
    {
        Assert.True(ReaderBookmarkPolicy.MatchesVisiblePosition(
            "pdf:12", 0, 0,
            "pdf:page:12", 0, 0, 4));
    }
}
