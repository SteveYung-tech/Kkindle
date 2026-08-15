using System.ComponentModel;
using System.Diagnostics;
using Kkindle.Platform.Common;

namespace Kkindle.Platform.MacOS;

public sealed class MacOSSecretProtector : AesGcmSecretProtector
{
    private const string ServiceName = "Kkindle.SecretProtectionKey";

    protected override byte[] GetOrCreateKey()
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("macOS secret protection can only run on macOS.");
        var account = Environment.UserName;
        var lookup = RunSecurity(["find-generic-password", "-s", ServiceName, "-a", account, "-w"], allowFailure: true);
        if (lookup.ExitCode == 0 && !string.IsNullOrWhiteSpace(lookup.Output)) return ParseStoredKey(lookup.Output);
        var key = CreateKey();
        var store = RunSecurity(["add-generic-password", "-U", "-s", ServiceName, "-a", account, "-w", Convert.ToBase64String(key)]);
        if (store.ExitCode != 0)
            throw new InvalidOperationException($"Unable to store the Kkindle key in Keychain: {store.Error}");
        return key;
    }

    private static CommandResult RunSecurity(IReadOnlyList<string> arguments, bool allowFailure = false)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/usr/bin/security",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Unable to start the macOS security tool.");
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            Task.WaitAll(outputTask, errorTask);
            var result = new CommandResult(process.ExitCode, outputTask.Result.Trim(), errorTask.Result.Trim());
            if (!allowFailure && result.ExitCode != 0)
                throw new InvalidOperationException($"The macOS security tool failed: {result.Error}");
            return result;
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("The macOS security tool is unavailable.", exception);
        }
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error);
}
