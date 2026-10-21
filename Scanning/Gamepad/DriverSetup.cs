using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace ZZZScannerNext.Scanning.Gamepad;

internal enum DriverState { Missing, Baseline, OtherVersion, NotRunning, Unknown }

internal sealed record DriverInfo(DriverState State, string Version, string ServiceState, string FilePath, string Detail)
{
    public bool CanConnect => State is DriverState.Baseline or DriverState.OtherVersion;
    public string Summary => State switch
    {
        DriverState.Missing => "未安装 ViGEmBus，请先打开安装引导。",
        DriverState.Baseline => $"ViGEmBus {Version} · 运行中 · 符合测试基准",
        DriverState.OtherVersion => $"ViGEmBus {Version} · 运行中 · 版本尚未验证，可尝试设备自检",
        DriverState.NotRunning => $"ViGEmBus {Version} · 未运行，请重启后重新检测",
        _ => "驱动状态无法确认，请查看检测详情并重新检测"
    };
}

internal static class DriverSetup
{
    public const string BaselineVersion = "1.21.442.0";
    public const string InstallerName = "ViGEmBus_1.22.0_x64_x86_arm64.exe";
    public const string InstallerSha256 = "89220A7865076B342892F98865F3499FB7C4CFD673159E89D352C360FD014C6A";
    public const string ReleaseUrl = "https://github.com/nefarius/ViGEmBus/releases/tag/v1.22.0";
    public static string InstallerPath => Path.Combine(AppContext.BaseDirectory, "Drivers", InstallerName);

    public static DriverInfo Inspect()
    {
        nint manager = 0, service = 0;
        try
        {
            manager = OpenSCManager(null, null, 1);
            if (manager == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            service = OpenService(manager, "ViGEmBus", 4);
            if (service == 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 1060) return Classify(false, false, null, "", "服务未注册");
                throw new Win32Exception(error);
            }
            if (!QueryServiceStatus(service, out var status)) throw new Win32Exception(Marshal.GetLastWin32Error());
            using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = registry.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ViGEmBus");
            var rawPath = key?.GetValue("ImagePath") as string;
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var path = Environment.ExpandEnvironmentVariables(rawPath ?? Path.Combine(windows, "System32", "drivers", "ViGEmBus.sys")).Trim('"');
            if (path.StartsWith(@"\??\", StringComparison.Ordinal)) path = path[4..];
            if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) path = Path.Combine(windows, path[12..]);
            else if (!Path.IsPathRooted(path)) path = Path.Combine(windows, path);
            string? version = null;
            if (File.Exists(path))
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                version = $"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}";
            }
            return Classify(true, status.CurrentState == 4, version, path, $"SCM state={status.CurrentState}");
        }
        catch (Exception ex)
        {
            return new DriverInfo(DriverState.Unknown, "未知", "未知", "", ex.Message);
        }
        finally
        {
            if (service != 0) CloseServiceHandle(service);
            if (manager != 0) CloseServiceHandle(manager);
        }
    }

    internal static DriverInfo Classify(bool installed, bool running, string? version, string path, string detail)
    {
        var state = !installed ? DriverState.Missing : !running ? DriverState.NotRunning
            : string.IsNullOrWhiteSpace(version) ? DriverState.Unknown
            : version == BaselineVersion ? DriverState.Baseline : DriverState.OtherVersion;
        return new DriverInfo(state, version ?? "未知", !installed ? "不存在" : running ? "Running" : "NotRunning", path, detail);
    }

    public static void VerifyInstaller(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("未找到随包安装器，请完整解压测试包，或通过官方发行页下载。", path);
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!hash.Equals(InstallerSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装器 SHA-256 与固定官方版本不符，已拒绝启动，请重新取得完整测试包。");
    }

    public static string Diagnostics(DriverInfo info) =>
        $"Time: {DateTimeOffset.Now:O}\nTool: {Application.ProductVersion}\nOS: {RuntimeInformation.OSDescription}\n" +
        $"OS architecture: {RuntimeInformation.OSArchitecture}\nProcess architecture: {RuntimeInformation.ProcessArchitecture}\n" +
        $"Runtime: {RuntimeInformation.FrameworkDescription}\nDriver: {info.Version}\nService: {info.ServiceState}\n" +
        $"Assessment: {info.State}\nDriver file: {info.FilePath}\nDetail: {info.Detail}\n" +
        $"Installer baseline: 1.22.0\nDriver baseline: {BaselineVersion}\nClient: 1.21.256\n" +
        $"Installer exists: {File.Exists(InstallerPath)}\nInstaller SHA256 expected: {InstallerSha256}\n";

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenSCManager(string? machineName, string? databaseName, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(nint service, out ServiceStatus status);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(nint handle);
}
