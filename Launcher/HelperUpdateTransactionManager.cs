using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZZZScannerHelper;

internal static partial class HelperUpdateTransactionManager
{
    private const string RestoreUpdateArgument = "--restore-helper-update";
    private const string BootstrapArgument = "--complete-managed-install";
    private const string UpdateMutexName = "Local\\ZZZScannerHelperUpdate";
    private static readonly TimeSpan ConfirmationTimeout = TimeSpan.FromSeconds(90);
    private static readonly object Sync = new();
    private static HelperUpdateTransactionReceipt? _active;

    public static HelperUpdateTransactionInfo? CurrentInfo()
    {
        lock (Sync)
        {
            var receipt = _active ?? ReadReceipt();
            if (receipt is null || receipt.State is "confirmed" or "restored")
            {
                return null;
            }
            return new HelperUpdateTransactionInfo
            {
                State = "pending_confirmation",
                TransactionId = receipt.TransactionId,
                PreviousVersion = receipt.PreviousVersion,
            };
        }
    }

    public static void BeginManagedStart(string transactionId, string updaterPath, string backupPath)
    {
        var receipt = ReadReceipt()
            ?? throw new InvalidDataException("Helper update receipt is missing.");
        ValidateReceipt(receipt, transactionId);
        if (!PathEquals(receipt.UpdaterPath, updaterPath) || !PathEquals(receipt.BackupPath, backupPath))
        {
            throw new InvalidDataException("Helper update receipt paths do not match the managed start request.");
        }
        receipt.Stage = "helper-started";
        receipt.UpdatedAt = DateTimeOffset.UtcNow;
        WriteReceipt(receipt);
        lock (Sync)
        {
            _active = receipt;
        }
    }

    public static HelperUpdateCommitResult Confirm(string transactionId)
    {
        lock (Sync)
        {
            if (File.Exists(ConfirmedPath(transactionId)))
            {
                return new HelperUpdateCommitResult
                {
                    TransactionId = transactionId,
                    Committed = true,
                    PreviousVersion = "",
                };
            }

            var receipt = _active ?? ReadReceipt()
                ?? throw new InvalidOperationException("No pending Helper update transaction exists.");
            ValidateReceipt(receipt, transactionId);
            receipt.State = "confirmed";
            receipt.Stage = "browser-confirmed";
            receipt.UpdatedAt = DateTimeOffset.UtcNow;
            WriteReceipt(receipt);
            WriteMarker(PreserveStoragePath(), transactionId);
            WriteMarker(ConfirmedPath(transactionId), receipt.UpdatedAt.ToString("O"));
            HelperInstallationManager.ScheduleBootstrapCleanup(receipt.UpdaterPath);
            _active = receipt;
            return new HelperUpdateCommitResult
            {
                TransactionId = transactionId,
                Committed = true,
                PreviousVersion = receipt.PreviousVersion,
            };
        }
    }

    public static bool SignalStartupFailure()
    {
        lock (Sync)
        {
            var receipt = _active ?? ReadReceipt();
            if (receipt is null || receipt.State == "confirmed")
            {
                return false;
            }
            receipt.State = "failed";
            receipt.Stage = "helper-startup-failed";
            receipt.UpdatedAt = DateTimeOffset.UtcNow;
            WriteReceipt(receipt);
            _active = receipt;
            return true;
        }
    }

    public static bool ConsumeStoragePreservation()
    {
        var path = PreserveStoragePath();
        if (!File.Exists(path))
        {
            return false;
        }
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
        return true;
    }

    public static Task<bool> RecoverInterruptedAsync() => RecoverInterruptedAsync(restartRestored: true);

    internal static Task<bool> RecoverInterruptedForTestsAsync() => RecoverInterruptedAsync(restartRestored: false);

    private static async Task<bool> RecoverInterruptedAsync(bool restartRestored)
    {
        var receipt = ReadReceipt();
        if (receipt is null)
        {
            CleanupOldConfirmationMarkers();
            return false;
        }

        if (receipt.State == "confirmed" || File.Exists(ConfirmedPath(receipt.TransactionId)))
        {
            CleanupCommitted(receipt);
            return false;
        }

        if (!File.Exists(receipt.BackupPath))
        {
            TryDelete(receipt.TargetPath + ".next");
            ClearTransactionFiles(receipt.TransactionId);
            return false;
        }

        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath) && PathEquals(processPath, receipt.TargetPath))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = receipt.BackupPath,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add(RestoreUpdateArgument);
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
            startInfo.ArgumentList.Add(receipt.TargetPath);
            startInfo.ArgumentList.Add(receipt.TransactionId);
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Cannot start the interrupted Helper update rollback.");
            return true;
        }

        await RestoreFromBackupAsync(receipt, null, restartRestored);
        return true;
    }

    public static async Task ApplyAsync(int oldProcessId, string targetPath)
    {
        using var mutex = new Mutex(initiallyOwned: true, UpdateMutexName, out var ownsMutex);
        if (!ownsMutex)
        {
            return;
        }

        targetPath = Path.GetFullPath(targetPath);
        var managedPath = Path.GetFullPath(HelperInstallationManager.ManagedHelperPath());
        if (!PathEquals(targetPath, managedPath))
        {
            throw new InvalidDataException("Helper update target is outside the managed installation path.");
        }

        if (!await WaitForExitAsync(oldProcessId, TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("The old Helper did not exit within 30 seconds.");
        }

        var updaterPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Helper updater path is unavailable.");
        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("The managed Helper target does not exist.", targetPath);
        }
        var backupPath = targetPath + ".previous";
        var candidatePath = targetPath + ".next";
        var transactionId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var previousVersion = File.Exists(targetPath)
            ? NormalizeVersion(FileVersionInfo.GetVersionInfo(targetPath).ProductVersion)
            : "unknown";
        var receipt = new HelperUpdateTransactionReceipt
        {
            TransactionId = transactionId,
            State = "pending",
            Stage = "prepared",
            PreviousVersion = previousVersion,
            TargetPath = targetPath,
            BackupPath = backupPath,
            UpdaterPath = Path.GetFullPath(updaterPath),
            CandidateSha256 = await FileSha256Async(updaterPath),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        Directory.CreateDirectory(UpdateRoot());
        TryDelete(backupPath);
        TryDelete(candidatePath);
        WriteReceipt(receipt);
        Process? managedProcess = null;
        try
        {
            await CopyWithRetryAsync(updaterPath, candidatePath);
            await VerifySameFileAsync(updaterPath, candidatePath);
            receipt.Stage = "candidate-staged";
            receipt.UpdatedAt = DateTimeOffset.UtcNow;
            WriteReceipt(receipt);

            await ReplaceWithRetryAsync(candidatePath, targetPath, backupPath);
            await VerifySameFileAsync(updaterPath, targetPath);
            receipt.Stage = "candidate-installed";
            receipt.UpdatedAt = DateTimeOffset.UtcNow;
            WriteReceipt(receipt);

            var startInfo = new ProcessStartInfo
            {
                FileName = targetPath,
                UseShellExecute = false,
            };
            startInfo.ArgumentList.Add("--complete-helper-update");
            startInfo.ArgumentList.Add(transactionId);
            startInfo.ArgumentList.Add(updaterPath);
            startInfo.ArgumentList.Add(backupPath);
            managedProcess = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Cannot start the updated Helper.");
            receipt.ManagedProcessId = managedProcess.Id;
            receipt.Stage = "awaiting-browser-confirmation";
            receipt.UpdatedAt = DateTimeOffset.UtcNow;
            WriteReceipt(receipt);

            var deadline = DateTime.UtcNow + ConfirmationTimeout;
            while (DateTime.UtcNow < deadline)
            {
                managedProcess.Refresh();
                if (managedProcess.HasExited)
                {
                    break;
                }
                var current = ReadReceipt();
                if (current?.State == "confirmed" || File.Exists(ConfirmedPath(transactionId)))
                {
                    CleanupCommitted(current ?? receipt);
                    return;
                }
                await Task.Delay(250);
            }

            await RestoreFromBackupAsync(receipt, managedProcess, restartRestored: true);
        }
        catch
        {
            await RestoreFromBackupAsync(receipt, managedProcess, restartRestored: true);
            throw;
        }
        finally
        {
            managedProcess?.Dispose();
        }
    }

    public static async Task RestoreAsync(int failedProcessId, string targetPath, string transactionId)
    {
        targetPath = Path.GetFullPath(targetPath);
        if (!PathEquals(targetPath, HelperInstallationManager.ManagedHelperPath()))
        {
            throw new InvalidDataException("Helper rollback target is outside the managed installation path.");
        }
        await WaitForExitAsync(failedProcessId, TimeSpan.FromSeconds(30));
        var backupPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Helper rollback path is unavailable.");
        await DeleteWithRetryAsync(targetPath);
        await CopyWithRetryAsync(backupPath, targetPath);
        await VerifySameFileAsync(backupPath, targetPath);
        ClearTransactionFiles(transactionId);

        var startInfo = new ProcessStartInfo { FileName = targetPath, UseShellExecute = false };
        startInfo.ArgumentList.Add(BootstrapArgument);
        startInfo.ArgumentList.Add(Environment.ProcessId.ToString());
        startInfo.ArgumentList.Add(backupPath);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Cannot restart the restored Helper.");
    }

    internal static HelperUpdateTransactionReceipt? ReadReceiptForTests() => ReadReceipt();

    internal static void WriteReceiptForTests(HelperUpdateTransactionReceipt receipt) => WriteReceipt(receipt);

    internal static void ResetForTests()
    {
        lock (Sync)
        {
            _active = null;
        }
    }

    private static async Task RestoreFromBackupAsync(
        HelperUpdateTransactionReceipt receipt,
        Process? managedProcess,
        bool restartRestored)
    {
        try
        {
            if (managedProcess is not null)
            {
                managedProcess.Refresh();
                if (!managedProcess.HasExited)
                {
                    managedProcess.Kill(entireProcessTree: true);
                    await managedProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }
            }
        }
        catch
        {
        }

        if (File.Exists(receipt.BackupPath))
        {
            await DeleteWithRetryAsync(receipt.TargetPath);
            await MoveWithRetryAsync(receipt.BackupPath, receipt.TargetPath);
            if (restartRestored)
            {
                _ = Process.Start(new ProcessStartInfo { FileName = receipt.TargetPath, UseShellExecute = false });
            }
        }
        TryDelete(receipt.TargetPath + ".next");
        ClearTransactionFiles(receipt.TransactionId);
    }

    private static void CleanupCommitted(HelperUpdateTransactionReceipt receipt)
    {
        TryDelete(receipt.BackupPath);
        TryDelete(receipt.TargetPath + ".next");
        TryDelete(ReceiptPath());
        lock (Sync)
        {
            _active = null;
        }
    }

    private static void ClearTransactionFiles(string transactionId)
    {
        TryDelete(ReceiptPath());
        TryDelete(ConfirmedPath(transactionId));
        lock (Sync)
        {
            _active = null;
        }
    }

    private static HelperUpdateTransactionReceipt? ReadReceipt()
    {
        var path = ReceiptPath();
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(
                File.ReadAllText(path),
                HelperUpdateTransactionJsonContext.Default.HelperUpdateTransactionReceipt);
        }
        catch (Exception ex)
        {
            HelperLog.Write($"HELPER_UPDATE_RECEIPT_READ_FAILED error={ex.Message}");
            return null;
        }
    }

    private static void WriteReceipt(HelperUpdateTransactionReceipt receipt)
    {
        Directory.CreateDirectory(UpdateRoot());
        var path = ReceiptPath();
        var temp = path + ".tmp";
        var json = JsonSerializer.Serialize(
            receipt,
            HelperUpdateTransactionJsonContext.Default.HelperUpdateTransactionReceipt);
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }

    private static void ValidateReceipt(HelperUpdateTransactionReceipt receipt, string transactionId)
    {
        if (string.IsNullOrWhiteSpace(transactionId)
            || !CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(receipt.TransactionId),
                System.Text.Encoding.UTF8.GetBytes(transactionId)))
        {
            throw new InvalidDataException("Helper update transaction id does not match.");
        }
        if (!PathEquals(receipt.TargetPath, HelperInstallationManager.ManagedHelperPath())
            || !PathEquals(receipt.BackupPath, receipt.TargetPath + ".previous"))
        {
            throw new InvalidDataException("Helper update receipt contains unsafe paths.");
        }
    }

    private static string UpdateRoot() => Path.Combine(HelperStorageManager.DefaultDataRoot(), "helper", ".update");

    private static string ReceiptPath() => Path.Combine(UpdateRoot(), "pending.json");

    private static string ConfirmedPath(string transactionId) => Path.Combine(UpdateRoot(), $"confirmed-{transactionId}.txt");

    private static string PreserveStoragePath() => Path.Combine(UpdateRoot(), "preserve-storage-once.txt");

    private static void WriteMarker(string path, string value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    private static void CleanupOldConfirmationMarkers()
    {
        var root = UpdateRoot();
        if (!Directory.Exists(root))
        {
            return;
        }
        foreach (var path in Directory.EnumerateFiles(root, "confirmed-*.txt"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }
    }

    private static async Task<bool> WaitForExitAsync(int processId, TimeSpan timeout)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync().WaitAsync(timeout);
            return true;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static async Task MoveWithRetryAsync(string source, string destination)
    {
        await RetryIoAsync(() => File.Move(source, destination, overwrite: true));
    }

    private static async Task CopyWithRetryAsync(string source, string destination)
    {
        await RetryIoAsync(() => File.Copy(source, destination, overwrite: false));
    }

    private static async Task ReplaceWithRetryAsync(string source, string destination, string backup)
    {
        await RetryIoAsync(() => File.Replace(source, destination, backup, ignoreMetadataErrors: true));
    }

    private static async Task DeleteWithRetryAsync(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }
        await RetryIoAsync(() => File.Delete(path));
    }

    private static async Task RetryIoAsync(Action action)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= 40; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                if (attempt < 40)
                {
                    await Task.Delay(250);
                }
            }
        }
        throw new IOException("Helper update file operation did not complete within 10 seconds.", last);
    }

    private static async Task VerifySameFileAsync(string source, string destination)
    {
        var sourceHash = await FileSha256Async(source);
        var destinationHash = await FileSha256Async(destination);
        if (!string.Equals(sourceHash, destinationHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Managed Helper copy failed SHA-256 verification.");
        }
    }

    private static async Task<string> FileSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static string NormalizeVersion(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Split(new[] { '+', '-' }, 2, StringSplitOptions.None)[0];
    }

    private static bool PathEquals(string left, string right)
    {
        return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    [JsonSerializable(typeof(HelperUpdateTransactionReceipt))]
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = false)]
    private sealed partial class HelperUpdateTransactionJsonContext : JsonSerializerContext
    {
    }
}

internal sealed class HelperUpdateTransactionReceipt
{
    public string TransactionId { get; set; } = "";
    public string State { get; set; } = "pending";
    public string Stage { get; set; } = "";
    public string PreviousVersion { get; set; } = "";
    public string TargetPath { get; set; } = "";
    public string BackupPath { get; set; } = "";
    public string UpdaterPath { get; set; } = "";
    public string CandidateSha256 { get; set; } = "";
    public int? ManagedProcessId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class HelperUpdateTransactionInfo
{
    public string State { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public string PreviousVersion { get; set; } = "";
}

internal sealed class HelperUpdateCommitResult
{
    public string TransactionId { get; set; } = "";
    public bool Committed { get; set; }
    public string PreviousVersion { get; set; } = "";
}
