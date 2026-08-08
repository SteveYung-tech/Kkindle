namespace Kkindle.Core;

public static class BookFormatConversionPolicy
{
    public static bool IsConvertibleFormat(string? format) => Normalize(format) is "epub" or "azw3" or "pdf";

    public static BookFile? SelectSource(
        IEnumerable<BookFile>? files,
        string? targetFormat)
    {
        if (files is null) return null;

        var target = Normalize(targetFormat);
        return files
            .Where(file => IsConvertibleFormat(file.Format)
                && !string.Equals(Normalize(file.Format), target, StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => GetPriority(file.Format))
            .FirstOrDefault();
    }

    public static string Normalize(string? format) =>
        format?.Trim().TrimStart('.').ToLowerInvariant() ?? string.Empty;

    private static int GetPriority(string? format) => Normalize(format) switch
    {
        "epub" => 0,
        "azw3" => 1,
        "pdf" => 2,
        _ => 3
    };
}
