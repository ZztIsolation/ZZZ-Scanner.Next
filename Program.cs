using System.Diagnostics;
using System.Security.Principal;
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
