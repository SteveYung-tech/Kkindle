namespace Kkindle.Core;

// Reader format selection belongs to the domain layer so every UI entry point
// (details, context menu, grid and list) uses the same capability rules.
public static class ReaderBookSelectionPolicy
{
    public static bool IsSupportedFormat(string? format) => GetPriority(format) < 2;

    public static BookFile? SelectPreferred(IEnumerable<BookFile>? files)
    {
        if (files is null) return null;

        return files
            .Where(file => IsSupportedFormat(file.Format))
            .OrderBy(file => GetPriority(file.Format))
            .FirstOrDefault();
    }

    private static int GetPriority(string? format) => format?.Trim().ToLowerInvariant() switch
    {
        "epub" => 0,
        "pdf" => 1,
        _ => 2
    };
}
