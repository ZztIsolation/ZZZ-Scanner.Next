using System.Diagnostics;
using System.Text.Json;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;

namespace ZZZ.GamepadProbe;

internal static class OcrBenchmark
{
    internal sealed record Speed(string Name, int HoldMs, int GapMs, int PollMs);
    public static readonly Speed[] Speeds = [new("baseline", 80, 350, 100), new("gap200", 80, 200, 100),
        new("poll50", 80, 100, 50), new("hold60-poll30", 60, 100, 30)];

    public static async Task<int> RunAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var rounds = new List<object>();
        try
        {
            using var mutex = new Mutex(true, @"Local\ZZZ.GamepadProbe", out var available);
            if (!available) throw new InvalidOperationException("请关闭旧验证工具再进行 OCR 速度实验。");
            var processes = Process.GetProcessesByName("ZenlessZoneZero");
            try
            {
                var windows = processes.Where(p => p.MainWindowHandle != 0).ToArray();
                if (windows.Length != 1) throw new InvalidOperationException("未找到唯一游戏窗口。");
                var game = windows[0];
                void Guard()
                {
                    if (game.HasExited || Native.ForegroundProcessId() != game.Id) throw new InvalidOperationException("游戏失焦或退出，停止速度实验。");
                }
                Guard();
                using var reader = new ItemOcr();
                using var device = new GamepadDevice();
                var capture = new ClientCapture(game, Guard);
                var initialDirectory = Path.Combine(directory, "initial");
                var initial = await new GridTraversal(capture, device, Guard, _ => { }, initialDirectory,
                    Options(Speeds[0]), reader).InspectAsync(default);
                if (initial.SelectedRow != 0 || initial.SelectedColumn != 0 || initial.Rows.Length < 2)
                    throw new InvalidOperationException("速度实验必须从背包首行首列开始，并至少显示两行。");
                using (var warmupImage = capture.Capture())
                {
                    var warmup = await reader.ReadAsync(warmupImage, initial, 1, initialDirectory, true, default);
                    Write("warmup.json", new { reader.InitializationMs, reader.Threads, ModelSha256 = ItemOcr.ModelHash, warmup });
                    if (warmup.Error is not null) throw new InvalidOperationException("起点 OCR 失败：" + warmup.Error);
                }
                string[]? baselineRaw = null, baselineClean = null;
                foreach (var speed in Speeds)
                {
                    Guard();
                    Write("progress.json", new { State = "Running", Speed = speed });
                    var roundDirectory = Path.Combine(directory, speed.Name);
                    var result = await new GridTraversal(capture, device, Guard, _ => { }, roundDirectory, Options(speed), reader).RunAsync(default);
                    var rows = File.ReadAllLines(Path.Combine(roundDirectory, "ocr.jsonl"));
                    var raw = rows.Select(line =>
                    {
                        using var row = JsonDocument.Parse(line);
                        return string.Join("|", row.RootElement.GetProperty("Result").GetProperty("Raw").EnumerateArray()
                            .Select(r => StringMatcher.SimplifyChinese(r.GetProperty("Text").GetString() ?? "")));
                    }).ToArray();
                    using var cleanDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(roundDirectory, "items.json")));
                    var clean = cleanDocument.RootElement.EnumerateArray().Select(x => JsonSerializer.Serialize(x)).ToArray();
                    var rawDifferences = baselineRaw is null ? Array.Empty<int>() : Differences(baselineRaw, raw);
                    var cleanDifferences = baselineClean is null ? Array.Empty<int>() : Differences(baselineClean, clean);
                    var valid = result.Outcome == "ProbeLimitReached" && result.CompletedRows == 2
                        && result.OcrItems == initial.Rows[0].Columns.Length + initial.Rows[1].Columns.Length
                        && rawDifferences.Length == 0 && cleanDifferences.Length == 0;
                    rounds.Add(new { Speed = speed, Result = result, MatchesBaseline = valid, RawDifferentIndices = rawDifferences,
                        CleanDifferentIndices = cleanDifferences });
                    Write("benchmark.json", new { Rounds = rounds, Note = "Same positions compared with conservative baseline, not independent ground truth. Init/warmup/rewind excluded from round times." });
                    if (!valid) throw new InvalidOperationException($"{speed.Name} 未通过完整性或基线对照，停止继续提速；当前位置保留用于诊断。");
                    baselineRaw ??= raw; baselineClean ??= clean;
                    // Every successful 2-row trial ends at row 1, column 0. Send one
                    // UP only, then prove the entire starting viewport is restored.
                    await new PulseRunner(device, Guard, _ => { }).RunAsync(Direction.Up, NavigationInput.DPad, 1, 80, 350, default);
                    var restored = await new GridTraversal(capture, device, Guard, _ => { }, Path.Combine(roundDirectory, "restored"), Options(Speeds[0]), reader).InspectAsync(default);
                    if (restored.SelectedRow != 0 || restored.SelectedColumn != 0 || !TraversalPolicy.SameViewport(initial, restored, 8))
                        throw new InvalidOperationException("一格上移未恢复初始网格，停止下一轮，避免绕回末行。");
                }
                Write("progress.json", new { State = "Completed", Rounds = rounds.Count });
                return 0;
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }
        catch (Exception ex)
        {
            Write("progress.json", new { State = "Stopped", ex.Message });
            File.WriteAllText(Path.Combine(directory, "error.txt"), ex.ToString());
            return 1;
        }

        void Write(string name, object value) => File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, JsonDefaults.Write));
    }

    private static TraversalOptions Options(Speed speed) => new(GridVision.DefaultArea, NavigationInput.DPad,
        speed.HoldMs, speed.GapMs, 0, RowLimit: 2, PollMs: speed.PollMs, SaveOcrPanels: true);
    internal static int[] Differences(string[] expected, string[] actual) => Enumerable.Range(0, Math.Max(expected.Length, actual.Length))
        .Where(i => i >= expected.Length || i >= actual.Length || expected[i] != actual[i]).Select(i => i + 1).ToArray();
}
