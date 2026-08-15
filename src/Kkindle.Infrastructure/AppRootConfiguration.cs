using System.Text.Json;

namespace Kkindle.Infrastructure;

public static class AppRootConfiguration
{
    private const string FileName = "app-root.json";
    private static string ConfigPath(string applicationDirectory) => Path.Combine(applicationDirectory, FileName);

    public static string ResolveRoot(string configurationDirectory, string? fallbackRoot = null)
    {
        var configurationRoot = Path.GetFullPath(configurationDirectory);
        var fallback = Path.GetFullPath(fallbackRoot ?? configurationRoot);
        var path = ConfigPath(configurationRoot);
        if (!File.Exists(path)) return fallback;
        try
        {
            var config = JsonSerializer.Deserialize<RootConfig>(File.ReadAllText(path));
            if (string.IsNullOrWhiteSpace(config?.Root)) return fallback;
            var configured = Path.GetFullPath(config.Root);
            Directory.CreateDirectory(configured);
            return configured;
        }
        catch { return fallback; }
    }

    public static void Save(string configurationDirectory, string root)
    {
        var configurationRoot = Path.GetFullPath(configurationDirectory);
        var target = Path.GetFullPath(root);
        Directory.CreateDirectory(configurationRoot);
        Directory.CreateDirectory(target);
        var temporary = ConfigPath(configurationRoot) + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new RootConfig { Root = target }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, ConfigPath(configurationRoot), true);
    }

    public static string MigrationBackupPath(string root) => Path.Combine(Path.GetFullPath(root), ".kkindle-migration.kkindle");

    private sealed class RootConfig { public string Root { get; set; } = string.Empty; }
}
