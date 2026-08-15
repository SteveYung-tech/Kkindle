namespace Kkindle.Platform.MacOS;

public static class MacOSAppData
{
    public static string ResolveRoot()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("macOS application paths can only be resolved on macOS.");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
            throw new InvalidOperationException("The current macOS user has no home directory.");
        return Path.Combine(home, "Library", "Application Support", "Kkindle");
    }

    public static string ResolveConfigurationDirectory() => ResolveRoot();
}
