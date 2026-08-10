using Kkindle.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Kkindle.Infrastructure;

public sealed class PdfTextService
{
    public Task<IReadOnlyList<PdfPageText>> ExtractAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<PdfPageText>>(() =>
        {
            var pages = new List<PdfPageText>();
            using var document = PdfDocument.Open(path);
            foreach (var page in document.GetPages())
            {
                cancellationToken.ThrowIfCancellationRequested();
                pages.Add(new PdfPageText(page.Number, ContentOrderTextExtractor.GetText(page).Trim()));
            }
            return pages;
        }, cancellationToken);

    public static IReadOnlyList<PdfSearchResult> Search(IReadOnlyList<PdfPageText> pages, string query, int limit = 100)
    {
        var term = query.Trim();
        if (term.Length == 0) return [];
        var result = new List<PdfSearchResult>();
        foreach (var page in pages)
        {
            var offset = 0;
            while (offset < page.Text.Length)
            {
                var index = page.Text.IndexOf(term, offset, StringComparison.CurrentCultureIgnoreCase);
                if (index < 0) break;
                var start = Math.Max(0, index - 45);
                var length = Math.Min(page.Text.Length - start, term.Length + 90);
                result.Add(new PdfSearchResult(page.PageNumber, page.Text.Substring(start, length).ReplaceLineEndings(" "), index));
                if (result.Count >= Math.Clamp(limit, 1, 500)) return result;
                offset = index + Math.Max(1, term.Length);
            }
        }
        return result;
    }
}
