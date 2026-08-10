using System.Text.Json;

namespace Kkindle.Infrastructure;

public static class AppRootConfiguration
{
    private const string FileName = "app-root.json";
    private static string ConfigPath(string applicationDirectory) => Path.Combine(applicationDirectory, FileName);

    public static string ResolveRoot(string applicationDirectory)
    {
        var fallback = Path.GetFullPath(applicationDirectory);
        var path = ConfigPath(fallback);
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

    public static void Save(string applicationDirectory, string root)
    {
        var applicationRoot = Path.GetFullPath(applicationDirectory);
        var target = Path.GetFullPath(root);
        Directory.CreateDirectory(target);
        var temporary = ConfigPath(applicationRoot) + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(new RootConfig { Root = target }, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, ConfigPath(applicationRoot), true);
    }

    public static string MigrationBackupPath(string root) => Path.Combine(Path.GetFullPath(root), ".kkindle-migration.kkindle");

    private sealed class RootConfig { public string Root { get; set; } = string.Empty; }
}
