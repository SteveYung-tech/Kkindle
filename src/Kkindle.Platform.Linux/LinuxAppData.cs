namespace Kkindle.Platform.Linux;

public static class LinuxAppData
{
    public static string ResolveRoot()
    {
        var home = ResolveHome();
        var xdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var dataHome = !string.IsNullOrWhiteSpace(xdgDataHome) && Path.IsPathRooted(xdgDataHome)
            ? xdgDataHome
            : Path.Combine(home, ".local", "share");
        return Path.Combine(dataHome, "Kkindle");
    }

    public static string ResolveConfigurationDirectory()
    {
        var home = ResolveHome();
        var xdgConfigHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var configHome = !string.IsNullOrWhiteSpace(xdgConfigHome) && Path.IsPathRooted(xdgConfigHome)
            ? xdgConfigHome
            : Path.Combine(home, ".config");
        return Path.Combine(configHome, "Kkindle");
    }

    private static string ResolveHome()
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home)) throw new InvalidOperationException("The current Linux user has no home directory.");
        return home;
    }
}
