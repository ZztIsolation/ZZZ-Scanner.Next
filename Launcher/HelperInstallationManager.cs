using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Text.Json;

namespace ZZZScannerHelper;

internal static class HelperInstallationManager
{
    private const string SkipManagedInstallEnvironmentVariable = "ZZZ_SCANNER_SKIP_MANAGED_INSTALL";
    private const string AcceptManagedInstallEnvironmentVariable = "ZZZ_SCANNER_ACCEPT_MANAGED_INSTALL";
    private const string AcceptHelperTakeoverEnvironmentVariable = "ZZZ_SCANNER_ACCEPT_HELPER_TAKEOVER";
    private const string BootstrapArgument = "--complete-managed-install";
    private const string ApplyUpdateArgument = "--apply-helper-update";
    private const string CompleteUpdateArgument = "--complete-helper-update";
    private const string RestoreUpdateArgument = "--restore-helper-update";
    private const string PendingBootstrapCleanupFile = "bootstrap-cleanup-pending.txt";
    public static async Task<HelperLifecycleResult> PrepareAsync(string[] args)
    {
        await RetryPendingBootstrapCleanupAsync();

        if (args.Length >= 3 && args[0].Equals(ApplyUpdateArgument, StringComparison.Ordinal))
        {
            await HelperUpdateTransactionManager.ApplyAsync(int.Parse(args[1]), args[2]);
            return new HelperLifecycleResult(true, []);
        }

        if (args.Length >= 4 && args[0].Equals(CompleteUpdateArgument, StringComparison.Ordinal))
        {
            HelperUpdateTransactionManager.BeginManagedStart(args[1], args[2], args[3]);
            return new HelperLifecycleResult(false, args.Skip(4).ToArray());
        }

        if (args.Length >= 4 && args[0].Equals(RestoreUpdateArgument, StringComparison.Ordinal))
        {
            await HelperUpdateTransactionManager.RestoreAsync(int.Parse(args[1]), args[2], args[3]);
            return new HelperLifecycleResult(true, []);
        }

        if (args.Length >= 3 && args[0].Equals(BootstrapArgument, StringComparison.Ordinal))
        {
            if (int.TryParse(args[1], out var parentPid))
            {
                await WaitForExitAsync(parentPid);
            }
            await TryDeleteVerifiedBootstrapAsync(args[2]);
            return new HelperLifecycleResult(false, args.Skip(3).ToArray());
        }

        if (await HelperUpdateTransactionManager.RecoverInterruptedAsync())
        {
            return new HelperLifecycleResult(true, []);
        }

        if (Environment.GetEnvironmentVariable(SkipManagedInstallEnvironmentVariable) == "1")
        {
            return new HelperLifecycleResult(false, args);
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath)
            || !Path.GetFileName(processPath).Equals("ZZZ-Scanner-Helper.exe", StringComparison.OrdinalIgnoreCase))
        {
            return new HelperLifecycleResult(false, args);
        }

        var managedPath = ManagedHelperPath();
        if (Path.GetFullPath(processPath).Equals(Path.GetFullPath(managedPath), StringComparison.OrdinalIgnoreCase))
        {
            return new HelperLifecycleResult(false, args);
        }

        var installConfirmed = false;
        var existingHelper = await ProbeExistingHelperAsync();
        if (existingHelper.PortOpen)
        {
            if (!existingHelper.TrustedService || !TryParseVersion(existingHelper.Version, out var existingVersion))
            {
                ShowUnsafePortWarning(existingHelper);
                return new HelperLifecycleResult(true, []);
            }

            var currentVersion = Version.Parse(Program.HelperVersion);
            if (existingVersion >= currentVersion)
            {
                ShowCurrentHelperRunning(existingHelper.Version);
                return new HelperLifecycleResult(true, []);
            }

            var selection = SelectTakeoverCandidate(
                existingHelper.Version,
                processPath,
                DiscoverHelperProcesses(processPath));
            if (selection.Candidate is null)
            {
                ShowAmbiguousHelperWarning(existingHelper.Version, selection.Reason);
                return new HelperLifecycleResult(true, []);
            }
            if (!ConfirmHelperTakeover(existingHelper.Version, selection.Candidate.Path))
            {
                return new HelperLifecycleResult(true, []);
            }
            if (!await StopExistingHelperAsync(selection.Candidate.ProcessId))
            {
                ShowTakeoverFailure(existingHelper.Version);
                return new HelperLifecycleResult(true, []);
            }
            installConfirmed = true;
        }

        if (!installConfirmed && !ConfirmManagedInstall())
        {
            return new HelperLifecycleResult(true, []);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(managedPath)!);
        var stagingPath = managedPath + ".installing";
        File.Copy(processPath, stagingPath, overwrite: true);
        await VerifySameFileAsync(processPath, stagingPath);

        if (File.Exists(managedPath))
        {
            File.Delete(managedPath);
        }
        File.Move(stagingPath, managedPath);

        var childArguments = new List<string>
        {
            BootstrapArgument,
            Environment.ProcessId.ToString(),
            processPath,
        };
        childArguments.AddRange(args);
        var startInfo = new ProcessStartInfo
        {
            FileName = managedPath,
            UseShellExecute = false,
        };
        foreach (var argument in childArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        Process.Start(startInfo);
        return new HelperLifecycleResult(true, []);
    }

    public static void LaunchPreparedUpdate(string updateExecutable)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = updateExecutable,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(ApplyUpdateArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add(ManagedHelperPath());
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Cannot start the Helper updater process.");
    }

    public static void CompletePendingUpdate()
    {
        // The updater remains pending until the browser confirms health, token,
        // WebSocket, and diagnostics on the new managed Helper.
    }

    public static bool TryRollbackPendingUpdate()
    {
        return HelperUpdateTransactionManager.SignalStartupFailure();
    }

    public static HelperUpdateTransactionInfo? CurrentPendingUpdate() => HelperUpdateTransactionManager.CurrentInfo();

    public static HelperUpdateCommitResult ConfirmPendingUpdate(string transactionId) =>
        HelperUpdateTransactionManager.Confirm(transactionId);

    public static bool ConsumePostUpdateStoragePreservation() =>
        HelperUpdateTransactionManager.ConsumeStoragePreservation();

    internal static void ScheduleBootstrapCleanup(string path) => RecordPendingBootstrapCleanup(path);

    public static string ManagedHelperPath()
    {
        return Path.Combine(HelperStorageManager.DefaultDataRoot(), "helper", "ZZZ-Scanner-Helper.exe");
    }

    private static bool ConfirmManagedInstall()
    {
        if (Environment.GetEnvironmentVariable(AcceptManagedInstallEnvironmentVariable) == "1")
        {
            return true;
        }
        if (!Environment.UserInteractive)
        {
            return true;
        }
        const string message = "扫描助手将安装到当前用户的固定目录，并在安装完成后删除本次下载的引导文件。以后更新会在固定目录内自动完成。\n\n是否继续？";
        return MessageBox(IntPtr.Zero, message, "安装 ZZZ Scanner Helper", 0x24) == 6;
    }

    private static async Task<ExistingHelperProbe> ProbeExistingHelperAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = await http.GetAsync("http://127.0.0.1:22355/");
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var service = root.TryGetProperty("service", out var serviceProperty) ? serviceProperty.GetString() ?? "" : "";
            var version = root.TryGetProperty("version", out var versionProperty) ? versionProperty.GetString() ?? "" : "";
            return new ExistingHelperProbe(true, response.IsSuccessStatusCode && service == "zzz-scanner-helper", service, version);
        }
        catch
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync("127.0.0.1", 22355).WaitAsync(TimeSpan.FromMilliseconds(500));
                return new ExistingHelperProbe(true, false, "", "");
            }
            catch
            {
                return new ExistingHelperProbe(false, false, "", "");
            }
        }
    }

    internal static HelperTakeoverSelection SelectTakeoverCandidate(
        string healthVersion,
        string currentProcessPath,
        IEnumerable<HelperProcessCandidate> candidates)
    {
        var matches = candidates
            .Where(candidate => !Path.GetFullPath(candidate.Path).Equals(
                Path.GetFullPath(currentProcessPath),
                StringComparison.OrdinalIgnoreCase))
            .Where(candidate => VersionsEqual(candidate.ProductVersion, healthVersion))
            .ToList();
        return matches.Count == 1
            ? new HelperTakeoverSelection(matches[0], "")
            : new HelperTakeoverSelection(
                null,
                matches.Count == 0 ? "未找到与服务版本匹配的 Helper 进程。" : "检测到多个与服务版本匹配的 Helper 进程。");
    }

    private static List<HelperProcessCandidate> DiscoverHelperProcesses(string currentProcessPath)
    {
        var processName = Path.GetFileNameWithoutExtension(currentProcessPath);
        var candidates = new List<HelperProcessCandidate>();
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                var path = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }
                var productVersion = FileVersionInfo.GetVersionInfo(path).ProductVersion ?? "";
                candidates.Add(new HelperProcessCandidate(process.Id, path, productVersion));
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
        return candidates;
    }

    private static bool VersionsEqual(string left, string right)
    {
        return TryParseVersion(left, out var leftVersion)
            && TryParseVersion(right, out var rightVersion)
            && leftVersion == rightVersion;
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        var normalized = value.Split(new[] { '+', '-' }, 2, StringSplitOptions.None)[0];
        return Version.TryParse(normalized, out version!);
    }

    private static bool ConfirmHelperTakeover(string version, string path)
    {
        if (Environment.GetEnvironmentVariable(AcceptHelperTakeoverEnvironmentVariable) == "1")
        {
            return true;
        }
        if (!Environment.UserInteractive)
        {
            return false;
        }
        var message = $"检测到正在运行的旧版扫描助手 {version}：\n{path}\n\n继续后将关闭旧版、安装到固定目录并启动新版。是否继续？";
        return MessageBox(IntPtr.Zero, message, "更新 ZZZ Scanner Helper", 0x24) == 6;
    }

    private static async Task<bool> StopExistingHelperAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var client = new TcpClient();
                    await client.ConnectAsync("127.0.0.1", 22355).WaitAsync(TimeSpan.FromMilliseconds(300));
                }
                catch
                {
                    return true;
                }
                await Task.Delay(250);
            }
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch
        {
        }
        return false;
    }

    private static void ShowUnsafePortWarning(ExistingHelperProbe helper)
    {
        if (!Environment.UserInteractive) return;
        var identity = string.IsNullOrWhiteSpace(helper.Service) ? "无法识别" : helper.Service;
        MessageBox(IntPtr.Zero, $"端口 22355 已被占用，但服务身份为“{identity}”。为避免结束错误进程，安装已停止。", "无法安全更新 Helper", 0x30);
    }

    private static void ShowCurrentHelperRunning(string version)
    {
        if (!Environment.UserInteractive) return;
        MessageBox(IntPtr.Zero, $"Helper {version} 已在运行，无需重复安装。", "ZZZ Scanner Helper", 0x40);
    }

    private static void ShowAmbiguousHelperWarning(string version, string reason)
    {
        if (!Environment.UserInteractive) return;
        MessageBox(IntPtr.Zero, $"检测到 Helper {version}，但无法安全确定需要关闭的进程。\n{reason}\n请在任务管理器中关闭旧 Helper 后重试。", "无法自动接管旧 Helper", 0x30);
    }

    private static void ShowTakeoverFailure(string version)
    {
        if (!Environment.UserInteractive) return;
        MessageBox(IntPtr.Zero, $"无法关闭 Helper {version} 或端口未及时释放。请在任务管理器中关闭旧 Helper 后重试。", "Helper 更新未完成", 0x30);
    }

    private static async Task VerifySameFileAsync(string source, string destination)
    {
        await using var sourceStream = File.OpenRead(source);
        await using var destinationStream = File.OpenRead(destination);
        var sourceHash = await SHA256.HashDataAsync(sourceStream);
        var destinationHash = await SHA256.HashDataAsync(destinationStream);
        if (!CryptographicOperations.FixedTimeEquals(sourceHash, destinationHash))
        {
            throw new InvalidDataException("Managed Helper copy failed SHA-256 verification.");
        }
    }

    private static async Task RetryPendingBootstrapCleanupAsync()
    {
        var pendingPath = PendingBootstrapCleanupPath();
        if (!File.Exists(pendingPath))
        {
            return;
        }
        try
        {
            var sourcePath = File.ReadAllText(pendingPath).Trim();
            if (!string.IsNullOrWhiteSpace(sourcePath))
            {
                await TryDeleteVerifiedBootstrapAsync(sourcePath);
            }
        }
        catch (Exception ex)
        {
            HelperLog.Write($"BOOTSTRAP_CLEANUP_RETRY_FAILED error={ex.Message}");
        }
    }

    private static async Task TryDeleteVerifiedBootstrapAsync(string sourcePath)
    {
        for (var attempt = 1; attempt <= 20; attempt++)
        {
            try
            {
                var managedPath = ManagedHelperPath();
                if (!File.Exists(sourcePath)
                    || Path.GetFullPath(sourcePath).Equals(Path.GetFullPath(managedPath), StringComparison.OrdinalIgnoreCase))
                {
                    ClearPendingBootstrapCleanup();
                    return;
                }
                bool hashesMatch;
                using (var sourceStream = File.OpenRead(sourcePath))
                using (var managedStream = File.OpenRead(managedPath))
                {
                    var sourceHash = SHA256.HashData(sourceStream);
                    var managedHash = SHA256.HashData(managedStream);
                    hashesMatch = CryptographicOperations.FixedTimeEquals(sourceHash, managedHash);
                }
                if (!hashesMatch)
                {
                    ClearPendingBootstrapCleanup();
                    HelperLog.Write($"BOOTSTRAP_CLEANUP_SKIPPED path={sourcePath} reason=hash-mismatch");
                    return;
                }
                File.Delete(sourcePath);
                ClearPendingBootstrapCleanup();
                return;
            }
            catch (Exception) when (attempt < 20)
            {
                await Task.Delay(250);
            }
            catch (Exception ex)
            {
                RecordPendingBootstrapCleanup(sourcePath);
                HelperLog.Write($"BOOTSTRAP_CLEANUP_FAILED path={sourcePath} error={ex.Message}");
            }
        }
    }

    private static string PendingBootstrapCleanupPath()
    {
        return Path.Combine(HelperStorageManager.DefaultDataRoot(), PendingBootstrapCleanupFile);
    }

    private static void RecordPendingBootstrapCleanup(string sourcePath)
    {
        Directory.CreateDirectory(HelperStorageManager.DefaultDataRoot());
        File.WriteAllText(PendingBootstrapCleanupPath(), Path.GetFullPath(sourcePath));
    }

    private static void ClearPendingBootstrapCleanup()
    {
        try { File.Delete(PendingBootstrapCleanupPath()); } catch { }
    }

    private static async Task WaitForExitAsync(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException)
        {
        }
        catch (TimeoutException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}

internal sealed record HelperLifecycleResult(bool Exit, string[] Arguments);
internal sealed record ExistingHelperProbe(bool PortOpen, bool TrustedService, string Service, string Version);
internal sealed record HelperProcessCandidate(int ProcessId, string Path, string ProductVersion);
internal sealed record HelperTakeoverSelection(HelperProcessCandidate? Candidate, string Reason);
