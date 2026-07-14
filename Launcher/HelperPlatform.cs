using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ZZZScannerHelper;

internal static class HelperPlatform
{
    private const long DiskSafetyMargin = 100L * 1024 * 1024;

    public static HelperEnvironmentSnapshot Inspect(ScannerManifest manifest)
    {
        var support = manifest.Support ?? new ScannerSupport
        {
            Os = "windows",
            Architectures = ["x64"],
            MinWindowsBuild = 17763
        };
        var snapshot = new HelperEnvironmentSnapshot
        {
            WindowsBuild = Environment.OSVersion.Version.Build,
            OsArchitecture = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        if (!OperatingSystem.IsWindows() || snapshot.WindowsBuild < support.MinWindowsBuild)
        {
            throw new HelperFailureException(
                "unsupported_os",
                "preflight",
                "当前 Windows 版本不受支持",
                $"扫描器需要 Windows 10 1809（Build {support.MinWindowsBuild}）或更高版本；当前 Build 为 {snapshot.WindowsBuild}。",
                "请升级 Windows 后重试。",
                retryable: false,
                new Dictionary<string, string> { ["windowsBuild"] = snapshot.WindowsBuild.ToString() });
        }

        if (RuntimeInformation.OSArchitecture != Architecture.X64
            || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new HelperFailureException(
                "unsupported_arch",
                "preflight",
                "当前系统架构不受支持",
                $"本版本仅支持 Windows x64；当前系统为 {snapshot.OsArchitecture}，Helper 为 {snapshot.ProcessArchitecture}。",
                "请使用 x64 Windows 10/11。ARM64 和 x86 暂不在支持范围内。",
                retryable: false);
        }

        return snapshot;
    }

    public static PackageSelection SelectPackage(ScannerManifest manifest, HelperEnvironmentSnapshot environment)
    {
        if (manifest.SchemaVersion == 1)
        {
            return new PackageSelection(manifest.LegacyPackage(), "legacy-manifest");
        }

        var frameworkPackage = manifest.Packages.Single(package =>
            package.Mode.Equals(ScannerPackageModes.FrameworkDependent, StringComparison.OrdinalIgnoreCase));
        var selfContainedPackage = manifest.Packages.Single(package =>
            package.Mode.Equals(ScannerPackageModes.SelfContained, StringComparison.OrdinalIgnoreCase));
        environment.DesktopRuntimeAvailable = frameworkPackage.Framework is not null
            && HasRequiredFramework(frameworkPackage.Framework);
        return environment.DesktopRuntimeAvailable
            ? new PackageSelection(frameworkPackage, "desktop-runtime-available")
            : new PackageSelection(selfContainedPackage, "desktop-runtime-missing");
    }

    internal static ScannerPackage SelectPackageForTesting(ScannerManifest manifest, bool desktopRuntimeAvailable)
    {
        if (manifest.SchemaVersion == 1)
        {
            return manifest.LegacyPackage();
        }

        var mode = desktopRuntimeAvailable
            ? ScannerPackageModes.FrameworkDependent
            : ScannerPackageModes.SelfContained;
        return manifest.Packages.Single(package => package.Mode.Equals(mode, StringComparison.OrdinalIgnoreCase));
    }

    public static void EnsureWritableDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HelperFailureException(
                "cache_unwritable",
                "preflight",
                "无法写入扫描器目录",
                $"Helper 无法写入运行目录：{directory}",
                "请检查磁盘权限、安全软件拦截或受控文件夹访问设置。",
                retryable: true,
                new Dictionary<string, string> { ["directory"] = directory },
                ex);
        }
    }

    public static void EnsureDiskSpace(ScannerPackage package, string directory)
    {
        var required = checked(package.Size + package.ExpandedSize + DiskSafetyMargin);
        var root = Path.GetPathRoot(Path.GetFullPath(directory))
            ?? throw new InvalidOperationException("Cannot resolve scanner cache drive.");
        var available = new DriveInfo(root).AvailableFreeSpace;
        if (available < required)
        {
            throw new HelperFailureException(
                "disk_insufficient",
                "preflight",
                "磁盘空间不足",
                $"准备扫描器至少需要 {FormatBytes(required)} 可用空间，当前只有 {FormatBytes(available)}。",
                "请释放系统盘空间后重试。Helper 不会留下未完成的安装。",
                retryable: true,
                new Dictionary<string, string>
                {
                    ["requiredBytes"] = required.ToString(),
                    ["availableBytes"] = available.ToString()
                });
        }
    }

    public static bool HasRequiredFramework(ScannerFramework framework)
    {
        if (!Version.TryParse(framework.MinVersion, out var minimum))
        {
            return false;
        }

        var roots = DotnetRoots().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var root in roots)
        {
            var sharedFramework = Path.Combine(root, "shared", framework.Name);
            if (!Directory.Exists(sharedFramework))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(sharedFramework))
            {
                if (Version.TryParse(Path.GetFileName(directory), out var installed)
                    && installed.Major == framework.Major
                    && installed >= minimum)
                {
                    return true;
                }
            }
        }

        return roots.Any(root => DotnetListRuntimesContains(root, framework, minimum));
    }

    private static IEnumerable<string> DotnetRoots()
    {
        foreach (var variable in new[] { "DOTNET_ROOT_X64", "DOTNET_ROOT" })
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            yield return Path.Combine(programFiles, "dotnet");
        }

        string? registered = null;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64");
            registered = key?.GetValue("InstallLocation") as string;
        }
        catch
        {
        }

        if (!string.IsNullOrWhiteSpace(registered))
        {
            yield return registered;
        }
    }

    private static bool DotnetListRuntimesContains(string root, ScannerFramework framework, Version minimum)
    {
        var executable = Path.Combine(root, "dotnet.exe");
        if (!File.Exists(executable))
        {
            return false;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--list-runtimes",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (process is null || !process.WaitForExit(3000))
            {
                try { process?.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            foreach (var line in process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2
                    && parts[0].Equals(framework.Name, StringComparison.OrdinalIgnoreCase)
                    && Version.TryParse(parts[1], out var installed)
                    && installed.Major == framework.Major
                    && installed >= minimum)
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static string FormatBytes(long value)
    {
        return value >= 1024L * 1024 * 1024
            ? $"{value / (1024d * 1024 * 1024):F1} GB"
            : $"{value / (1024d * 1024):F0} MB";
    }
}

internal sealed class HelperEnvironmentSnapshot
{
    public int WindowsBuild { get; set; }
    public string OsArchitecture { get; set; } = "";
    public string ProcessArchitecture { get; set; } = "";
    public bool DesktopRuntimeAvailable { get; set; }
}

internal sealed record PackageSelection(ScannerPackage Package, string Reason);

internal sealed class HelperFailureException : Exception
{
    public HelperFailureException(
        string code,
        string phase,
        string title,
        string message,
        string remedy,
        bool retryable,
        Dictionary<string, string>? details = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        Phase = phase;
        Title = title;
        Remedy = remedy;
        Retryable = retryable;
        Details = details ?? [];
    }

    public string Code { get; }
    public string Phase { get; }
    public string Title { get; }
    public string Remedy { get; }
    public bool Retryable { get; }
    public Dictionary<string, string> Details { get; }
}

internal sealed class HelperErrorMessage
{
    public string Code { get; set; } = "unknown_error";
    public string Phase { get; set; } = "unknown";
    public string Title { get; set; } = "扫描器发生错误";
    public string Message { get; set; } = "";
    public string Remedy { get; set; } = "请重试；如果问题持续，请打开日志。";
    public bool Retryable { get; set; }
    public List<HelperErrorAction> Actions { get; set; } = [];
    public string DiagnosticId { get; set; } = "";
    public Dictionary<string, string> Details { get; set; } = [];
}

internal sealed class HelperErrorAction
{
    public string Kind { get; set; } = "";
    public string Label { get; set; } = "";
}

internal static class HelperErrors
{
    public static HelperErrorMessage FromException(Exception exception, string fallbackPhase)
    {
        var failure = exception as HelperFailureException;
        var phase = failure?.Phase ?? fallbackPhase;
        var code = failure?.Code ?? exception switch
        {
            HttpRequestException => phase == "manifest" ? "manifest_unreachable" : "download_failed",
            UnauthorizedAccessException => "install_access_denied",
            InvalidDataException => phase == "manifest" ? "manifest_invalid" : "package_corrupt",
            IOException => "install_failed",
            System.Net.HttpListenerException => "port_in_use",
            Win32Exception win32 when win32.NativeErrorCode == 1223 => "uac_cancelled",
            _ => $"{phase}_failed"
        };
        var diagnosticId = HelperLog.RecordException(code, phase, exception);
        var retryable = failure?.Retryable ?? code is not ("unsupported_os" or "unsupported_arch");
        var actions = new List<HelperErrorAction>();
        if (retryable)
        {
            actions.Add(new HelperErrorAction { Kind = "retry", Label = "重试" });
        }

        if (code is "package_corrupt" or "install_failed" or "install_access_denied")
        {
            actions.Add(new HelperErrorAction { Kind = "repair", Label = "重新下载并修复" });
        }

        actions.Add(new HelperErrorAction { Kind = "open_logs", Label = "打开日志目录" });
        actions.Add(new HelperErrorAction { Kind = "copy_diagnostics", Label = "复制诊断信息" });
        return new HelperErrorMessage
        {
            Code = code,
            Phase = phase,
            Title = failure?.Title ?? DefaultTitle(code),
            Message = failure?.Message ?? exception.Message,
            Remedy = failure?.Remedy ?? DefaultRemedy(code),
            Retryable = retryable,
            Actions = actions,
            DiagnosticId = diagnosticId,
            Details = failure?.Details ?? []
        };
    }

    private static string DefaultTitle(string code) => code switch
    {
        "manifest_unreachable" => "无法获取扫描器版本信息",
        "manifest_invalid" => "扫描器版本信息无效",
        "download_failed" => "扫描器下载失败",
        "package_corrupt" => "扫描器安装包校验失败",
        "install_access_denied" => "没有权限安装扫描器",
        "uac_cancelled" => "已取消管理员授权",
        "native_dependency_missing" => "扫描器缺少运行组件",
        "child_exited" => "扫描器启动后立即退出",
        "child_handshake_timeout" => "扫描器启动超时",
        "port_in_use" => "扫描助手端口被占用",
        _ => "OCR 扫描器准备失败"
    };

    private static string DefaultRemedy(string code) => code switch
    {
        "manifest_unreachable" or "download_failed" => "请检查网络后重试；Helper 会自动切换备用下载地址。",
        "package_corrupt" => "请选择“重新下载并修复”，Helper 会清除损坏缓存后重新安装。",
        "install_access_denied" => "请检查安全软件或受控文件夹访问设置，然后重新修复。",
        "uac_cancelled" => "需要扫描提升权限的游戏时，请重新选择管理员启动并确认 UAC。",
        "native_dependency_missing" => "请选择“重新下载并修复”；所需 VC 组件应已包含在扫描器包中。",
        "port_in_use" => "请关闭其他扫描助手实例；如果仍无法启动，请重启电脑后再运行 Helper。",
        _ => "请重试；如果问题持续，请打开日志并提供诊断编号。"
    };
}

internal static class HelperLog
{
    private static readonly object Sync = new();

    public static string DirectoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZZZScannerNext",
        "logs");

    public static string RecordException(string code, string phase, Exception exception)
    {
        var id = Convert.ToHexString(Guid.NewGuid().ToByteArray()[..6]).ToLowerInvariant();
        Write($"ERROR id={id} code={code} phase={phase} type={exception.GetType().Name} message={exception.Message}\n{exception}");
        return id;
    }

    public static void Write(string message)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(DirectoryPath);
                var path = Path.Combine(DirectoryPath, $"helper-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
                foreach (var stale in new DirectoryInfo(DirectoryPath)
                    .EnumerateFiles("helper-*.log")
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .Skip(7))
                {
                    try { stale.Delete(); } catch { }
                }
            }
        }
        catch
        {
        }
    }
}
