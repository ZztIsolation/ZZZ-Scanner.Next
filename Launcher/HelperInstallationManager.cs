using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Net.Sockets;

namespace ZZZScannerHelper;

internal static class HelperInstallationManager
{
    private const string SkipManagedInstallEnvironmentVariable = "ZZZ_SCANNER_SKIP_MANAGED_INSTALL";
    private const string AcceptManagedInstallEnvironmentVariable = "ZZZ_SCANNER_ACCEPT_MANAGED_INSTALL";
    private const string BootstrapArgument = "--complete-managed-install";
    private const string ApplyUpdateArgument = "--apply-helper-update";
    private const string CompleteUpdateArgument = "--complete-helper-update";
    private const string RestoreUpdateArgument = "--restore-helper-update";
    private const string PendingBootstrapCleanupFile = "bootstrap-cleanup-pending.txt";
    private static string? _pendingUpdateBackup;
    private static string? _pendingUpdateTarget;

    public static async Task<HelperLifecycleResult> PrepareAsync(string[] args)
    {
        await RetryPendingBootstrapCleanupAsync();

        if (args.Length >= 3 && args[0].Equals(ApplyUpdateArgument, StringComparison.Ordinal))
        {
            await ApplyUpdateAsync(args);
            return new HelperLifecycleResult(true, []);
        }

        if (args.Length >= 4 && args[0].Equals(CompleteUpdateArgument, StringComparison.Ordinal))
        {
            if (int.TryParse(args[1], out var updaterPid))
            {
                await WaitForExitAsync(updaterPid);
            }
            TryDelete(args[2]);
            _pendingUpdateBackup = args[3];
            _pendingUpdateTarget = ManagedHelperPath();
            return new HelperLifecycleResult(false, args.Skip(4).ToArray());
        }

        if (args.Length >= 3 && args[0].Equals(RestoreUpdateArgument, StringComparison.Ordinal))
        {
            await RestoreUpdateAsync(args);
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

        if (await IsExistingHelperRunningAsync())
        {
            ShowExistingHelperWarning();
            return new HelperLifecycleResult(false, args);
        }

        if (!ConfirmManagedInstall())
        {
            return new HelperLifecycleResult(false, args);
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
        if (!string.IsNullOrWhiteSpace(_pendingUpdateBackup))
        {
            TryDelete(_pendingUpdateBackup);
        }
        _pendingUpdateBackup = null;
        _pendingUpdateTarget = null;
    }

    public static bool TryRollbackPendingUpdate()
    {
        if (string.IsNullOrWhiteSpace(_pendingUpdateBackup)
            || string.IsNullOrWhiteSpace(_pendingUpdateTarget)
            || !File.Exists(_pendingUpdateBackup))
        {
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _pendingUpdateBackup,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(RestoreUpdateArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add(_pendingUpdateTarget);
        Process.Start(startInfo);
        return true;
    }

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

    private static async Task<bool> IsExistingHelperRunningAsync()
    {
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync("127.0.0.1", 22355).WaitAsync(TimeSpan.FromMilliseconds(500));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void ShowExistingHelperWarning()
    {
        if (!Environment.UserInteractive)
        {
            return;
        }
        MessageBox(
            IntPtr.Zero,
            "检测到旧版扫描助手仍在运行。请先关闭旧 Helper，然后再次运行刚下载的安装文件。",
            "请先关闭旧版 Helper",
            0x30);
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

    private static async Task ApplyUpdateAsync(string[] args)
    {
        var oldProcessId = int.Parse(args[1]);
        var targetPath = Path.GetFullPath(args[2]);
        var managedPath = Path.GetFullPath(ManagedHelperPath());
        if (!targetPath.Equals(managedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Helper update target is outside the managed installation path.");
        }

        await WaitForExitAsync(oldProcessId);
        var updaterPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Helper updater path is unavailable.");
        var backupPath = targetPath + ".previous";
        TryDelete(backupPath);
        try
        {
            if (File.Exists(targetPath))
            {
                File.Move(targetPath, backupPath);
            }
            File.Copy(updaterPath, targetPath, overwrite: false);
            await VerifySameFileAsync(updaterPath, targetPath);

            var startInfo = new ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(CompleteUpdateArgument);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add(updaterPath);
            startInfo.ArgumentList.Add(backupPath);
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Cannot start the updated Helper.");
        }
        catch
        {
            TryDelete(targetPath);
            if (File.Exists(backupPath))
            {
                File.Move(backupPath, targetPath);
                Process.Start(new ProcessStartInfo { FileName = targetPath, UseShellExecute = false });
            }
            throw;
        }
    }

    private static async Task RestoreUpdateAsync(string[] args)
    {
        var failedProcessId = int.Parse(args[1]);
        var targetPath = Path.GetFullPath(args[2]);
        if (!targetPath.Equals(Path.GetFullPath(ManagedHelperPath()), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Helper rollback target is outside the managed installation path.");
        }
        await WaitForExitAsync(failedProcessId);
        var backupPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Helper rollback path is unavailable.");
        TryDelete(targetPath);
        File.Copy(backupPath, targetPath, overwrite: false);
        await VerifySameFileAsync(backupPath, targetPath);

        var startInfo = new ProcessStartInfo { FileName = targetPath, UseShellExecute = false };
        startInfo.ArgumentList.Add(BootstrapArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add(backupPath);
        Process.Start(startInfo);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}

internal sealed record HelperLifecycleResult(bool Exit, string[] Arguments);
