using System.Collections.Concurrent;
using System.Diagnostics;
using ZZZScannerNext.Scanning.Gamepad;

namespace ZZZScannerNext.Scanning;

public sealed partial class ScanController
{
    private static GamepadDevice ConnectGamepad(ScanLog log)
    {
        var driver = DriverSetup.Inspect();
        log.Write(DriverSetup.Diagnostics(driver));
        if (!driver.CanConnect)
            throw new ScannerFailureException("gamepad_driver_unavailable", "虚拟手柄驱动不可用", driver.Summary,
                "请在扫描器中打开 ViGEmBus 检测 / 安装引导，完成后重新开始扫描。");
        return new GamepadDevice();
    }

    private static async Task ProduceGamepadCapturesAsync(GameWindow window, ScanProfile profile,
        BlockingCollection<DiscCapture> queue, ScanOptions options, ScanRuntimeState runtimeState, Counters counters,
        IProgress<ScanProgress> progress, ScanLog log, int? inventoryCount, GamepadDevice device,
        Action checkWarehouse, string outputDirectory, CancellationToken token)
    {
        if (inventoryCount is not > 0) throw new InvalidOperationException("未取得独立背包数量，无法确认遍历边界。");
        void Guard() { token.ThrowIfCancellationRequested(); window.EnsureGamepadTarget(); }
        if (options.GamepadHoldMilliseconds is < 20 or > 100)
            throw new ArgumentOutOfRangeException(nameof(options.GamepadHoldMilliseconds), "手柄按压时间必须在 20–100 ms 之间。");
        log.Write($"GAMEPAD_INPUT_TIMING holdMs={options.GamepadHoldMilliseconds}, minimumReleaseMs=40, releaseWait=remaining_only");
        var pulse = new GamepadPulse(device, Guard, holdMilliseconds: options.GamepadHoldMilliseconds);
        var gridReads = 0;
        async Task<GridObservation> Observe(CancellationToken ct)
        {
            // Setup, row transitions and exceptional recovery only. Normal items
            // use the original panel probes, with no full-screen grid observations.
            var watch = Stopwatch.StartNew();
            GridObservation? previous = null;
            string? reason = null;
            do
            {
                ct.ThrowIfCancellationRequested();
                Guard();
                checkWarehouse();
                try
                {
                    using var frame = window.Capture(window.ClientScreenRect);
                    Guard();
                    var current = GridVision.Analyze(frame, GridVision.DefaultArea);
                    gridReads++;
                    if (previous is not null && current.SelectedRow == previous.SelectedRow
                        && current.SelectedColumn == previous.SelectedColumn
                        && TraversalPolicy.SameViewport(previous, current, 8)) return current;
                    previous = current;
                }
                catch (InvalidOperationException ex)
                {
                    Guard();
                    reason = ex.Message;
                    previous = null;
                }
                await Task.Delay(Math.Max(15, profile.LoadPollMs), ct);
            } while (watch.ElapsedMilliseconds < profile.LoadTimeoutMs);
            Guard();
            try
            {
                using var failureFrame = window.Capture(window.ClientScreenRect);
                var failureDirectory = Path.Combine(outputDirectory, "navigation");
                Directory.CreateDirectory(failureDirectory);
                failureFrame.Save(Path.Combine(failureDirectory, "grid-failure.png"));
            }
            catch (Exception error) { log.Write($"GAMEPAD_DIAGNOSTIC_SAVE_FAILED {error.Message}"); }
            throw NavigationFailure($"换行/起点网格未稳定：{reason ?? "选中框或行特征仍在变化"}");
        }

        var grid = await Observe(token);
        ValidateGamepadTop(window, profile, grid, inventoryCount.Value, log);
        if (grid.SelectedRow != 0 || grid.SelectedColumn != 0)
            throw NavigationFailure("请从整个背包的第一个盘开始手柄扫描。");
        // Prime once and leave focus on the neighbor. Its panel becomes the
        // baseline for the normal first-cell capture, avoiding a second round trip.
        // A mode switch may consume the first report; only this pulse may retry.
        if (grid.RowCount > 1)
        {
            var start = grid;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                log.Write($"GAMEPAD_MODE_PRIME direction=Right, attempt={attempt + 1}");
                await pulse.SendAsync(Direction.Right, token);
                grid = await Observe(token);
                var move = TraversalPolicy.Right(start, grid);
                if (move.Kind == MoveKind.Advanced) break;
                if (move.Kind != MoveKind.Unchanged || attempt == 1)
                    throw NavigationFailure("手柄初始化右移未能确认。");
            }
        }
        var selector = new DiscSelectionInput(grid, window.ClientScreenRect.Location, pulse, Observe,
            message => log.Write("GAMEPAD_SELECT " + message), (before, actual) =>
            {
                var directory = Path.Combine(outputDirectory, "navigation");
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "viewport-mismatch.json"),
                    System.Text.Json.JsonSerializer.Serialize(new { Before = before, After = actual }));
                Guard();
                using var frame = window.Capture(window.ClientScreenRect);
                frame.Save(Path.Combine(directory, "viewport-mismatch.png"));
            });
        var firstCellHasNeighborBaseline = grid.SelectedRow == 0 && grid.SelectedColumn == 1;

        var columns = grid.Columns;
        var totalRows = (inventoryCount.Value + columns - 1) / columns;
        var panelRect = window.ToScreenRectangle(profile.Rectangle("detailPanel"));
        var panelProbeRect = ProfileRectangleOrFallback(window, profile, "panelChangeProbeRect", panelRect);
        var rois = BuildRois(window, profile, panelRect);
        var statOffset = window.ToScreenPoint(profile.Point("statBackgroundOffset"), clientToScreen: false);
        var background = profile.Color("statRowBackground");
        ImageSignature[]? precedingPanel = firstCellHasNeighborBaseline
            ? CaptureCurrentPanelSignatures(window, panelRect, panelProbeRect, rois) : null;
        if (firstCellHasNeighborBaseline)
            log.Write("GAMEPAD_FIRST_CELL_PREPARED neighborColumn=2, targetColumn=1, capture=original, duplicateFirstPair=False");
        var afterScroll = false;
        var scrollCalibration = new ScrollRowCalibration();
        var timer = Stopwatch.StartNew();
        window.DiscSelectionInput = selector;
        log.Write($"GAMEPAD_INPUT_ONLY columns={columns}, inventory={inventoryCount}, holdMs={options.GamepadHoldMilliseconds}, minimumReleaseMs=40, capture=ScanVisualRowAsync, panelTiming=original");
        try
        {
            for (var logicalRow = 1; logicalRow <= totalRows; logicalRow++)
            {
                Guard();
                var count = Math.Min(columns, inventoryCount.Value - (logicalRow - 1) * columns);
                if (grid.RowCount != count)
                    throw NavigationFailure($"逻辑第 {logicalRow} 行格数不一致：网格 {grid.RowCount}，库存计算 {count}。");
                var (offset, step) = GamepadRowCoordinates(grid);
                var result = await ScanVisualRowAsync(window, profile, queue, options, runtimeState, counters,
                    progress, log, offset, step, panelRect, rois, statOffset, background,
                    pass: logicalRow, row: grid.SelectedRow + 1, isBottom: logicalRow == totalRows,
                    maxColumns: count, logicalRow: logicalRow, treatBlankAsEnd: false,
                    panelChangeProbeRect: panelProbeRect, token: token,
                    afterScroll: afterScroll, postScrollFirstCell: afterScroll,
                    initialPanelSignatures: precedingPanel,
                    firstCellHasNeighborBaseline: logicalRow == 1 && firstCellHasNeighborBaseline);
                log.Write($"GAMEPAD_ROW_CAPTURED logicalRow={logicalRow}, columns={count}, queued={counters.Queued}");
                var transitionWatch = Stopwatch.StartNew();
                var transitionReads = gridReads;
                if (result == RowScanResult.Stop || (options.MaxItems > 0 && counters.Queued >= options.MaxItems)
                    || logicalRow == totalRows)
                {
                    await selector.VerifyPositionAsync(token);
                    grid = selector.Grid;
                    log.Write($"GAMEPAD_ROW_DONE logicalRow={logicalRow}, visualRow={grid.SelectedRow + 1}, columns={count}, gridReads={gridReads}, queued={counters.Queued}, verification=terminal");
                    log.Write($"GAMEPAD_ROW_TRANSITION logicalRow={logicalRow}, milliseconds={transitionWatch.Elapsed.TotalMilliseconds:F1}, gridReads={gridReads - transitionReads}, terminal=True");
                    break;
                }
                precedingPanel = CaptureCurrentPanelSignatures(window, panelRect, panelProbeRect, rois);
                // Reuse the row's accepted grid. Wrap then down as one transition;
                // the final observed first-column focus detects a dropped wrap.
                // There are no separate full-screen checks at row end or after wrap.
                var before = selector.Grid;
                var beforeThumb = CaptureScrollbarThumbProbe(window, profile);
                log.Write($"GAMEPAD_ROW_DOWN direction=Down, logicalRow={logicalRow + 1}");
                var next = await selector.AdvanceRowAsync(token);
                var afterThumb = CaptureScrollbarThumbProbe(window, profile);
                var visualDecision = TraversalPolicy.Down(before, next, afterHorizontalWrap: true);
                var thumbDelta = ScrollbarDelta(beforeThumb, afterThumb);
                var decision = visualDecision;
                if (visualDecision.Kind != MoveKind.Advanced)
                {
                    await Task.Delay(Math.Max(15, profile.LoadPollMs), token);
                    Guard();
                    var confirmation = CaptureScrollbarThumbProbe(window, profile);
                    var stable = beforeThumb.Found && afterThumb.Found && confirmation.Found
                        && Math.Abs(afterThumb.StartY - confirmation.StartY) <= 1
                        && Math.Abs(afterThumb.EndY - confirmation.EndY) <= 1
                        && Math.Abs((beforeThumb.EndY - beforeThumb.StartY) - (afterThumb.EndY - afterThumb.StartY)) <= 2;
                    decision = scrollCalibration.Reconcile(before, next, visualDecision, thumbDelta, stable, afterHorizontalWrap: true);
                }
                scrollCalibration.Learn(visualDecision, thumbDelta);
                log.Write($"GAMEPAD_SCROLL_EVIDENCE logicalRow={logicalRow + 1}, delta={thumbDelta}, calibrated={scrollCalibration.PixelsPerRow:F2}, kind={decision.Kind}, reason={decision.Reason}");
                if (options.OcrShadowDataset || decision.Kind != MoveKind.Advanced)
                {
                    var navigationDirectory = Path.Combine(outputDirectory, "navigation");
                    Directory.CreateDirectory(navigationDirectory);
                    File.WriteAllText(Path.Combine(navigationDirectory, $"row-{logicalRow + 1:D3}.json"),
                        System.Text.Json.JsonSerializer.Serialize(new { Before = before, After = next, CombinedTransition = true, BeforeThumb = beforeThumb,
                            AfterThumb = afterThumb, VisualDecision = visualDecision, Decision = decision }));
                }
                if (decision.Kind != MoveKind.Advanced)
                {
                    using var failureFrame = window.Capture(window.ClientScreenRect);
                    failureFrame.Save(Path.Combine(outputDirectory, "navigation", "down-failure.png"));
                    throw NavigationFailure($"手柄下移未证明前进一行：{decision.Reason}");
                }
                afterScroll = decision.GridShift != 0;
                log.Write($"GAMEPAD_ROW_DONE logicalRow={logicalRow}, visualRow={grid.SelectedRow + 1}, columns={count}, gridReads={gridReads}, queued={counters.Queued}, verification=combined_transition");
                selector.RebaseAfterVerifiedRowAdvance(next, decision);
                grid = next;
                log.Write($"GAMEPAD_ROW_TRANSITION logicalRow={logicalRow}, milliseconds={transitionWatch.Elapsed.TotalMilliseconds:F1}, gridReads={gridReads - transitionReads}, terminal=False");
                log.Write($"GAMEPAD_ROW_ADVANCE logicalRow={logicalRow + 1}, shift={decision.GridShift}, gridReads={gridReads}");
            }
            log.Write($"GAMEPAD_CAPTURE_DONE visited={counters.Visited}, queued={counters.Queued}, actions={pulse.Count}, gridReads={gridReads}, seconds={timer.Elapsed.TotalSeconds:F3}, capture=original");
        }
        finally
        {
            window.DiscSelectionInput = null;
            device.Neutral();
        }
    }

    internal static (PointF Offset, PointF Step) GamepadRowCoordinates(GridObservation grid)
    {
        var first = DiscSelectionInput.CellPoint(grid, 0, 0);
        var stepX = grid.Columns > 1 ? (grid.ColumnX[^1] - grid.ColumnX[0]) / (grid.Columns - 1) : grid.CellWidth;
        var stepY = grid.Rows.Length > 1 ? (grid.Rows[^1].Y - grid.Rows[0].Y) / (grid.Rows.Length - 1) : grid.CellHeight;
        return (new PointF((float)((first.X - stepX) / grid.Width), (float)((first.Y - stepY) / grid.Height)),
            new PointF((float)(stepX / grid.Width), (float)(stepY / grid.Height)));
    }

    private static void ValidateGamepadTop(GameWindow window, ScanProfile profile, GridObservation grid,
        int inventoryCount, ScanLog log)
    {
        if (grid.Rows.Sum(r => r.Columns.Length) == inventoryCount) return;
        for (var sample = 0; sample < 3; sample++)
        {
            window.EnsureGamepadTarget();
            var thumb = CaptureScrollbarThumbProbe(window, profile);
            var top = window.ToScreenPoint(profile.Point("scrollBarTop"));
            var bottom = window.ToScreenPoint(profile.Point("scrollBarBottom"));
            var atTop = thumb.Found && ScrollbarTopResetPlanner.IsAtTop(top.Y, bottom.Y, thumb.StartY, thumb.EndY);
            log.Write($"GAMEPAD_START_TOP sample={sample + 1}, found={thumb.Found}, start={thumb.StartY}, trackTop={top.Y}, atTop={atTop}");
            if (!atTop) throw NavigationFailure("无法确认背包滚动条位于顶部，请回到整个背包首盘。");
        }
    }
}

