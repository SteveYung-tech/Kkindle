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
}
