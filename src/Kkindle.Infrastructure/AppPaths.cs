namespace Kkindle.Infrastructure;

public sealed class AppPaths
{
    public AppPaths(string? root = null)
    {
        Root = root ?? AppContext.BaseDirectory;
        Data = Path.Combine(Root, "data");
        Library = Path.Combine(Data, "library");
        Covers = Path.Combine(Data, "covers");
        Logs = Path.Combine(Data, "logs");
        ReaderCache = Path.Combine(Data, "reader-cache");
        Database = Path.Combine(Data, "kkindle.db");
    }

    public string Root { get; }
    public string Data { get; }
    public string Library { get; }
    public string Covers { get; }
    public string Logs { get; }
    public string ReaderCache { get; }
    public string Database { get; }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(Data);
        Directory.CreateDirectory(Library);
        Directory.CreateDirectory(Covers);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(ReaderCache);
    }
}
