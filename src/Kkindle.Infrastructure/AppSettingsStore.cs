using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    public AppSettingsStore(AppPaths paths) => _paths = paths;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        if (!File.Exists(_paths.Settings)) return new AppSettings();
        try
        {
            await using var stream = new FileStream(_paths.Settings, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            return AppSettings.Normalize(await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken));
        }
        catch (JsonException) { return new AppSettings(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var temporary = _paths.Settings + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            await JsonSerializer.SerializeAsync(stream, AppSettings.Normalize(settings), JsonOptions, cancellationToken);
        File.Move(temporary, _paths.Settings, true);
    }
}
