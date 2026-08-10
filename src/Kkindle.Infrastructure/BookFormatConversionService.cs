using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

public sealed class BookFormatConversionService : IBookFormatConverter
{
    private const string KfxInputPluginName = "KFX Input";

    private static readonly Encoding CalibreOutputEncoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    private static readonly Regex TerminalEscapePattern = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ProgressPercentagePattern = new(
        @"(?<!\d)(?<percent>\d{1,3}(?:\.\d+)?)\s*%",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] BundledCalibreRelativePaths =
    [
        Path.Combine("Calibre", "ebook-convert.exe"),
        Path.Combine("Calibre2", "ebook-convert.exe")
    ];

    private static readonly string[] KnownCalibrePaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Calibre2", "ebook-convert.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Calibre2", "ebook-convert.exe")
    ];

    private readonly SemaphoreSlim _kfxPluginGate = new(1, 1);
    private bool _kfxPluginReady;

    public async Task ConvertAsync(
        string sourcePath,
        string destinationPath,
        IProgress<FormatConversionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var source = Path.GetFullPath(sourcePath);
        var destination = Path.GetFullPath(destinationPath);
        var sourceFormat = BookFormatConversionPolicy.Normalize(Path.GetExtension(source));
        var targetFormat = BookFormatConversionPolicy.Normalize(Path.GetExtension(destination));

        if (!File.Exists(source))
            throw new FileNotFoundException("源书籍文件不存在。", source);
        if (!BookFormatConversionPolicy.IsCalibreInputFormat(sourceFormat)
            || !BookFormatConversionPolicy.IsConvertibleFormat(targetFormat))
            throw new NotSupportedException("目前支持 EPUB、AZW3、PDF、MOBI 和 KFX 作为转换源，输出支持 EPUB、AZW3、PDF 和 MOBI。");
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("转换目标不能与源文件相同。 ");
        if (File.Exists(destination))
            throw new IOException("转换目标文件已经存在。 ");

        var executable = LocateExecutable();
        if (executable is null)
            throw new InvalidOperationException(
                "未找到 Calibre 转换器。请使用包含 Calibre 运行时的 Kkindle 发布包，或配置 KKINDLE_CALIBRE_CONVERT 后重试。 ");

        var isKfx = sourceFormat == "kfx";
        if (isKfx)
            await EnsureKfxInputPluginAsync(executable, progress, cancellationToken);

        var directory = Path.GetDirectoryName(destination);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidOperationException("转换目标路径无效。 ");
        Directory.CreateDirectory(directory);

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = CalibreOutputEncoding,
            StandardErrorEncoding = CalibreOutputEncoding
        };

        ConfigureCalibreEnvironment(startInfo, executable, isKfx);

        startInfo.ArgumentList.Add(source);
        startInfo.ArgumentList.Add(destination);

        using var process = new Process { StartInfo = startInfo };
        Task<string>? standardOutput = null;
        Task<string>? standardError = null;
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("无法启动 Calibre 转换器。 ");

            progress?.Report(new FormatConversionProgress(0, "转换器已启动…"));
            standardOutput = ReadStandardOutputAsync(process.StandardOutput, progress, cancellationToken);
            standardError = ReadStreamAsync(process.StandardError, cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var outputText = await standardOutput;
            var errorText = await standardError;

            if (process.ExitCode != 0)
            {
                var detail = errorText.Trim();
                if (detail.Length == 0) detail = outputText.Trim();
                if (detail.Length > 1200) detail = detail[^1200..];
                if (isKfx && detail.Contains("DRM", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("这本 KFX 受 DRM 保护，Calibre KFX Input 无法转换。Kkindle 不会绕过 DRM。");
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(detail)
                        ? $"Calibre 转换失败（退出码 {process.ExitCode}）。"
                        : $"Calibre 转换失败：{detail}");
            }

            var output = new FileInfo(destination);
            if (!output.Exists || output.Length == 0)
                throw new InvalidDataException("转换器未生成有效的目标文件。 ");
            progress?.Report(new FormatConversionProgress(100, "转换完成。"));
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try { if (standardOutput is not null) await standardOutput; } catch { }
            try { if (standardError is not null) await standardError; } catch { }
            TryDelete(destination);
            throw;
        }
        catch
        {
            TryDelete(destination);
            throw;
        }
    }

    private async Task EnsureKfxInputPluginAsync(
        string ebookConvertPath,
        IProgress<FormatConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (_kfxPluginReady) return;

        await _kfxPluginGate.WaitAsync(cancellationToken);
        try
        {
            if (_kfxPluginReady) return;

            var calibreDirectory = Path.GetDirectoryName(ebookConvertPath)
                ?? throw new InvalidOperationException("Calibre 路径无效。");
            var customizer = Path.Combine(calibreDirectory, "calibre-customize.exe");
            if (!File.Exists(customizer))
                throw new InvalidOperationException("当前 Calibre 运行时缺少 calibre-customize.exe，无法准备 KFX Input 插件。");

            var listed = await RunCalibreToolAsync(customizer, ["--list-plugins"], ebookConvertPath, useKfxPluginConfig: true, cancellationToken);
            if (ContainsKfxInputPlugin(listed.Output))
            {
                _kfxPluginReady = true;
                return;
            }

            var pluginPackage = LocateKfxInputPluginPackage();
            if (pluginPackage is null)
                throw new InvalidOperationException("未找到 KFX Input 插件包。请使用包含该插件的 Kkindle 发布包，或设置 KKINDLE_KFX_INPUT_PLUGIN。");

            progress?.Report(new FormatConversionProgress(0, "正在安装 Calibre KFX Input 插件…"));
            var installed = await RunCalibreToolAsync(customizer, ["--add-plugin", pluginPackage], ebookConvertPath, useKfxPluginConfig: true, cancellationToken);
            if (installed.ExitCode != 0)
                throw new InvalidOperationException($"KFX Input 插件安装失败：{GetProcessFailureDetail(installed)}");

            listed = await RunCalibreToolAsync(customizer, ["--list-plugins"], ebookConvertPath, useKfxPluginConfig: true, cancellationToken);
            if (!ContainsKfxInputPlugin(listed.Output))
                throw new InvalidOperationException("KFX Input 插件安装后未被 Calibre 识别。");

            _kfxPluginReady = true;
            progress?.Report(new FormatConversionProgress(0, "KFX Input 插件已就绪，正在启动转换…"));
        }
        finally
        {
            _kfxPluginGate.Release();
        }
    }

    private static string? LocateKfxInputPluginPackage()
    {
        var overridePath = Environment.GetEnvironmentVariable("KKINDLE_KFX_INPUT_PLUGIN");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return Path.GetFullPath(overridePath);

        var applicationDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(applicationDirectory, "CalibrePlugins", "KFX Input.zip"),
            Path.Combine(applicationDirectory, "Assets", "CalibrePlugins", "KFX Input.zip")
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static bool ContainsKfxInputPlugin(string output) =>
        output.Contains(KfxInputPluginName, StringComparison.OrdinalIgnoreCase);

    private static async Task<CalibreToolResult> RunCalibreToolAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string ebookConvertPath,
        bool useKfxPluginConfig,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = CalibreOutputEncoding,
            StandardErrorEncoding = CalibreOutputEncoding
        };
        ConfigureCalibreEnvironment(startInfo, ebookConvertPath, useKfxPluginConfig);
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"无法启动 {Path.GetFileName(executable)}。");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return new CalibreToolResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }
    }

    private static string GetProcessFailureDetail(CalibreToolResult result)
    {
        var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
        detail = SanitizeProgressMessage(detail);
        if (detail.Length > 800) detail = detail[^800..];
        return string.IsNullOrWhiteSpace(detail) ? $"退出码 {result.ExitCode}" : detail;
    }

    private static void ConfigureCalibreEnvironment(ProcessStartInfo startInfo, string ebookConvertPath, bool useKfxPluginConfig)
    {
        var configuredDirectory = Environment.GetEnvironmentVariable("KKINDLE_CALIBRE_CONFIG_DIRECTORY");
        if (string.IsNullOrWhiteSpace(configuredDirectory) && (IsBundledExecutable(ebookConvertPath) || useKfxPluginConfig))
        {
            configuredDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kkindle",
                "CalibreConfig");
        }
        if (string.IsNullOrWhiteSpace(configuredDirectory)) return;

        var configDirectory = Path.GetFullPath(configuredDirectory);
        Directory.CreateDirectory(configDirectory);
        startInfo.Environment["CALIBRE_CONFIG_DIRECTORY"] = configDirectory;
    }

    public static string? LocateExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable("KKINDLE_CALIBRE_CONVERT");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return Path.GetFullPath(overridePath);

        var applicationDirectory = AppContext.BaseDirectory;
        foreach (var relativePath in BundledCalibreRelativePaths)
        {
            var candidate = Path.Combine(applicationDirectory, relativePath);
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var path in KnownCalibrePaths)
        {
            if (File.Exists(path)) return path;
        }

        var pathVariable = Environment.GetEnvironmentVariable("Path");
        if (string.IsNullOrWhiteSpace(pathVariable)) return null;
        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), "ebook-convert.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static async Task<string> ReadStandardOutputAsync(
        StreamReader reader,
        IProgress<FormatConversionProgress>? progress,
        CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[1024];
        var carry = string.Empty;
        var lastPercentage = -1d;

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;

            var chunk = new string(buffer, 0, count);
            AppendLimited(output, chunk);
            var progressText = carry + chunk;
            foreach (Match match in ProgressPercentagePattern.Matches(progressText))
            {
                if (!double.TryParse(
                        match.Groups["percent"].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var percentage)) continue;

                percentage = Math.Clamp(percentage, 0, 100);
                if (percentage < lastPercentage) continue;
                if (percentage == lastPercentage) continue;
                lastPercentage = percentage;
                var message = ExtractProgressMessage(progressText, match.Index + match.Length, percentage);
                progress?.Report(new FormatConversionProgress(percentage, message));
            }

            carry = progressText.Length <= 32 ? progressText : progressText[^32..];
        }

        return output.ToString();
    }

    private static async Task<string> ReadStreamAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var output = new StringBuilder();
        var buffer = new char[1024];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (count == 0) break;
            AppendLimited(output, new string(buffer, 0, count));
        }
        return output.ToString();
    }

    private static void AppendLimited(StringBuilder output, string value)
    {
        const int maxLength = 32_000;
        if (output.Length >= maxLength) return;
        output.Append(value.AsSpan(0, Math.Min(value.Length, maxLength - output.Length)));
    }

    private static string ExtractProgressMessage(string text, int start, double percentage)
    {
        var end = text.IndexOfAny(['\r', '\n'], start);
        var message = SanitizeProgressMessage((end < 0 ? text[start..] : text[start..end]).Trim());
        if (message.Length > 120) message = message[..120].TrimEnd();
        if (message.Contains('\uFFFD'))
            return $"正在转换… {percentage:0}%";
        return string.IsNullOrWhiteSpace(message)
            ? $"正在转换… {percentage:0}%"
            : message;
    }

    private static string SanitizeProgressMessage(string value)
    {
        var withoutTerminalEscapes = TerminalEscapePattern.Replace(value, string.Empty);
        var builder = new StringBuilder(withoutTerminalEscapes.Length);
        foreach (var character in withoutTerminalEscapes)
        {
            if (char.IsControl(character) && character != '\t') continue;
            builder.Append(character);
        }

        return builder.ToString().Trim();
    }

    private static bool IsBundledExecutable(string executable)
    {
        var applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(executable);
        return candidate.StartsWith(
            applicationDirectory + "Calibre" + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(
                applicationDirectory + "Calibre2" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private sealed record CalibreToolResult(int ExitCode, string Output, string Error);
}
