namespace Kkindle.Core;

/// <summary>
/// Matches the visible reader position to a saved bookmark. A fragment is
/// useful for navigation, but it is not sufficient for the corner indicator:
/// many EPUB chapters keep the same document fragment while the reader moves
/// through multiple pages.
/// </summary>
public static class ReaderBookmarkPolicy
{
    public static bool MatchesVisiblePosition(
        string? bookmarkChapterPath,
        int bookmarkFlowMode,
        int? bookmarkScrollPosition,
        string? currentChapterPath,
        int currentFlowMode,
        int currentScrollPosition,
        int tolerance)
    {
        if (string.IsNullOrWhiteSpace(bookmarkChapterPath)
            || string.IsNullOrWhiteSpace(currentChapterPath)
            || !ChapterPathsEqual(bookmarkChapterPath, currentChapterPath)
            || bookmarkFlowMode != currentFlowMode
            || bookmarkScrollPosition is not int savedPosition)
            return false;

        var distance = Math.Abs((long)savedPosition - currentScrollPosition);
        return distance <= Math.Max(0, tolerance);
    }

    private static bool ChapterPathsEqual(string left, string right)
    {
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        return TryGetPdfPage(left, out var leftPage)
            && TryGetPdfPage(right, out var rightPage)
            && leftPage == rightPage;
    }

    private static bool TryGetPdfPage(string path, out int page)
    {
        page = 0;
        if (!path.StartsWith("pdf:", StringComparison.OrdinalIgnoreCase)) return false;
        var value = path[4..];
        if (value.StartsWith("page:", StringComparison.OrdinalIgnoreCase))
            value = value[5..];
        return int.TryParse(value, out page) && page > 0;
    }
}
