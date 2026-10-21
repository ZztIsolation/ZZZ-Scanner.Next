namespace ZZZ.GamepadProbe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--ocr-runtime-check")
        {
            using var output = new StreamWriter(args[1]);
            return ZZZScannerNext.Ocr.OcrRuntimeSmoke.Run(Path.Combine(AppContext.BaseDirectory, "Validation", "ocr-equivalence.png.b64"), output);
        }
        if (args.Length == 2 && args[0] == "--ocr-benchmark")
        {
            ApplicationConfiguration.Initialize();
            return OcrBenchmark.RunAsync(args[1]).GetAwaiter().GetResult();
        }
        if (args.Length == 3 && args[0] == "--ocr-image")
        {
            Directory.CreateDirectory(args[2]);
            try
            {
                using var frame = new Bitmap(args[1]);
                var grid = GridVision.Analyze(frame, GridVision.DefaultArea);
                using var reader = new ItemOcr();
                var result = reader.ReadAsync(frame, grid, 1, args[2], true, default).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(args[2], "ocr.json"), System.Text.Json.JsonSerializer.Serialize(new { reader.InitializationMs, result }, ZZZScannerNext.Core.JsonDefaults.Write));
                return result.Error is null ? 0 : 1;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(args[2], "error.txt"), ex.ToString()); return 1; }
        }
        if (args.Length == 2 && args[0] == "--capture-window")
        {
            ApplicationConfiguration.Initialize();
            try { ClientCapture.CaptureDiagnostic(args[1]); return 0; }
            catch (Exception ex) { File.WriteAllText(args[1] + ".error.txt", ex.ToString()); return 1; }
        }
        if (args.Length == 3 && args[0] == "--analyze-grid")
        {
            try
            {
                using var source = new Bitmap(args[1]);
                var grid = GridVision.Analyze(source, GridVision.DefaultArea);
                using var overlay = GridVision.Overlay(source, grid);
                overlay.Save(args[2] + ".png");
                File.WriteAllText(args[2] + ".json", System.Text.Json.JsonSerializer.Serialize(grid));
                return 0;
            }
            catch (Exception ex) { File.WriteAllText(args[2] + ".error.txt", ex.ToString()); return 1; }
        }
        if ((args.Length == 3 && args[0] is "--traverse-probe" or "--up-probe" or "--down-probe")
            || (args.Length == 6 && args[0] == "--ocr-traverse"))
        {
            ApplicationConfiguration.Initialize();
            Directory.CreateDirectory(args[1]);
            try
            {
                if (!int.TryParse(args[2], out var rows) || rows is < 1 or > 10) throw new ArgumentException("诊断行数范围为 1–10。");
                using var probeMutex = new Mutex(true, @"Local\ZZZ.GamepadProbe", out var available);
                if (!available) throw new InvalidOperationException("请先断开并关闭已有验证工具。");
                var processes = System.Diagnostics.Process.GetProcessesByName("ZenlessZoneZero");
                try
                {
                    var windows = processes.Where(p => p.MainWindowHandle != 0).ToArray();
                    if (windows.Length != 1) throw new InvalidOperationException("未找到唯一游戏窗口。");
                    var process = windows[0];
                    void Guard()
                    {
                        if (process.HasExited || Native.ForegroundProcessId() != process.Id) throw new InvalidOperationException("游戏已退出或失焦。");
                    }
                    Guard();
                    using var reader = args[0] == "--ocr-traverse" ? new ItemOcr() : null;
                    using var device = new GamepadDevice();
                    var holdMs = reader is null ? 80 : int.Parse(args[3]);
                    var gapMs = reader is null ? 350 : int.Parse(args[4]);
                    var pollMs = reader is null ? 100 : int.Parse(args[5]);
                    var traversal = new GridTraversal(new ClientCapture(process, Guard), device, Guard, _ => { }, args[1],
                        new TraversalOptions(GridVision.DefaultArea, NavigationInput.DPad, holdMs, gapMs, 0,
                            RowLimit: rows, PollMs: pollMs, SaveOcrPanels: reader is not null), reader);
                    if (args[0] is "--up-probe" or "--down-probe")
                    {
                        var before = traversal.InspectAsync(default).GetAwaiter().GetResult();
                        if (before.SelectedColumn != 0) throw new InvalidOperationException("纵向诊断必须位于首列。");
                        File.Copy(Path.Combine(args[1], "calibration-source.png"), Path.Combine(args[1], "before-source.png"), true);
                        File.Copy(Path.Combine(args[1], "calibration-grid.png"), Path.Combine(args[1], "before-grid.png"), true);
                        var direction = args[0] == "--up-probe" ? Direction.Up : Direction.Down;
                        new PulseRunner(device, Guard, _ => { }).RunAsync(direction, NavigationInput.DPad, rows, 80, 350, default).GetAwaiter().GetResult();
                        var after = traversal.InspectAsync(default).GetAwaiter().GetResult();
                        File.WriteAllText(Path.Combine(args[1], "navigation.json"), System.Text.Json.JsonSerializer.Serialize(new { Direction = direction.ToString(), Pulses = rows, Before = before, After = after }));
                        // Success means observations were saved, never that these
                        // unverified pulses reached the top/bottom or advanced N rows.
                        return 0;
                    }
                    var result = traversal.RunAsync(default).GetAwaiter().GetResult();
                    return result.Outcome == "ProbeLimitReached" ? 0 : 1;
                }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(args[1], "error.txt"), ex.ToString()); return 1; }
        }
        if (args.Length == 2 && args[0] is "--self-test" or "--device-check" or "--diagnostics" or "--verify-installer")
        {
            try
            {
                var report = args[0] switch
                {
                    "--self-test" => ProbeChecks.RunAsync().GetAwaiter().GetResult(),
                    "--device-check" => ProbeChecks.CheckDeviceAsync().GetAwaiter().GetResult(),
                    "--diagnostics" => DriverSetup.Diagnostics(DriverSetup.Inspect()),
                    _ => VerifyInstaller()
                };
                File.WriteAllText(args[1], report);
                return 0;
            }
            catch (Exception ex)
            {
                File.WriteAllText(args[1], "FAIL\n" + ex);
                return 1;
            }
        }

        using var mutex = new Mutex(true, @"Local\ZZZ.GamepadProbe", out var first);
        if (!first)
        {
            MessageBox.Show("手柄验证工具已在运行，请使用已有窗口。", "ZZZ 手柄验证");
            return 1;
        }
        ApplicationConfiguration.Initialize();
        Application.Run(new ProbeForm());
        return 0;
    }

    private static string VerifyInstaller()
    {
        DriverSetup.VerifyInstaller(DriverSetup.InstallerPath);
        return "PASS bundled installer SHA256 matches pinned official asset; installer was NOT executed.\n";
    }
}
