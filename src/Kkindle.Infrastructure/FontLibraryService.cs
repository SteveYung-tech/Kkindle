using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class FontLibraryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    public FontLibraryService(AppPaths paths) => _paths = paths;
    private string ManifestPath => Path.Combine(_paths.Fonts, "manifest.json");

    public async Task<IReadOnlyList<ManagedFont>> ListAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        if (!File.Exists(ManifestPath)) return [];
        try
        {
            await using var stream = File.OpenRead(ManifestPath);
            return await JsonSerializer.DeserializeAsync<List<ManagedFont>>(stream, JsonOptions, cancellationToken) ?? [];
        }
        catch (JsonException) { return []; }
    }

    public async Task<ManagedFont> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (extension is not (".ttf" or ".otf" or ".woff" or ".woff2")) throw new InvalidDataException("仅支持 TTF、OTF、WOFF 和 WOFF2 字体。");
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("字体文件不存在。", sourcePath);
        _paths.EnsureDirectories();
        var id = Guid.NewGuid().ToString("N");
        var relative = id + extension;
        await using (var input = File.OpenRead(sourcePath))
        await using (var output = File.Create(Path.Combine(_paths.Fonts, relative)))
            await input.CopyToAsync(output, cancellationToken);
        var item = new ManagedFont(id, Path.GetFileNameWithoutExtension(sourcePath), $"KkindleFont_{id}", relative, DateTimeOffset.UtcNow);
        var manifest = (await ListAsync(cancellationToken)).ToList();
        manifest.Add(item);
        await SaveAsync(manifest, cancellationToken);
        return item;
    }

    public async Task RemoveAsync(string id, CancellationToken cancellationToken = default)
    {
        var manifest = (await ListAsync(cancellationToken)).ToList();
        var item = manifest.FirstOrDefault(font => font.Id == id);
        if (item is null) return;
        var path = GetAbsolutePath(item);
        if (File.Exists(path)) File.Delete(path);
        manifest.Remove(item);
        await SaveAsync(manifest, cancellationToken);
    }

    public string GetAbsolutePath(ManagedFont font)
    {
        var root = Path.GetFullPath(_paths.Fonts) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(_paths.Fonts, font.RelativePath));
        if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("字体路径无效。");
        return path;
    }

    private async Task SaveAsync(IReadOnlyList<ManagedFont> items, CancellationToken cancellationToken)
    {
        var temporary = ManifestPath + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, items, JsonOptions, cancellationToken);
        File.Move(temporary, ManifestPath, true);
    }
}
