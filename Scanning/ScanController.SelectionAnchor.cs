using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using ZZZScannerNext.Core;
using ZZZScannerNext.Ocr;

namespace ZZZScannerNext.Scanning;

public sealed partial class ScanController
{
    internal static RowVisualSignature[] CaptureAnchorRowSignatures(CapturedFrame frame, SelectionAnchorGeometry geometry, int gridLeft, int gridWidth)
    {
        // Compare the same interior region at the actual row pitch. The legacy equal-height
        // quarters are retained for off/fallback, but do not align with the game's 175px rows.
        return Enumerable.Range(1, geometry.Rows).Select(row => RowVisualSignatureExtractor.CreateAveraged(frame,
            new Rectangle(gridLeft + (int)Math.Round(8 * geometry.ScaleX),
                geometry.Center(row, 1).Y - (int)Math.Round(40 * geometry.ScaleY),
                gridWidth - (int)Math.Round(16 * geometry.ScaleX), Math.Max(1, (int)Math.Round(80 * geometry.ScaleY))))).ToArray();
    }

    private static RowAdvanceEvidence EvaluateAnchorStructure(ViewportSignatures before, ViewportSignatures after)
    {
        var scores = VerifyOneRowDown(before.AnchorRows!, after.AnchorRows!);
        // The coarse legacy grid includes hover effects. Anchor transactions measure movement
        // from the same normalized, pitch-aligned item interiors used for their row evidence.
        return RowAdvanceEvaluator.Evaluate(scores.NoMoveScore, scores.OneRowScore, scores.TwoRowScore, scores.NoMoveScore);
    }

    public sealed record WheelAnchorProbeResult(bool Success, string OutputDirectory, int RequestedRuns, int OneRowRuns,
        int FailedRuns, int AttemptedRuns, int UnattemptedRuns);

    public async Task<WheelAnchorProbeResult> ProbeWheelAnchorAsync(ScanOptions options, int runs, bool continuous, CancellationToken token,
        IReadOnlyList<int>? inspectIndices = null, bool forceResetDrag = false)
    {
        var profile = _profiles.ResolveRequired(options.ProfileName);
        var output = AppPaths.CreateScanDirectory();
        using var log = new ScanLog(Path.Combine(output, "scan.log"));
        var results = new List<object>();
        var oneRow = 0;
        try
        {
            using var window = GameWindow.Find(options.ProcessName);
            if (options.BringToFront) window.BringToFront();
            window.ConfigureCaptureMode(options.CaptureMode, log.Write);
            using var recognizer = new PaddleOcrRecognizer(AppPaths.ModelFile, AppPaths.CharacterDictFile, 1);
            var progress = new Progress<ScanProgress>(_ => { });
            var counters = new Counters();
            var preflight = await PrepareBackpackAsync(window, profile, recognizer, "wheel-anchor-probe", progress, counters, log, token);
            var guard = new WarehouseContextGuard(window, preflight.MonitorPlan, profile.VisualProbes.WarehousePreflight, recognizer, log);
            window.ConfigureInputGuard(guard.EnsureHealthy);
            var grid = window.ToScreenRectangle(profile.Rectangle("listGridRect"));
            var rects = BuildVisualRowSignatureRects(grid, profile.VisibleRows);
            var settings = ResolveSelectionAnchor(options, profile, log);
            var maxTop = Math.Max(1, (int)Math.Ceiling((preflight.InventoryCount ?? 0) / (double)profile.VisibleColumns) - profile.VisibleRows + 1);
            var top = 1;
            for (var run = 1; run <= runs; run++)
            {
                if (!continuous || run == 1)
                {
                    var resetTicks = profile.ResetToTopWheelTicks;
                    try
                    {
                        if (forceResetDrag) profile.ResetToTopWheelTicks = 0;
                        await ResetListToTopAsync(window, profile, progress, counters, log, token, settings.Verify && !forceResetDrag, preflight.InventoryCount);
                    }
                    finally { profile.ResetToTopWheelTicks = resetTicks; }
                    top = 1;
                }
                var offset = profile.Point("driveDiscOffset");
                var step = profile.Point("driveDiscStep");
                window.LeftClick(window.ToScreenPoint(new PointF(offset.X + step.X * profile.VisibleColumns, offset.Y + step.Y * 3)));
                await Task.Delay(120, token);
                window.MoveCursor(ListNeutralPoint(window, grid));
                await WaitForListStableAsync(window, profile, grid, log, token);
                using (var before = window.Capture(grid)) before.Save(Path.Combine(output, $"run-{run:000}-before.png"));
                var result = await ScrollSafeBandOneRowWithRetryAsync(window, profile, options.ScrollAcceptMode, grid, rects,
                    top, maxTop, false, log, options.ScrollTickDelayOverrideMs, token, settings);
                using (var after = window.Capture(grid)) after.Save(Path.Combine(output, $"run-{run:000}-after.png"));
                if (result.Success && result.RowsAdvanced == 1) { oneRow++; top++; }
                if (result.Success && inspectIndices is { Count: > 0 })
                {
                    foreach (var index in inspectIndices.Where(index => (index - 1) / profile.VisibleColumns + 1 == top + 2))
                    {
                        var col = (index - 1) % profile.VisibleColumns + 1;
                        window.LeftClick(window.ToScreenPoint(new PointF(offset.X + step.X * col, offset.Y + step.Y * 3)));
                        await Task.Delay(300, token);
                        window.MoveCursor(ListNeutralPoint(window, grid));
                        await Task.Delay(120, token);
                        var proof = CaptureViewportSignatures(window, grid, rects, profile);
                        if (proof.Anchor is not { Unique: true, Row: 3 } || proof.Anchor.Column != col)
                            throw NavigationFailure("inspection did not select the requested cell");
                        var detail = window.ToScreenRectangle(profile.Rectangle("detailPanel"));
                        using var picture = window.Capture(Rectangle.Union(grid, detail));
                        picture.Save(Path.Combine(output, $"inventory-{index:0000}-row-{top + 2}-col-{col}.png"));
                        log.WriteEvent("WHEEL_ANCHOR_ITEM_INSPECTION", $"inventoryIndex={index}, logicalRow={top + 2}, column={col}, {proof.Anchor.Describe()}");
                    }
                }
                results.Add(new { run, result.Success, result.RowsAdvanced, result.Message, result.Evidence });
                log.WriteEvent("WHEEL_ANCHOR_PROBE_RESULT", $"run={run}, success={result.Success}, rows={result.RowsAdvanced}, reason={result.Evidence.Reason}");
                if (continuous && !result.Success) break;
            }
            var summary = new WheelAnchorProbeResult(oneRow == runs, output, runs, oneRow, results.Count - oneRow, results.Count, runs - results.Count);
            File.WriteAllText(Path.Combine(output, "wheel-anchor-probe-result.json"), JsonSerializer.Serialize(new
            { summary, clientWidth = window.ClientScreenRect.Width, clientHeight = window.ClientScreenRect.Height,
                inventoryCount = preflight.InventoryCount, captureMode = window.ActiveCaptureMode, settings.Mode, continuous, results }, JsonDefaults.Write));
            return summary;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "wheel-anchor-probe-result.json"), JsonSerializer.Serialize(new
            { success = false, error = ex.Message, results }, JsonDefaults.Write));
            throw;
        }
    }

    private static SelectionAnchorSettings ResolveSelectionAnchor(ScanOptions options, ScanProfile profile, ScanLog log)
    {
        var settings = SelectionAnchorModeResolver.Resolve(options, profile, Environment.GetEnvironmentVariable("ZZZ_SCANNER_SELECTION_ANCHOR"));
        if (settings.Source == "invalid_policy") log.WriteEvent("SELECTION_ANCHOR_IGNORED", "reason=invalid_selection_anchor_policy");
        return settings;
    }
    public async Task<string> CaptureSelectionAnchorAsync(ScanOptions options, CancellationToken token)
    {
        var profile = _profiles.ResolveRequired(options.ProfileName);
        var output = AppPaths.CreateScanDirectory();
        using var log = new ScanLog(Path.Combine(output, "scan.log"));
        var samples = new List<object>();
        try
        {
            using var window = GameWindow.Find(options.ProcessName);
            if (options.BringToFront) window.BringToFront();
            window.ConfigureCaptureMode(options.CaptureMode, log.Write);
            using var recognizer = new PaddleOcrRecognizer(AppPaths.ModelFile, AppPaths.CharacterDictFile, 1);
            var progress = new Progress<ScanProgress>(_ => { });
            var counters = new Counters();
            var preflight = await PrepareBackpackAsync(window, profile, recognizer, "selection-anchor-capture", progress, counters, log, token);
            var guard = new WarehouseContextGuard(window, preflight.MonitorPlan,
                profile.VisualProbes.WarehousePreflight, recognizer, log);
            window.ConfigureInputGuard(guard.EnsureHealthy);
            await ResetListToTopAsync(window, profile, progress, counters, log, token, ResolveSelectionAnchor(options, profile, log).Verify, preflight.InventoryCount);
            var grid = window.ToScreenRectangle(profile.Rectangle("listGridRect"));
            var crop = Rectangle.Intersect(Rectangle.Inflate(grid, 16, 16), window.ClientScreenRect);
            var neutral = ListNeutralPoint(window, grid);
            var wheel = window.ToScreenPoint(profile.Point("listWheelArea"));
            var offset = profile.Point("driveDiscOffset");
            var step = profile.Point("driveDiscStep");
            Point Cell(int row, int col) => window.ToScreenPoint(new PointF(offset.X + col * step.X, offset.Y + row * step.Y));
            void Save(string name, int? expectedRow = null, int? expectedColumn = null, double? elapsedMs = null)
            {
                token.ThrowIfCancellationRequested();
                using var bitmap = window.Capture(crop);
                bitmap.Save(Path.Combine(output, name + ".png"), ImageFormat.Png);
                samples.Add(new { file = name + ".png", expectedRow, expectedColumn, elapsedMs });
            }
            async Task Select(int row, int col)
            {
                window.LeftClick(Cell(row, col));
                await Task.Delay(180, token);
                window.MoveCursor(neutral);
                await Task.Delay(180, token);
            }
            await Select(3, profile.VisibleColumns);
            Save("selected-r3-c9", 3, profile.VisibleColumns);
            window.MoveCursor(wheel);
            await Task.Delay(250, token);
            Save("hover-wheel-area", 3, profile.VisibleColumns);
            window.MoveCursor(Cell(3, 1));
            await Task.Delay(250, token);
            Save("hover-other-cell", 3, profile.VisibleColumns);
            await Select(3, 1);
            window.MoveCursor(Cell(3, 1));
            Save("selected-and-hovered", 3, 1);
            window.MoveCursor(neutral);
            for (var i = 0; i < 40; i++)
            {
                Save($"pulse-{i:000}", 3, 1);
                await Task.Delay(50, token);
            }
            await Select(3, profile.VisibleColumns);
            Save("wheel-before", 3, profile.VisibleColumns);
            window.MoveCursor(wheel);
            await Task.Delay(40, token);
            window.MouseWheel(-120);
            var watch = Stopwatch.StartNew();
            for (var i = 0; watch.ElapsedMilliseconds < 800; i++)
            {
                Save($"wheel-motion-{i:000}", elapsedMs: watch.Elapsed.TotalMilliseconds);
                await Task.Delay(15, token);
            }
            window.MoveCursor(neutral);
            await Task.Delay(200, token);
            Save("wheel-after", 2, profile.VisibleColumns);
            for (var row = 1; row <= 4; row++)
            {
                Save($"click-r{row}-before");
                await Select(row, 1);
                Save($"click-r{row}-after", row, 1);
            }
            await Select(3, profile.VisibleColumns);
            Save("two-rows-before", 3, profile.VisibleColumns);
            window.MoveCursor(wheel);
            await Task.Delay(40, token);
            window.MouseWheel(-120);
            await Task.Delay(450, token);
            window.MouseWheel(-120);
            await Task.Delay(450, token);
            window.MoveCursor(neutral);
            await Task.Delay(200, token);
            Save("two-rows-after", 1, profile.VisibleColumns);
            File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new
            {
                success = true, profile = profile.Name, inventoryCount = preflight.InventoryCount,
                clientWidth = window.ClientScreenRect.Width, clientHeight = window.ClientScreenRect.Height,
                dpi = window.Dpi, captureMode = window.ActiveCaptureMode, frameBackend = window.ActiveFrameBackend,
                cropX = crop.X - window.ClientScreenRect.X, cropY = crop.Y - window.ClientScreenRect.Y,
                labelsVerified = false, samples
            }, JsonDefaults.Write));
            return output;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new
            { success = false, error = ex.Message, samples }, JsonDefaults.Write));
            throw;
        }
    }
}
