using System.Diagnostics;
using System.Security.Principal;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Interop;
using ZZZScannerNext.Scanning;
using ZZZScannerNext.Ui;
using ZZZScannerNext.WebSocket;

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

    private static int RunWebSocketHost(int port, string? connectionToken, bool openBrowser)
    {
        if (!IsAdministrator())
        {
            Console.Error.WriteLine("WebSocket scanner host must run as administrator so mouse input can reach the game window.");
            return 5;
        }

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
