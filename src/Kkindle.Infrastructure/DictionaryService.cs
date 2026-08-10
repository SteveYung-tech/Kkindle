using System.Text;
using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class DictionaryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    public DictionaryService(AppPaths paths) => _paths = paths;
    private string ManifestPath => Path.Combine(_paths.Dictionaries, "manifest.json");

    public async Task<IReadOnlyList<DictionaryDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        if (!File.Exists(ManifestPath)) return [];
        try
        {
            await using var stream = File.OpenRead(ManifestPath);
            return await JsonSerializer.DeserializeAsync<List<DictionaryDefinition>>(stream, JsonOptions, cancellationToken) ?? [];
        }
        catch (JsonException) { return []; }
    }

    public async Task<DictionaryDefinition> ImportAsync(string sourcePath, string? name = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("词典文件不存在。", sourcePath);
        var entries = await ParseAsync(sourcePath, cancellationToken);
        if (entries.Count == 0) throw new InvalidDataException("词典中没有可用条目；请使用“词条<TAB>释义”格式。");
        _paths.EnsureDirectories();
        var id = Guid.NewGuid().ToString("N");
        var relativePath = $"{id}.json";
        await using (var stream = File.Create(Path.Combine(_paths.Dictionaries, relativePath)))
            await JsonSerializer.SerializeAsync(stream, entries, JsonOptions, cancellationToken);
        var manifest = (await ListAsync(cancellationToken)).ToList();
        var definition = new DictionaryDefinition(id, string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(sourcePath) : name.Trim(), relativePath, entries.Count, DateTimeOffset.UtcNow);
        manifest.Add(definition);
        await SaveManifestAsync(manifest, cancellationToken);
        return definition;
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        var manifest = (await ListAsync(cancellationToken)).ToList();
        var item = manifest.FirstOrDefault(entry => entry.Id == id);
        if (item is null) return;
        var path = ResolveSafePath(item.RelativePath);
        if (File.Exists(path)) File.Delete(path);
        manifest.Remove(item);
        await SaveManifestAsync(manifest, cancellationToken);
    }

    public async Task<IReadOnlyList<DictionaryEntry>> LookupAsync(string term, CancellationToken cancellationToken = default)
    {
        var normalized = term.Trim();
        if (normalized.Length == 0) return [];
        var result = new List<DictionaryEntry>();
        foreach (var dictionary in (await ListAsync(cancellationToken)).Where(item => item.Enabled))
        {
            var path = ResolveSafePath(dictionary.RelativePath);
            if (!File.Exists(path)) continue;
            await using var stream = File.OpenRead(path);
            var entries = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(stream, JsonOptions, cancellationToken);
            if (entries is null) continue;
            var match = entries.FirstOrDefault(pair => pair.Key.Equals(normalized, StringComparison.CurrentCultureIgnoreCase));
            if (!string.IsNullOrEmpty(match.Key)) result.Add(new DictionaryEntry(match.Key, match.Value, dictionary.Name));
            if (result.Count >= 8) break;
        }
        return result;
    }

    internal static async Task<Dictionary<string, string>> ParseAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, string>(StringComparer.CurrentCultureIgnoreCase);
        using var reader = new StreamReader(path, Encoding.UTF8, true);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#')) continue;
            var separator = line.IndexOf('\t');
            if (separator <= 0) separator = line.IndexOf('=');
            if (separator <= 0) continue;
            var term = line[..separator].Trim();
            var definition = line[(separator + 1)..].Trim();
            if (term.Length > 0 && definition.Length > 0) result[term] = definition;
        }
        return result;
    }

    private string ResolveSafePath(string relativePath)
    {
        var root = Path.GetFullPath(_paths.Dictionaries) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(_paths.Dictionaries, relativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("词典路径无效。");
        return path;
    }

    private async Task SaveManifestAsync(IReadOnlyList<DictionaryDefinition> items, CancellationToken cancellationToken)
    {
        var temporary = ManifestPath + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
        File.Move(temporary, ManifestPath, true);
    }
}
