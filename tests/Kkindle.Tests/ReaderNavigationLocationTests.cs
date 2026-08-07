using Kkindle.Core;

namespace Kkindle.Tests;

public sealed class ReaderNavigationLocationTests
{
    [Theory]
    [InlineData(ReaderNavigationIntent.Toc, true)]
    [InlineData(ReaderNavigationIntent.Progress, true)]
    [InlineData(ReaderNavigationIntent.None, false)]
    [InlineData(ReaderNavigationIntent.Bookmark, false)]
    [InlineData(ReaderNavigationIntent.Annotation, false)]
    [InlineData(ReaderNavigationIntent.Search, false)]
    [InlineData(ReaderNavigationIntent.AiSource, false)]
    public void OnlyPlainChapterTargetsGoToChapterStart(ReaderNavigationIntent intent, bool expected)
    {
        Assert.Equal(expected, ReaderNavigationLocationPolicy.GoesToChapterStart(intent));
    }

    [Theory]
    [InlineData(ReaderNavigationIntent.None, true)]
    [InlineData(ReaderNavigationIntent.Toc, false)]
    [InlineData(ReaderNavigationIntent.Progress, false)]
    [InlineData(ReaderNavigationIntent.Bookmark, false)]
    [InlineData(ReaderNavigationIntent.Annotation, false)]
    [InlineData(ReaderNavigationIntent.Search, false)]
    [InlineData(ReaderNavigationIntent.AiSource, false)]
    public void AutomaticRestoreOnlyForUnspecifiedNavigation(ReaderNavigationIntent intent, bool expected)
    {
        Assert.Equal(expected, ReaderNavigationLocationPolicy.UsesRestorePosition(intent));
    }

    [Theory]
    [InlineData(ReaderNavigationIntent.Search, true)]
    [InlineData(ReaderNavigationIntent.AiSource, true)]
    [InlineData(ReaderNavigationIntent.Toc, false)]
    [InlineData(ReaderNavigationIntent.Progress, false)]
    [InlineData(ReaderNavigationIntent.None, false)]
    [InlineData(ReaderNavigationIntent.Bookmark, false)]
    [InlineData(ReaderNavigationIntent.Annotation, false)]
    public void ChunkOffsetSurvivesOnlySearchAndAiNavigation(ReaderNavigationIntent intent, bool expected)
    {
        Assert.Equal(expected, ReaderNavigationLocationPolicy.KeepsChunkOffset(intent));
    }

    [Fact]
    public void BookmarkAndAnnotationPayloadsSurviveOnlyTheirOwnNavigation()
    {
        Assert.True(ReaderNavigationLocationPolicy.KeepsBookmarkQuote(ReaderNavigationIntent.Bookmark));
        Assert.False(ReaderNavigationLocationPolicy.KeepsBookmarkQuote(ReaderNavigationIntent.Toc));
        Assert.False(ReaderNavigationLocationPolicy.KeepsBookmarkQuote(ReaderNavigationIntent.Annotation));
        Assert.False(ReaderNavigationLocationPolicy.KeepsBookmarkQuote(ReaderNavigationIntent.Search));

        Assert.True(ReaderNavigationLocationPolicy.KeepsAnnotationScroll(ReaderNavigationIntent.Annotation));
        Assert.False(ReaderNavigationLocationPolicy.KeepsAnnotationScroll(ReaderNavigationIntent.Toc));
        Assert.False(ReaderNavigationLocationPolicy.KeepsAnnotationScroll(ReaderNavigationIntent.Bookmark));
        Assert.False(ReaderNavigationLocationPolicy.KeepsAnnotationScroll(ReaderNavigationIntent.AiSource));
    }

    [Fact]
    public void RestorePositionSurvivesOnlyOpenBookNavigation()
    {
        Assert.True(ReaderNavigationLocationPolicy.KeepsRestorePosition(ReaderNavigationIntent.None));
        Assert.False(ReaderNavigationLocationPolicy.KeepsRestorePosition(ReaderNavigationIntent.Toc));
        Assert.False(ReaderNavigationLocationPolicy.KeepsRestorePosition(ReaderNavigationIntent.Progress));
        Assert.False(ReaderNavigationLocationPolicy.KeepsRestorePosition(ReaderNavigationIntent.Bookmark));
        Assert.False(ReaderNavigationLocationPolicy.KeepsRestorePosition(ReaderNavigationIntent.Annotation));
        Assert.False(ReaderNavigationLocationPolicy.KeepsRestorePosition(ReaderNavigationIntent.Search));
        Assert.False(ReaderNavigationLocationPolicy.KeepsRestorePosition(ReaderNavigationIntent.AiSource));
    }

    [Fact]
    public void PlainTocEntryIsNotAnAnchor()
    {
        var plain = new Uri("file:///c:/cache/EPUB/chapter.xhtml");
        Assert.False(ReaderNavigationLocationPolicy.TocTargetHasAnchor(plain));
        Assert.Equal(string.Empty, ReaderNavigationLocationPolicy.TocAnchorId(plain));
    }

    [Theory]
    [InlineData("file:///c:/cache/EPUB/chapter.xhtml#sec-2", "sec-2")]
    [InlineData("file:///c:/cache/EPUB/chapter.xhtml#h1", "h1")]
    public void TocEntryWithExplicitFragmentIsAnAnchor(string target, string expectedId)
    {
        var uri = new Uri(target);
        Assert.True(ReaderNavigationLocationPolicy.TocTargetHasAnchor(uri));
        Assert.Equal(expectedId, ReaderNavigationLocationPolicy.TocAnchorId(uri));
    }

    [Fact]
    public void NullTargetIsNeverAnAnchor()
    {
        Assert.False(ReaderNavigationLocationPolicy.TocTargetHasAnchor(null!));
        Assert.Equal(string.Empty, ReaderNavigationLocationPolicy.TocAnchorId(null!));
    }

    // Chapter-start normalization may only run when the navigation shows the
    // chapter's first line; explicit targets (fragment/bookmark/annotation/
    // search/AI) and breakpoint restore must never be touched.
    [Theory]
    [InlineData(ReaderNavigationIntent.Toc, true)]
    [InlineData(ReaderNavigationIntent.Progress, true)]
    [InlineData(ReaderNavigationIntent.None, true)]
    [InlineData(ReaderNavigationIntent.Bookmark, false)]
    [InlineData(ReaderNavigationIntent.Annotation, false)]
    [InlineData(ReaderNavigationIntent.Search, false)]
    [InlineData(ReaderNavigationIntent.AiSource, false)]
    public void ChapterStartNormalizationRunsOnlyForPlainChapterTargets(
        ReaderNavigationIntent intent,
        bool expected)
    {
        var plain = new Uri("file:///c:/cache/EPUB/chapter.xhtml");
        Assert.Equal(expected, ReaderNavigationLocationPolicy.ShouldNormalizeChapterStart(intent, plain, hasPendingRestorePosition: false));
    }

    [Fact]
    public void ChapterStartNormalizationSkipsTocAnchorTargets()
    {
        var anchored = new Uri("file:///c:/cache/EPUB/chapter.xhtml#sec-2");
        Assert.False(ReaderNavigationLocationPolicy.ShouldNormalizeChapterStart(ReaderNavigationIntent.Toc, anchored, false));
    }

    [Fact]
    public void ChapterStartNormalizationSkipsBreakpointRestore()
    {
        var plain = new Uri("file:///c:/cache/EPUB/chapter.xhtml");
        Assert.False(ReaderNavigationLocationPolicy.ShouldNormalizeChapterStart(ReaderNavigationIntent.None, plain, hasPendingRestorePosition: true));
        Assert.True(ReaderNavigationLocationPolicy.ShouldNormalizeChapterStart(ReaderNavigationIntent.None, plain, hasPendingRestorePosition: false));
    }

    [Fact]
    public void ChapterStartNormalizationIsAlwaysSafeForNullTarget()
    {
        Assert.False(ReaderNavigationLocationPolicy.ShouldNormalizeChapterStart(ReaderNavigationIntent.Toc, null, false));
    }

    // A subchapter TOC entry (legit fragment) must keep its anchor intent and
    // never be treated like a plain chapter entry, no matter what stale state
    // (old progress / breakpoint restore / a superseded pending location) is
    // still around: the anchor wins, chapter-start normalization stays off.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FragmentTocAnchorNeverNormalizedEvenWithPendingRestore(bool hasPendingRestorePosition)
    {
        var anchored = new Uri("file:///c:/cache/EPUB/09.xhtml#sigil_toc_id_68");
        Assert.True(ReaderNavigationLocationPolicy.TocTargetHasAnchor(anchored));
        Assert.Equal("sigil_toc_id_68", ReaderNavigationLocationPolicy.TocAnchorId(anchored));
        Assert.False(ReaderNavigationLocationPolicy.ShouldNormalizeChapterStart(
            ReaderNavigationIntent.Toc, anchored, hasPendingRestorePosition));
    }

    [Fact]
    public void FragmentTocAnchorIsNeverChapterStartAndNeverRestore()
    {
        var anchored = new Uri("file:///c:/cache/EPUB/01.xhtml#sec-2");
        // GoesToChapterStart covers the plain-Toc switch branch; an anchored
        // TOC entry is routed to the anchor BEFORE that branch by
        // TocTargetHasAnchor, so the anchored target never reaches it.
        Assert.True(ReaderNavigationLocationPolicy.GoesToChapterStart(ReaderNavigationIntent.Toc));
        Assert.False(ReaderNavigationLocationPolicy.UsesRestorePosition(ReaderNavigationIntent.Toc));
        Assert.False(ReaderNavigationLocationPolicy.KeepsRestorePosition(ReaderNavigationIntent.Toc));
        Assert.False(ReaderNavigationLocationPolicy.KeepsChunkOffset(ReaderNavigationIntent.Toc));
        Assert.False(ReaderNavigationLocationPolicy.KeepsBookmarkQuote(ReaderNavigationIntent.Toc));
        Assert.False(ReaderNavigationLocationPolicy.KeepsAnnotationScroll(ReaderNavigationIntent.Toc));
    }

    // A URI ending in a bare '#' is not a genuine anchor (.NET Uri.Fragment
    // reports "#" for it, but the EPUB navigation parser never emits such
    // targets), so it must be treated as a plain chapter entry — first-line
    // normalization applies, never a fragment jump.
    [Fact]
    public void EmptyFragmentIsNotAnAnchor()
    {
        var bareHash = new Uri("file:///c:/cache/EPUB/chapter.xhtml#");
        Assert.False(ReaderNavigationLocationPolicy.TocTargetHasAnchor(bareHash));
        Assert.Equal(string.Empty, ReaderNavigationLocationPolicy.TocAnchorId(bareHash));
        Assert.True(ReaderNavigationLocationPolicy.ShouldNormalizeChapterStart(
            ReaderNavigationIntent.Toc, bareHash, hasPendingRestorePosition: false));
    }
}
