using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace ZZZScannerNext.Scanning;

internal readonly record struct ResourceCounterSnapshot(
    int Visited,
    int Queued,
    int Completed,
    int Failed);

internal sealed class ResourceMonitor
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    private readonly string _path;
    private readonly string _targetProcessName;
    private readonly Func<ResourceCounterSnapshot> _counterSnapshot;
    private readonly Action<string> _writeLog;
    private readonly CancellationTokenSource _stop = new();
    private readonly Process _scannerProcess;

    private Task? _task;
    private Process? _targetProcess;
    private ProcessCpuState _scannerCpuState;
    private ProcessCpuState _targetCpuState;
    private bool _sampleFailureLogged;
    private int _stopped;
    private int _sampleCount;
    private double _peakScannerCpuPercent;
    private double _peakScannerWorkingSetMb;
    private double _peakScannerPrivateMb;
    private double _peakGameCpuPercent;
    private double _peakGameWorkingSetMb;
    private double _peakGamePrivateMb;
    private int _maxOcrBacklog;

    private ResourceMonitor(
        string path,
        string targetProcessName,
        Func<ResourceCounterSnapshot> counterSnapshot,
        Action<string> writeLog)
    {
        _path = path;
        _targetProcessName = Path.GetFileNameWithoutExtension(targetProcessName.Trim());
        _counterSnapshot = counterSnapshot;
        _writeLog = writeLog;
        _scannerProcess = Process.GetCurrentProcess();
    }

    public static ResourceMonitor Start(
        string path,
        string targetProcessName,
        Func<ResourceCounterSnapshot> counterSnapshot,
        Action<string> writeLog)
    {
        var monitor = new ResourceMonitor(path, targetProcessName, counterSnapshot, writeLog);
        monitor.Start();
        return monitor;
    }

    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        try
        {
            await _stop.CancelAsync();
            if (_task is not null)
            {
                await _task;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _writeLog($"Resource monitor stop failed: {ex.Message}");
        }
        finally
        {
            _scannerProcess.Dispose();
            _targetProcess?.Dispose();
            _stop.Dispose();
        }

        _writeLog(
            "Resource monitor stopped. " +
            $"File={_path}, Samples={_sampleCount}, " +
            $"PeakScannerCpu={_peakScannerCpuPercent:F1}%, " +
            $"PeakScannerWorkingSet={_peakScannerWorkingSetMb:F1} MiB, " +
            $"PeakScannerPrivate={_peakScannerPrivateMb:F1} MiB, " +
            $"PeakGameCpu={_peakGameCpuPercent:F1}%, " +
            $"PeakGameWorkingSet={_peakGameWorkingSetMb:F1} MiB, " +
            $"PeakGamePrivate={_peakGamePrivateMb:F1} MiB, " +
            $"MaxOcrBacklog={_maxOcrBacklog}.");
    }

    private void Start()
    {
        _writeLog($"Resource monitor started. File={_path}, Interval={SampleInterval.TotalMilliseconds:F0}ms, TargetProcess={_targetProcessName}.");
        _task = Task.Run(RunAsync);
    }

    private async Task RunAsync()
    {
        try
        {
            await using var writer = new StreamWriter(_path, append: false, Encoding.UTF8)
            {
                AutoFlush = false
            };
            await writer.WriteLineAsync(
                "timestamp,scanner_cpu_percent,scanner_working_set_mb,scanner_private_mb,scanner_managed_heap_mb,scanner_threads,scanner_handles,game_cpu_percent,game_working_set_mb,game_private_mb,game_threads,game_handles,visited,queued,completed,failed,ocr_backlog");

            await WriteSampleAsync(writer);
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(SampleInterval, _stop.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                await WriteSampleAsync(writer);
            }

            await writer.FlushAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _writeLog($"Resource monitor failed: {ex.Message}");
        }
    }

    private async Task WriteSampleAsync(StreamWriter writer)
    {
        try
        {
            var line = CollectSample();
            await writer.WriteLineAsync(line);
            _sampleCount++;
            if (_sampleCount % 10 == 0)
            {
                await writer.FlushAsync();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_sampleFailureLogged)
            {
                _sampleFailureLogged = true;
                _writeLog($"Resource monitor sample failed: {ex.Message}");
            }
        }
    }

    private string CollectSample()
    {
        var now = DateTimeOffset.Now;
        var scanner = SampleProcess(_scannerProcess, ref _scannerCpuState, now);
        var game = TrySampleTarget(now);
        var managedHeapMb = BytesToMiB(GC.GetTotalMemory(forceFullCollection: false));
        var counters = _counterSnapshot();
        var backlog = Math.Max(0, counters.Queued - counters.Completed - counters.Failed);

        _peakScannerCpuPercent = Math.Max(_peakScannerCpuPercent, scanner.CpuPercent);
        _peakScannerWorkingSetMb = Math.Max(_peakScannerWorkingSetMb, scanner.WorkingSetMb);
        _peakScannerPrivateMb = Math.Max(_peakScannerPrivateMb, scanner.PrivateMemoryMb);
        if (game is not null)
        {
            _peakGameCpuPercent = Math.Max(_peakGameCpuPercent, game.Value.CpuPercent);
            _peakGameWorkingSetMb = Math.Max(_peakGameWorkingSetMb, game.Value.WorkingSetMb);
            _peakGamePrivateMb = Math.Max(_peakGamePrivateMb, game.Value.PrivateMemoryMb);
        }

        _maxOcrBacklog = Math.Max(_maxOcrBacklog, backlog);

        return string.Join(",", [
            now.ToString("O", CultureInfo.InvariantCulture),
            Format(scanner.CpuPercent),
            Format(scanner.WorkingSetMb),
            Format(scanner.PrivateMemoryMb),
            Format(managedHeapMb),
            Format(scanner.ThreadCount),
            Format(scanner.HandleCount),
            Format(game?.CpuPercent),
            Format(game?.WorkingSetMb),
            Format(game?.PrivateMemoryMb),
            Format(game?.ThreadCount),
            Format(game?.HandleCount),
            Format(counters.Visited),
            Format(counters.Queued),
            Format(counters.Completed),
            Format(counters.Failed),
            Format(backlog)
        ]);
    }

    private ProcessResourceSnapshot? TrySampleTarget(DateTimeOffset now)
    {
        var process = ResolveTargetProcess();
        if (process is null)
        {
            _targetCpuState = default;
            return null;
        }

        try
        {
            return SampleProcess(process, ref _targetCpuState, now);
        }
        catch
        {
            _targetProcess?.Dispose();
            _targetProcess = null;
            _targetCpuState = default;
            return null;
        }
    }

    private Process? ResolveTargetProcess()
    {
        if (string.IsNullOrWhiteSpace(_targetProcessName))
        {
            return null;
        }

        try
        {
            if (_targetProcess is not null && !_targetProcess.HasExited)
            {
                return _targetProcess;
            }
        }
        catch
        {
        }

        _targetProcess?.Dispose();
        _targetProcess = null;

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName(_targetProcessName);
        }
        catch
        {
            return null;
        }

        Process? selected = null;
        foreach (var process in processes)
        {
            if (selected is null)
            {
                selected = process;
                continue;
            }

            if (HasMainWindow(process) && !HasMainWindow(selected))
            {
                selected.Dispose();
                selected = process;
            }
            else
            {
                process.Dispose();
            }
        }

        _targetProcess = selected;
        return _targetProcess;
    }

    private static bool HasMainWindow(Process process)
    {
        try
        {
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessResourceSnapshot SampleProcess(Process process, ref ProcessCpuState state, DateTimeOffset now)
    {
        process.Refresh();
        var cpuPercent = CalculateCpuPercent(process, ref state, now);
        return new ProcessResourceSnapshot(
            cpuPercent,
            BytesToMiB(SafeRead(() => process.WorkingSet64)),
            BytesToMiB(SafeRead(() => process.PrivateMemorySize64)),
            SafeRead(() => process.Threads.Count),
            SafeRead(() => process.HandleCount));
    }

    private static double CalculateCpuPercent(Process process, ref ProcessCpuState state, DateTimeOffset now)
    {
        var processId = SafeRead(() => process.Id);
        var totalProcessorTime = process.TotalProcessorTime;
        var cpuPercent = 0d;
        if (state.ProcessId == processId && state.Timestamp is not null)
        {
            var elapsed = now - state.Timestamp.Value;
            var cpuDelta = totalProcessorTime - state.TotalProcessorTime;
            if (elapsed.TotalMilliseconds > 0 && cpuDelta.TotalMilliseconds >= 0)
            {
                cpuPercent = cpuDelta.TotalMilliseconds / elapsed.TotalMilliseconds / Environment.ProcessorCount * 100d;
            }
        }

        state = new ProcessCpuState(processId, now, totalProcessorTime);
        return Math.Max(0d, cpuPercent);
    }

    private static long SafeRead(Func<long> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return 0;
        }
    }

    private static int SafeRead(Func<int> read)
    {
        try
        {
            return read();
        }
        catch
        {
            return 0;
        }
    }

    private static double BytesToMiB(long bytes)
    {
        return bytes / 1024d / 1024d;
    }

    private static string Format(double value)
    {
        return value.ToString("F1", CultureInfo.InvariantCulture);
    }

    private static string Format(double? value)
    {
        return value.HasValue ? Format(value.Value) : "";
    }

    private static string Format(int value)
    {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static string Format(int? value)
    {
        return value.HasValue ? Format(value.Value) : "";
    }

    private readonly record struct ProcessResourceSnapshot(
        double CpuPercent,
        double WorkingSetMb,
        double PrivateMemoryMb,
        int ThreadCount,
        int HandleCount);

    private readonly record struct ProcessCpuState(
        int ProcessId,
        DateTimeOffset? Timestamp,
        TimeSpan TotalProcessorTime);
}
