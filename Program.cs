using System.Diagnostics;
using System.Security.Principal;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Interop;
using ZZZScannerNext.Ocr;
using ZZZScannerNext.Scanning;
using ZZZScannerNext.Ui;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (TryRunCommandLine(args, out var exitCode))
        {
            return exitCode;
        }

        NativeMethods.TryEnablePerMonitorDpiAwareness();

        if (!IsAdministrator())
        {
            TryRelaunchAsAdministrator();
            return 0;
        }

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

        if (string.Equals(args[0], "--collect-ocr-samples", StringComparison.OrdinalIgnoreCase))
        {
            var sampleLimit = args.Length > 1 && int.TryParse(args[1], out var parsedLimit) ? parsedLimit : 1000;
            var maxItems = args.Length > 2 && int.TryParse(args[2], out var parsedMaxItems) ? parsedMaxItems : 0;
            var rarities = args.Length > 3 ? args[3] : "S,A,B";
            exitCode = CollectOcrSamples(sampleLimit, maxItems, rarities);
            return true;
        }

        if (!string.Equals(args[0], "--ocr-benchmark", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: ZZZ-Scanner.Next.exe --ocr-benchmark <ocr-samples-dir> [workers] [batchSize] [intraOpThreads]");
            exitCode = 2;
            return true;
        }

        var workers = args.Length > 2 && int.TryParse(args[2], out var parsedWorkers) ? parsedWorkers : 1;
        var batchSize = args.Length > 3 && int.TryParse(args[3], out var parsedBatchSize) ? parsedBatchSize : 8;
        var intraOpThreads = args.Length > 4 && int.TryParse(args[4], out var parsedThreads) ? parsedThreads : 4;
        exitCode = OcrBenchmark.Run(args[1], workers, batchSize, intraOpThreads);
        return true;
    }

    private static int CollectOcrSamples(int sampleLimit, int maxItems, string raritiesCsv)
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("OCR sample collection must run as administrator so mouse input can reach the game window.");
            return 5;
        }

        NativeMethods.TryEnablePerMonitorDpiAwareness();
        var profiles = ScanProfileFile.Load();
        var controller = new ScanController(profiles, WikiData.Load());
        var options = new ScanOptions
        {
            ProcessName = "ZenlessZoneZero",
            ProfileName = profiles.Profiles.FirstOrDefault()?.Name ?? "",
            TraversalMode = ScanTraversalMode.FromProfile,
            MaxItems = Math.Max(0, maxItems),
            BringToFront = true,
            ShowDebugImages = false,
            StopAtNonLevel15 = false,
            OcrEngine = OcrEngineMode.Auto,
            HighSpeedOcr = true,
            OcrWorkerCount = 0,
            OcrSampleLimit = Math.Max(1, sampleLimit)
        };

        options.Rarities.Clear();
        foreach (var rarity in raritiesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            options.Rarities.Add(rarity);
        }

        if (options.Rarities.Count == 0)
        {
            Console.Error.WriteLine("At least one rarity must be provided, for example S,A,B.");
            return 2;
        }

        var progress = new Progress<ScanProgress>(p =>
        {
            if (p.Queued > 0 && p.Queued % 25 == 0)
            {
                Console.WriteLine($"progress visited={p.Visited} queued={p.Queued} completed={p.Completed} failed={p.Failed}");
            }
        });

        try
        {
            Console.WriteLine($"collect_ocr_samples limit={options.OcrSampleLimit} max_items={options.MaxItems} rarities={string.Join(",", options.Rarities)} stop_at_non15={options.StopAtNonLevel15}");
            var result = controller.ScanAsync(options, progress, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"output_dir={result.OutputDirectory}");
            Console.WriteLine($"export_file={result.ExportFile}");
            Console.WriteLine($"visited={result.Visited} items={result.Items.Count} failed={result.Failed}");
            return result.Failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void TryRelaunchAsAdministrator()
    {
        try
        {
            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory
            });
        }
        catch
        {
            MessageBox.Show("需要管理员权限才能向游戏窗口发送鼠标输入。", "ZZZ Scanner Next",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
