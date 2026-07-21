using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;
using ZZZScannerNext.Interop;
using ZZZScannerNext.Ocr;
using ZZZScannerNext.Scanning;
using ZZZScannerNext.Ui;
using ZZZScannerNext.WebSocket;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var outputRoot = ReadOption(args, "--output-root");
        if (!string.IsNullOrWhiteSpace(outputRoot))
        {
            Environment.SetEnvironmentVariable("ZZZ_SCANNER_OUTPUT_ROOT", Path.GetFullPath(outputRoot));
        }

        if (TryRunCommandLine(args, out var exitCode))
        {
            return exitCode;
        }

        NativeMethods.TryEnablePerMonitorDpiAwareness();

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    private static bool TryRunCommandLine(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0)
        {
            return false;
        }

        if (string.Equals(args[0], "--scan-benchmark", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --scan-benchmark <scan-dir> [baseline-scan-dir]");
                exitCode = 2;
                return true;
            }

            exitCode = ScanBenchmark.Run(args[1], args.Length > 2 ? args[2] : null);
            return true;
        }

        if (string.Equals(args[0], "--scan-stability-suite", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --scan-stability-suite <scan-parent>");
                exitCode = 2;
                return true;
            }

            exitCode = ScanBenchmark.RunStabilitySuite(args[1]);
            return true;
        }

        if (string.Equals(args[0], "--capture-stability-suite", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --capture-stability-suite gdi|dxgi|both [--max-items 120] [--rounds 5] [--suite-profile speed-1.0.27]");
                exitCode = 2;
                return true;
            }

            exitCode = RunCaptureStabilitySuite(args);
            return true;
        }

        if (string.Equals(args[0], "--ocr-shadow-analyze", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ocr-shadow-analyze <scan-dir-or-parent> [--build-fast-index <file>]");
                exitCode = 2;
                return true;
            }

            exitCode = OcrShadowDatasetAnalyzer.Run(args[1], ReadOption(args, "--build-fast-index"));
            return true;
        }

        if (string.Equals(args[0], "--ocr-fast-eval", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ocr-fast-eval <index.json> <shadow-dir-or-parent>");
                exitCode = 2;
                return true;
            }

            exitCode = FastOcrEvaluator.RunEval(args[1], args[2]);
            return true;
        }

        if (string.Equals(args[0], "--ocr-runtime-smoke", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length != 2)
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    ok = false,
                    command = "ocr-runtime-smoke",
                    error = "Usage: ZZZ-Scanner.Next.exe --ocr-runtime-smoke <fixture>"
                }));
                exitCode = 2;
                return true;
            }

            exitCode = OcrRuntimeSmoke.Run(args[1], Console.Out);
            return true;
        }

        if (string.Equals(args[0], "--ocr-fast-calibrate", StringComparison.OrdinalIgnoreCase))
        {
            var outputFile = ReadOption(args, "--output");
            if (args.Length < 2 || string.IsNullOrWhiteSpace(outputFile))
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ocr-fast-calibrate <shadow-parent> --output <index.json>");
                exitCode = 2;
                return true;
            }

            exitCode = FastOcrEvaluator.RunCalibrate(args[1], outputFile, ReadOption(args, "--feature"));
            return true;
        }

        if (string.Equals(args[0], "--ocr-fast-calibrate-visual-profiles", StringComparison.OrdinalIgnoreCase))
        {
            var outputFile = ReadOption(args, "--output");
            if (args.Length < 2 || string.IsNullOrWhiteSpace(outputFile))
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ocr-fast-calibrate-visual-profiles <shadow-parent> --output <index.json>");
                exitCode = 2;
                return true;
            }

            exitCode = FastOcrEvaluator.RunCalibrateVisualProfiles(args[1], outputFile, ReadOption(args, "--feature"));
            return true;
        }

        if (string.Equals(args[0], "--ocr-fast-feature-eval", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ocr-fast-feature-eval <shadow-parent>");
                exitCode = 2;
                return true;
            }

            exitCode = FastOcrEvaluator.RunFeatureEval(args[1]);
            return true;
        }

        if (string.Equals(args[0], "--ocr-fast-cross-validate", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 2)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ocr-fast-cross-validate <shadow-parent>");
                exitCode = 2;
                return true;
            }

            exitCode = FastOcrEvaluator.RunCrossValidate(args[1]);
            return true;
        }

        if (string.Equals(args[0], "--ocr-fast-merge-indexes", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length < 4)
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ocr-fast-merge-indexes <output.json> <index1.json> <index2.json> [...]");
                exitCode = 2;
                return true;
            }

            exitCode = RunFastOcrMergeIndexes(args[1], args.Skip(2));
            return true;
        }

        if (string.Equals(args[0], "--scan-once", StringComparison.OrdinalIgnoreCase))
        {
            exitCode = RunScanOnce(args);
            return true;
        }

        if (string.Equals(args[0], "--ws", StringComparison.OrdinalIgnoreCase))
        {
            var port = args.Length > 1 && int.TryParse(args[1], out var parsedPort) ? parsedPort : 22350;
            var openBrowser = !args.Any(arg => string.Equals(arg, "--no-browser", StringComparison.OrdinalIgnoreCase));
            exitCode = RunWebSocketHost(port, connectionToken: null, openBrowser);
            return true;
        }

        if (string.Equals(args[0], "--ws-child", StringComparison.OrdinalIgnoreCase))
        {
            var port = args.Length > 1 && int.TryParse(args[1], out var parsedPort) ? parsedPort : 0;
            var token = ReadOption(args, "--child-token");
            if (port <= 0 || string.IsNullOrWhiteSpace(token))
            {
                Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ws-child <port> --child-token <token> [--no-browser]");
                exitCode = 2;
                return true;
            }

            var openBrowser = !args.Any(arg => string.Equals(arg, "--no-browser", StringComparison.OrdinalIgnoreCase));
            exitCode = RunWebSocketHost(port, token, openBrowser);
            return true;
        }

        return false;
    }

    private static int RunCaptureStabilitySuite(string[] args)
    {
        NativeMethods.TryEnablePerMonitorDpiAwareness();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();

        var mode = args[1].Trim().ToLowerInvariant();
        var suiteProfile = ReadOption(args, "--suite-profile") ?? "";
        var cases = BuildCaptureSuiteCases(mode, suiteProfile);
        if (cases.Count == 0)
        {
            Console.Error.WriteLine($"Unknown capture stability suite mode/profile: mode={args[1]}, suiteProfile={suiteProfile}. Expected gdi, dxgi, or both.");
            return 2;
        }

        var maxItems = TryReadIntOption(args, "--max-items", out var parsedMaxItems) ? Math.Max(1, parsedMaxItems) : 120;
        var rounds = TryReadIntOption(args, "--rounds", out var parsedRounds) ? Math.Clamp(parsedRounds, 1, 20) : 5;
        var suiteRoot = Path.Combine(AppContext.BaseDirectory, "StabilitySuites", $"capture-{mode}-{DateTime.Now:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(suiteRoot);

        Console.WriteLine($"capture_suite.root={suiteRoot}");
        Console.WriteLine($"capture_suite.mode={mode}");
        Console.WriteLine($"capture_suite.profile={(string.IsNullOrWhiteSpace(suiteProfile) ? "default" : suiteProfile)}");
        Console.WriteLine($"capture_suite.rounds={rounds}");
        Console.WriteLine($"capture_suite.max_items={maxItems}");

        foreach (var suiteCase in cases)
        {
            var caseRoot = Path.Combine(suiteRoot, suiteCase.Name);
            Directory.CreateDirectory(caseRoot);
            Console.WriteLine($"capture_suite.case={suiteCase.Name}");

            for (var round = 1; round <= rounds; round++)
            {
                var scanArgs = new List<string>
                {
                    "--scan-once",
                    "--fast-mode",
                    "--capture-mode",
                    suiteCase.CaptureMode,
                    "--max-items",
                    maxItems.ToString(CultureInfo.InvariantCulture)
                };
                scanArgs.AddRange(suiteCase.ExtraArgs);

                var scanDirectory = RunScanProcessAndWait(scanArgs, timeout: TimeSpan.FromMinutes(8));
                var targetDirectory = Path.Combine(caseRoot, Path.GetFileName(scanDirectory));
                CopyDirectory(scanDirectory, targetDirectory);
                Console.WriteLine($"capture_suite.{suiteCase.Name}.round_{round}_scan_dir={scanDirectory}");
            }

            ScanBenchmark.RunStabilitySuite(caseRoot);
        }

        return 0;
    }

    private static IReadOnlyList<CaptureSuiteCase> BuildCaptureSuiteCases(string mode, string suiteProfile)
    {
        if (string.Equals(suiteProfile, "speed-1.0.27", StringComparison.OrdinalIgnoreCase))
        {
            var speedCases = new[]
            {
                new CaptureSuiteCase("dxgi-default", "dxgi", []),
                new CaptureSuiteCase("dxgi-floor110-postscroll", "dxgi", ["--panel-min-accept-floor", "110", "--post-scroll-panel-accept-mode", "adaptive-after-scroll"]),
                new CaptureSuiteCase("dxgi-scene105-post110-scroll60", "dxgi", ["--panel-floor-mode", "scene-adaptive", "--same-row-panel-min-accept-floor", "105", "--post-scroll-panel-min-accept-floor", "110", "--post-scroll-panel-accept-mode", "adaptive-after-scroll", "--scroll-tick-delay-ms", "60"]),
                new CaptureSuiteCase("dxgi-scene100-post110-scroll60", "dxgi", ["--panel-floor-mode", "scene-adaptive", "--same-row-panel-min-accept-floor", "100", "--post-scroll-panel-min-accept-floor", "110", "--post-scroll-panel-accept-mode", "adaptive-after-scroll", "--scroll-tick-delay-ms", "60"]),
                new CaptureSuiteCase("dxgi-scene105-post110-scroll50", "dxgi", ["--panel-floor-mode", "scene-adaptive", "--same-row-panel-min-accept-floor", "105", "--post-scroll-panel-min-accept-floor", "110", "--post-scroll-panel-accept-mode", "adaptive-after-scroll", "--scroll-tick-delay-ms", "50"])
            };

            return mode is "dxgi" or "both" ? speedCases : [];
        }

        var dxgiCases = new[]
        {
            new CaptureSuiteCase("dxgi-default", "dxgi", []),
            new CaptureSuiteCase("dxgi-floor110", "dxgi", ["--panel-min-accept-floor", "110"]),
            new CaptureSuiteCase("dxgi-floor110-postscroll", "dxgi", ["--panel-min-accept-floor", "110", "--post-scroll-panel-accept-mode", "adaptive-after-scroll"])
        };
        var gdiCases = new[]
        {
            new CaptureSuiteCase("gdi-default", "gdi", []),
            new CaptureSuiteCase("gdi-postscroll", "gdi", ["--post-scroll-panel-accept-mode", "adaptive-after-scroll"])
        };

        return mode switch
        {
            "dxgi" => dxgiCases,
            "gdi" => gdiCases,
            "both" => dxgiCases.Concat(gdiCases).ToArray(),
            _ => []
        };
    }

    private static string RunScanProcessAndWait(IReadOnlyList<string> scanArgs, TimeSpan timeout)
    {
        var executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ZZZ-Scanner.Next.exe");
        var scansRoot = Path.Combine(AppContext.BaseDirectory, "Scans");
        Directory.CreateDirectory(scansRoot);
        var existingDirectories = Directory.EnumerateDirectories(scansRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var argument in scanArgs)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start scan process.");
        var deadline = DateTime.UtcNow + timeout;
        string? scanDirectory = null;
        while (DateTime.UtcNow < deadline)
        {
            scanDirectory = Directory.EnumerateDirectories(scansRoot)
                .Where(directory => !existingDirectories.Contains(directory))
                .Select(directory => new DirectoryInfo(directory))
                .OrderByDescending(info => info.CreationTimeUtc)
                .Select(info => info.FullName)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(scanDirectory))
            {
                break;
            }

            if (process.HasExited)
            {
                throw new InvalidOperationException($"Scan process exited before creating a scan directory. ExitCode={process.ExitCode}.");
            }

            Thread.Sleep(500);
        }

        if (string.IsNullOrWhiteSpace(scanDirectory))
        {
            throw new TimeoutException("Timed out waiting for scan directory.");
        }

        var resultFile = Path.Combine(scanDirectory, "scan-once-result.json");
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(resultFile))
            {
                process.WaitForExit(5000);
                return scanDirectory;
            }

            Thread.Sleep(1000);
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }

        throw new TimeoutException($"Timed out waiting for scan completion: {scanDirectory}");
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        if (Directory.Exists(targetDirectory))
        {
            Directory.Delete(targetDirectory, recursive: true);
        }

        Directory.CreateDirectory(targetDirectory);
        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(targetDirectory, Path.GetRelativePath(sourceDirectory, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            File.Copy(file, Path.Combine(targetDirectory, relativePath), overwrite: true);
        }
    }

    private static int RunScanOnce(string[] args)
    {
        NativeMethods.TryEnablePerMonitorDpiAwareness();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();

        var scanMutexAcquired = false;
        using var scanMutex = new Mutex(false, @"Local\ZZZScannerNext.ScanOnce");
        try
        {
            scanMutexAcquired = scanMutex.WaitOne(0);
            if (!scanMutexAcquired)
            {
                Console.Error.WriteLine("Another scan is already running. Refusing to start a second scan against the same game window.");
                return 73;
            }

            var command = ParseScanRunCommand(args);
            var options = BuildScanOptions(command);
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                cts.Cancel();
            };

            var progress = new Progress<ScanProgress>(progress =>
            {
                if (!string.IsNullOrWhiteSpace(progress.Message))
                {
                    Console.WriteLine($"progress={progress.Message}; visited={progress.Visited}; queued={progress.Queued}; completed={progress.Completed}; failed={progress.Failed}");
                }
            });

            var controller = new ScanController(ScanProfileFile.Load(), WikiData.Load());
            var result = controller.ScanAsync(options, progress, cts.Token).GetAwaiter().GetResult();
            var runResult = new ScanRunResult
            {
                Success = result.Failed == 0,
                Status = result.Failed == 0 ? "completed" : "completed_with_errors",
                OutputDirectory = result.OutputDirectory,
                ExportFile = result.ExportFile,
                Items = result.Items.Count,
                Visited = result.Visited,
                Queued = result.Queued,
                Completed = result.Completed,
                Failed = result.Failed
            };
            var resultFile = WriteScanRunResult(runResult);
            Console.WriteLine($"output_dir={result.OutputDirectory}");
            Console.WriteLine($"export_file={result.ExportFile}");
            Console.WriteLine($"items={result.Items.Count}");
            Console.WriteLine($"visited={result.Visited}");
            Console.WriteLine($"queued={result.Queued}");
            Console.WriteLine($"completed={result.Completed}");
            Console.WriteLine($"failed={result.Failed}");
            Console.WriteLine($"result_file={resultFile}");
            return result.Failed == 0 ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            var resultFile = WriteScanRunResult(new ScanRunResult
            {
                Success = false,
                Status = "canceled",
                OutputDirectory = FindLatestScanDirectory(),
                Error = "Scan canceled."
            });
            Console.Error.WriteLine("Scan canceled.");
            Console.Error.WriteLine($"result_file={resultFile}");
            return 130;
        }
        catch (Exception ex)
        {
            var resultFile = WriteScanRunResult(new ScanRunResult
            {
                Success = false,
                Status = "failed",
                OutputDirectory = FindLatestScanDirectory(),
                Error = ex.ToString()
            });
            Console.Error.WriteLine(ex);
            Console.Error.WriteLine($"result_file={resultFile}");
            return 1;
        }
        finally
        {
            if (scanMutexAcquired)
            {
                scanMutex.ReleaseMutex();
            }
        }
    }

    private static ScanRunCommand ParseScanRunCommand(string[] args)
    {
        var configPath = ReadOption(args, "--config");
        var command = !string.IsNullOrWhiteSpace(configPath)
            ? JsonSerializer.Deserialize<ScanRunCommand>(File.ReadAllText(configPath), JsonDefaults.Read) ?? new ScanRunCommand()
            : new ScanRunCommand();

        if (TryReadIntOption(args, "--max-items", out var maxItems))
        {
            command.MaxItems = Math.Max(0, maxItems);
        }

        if (TryReadIntOption(args, "--ocr-workers", out var ocrWorkers))
        {
            command.OcrWorkerCount = Math.Clamp(ocrWorkers, 0, 4);
        }

        if (TryReadIntOption(args, "--ocr-batch", out var ocrBatch))
        {
            command.OcrBatchSize = Math.Clamp(ocrBatch, 1, 16);
        }

        if (TryReadIntOption(args, "--ocr-queue", out var ocrQueue))
        {
            command.OcrQueueCapacity = Math.Clamp(ocrQueue, 1, 256);
        }

        if (TryReadIntOption(args, "--ocr-intra-op", out var intraOp))
        {
            command.OcrIntraOpThreads = Math.Clamp(intraOp, 1, 8);
        }

        var processName = ReadOption(args, "--process");
        if (!string.IsNullOrWhiteSpace(processName))
        {
            command.ProcessName = processName;
        }

        var profileName = ReadOption(args, "--profile");
        if (!string.IsNullOrWhiteSpace(profileName))
        {
            command.ProfileName = profileName;
        }

        var captureMode = ReadOption(args, "--capture-mode");
        if (!string.IsNullOrWhiteSpace(captureMode))
        {
            if (Enum.TryParse<CaptureMode>(captureMode, ignoreCase: true, out var parsedCaptureMode))
            {
                command.CaptureMode = parsedCaptureMode;
            }
            else
            {
                throw new ArgumentException($"Unknown capture mode: {captureMode}. Expected gdi or dxgi.");
            }
        }

        var panelStabilityMode = ReadOption(args, "--panel-stability-mode");
        if (!string.IsNullOrWhiteSpace(panelStabilityMode))
        {
            if (TryParsePanelStabilityMode(panelStabilityMode, out var parsedPanelStabilityMode))
            {
                command.PanelStabilityMode = parsedPanelStabilityMode;
            }
            else
            {
                throw new ArgumentException($"Unknown panel stability mode: {panelStabilityMode}. Expected panel, text-core, or auto.");
            }
        }

        var scrollAcceptMode = ReadOption(args, "--scroll-accept-mode");
        if (!string.IsNullOrWhiteSpace(scrollAcceptMode))
        {
            if (TryParseScrollAcceptMode(scrollAcceptMode, out var parsedScrollAcceptMode))
            {
                command.ScrollAcceptMode = parsedScrollAcceptMode;
            }
            else
            {
                throw new ArgumentException($"Unknown scroll accept mode: {scrollAcceptMode}. Expected safe or early-one-row.");
            }
        }

        var panelAcceptMode = ReadOption(args, "--panel-accept-mode");
        if (!string.IsNullOrWhiteSpace(panelAcceptMode))
        {
            if (TryParsePanelAcceptMode(panelAcceptMode, out var parsedPanelAcceptMode))
            {
                command.PanelAcceptMode = parsedPanelAcceptMode;
            }
            else
            {
                throw new ArgumentException($"Unknown panel accept mode: {panelAcceptMode}. Expected safe or adaptive-early-full-roi.");
            }
        }

        var postScrollPanelAcceptMode = ReadOption(args, "--post-scroll-panel-accept-mode");
        if (!string.IsNullOrWhiteSpace(postScrollPanelAcceptMode))
        {
            if (TryParsePostScrollPanelAcceptMode(postScrollPanelAcceptMode, out var parsedPostScrollPanelAcceptMode))
            {
                command.PostScrollPanelAcceptMode = parsedPostScrollPanelAcceptMode;
            }
            else
            {
                throw new ArgumentException($"Unknown post-scroll panel accept mode: {postScrollPanelAcceptMode}. Expected safe or adaptive-after-scroll.");
            }
        }

        if (TryReadIntOption(args, "--panel-min-accept-floor", out var panelMinAcceptFloorMs))
        {
            command.PanelMinAcceptFloorMs = Math.Clamp(panelMinAcceptFloorMs, 90, 120);
        }

        var panelFloorMode = ReadOption(args, "--panel-floor-mode");
        if (!string.IsNullOrWhiteSpace(panelFloorMode))
        {
            if (TryParsePanelFloorMode(panelFloorMode, out var parsedPanelFloorMode))
            {
                command.PanelFloorMode = parsedPanelFloorMode;
            }
            else
            {
                throw new ArgumentException($"Unknown panel floor mode: {panelFloorMode}. Expected static or scene-adaptive.");
            }
        }

        if (TryReadIntOption(args, "--same-row-panel-min-accept-floor", out var sameRowPanelMinAcceptFloorMs))
        {
            command.SameRowPanelMinAcceptFloorMs = Math.Clamp(sameRowPanelMinAcceptFloorMs, 100, 120);
        }

        if (TryReadIntOption(args, "--post-scroll-panel-min-accept-floor", out var postScrollPanelMinAcceptFloorMs))
        {
            command.PostScrollPanelMinAcceptFloorMs = Math.Clamp(postScrollPanelMinAcceptFloorMs, 100, 120);
        }

        if (TryReadIntOption(args, "--scroll-tick-delay-ms", out var scrollTickDelayMs))
        {
            command.ScrollTickDelayOverrideMs = Math.Clamp(scrollTickDelayMs, 50, 80);
        }

        var overlapConflictMode = ReadOption(args, "--overlap-conflict-mode");
        if (!string.IsNullOrWhiteSpace(overlapConflictMode))
        {
            if (TryParseOverlapConflictMode(overlapConflictMode, out var parsedOverlapConflictMode))
            {
                command.OverlapConflictMode = parsedOverlapConflictMode;
            }
            else
            {
                throw new ArgumentException($"Unknown overlap conflict mode: {overlapConflictMode}. Expected strict, recheck, or recover.");
            }
        }

        var collectVisualProfile = ReadOption(args, "--collect-visual-profile");
        if (!string.IsNullOrWhiteSpace(collectVisualProfile))
        {
            command.CollectVisualProfile = true;
            command.VisualProfileId = collectVisualProfile;
            command.OcrShadowDataset = true;
            command.FastMode = false;
            command.FastOcrAssist = false;
            command.FastOcrShadow = false;
            command.AdaptiveTiming = false;
            command.PanelAcceptMode = PanelAcceptMode.Safe;
            command.PostScrollPanelAcceptMode = PostScrollPanelAcceptMode.Safe;
            command.ScrollAcceptMode = ScrollAcceptMode.Safe;
            command.PanelStabilityMode = PanelStabilityMode.Panel;
            command.PanelFloorMode = PanelFloorMode.Static;
            command.PanelMinAcceptFloorMs = 120;
            command.OverlapConflictMode = OverlapConflictMode.Recover;
            if (string.IsNullOrWhiteSpace(profileName))
            {
                command.ProfileName = ScanOptions.FastProfileName;
            }
        }

        var rarities = ReadOption(args, "--rarities");
        if (!string.IsNullOrWhiteSpace(rarities))
        {
            command.Rarities = rarities
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToArray();
        }

        if (args.Any(arg => string.Equals(arg, "--include-non15", StringComparison.OrdinalIgnoreCase)))
        {
            command.StopAtNonLevel15 = false;
        }

        if (args.Any(arg => string.Equals(arg, "--no-bring-to-front", StringComparison.OrdinalIgnoreCase)))
        {
            command.BringToFront = false;
        }

        if (args.Any(arg => string.Equals(arg, "--low-speed-ocr", StringComparison.OrdinalIgnoreCase)))
        {
            command.HighSpeedOcr = false;
        }

        if (args.Any(arg => string.Equals(arg, "--ocr-shadow-dataset", StringComparison.OrdinalIgnoreCase)))
        {
            command.OcrShadowDataset = true;
        }

        if (args.Any(arg => string.Equals(arg, "--ocr-fast-shadow", StringComparison.OrdinalIgnoreCase)))
        {
            command.FastOcrShadow = true;
        }

        if (args.Any(arg => string.Equals(arg, "--ocr-fast-assist", StringComparison.OrdinalIgnoreCase)))
        {
            command.FastOcrAssist = true;
        }

        if (args.Any(arg => string.Equals(arg, "--fast-mode", StringComparison.OrdinalIgnoreCase)))
        {
            command.FastMode = true;
            command.FastOcrAssist = true;
            if (string.IsNullOrWhiteSpace(panelStabilityMode))
            {
                command.PanelStabilityMode = PanelStabilityMode.Panel;
            }

            if (string.IsNullOrWhiteSpace(profileName))
            {
                command.ProfileName = ScanOptions.FastProfileName;
            }

            if (string.IsNullOrWhiteSpace(scrollAcceptMode))
            {
                command.ScrollAcceptMode = ScanModeDefaults.ScrollAccept(true);
            }

            if (string.IsNullOrWhiteSpace(panelAcceptMode))
            {
                command.PanelAcceptMode = ScanModeDefaults.PanelAccept(true);
            }

            if (string.IsNullOrWhiteSpace(overlapConflictMode))
            {
                command.OverlapConflictMode = ScanModeDefaults.OverlapConflict(true);
            }
        }

        if (args.Any(arg => string.Equals(arg, "--adaptive-timing", StringComparison.OrdinalIgnoreCase)))
        {
            command.AdaptiveTiming = true;
        }

        if (args.Any(arg => string.Equals(arg, "--no-adaptive-timing", StringComparison.OrdinalIgnoreCase)))
        {
            command.AdaptiveTiming = false;
        }

        var fastOcrIndex = ReadOption(args, "--ocr-fast-index");
        if (!string.IsNullOrWhiteSpace(fastOcrIndex))
        {
            command.FastOcrTemplateIndexFile = fastOcrIndex;
        }

        var visualProfile = ReadOption(args, "--visual-profile");
        if (!string.IsNullOrWhiteSpace(visualProfile))
        {
            command.VisualProfileId = visualProfile;
        }

        var visualQuality = ReadOption(args, "--visual-quality");
        visualQuality ??= ReadOption(args, "--visual-profile-quality");
        if (!string.IsNullOrWhiteSpace(visualQuality))
        {
            command.VisualQualityLabel = visualQuality;
        }

        var visualClient = ReadOption(args, "--visual-profile-client");
        if (!string.IsNullOrWhiteSpace(visualClient))
        {
            if (TryParseVisualProfileClientKind(visualClient, out var parsedClientKind))
            {
                command.VisualProfileClient = parsedClientKind;
            }
            else
            {
                throw new ArgumentException($"Unknown visual profile client: {visualClient}. Expected auto, local, cloud, or unknown.");
            }
        }

        var profileRouting = ReadOption(args, "--profile-routing");
        if (!string.IsNullOrWhiteSpace(profileRouting))
        {
            if (TryParseProfileRoutingMode(profileRouting, out var parsedProfileRouting))
            {
                command.ProfileRouting = parsedProfileRouting;
            }
            else
            {
                throw new ArgumentException($"Unknown profile routing mode: {profileRouting}. Expected strict, family, compatible, or auto.");
            }
        }

        if (!string.IsNullOrWhiteSpace(collectVisualProfile))
        {
            command.CollectVisualProfile = true;
            command.VisualProfileId = collectVisualProfile;
            command.OcrShadowDataset = true;
            command.FastMode = false;
            command.FastOcrAssist = false;
            command.FastOcrShadow = false;
            command.AdaptiveTiming = false;
            command.PanelAcceptMode = PanelAcceptMode.Safe;
            command.PostScrollPanelAcceptMode = PostScrollPanelAcceptMode.Safe;
            command.ScrollAcceptMode = ScrollAcceptMode.Safe;
            command.PanelStabilityMode = PanelStabilityMode.Panel;
            command.PanelFloorMode = PanelFloorMode.Static;
            command.PanelMinAcceptFloorMs = 120;
            command.OverlapConflictMode = OverlapConflictMode.Recover;
        }

        return command;
    }

    private static ScanOptions BuildScanOptions(ScanRunCommand command)
    {
        var options = new ScanOptions
        {
            ProcessName = command.ProcessName,
            ProfileName = command.ProfileName,
            TraversalMode = command.TraversalMode,
            MaxItems = Math.Max(0, command.MaxItems),
            BringToFront = command.BringToFront,
            StopAtNonLevel15 = command.StopAtNonLevel15,
            HighSpeedOcr = command.HighSpeedOcr,
            OcrShadowDataset = command.OcrShadowDataset,
            FastOcrShadow = command.FastOcrShadow,
            FastOcrAssist = command.FastOcrAssist,
            FastMode = command.FastMode,
            AdaptiveTiming = command.AdaptiveTiming,
            CaptureMode = command.CaptureMode,
            PanelStabilityMode = command.PanelStabilityMode,
            ScrollAcceptMode = command.ScrollAcceptMode,
            PanelAcceptMode = command.PanelAcceptMode,
            PostScrollPanelAcceptMode = command.PostScrollPanelAcceptMode,
            PanelFloorMode = command.PanelFloorMode,
            PanelMinAcceptFloorMs = Math.Clamp(command.PanelMinAcceptFloorMs, 90, 120),
            SameRowPanelMinAcceptFloorMs = Math.Clamp(command.SameRowPanelMinAcceptFloorMs, 100, 120),
            PostScrollPanelMinAcceptFloorMs = Math.Clamp(command.PostScrollPanelMinAcceptFloorMs, 100, 120),
            ScrollTickDelayOverrideMs = command.ScrollTickDelayOverrideMs <= 0 ? 0 : Math.Clamp(command.ScrollTickDelayOverrideMs, 50, 80),
            OverlapConflictMode = command.OverlapConflictMode,
            FastOcrTemplateIndexFile = command.FastOcrTemplateIndexFile,
            VisualProfileId = command.VisualProfileId,
            VisualQualityLabel = command.VisualQualityLabel,
            VisualProfileClient = command.VisualProfileClient,
            CollectVisualProfile = command.CollectVisualProfile,
            ProfileRouting = command.ProfileRouting,
            OcrBatchSize = Math.Clamp(command.OcrBatchSize, 1, 16),
            OcrWorkerCount = Math.Clamp(command.OcrWorkerCount, 0, 4),
            OcrQueueCapacity = Math.Clamp(command.OcrQueueCapacity, 1, 256),
            OcrIntraOpThreads = Math.Clamp(command.OcrIntraOpThreads, 1, 8)
        };

        options.Rarities.Clear();
        foreach (var rarity in command.Rarities.Where(rarity => !string.IsNullOrWhiteSpace(rarity)))
        {
            options.Rarities.Add(rarity.Trim());
        }

        if (options.Rarities.Count == 0)
        {
            options.Rarities.Add("S");
        }

        return options;
    }

    private static int RunWebSocketHost(int port, string? connectionToken, bool openBrowser)
    {
        NativeMethods.TryEnablePerMonitorDpiAwareness();
        var profiles = ScanProfileFile.Load();
        var wikiData = WikiData.Load();
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        using var host = new WebSocketHost(profiles, wikiData, port, connectionToken);
        if (openBrowser)
        {
            host.BrowserUrl = "http://localhost:8787/drive-discs.html";
        }

        try
        {
            host.RunAsync(cts.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    private static string? ReadOption(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], optionName, StringComparison.OrdinalIgnoreCase))
            {
                return i + 1 < args.Length ? args[i + 1] : null;
            }
        }

        return null;
    }

    private static bool TryReadIntOption(string[] args, string optionName, out int value)
    {
        value = 0;
        var text = ReadOption(args, optionName);
        return int.TryParse(text, out value);
    }

    private static bool TryParsePanelStabilityMode(string value, out PanelStabilityMode mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<PanelStabilityMode>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = PanelStabilityMode.Panel;
        return false;
    }

    private static bool TryParseScrollAcceptMode(string value, out ScrollAcceptMode mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<ScrollAcceptMode>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = ScrollAcceptMode.Safe;
        return false;
    }

    private static bool TryParsePanelAcceptMode(string value, out PanelAcceptMode mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<PanelAcceptMode>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = PanelAcceptMode.Safe;
        return false;
    }

    private static bool TryParsePostScrollPanelAcceptMode(string value, out PostScrollPanelAcceptMode mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<PostScrollPanelAcceptMode>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = PostScrollPanelAcceptMode.Safe;
        return false;
    }

    private static bool TryParsePanelFloorMode(string value, out PanelFloorMode mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<PanelFloorMode>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = PanelFloorMode.Static;
        return false;
    }

    private static bool TryParseOverlapConflictMode(string value, out OverlapConflictMode mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<OverlapConflictMode>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = OverlapConflictMode.Recheck;
        return false;
    }

    private static bool TryParseVisualProfileClientKind(string value, out VisualProfileClientKind mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<VisualProfileClientKind>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = VisualProfileClientKind.Auto;
        return false;
    }

    private static bool TryParseProfileRoutingMode(string value, out ProfileRoutingMode mode)
    {
        var normalized = value.Replace("-", "", StringComparison.OrdinalIgnoreCase);
        if (Enum.TryParse<ProfileRoutingMode>(normalized, ignoreCase: true, out mode))
        {
            return true;
        }

        mode = ProfileRoutingMode.Strict;
        return false;
    }

    private static string WriteScanRunResult(ScanRunResult result)
    {
        if (string.IsNullOrWhiteSpace(result.OutputDirectory))
        {
            return "";
        }

        var resultFile = Path.Combine(result.OutputDirectory, "scan-once-result.json");
        try
        {
            File.WriteAllText(resultFile, JsonSerializer.Serialize(result, JsonDefaults.Write));
            return resultFile;
        }
        catch
        {
            // The console output still reports the scan outcome if the sidecar cannot be written.
            return "";
        }
    }

    private static string FindLatestScanDirectory()
    {
        var scansDirectory = Path.Combine(AppContext.BaseDirectory, "Scans");
        if (!Directory.Exists(scansDirectory))
        {
            return "";
        }

        return Directory.EnumerateDirectories(scansDirectory)
            .Select(path => new DirectoryInfo(path))
            .OrderByDescending(info => info.LastWriteTimeUtc)
            .Select(info => info.FullName)
            .FirstOrDefault() ?? "";
    }

    private static int RunFastOcrMergeIndexes(string outputFile, IEnumerable<string> inputFiles)
    {
        var inputs = inputFiles
            .Where(file => !string.IsNullOrWhiteSpace(file))
            .Select(Path.GetFullPath)
            .ToArray();
        if (inputs.Length < 2)
        {
            Console.Error.WriteLine("At least two input indexes are required.");
            return 2;
        }

        var merged = new FastOcrTemplateIndex
        {
            Version = FastOcrTemplateIndex.CurrentVersion,
            Feature = FastOcrTemplateIndex.CanonicalFeature,
            CreatedAt = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)
        };
        var templateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var input in inputs)
        {
            if (!File.Exists(input))
            {
                Console.Error.WriteLine($"Input index not found: {input}");
                return 2;
            }

            var index = FastOcrTemplateIndex.Load(input);
            if (!string.Equals(index.Feature, FastOcrTemplateIndex.CanonicalFeature, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"Only v6 canonical indexes can be merged. {input} uses feature={index.Feature}");
                return 2;
            }

            foreach (var template in index.Templates)
            {
                var key = string.Join(
                    '\u001f',
                    template.FieldKey,
                    template.Label,
                    template.VisualProfileId,
                    template.ProfileFamilyId,
                    string.Join("|", template.Bits));
                if (templateKeys.Add(key))
                {
                    merged.Templates.Add(new FastOcrTemplate
                    {
                        FieldKey = template.FieldKey,
                        Label = template.Label,
                        VisualProfileId = template.VisualProfileId,
                        ProfileFamilyId = template.ProfileFamilyId,
                        Bits = template.Bits.ToArray(),
                        SourceImage = template.SourceImage
                    });
                }
            }

            MergePolicies(merged.FieldPolicies, index.FieldPolicies);
            MergePolicies(merged.ProfileFieldPolicies, index.ProfileFieldPolicies);
            MergePolicies(merged.FamilyFieldPolicies, index.FamilyFieldPolicies);
        }

        merged.Save(outputFile);
        Console.WriteLine($"fast_merge.output={Path.GetFullPath(outputFile)}");
        Console.WriteLine($"fast_merge.inputs={inputs.Length}");
        Console.WriteLine($"fast_merge.templates={merged.Templates.Count}");
        Console.WriteLine($"fast_merge.field_policies={merged.FieldPolicies.Count}");
        Console.WriteLine($"fast_merge.profile_policies={merged.ProfileFieldPolicies.Count}");
        Console.WriteLine($"fast_merge.family_policies={merged.FamilyFieldPolicies.Count}");
        return 0;
    }

    private static void MergePolicies(
        IDictionary<string, FastOcrFieldPolicy> target,
        IReadOnlyDictionary<string, FastOcrFieldPolicy> source)
    {
        foreach (var (key, policy) in source)
        {
            if (target.TryGetValue(key, out var existing))
            {
                existing.AssistEnabled = existing.AssistEnabled && policy.AssistEnabled;
                existing.MinScore = Math.Max(existing.MinScore, policy.MinScore);
                existing.MinMargin = Math.Max(existing.MinMargin, policy.MinMargin);
                existing.TemplateCount += policy.TemplateCount;
                existing.LabelCount = Math.Max(existing.LabelCount, policy.LabelCount);
                target[key] = existing;
            }
            else
            {
                target[key] = new FastOcrFieldPolicy
                {
                    AssistEnabled = policy.AssistEnabled,
                    MinScore = policy.MinScore,
                    MinMargin = policy.MinMargin,
                    TemplateCount = policy.TemplateCount,
                    LabelCount = policy.LabelCount
                };
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private sealed record CaptureSuiteCase(string Name, string CaptureMode, string[] ExtraArgs);
}
