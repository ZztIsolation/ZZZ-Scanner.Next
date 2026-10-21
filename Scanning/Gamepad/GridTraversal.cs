using System.Diagnostics;
using System.Drawing.Imaging;
using System.Text.Json;

namespace ZZZScannerNext.Scanning.Gamepad;

internal sealed record TraversalOptions(RectangleF Area, NavigationInput Input, int HoldMs, int GapMs,
    int ExpectedItems, int MaxItems = 5000, int MaxActions = 15000, int RowLimit = 0,
    int PollMs = 100, int StableObservations = 3, int MinimumDetailSettleMs = 180, bool SaveOcrPanels = false);
internal sealed record TraversalResult(string Outcome, int VisitedItems, int CompletedRows, int Actions,
    string Reason, double ElapsedSeconds, int ExpectedItems, int OcrItems = 0, int OcrReviewItems = 0);

internal sealed class GridFrame(Bitmap image, GridObservation grid) : IDisposable
{
    public Bitmap Image { get; } = image;
    public GridObservation Grid { get; } = grid;
    public void Dispose() => Image.Dispose();
}

internal sealed class GridTraversal(IClientCapture capture, IGamepadOutput output, Action ensureTarget,
    Action<string> log, string directory, TraversalOptions options, IItemReader? itemReader = null,
    ITraversalSink? sink = null)
{
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private GridFrame? current;
    private int actions, visited, completedRows;
    private int ocrReviewItems;
    private int imageNumber;
    private StreamWriter? events;
    private StreamWriter? ocrEvents;
    private readonly List<ZZZScannerNext.Scanning.DriveDiscExport> items = [];
    private readonly ProbeMetrics metrics = new();

    public async Task<GridObservation> InspectAsync(CancellationToken token)
    {
        Directory.CreateDirectory(directory);
        using var frame = await ObserveStableAsync(token);
        frame.Image.Save(Path.Combine(directory, "calibration-source.png"), ImageFormat.Png);
        using var overlay = GridVision.Overlay(frame.Image, frame.Grid);
        overlay.Save(Path.Combine(directory, "calibration-grid.png"), ImageFormat.Png);
        File.WriteAllText(Path.Combine(directory, "calibration.json"), JsonSerializer.Serialize(frame.Grid));
        return frame.Grid;
    }

    public async Task<TraversalResult> RunAsync(CancellationToken token)
    {
        if (options.PollMs is < 20 or > 1000 || options.StableObservations is < 3 or > 10
            || options.MinimumDetailSettleMs is < 180 or > 2000) throw new ArgumentOutOfRangeException(nameof(options));
        watch.Restart();
        using var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime.TotalMilliseconds;
        Directory.CreateDirectory(directory);
        events = new StreamWriter(Path.Combine(directory, "events.jsonl")) { AutoFlush = true };
        if (itemReader is not null) ocrEvents = new StreamWriter(Path.Combine(directory, "ocr.jsonl")) { AutoFlush = true };
        File.WriteAllText(Path.Combine(directory, "settings.json"), JsonSerializer.Serialize(new
        {
            options, StartConfirmedByUser = true, Tool = Application.ProductVersion,
            OcrEnabled = itemReader is not null || sink is not null,
            Pipeline = sink is not null ? "production-queued" : "serial-probe",
            Note = "Position counts are geometric confirmations. Matching OCR values are not deduplicated."
        }));
        var outcome = "StoppedAmbiguous";
        var reason = "";
        try
        {
            current = await ObserveStableAsync(token);
            if (current.Grid.SelectedColumn != 0 || current.Grid.SelectedRow != 0 || !current.Grid.Rows[0].FullyVisible)
                throw new InvalidOperationException("起点不是最上方完整可见行的首列。请手动回到整个背包第一个盘后重试。");
            sink?.ValidateStart(current.Grid);
            SaveImage("start", current, full: true);
            Emit("START", new { current.Grid.Columns, VisibleRows = current.Grid.Rows.Length, options.ExpectedItems });
            var startGrid = current.Grid;
            var logicalRow = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var rowSize = current.Grid.RowCount;
                if (current.Grid.SelectedColumn != 0) throw new InvalidOperationException("行起点不是首列。");
                if (!current.Grid.Rows[current.Grid.SelectedRow].FullyVisible)
                    throw new InvalidOperationException("选中行未完整显示，无法验证格子数量。");
                SaveImage($"row-{logicalRow:D4}-start", current);
                await VisitAsync(logicalRow, 0, rowSize, token);
                for (var col = 1; col < rowSize; col++)
                {
                    var decision = await MoveAsync(Direction.Right, token);
                    if (logicalRow == 0 && col == 1 && actions == 1 && decision.Kind == MoveKind.Unchanged)
                    {
                        // The first direction can be consumed by keyboard/controller
                        // hot switching. Retry once only after stable unchanged evidence.
                        Emit("STARTUP_RETRY", new { Reason = "首次右移未改变焦点，可能仅切换了输入模式；不增加访问数" });
                        await Task.Delay(350, token);
                        decision = await MoveAsync(Direction.Right, token);
                    }
                    if (decision.Kind != MoveKind.Advanced || current.Grid.SelectedColumn != col)
                        throw new InvalidOperationException("右移未准确前进一列：" + decision.Reason);
                    await VisitAsync(logicalRow, col, rowSize, token);
                }
                if (rowSize > 1)
                {
                    var decision = await MoveAsync(Direction.Right, token);
                    if (decision.Kind != MoveKind.Wrapped)
                        throw new InvalidOperationException("本行已计数，但未确认末列回到首列：" + decision.Reason);
                }
                else Emit("SINGLE_CELL_ROW", new { logicalRow, Reason = "视觉只发现一个格子，不以右移不动证明循环" });
                completedRows++;
                Emit("ROW_COMPLETE", new { logicalRow, rowSize, visited });
                SaveImage($"row-{logicalRow:D4}-wrapped", current);
                if (options.RowLimit > 0 && completedRows >= options.RowLimit)
                {
                    outcome = "ProbeLimitReached"; reason = "达到用户设置的验证行数；未宣称遍历完整背包。";
                    break;
                }
                var beforeDown = current.Grid;
                var down = await MoveAsync(Direction.Down, token);
                if (down.Kind == MoveKind.Unchanged)
                {
                    // A second bounded probe can resolve a dropped down input. Identical
                    // rows remain ambiguous unless the independently supplied count agrees.
                    down = await MoveAsync(Direction.Down, token);
                    if (down.Kind == MoveKind.Unchanged)
                    {
                        var noVisibleRowBelow = current.Grid.SelectedRow == current.Grid.Rows.Length - 1;
                        if (!noVisibleRowBelow) throw new InvalidOperationException("下方仍有可见行，但两次下移未确认移动。");
                        if (options.ExpectedItems > 0 && visited != options.ExpectedItems)
                            throw new InvalidOperationException($"底部候选与人工总数不一致：已访问 {visited}，预期 {options.ExpectedItems}。");
                        outcome = options.ExpectedItems > 0 ? "CompletedWithExpectedCount" : "BottomCandidate";
                        reason = options.ExpectedItems > 0 ? "人工总数一致，末行首列两次下移均未观察到变化。"
                            : "末行首列两次下移未观察到变化；无独立总数，需人工核对完整性。";
                        break;
                    }
                }
                if (down.Kind != MoveKind.Advanced && completedRows > 1
                    && TraversalPolicy.ReturnedToStart(startGrid, beforeDown, current.Grid))
                {
                    if (options.ExpectedItems > 0 && visited != options.ExpectedItems)
                        throw new InvalidOperationException($"纵向绕回起点，但计数不符：已访问 {visited}，预期 {options.ExpectedItems}。");
                    outcome = options.ExpectedItems > 0 ? "CompletedWithExpectedCount" : "WrappedToStartCandidate";
                    reason = options.ExpectedItems > 0 ? "下移绕回初始首列及整屏网格，访问数与人工总数一致。"
                        : "下移绕回初始首列及整屏网格；无独立总数，需人工核对完整性。";
                    Emit("RETURNED_TO_START", new { visited, completedRows, options.ExpectedItems });
                    break;
                }
                if (down.Kind != MoveKind.Advanced) throw new InvalidOperationException("无法确认进入下一行：" + down.Reason);
                logicalRow++;
            }
        }
        catch (TraversalLimitReachedException) { outcome = "RequestedLimitReached"; reason = "达到本次扫描数量上限。"; }
        catch (OperationCanceledException) { outcome = "Canceled"; reason = "用户取消或停止。"; }
        catch (Exception ex) { reason = ex.Message; Emit("ERROR", new { ex.Message, Exception = ex.GetType().Name }); }
        finally
        {
            try { output.Neutral(); }
            catch (Exception ex) { outcome = "StoppedAmbiguous"; reason += " 输入释放失败：" + ex.Message; }
            if (current is not null)
            {
                try { SaveImage("last-confirmed", current, full: true); }
                catch (Exception ex) { outcome = "StoppedAmbiguous"; reason += " 最终截图写入失败：" + ex.Message; }
                finally { current.Dispose(); current = null; }
            }
            events?.Dispose(); events = null;
            ocrEvents?.Dispose(); ocrEvents = null;
        }
        var result = new TraversalResult(outcome, visited, completedRows, actions, reason, watch.Elapsed.TotalSeconds, options.ExpectedItems, items.Count, ocrReviewItems);
        File.WriteAllText(Path.Combine(directory, "summary.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        if (itemReader is not null) File.WriteAllText(Path.Combine(directory, "items.json"), JsonSerializer.Serialize(items, ZZZScannerNext.Core.JsonDefaults.Write));
        process.Refresh();
        File.WriteAllText(Path.Combine(directory, "performance.json"), JsonSerializer.Serialize(metrics.Report(result.ElapsedSeconds,
            sink is null ? items.Count : null, process.TotalProcessorTime.TotalMilliseconds - cpuStart, process.PeakWorkingSet64), ZZZScannerNext.Core.JsonDefaults.Write));
        log($"AUTO_RESULT {outcome}; visited={visited}; rows={completedRows}; {reason}; evidence={directory}");
        return result;
    }

    private async Task VisitAsync(int row, int col, int rowSize, CancellationToken token)
    {
        if (visited >= options.MaxItems || (options.ExpectedItems > 0 && visited >= options.ExpectedItems))
            throw new InvalidOperationException("访问计数超过人工总数或安全上限，已停止。");
        visited++;
        Emit("VISIT", new { Index = visited, Row = row, Column = col, RowSize = rowSize,
            VisualRow = current!.Grid.SelectedRow, Selection = current.Grid.Selection });
        if (sink is not null)
        {
            ensureTarget();
            using (metrics.Measure("enqueueWall"))
                if (!await sink.VisitAsync(current.Image, current.Grid, visited, token))
                    throw new TraversalLimitReachedException();
        }
        if (itemReader is null) return;
        ensureTarget();
        ItemRead read;
        using (metrics.Measure("ocrWall"))
            read = await itemReader.ReadAsync(current.Image, current.Grid, visited, directory, options.SaveOcrPanels, token);
        token.ThrowIfCancellationRequested();
        ensureTarget();
        ocrEvents!.WriteLine(JsonSerializer.Serialize(new { Row = row, Column = col, Result = read }, ZZZScannerNext.Core.JsonDefaults.Wire));
        metrics.Add("ocrCrop", read.CropMs);
        metrics.Add("ocrPreprocess", read.Ocr.PreprocessMs);
        metrics.Add("ocrInference", read.Ocr.InferenceMs);
        metrics.Add("ocrDecode", read.Ocr.DecodeMs);
        metrics.Add("ocrClean", read.CleanMs);
        if (read.Error is not null || read.Item is null) throw new InvalidOperationException($"第 {visited} 个位置 OCR 校验失败：{read.Error}");
        if (read.Warnings.Length > 0) { ocrReviewItems++; Emit("OCR_REVIEW_REQUIRED", new { Index = visited, read.Warnings }); }
        items.Add(read.Item);
        Emit("OCR_ACCEPTED", new { Index = visited, read.Item.Name, read.Item.Slot, read.TotalMs });
    }

    private async Task<MoveDecision> MoveAsync(Direction direction, CancellationToken token)
    {
        if (++actions > options.MaxActions) throw new InvalidOperationException("达到方向操作次数上限。");
        ensureTarget();
        var before = current ?? throw new InvalidOperationException();
        using (metrics.Measure("inputPulseAndGap"))
            await new PulseRunner(output, ensureTarget, log).RunAsync(direction, options.Input, 1, options.HoldMs, options.GapMs, token);
        if ((itemReader is not null || sink is not null) && options.GapMs < options.MinimumDetailSettleMs)
            using (metrics.Measure("detailMinimumWait")) await Task.Delay(options.MinimumDetailSettleMs - options.GapMs, token);
        var after = await ObserveStableAsync(token);
        try
        {
            var decision = direction == Direction.Right ? TraversalPolicy.Right(before.Grid, after.Grid) : TraversalPolicy.Down(before.Grid, after.Grid);
            Emit("MOVE", new { Action = actions, Direction = direction.ToString(), decision,
                BeforeRow = before.Grid.SelectedRow, BeforeColumn = before.Grid.SelectedColumn,
                AfterRow = after.Grid.SelectedRow, AfterColumn = after.Grid.SelectedColumn,
                Before = before.Grid, After = after.Grid });
            if (decision.Kind == MoveKind.Ambiguous || direction == Direction.Down)
            {
                SaveImage($"action-{actions:D5}-before", before);
                SaveImage($"action-{actions:D5}-after", after);
            }
            current = after;
            before.Dispose();
            return decision;
        }
        finally { if (!ReferenceEquals(current, after)) after.Dispose(); }
    }

    private async Task<GridFrame> ObserveStableAsync(CancellationToken token)
    {
        using var observationTime = metrics.Measure("observeStableWall");
        var timer = Stopwatch.StartNew();
        GridFrame? previous = null;
        var stable = 0;
        byte[]? previousDetails = null;
        var previousLayout = -1;
        string error = "画面未稳定";
        try
        {
            while (timer.ElapsedMilliseconds < 3000)
            {
                token.ThrowIfCancellationRequested();
                ensureTarget();
                Bitmap image;
                using (metrics.Measure("capture")) image = capture.Capture();
                GridFrame? next = null;
                try
                {
                    using (metrics.Measure("gridAnalyze")) next = new GridFrame(image, GridVision.Analyze(image, options.Area));
                    byte[]? details;
                    var layout = 0;
                    using (metrics.Measure("detailSignature"))
                    {
                        var observation = sink?.ObserveDetails(image);
                        details = observation?.Signature ?? itemReader?.Signature(image);
                        layout = observation?.LayoutKey ?? 0;
                    }
                    if (previous is not null && previous.Grid.SelectedRow == next.Grid.SelectedRow
                        && previous.Grid.SelectedColumn == next.Grid.SelectedColumn
                        && TraversalPolicy.SameViewport(previous.Grid, next.Grid, 3)
                        && previousLayout == layout
                        && (details is null || (previousDetails is not null && DetailFrameSignature.SignatureDifference(previousDetails, details) <= 1.5))) stable++;
                    else stable = 1;
                    previousDetails = details;
                    previousLayout = layout;
                    previous?.Dispose(); previous = next; next = null;
                    if (stable >= options.StableObservations)
                    {
                        var accepted = previous; previous = null;
                        return accepted;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    try { image.Save(Path.Combine(directory, "last-detection-failure.png"), ImageFormat.Png); }
                    finally { image.Dispose(); next?.Dispose(); previous?.Dispose(); previous = null; }
                    stable = 0; previousDetails = null; error = ex.Message;
                }
                catch { image.Dispose(); next?.Dispose(); throw; }
                using (metrics.Measure("observationWait")) await Task.Delay(options.PollMs, token);
            }
            throw new InvalidOperationException("网格／焦点未在 3 秒内稳定：" + error);
        }
        finally { previous?.Dispose(); }
    }

    private sealed class TraversalLimitReachedException : Exception;

    private void Emit(string name, object data)
    {
        events?.WriteLine(JsonSerializer.Serialize(new { Time = DateTimeOffset.Now, Event = name, Data = data }));
        if (name != "MOVE") log($"AUTO_{name} {JsonSerializer.Serialize(data)}");
    }

    private void SaveImage(string name, GridFrame frame, bool full = false)
    {
        using var evidenceTime = metrics.Measure("evidenceImage");
        using var overlay = GridVision.Overlay(frame.Image, frame.Grid);
        if (full) overlay.Save(Path.Combine(directory, name + ".png"), ImageFormat.Png);
        else
        {
            if (++imageNumber > 600)
            {
                if (imageNumber == 601) Emit("SCREENSHOT_LIMIT", new { Limit = 600, Note = "后续只保留事件记录与最终截图" });
                return;
            }
            var width = Math.Min(1280, overlay.Width);
            using var small = new Bitmap(overlay, new Size(width, overlay.Height * width / overlay.Width));
            small.Save(Path.Combine(directory, name + ".jpg"), ImageFormat.Jpeg);
        }
    }
}
