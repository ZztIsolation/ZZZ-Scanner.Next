using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;
using ZZZScannerNext.Ocr;
using CvRect = OpenCvSharp.Rect;
using OcrBatchInput = ZZZScannerNext.Ocr.PaddleOcrRecognizer.OcrBatchInput;

namespace ZZZScannerNext.Scanning;

public sealed class ScanController
{
    private const int PanelChangeTolerance = 8;
    private const int PanelStableTolerance = 4;
    private const int ListMovementTolerance = 6;
    private const int ListStableTolerance = 4;
    private const int RowShiftMatchTolerance = 36;
    private const int RowShiftClearMargin = 8;
    private const int SignatureColumns = 8;
    private const int SignatureRows = 4;

    private readonly ScanProfileFile _profiles;
    private readonly WikiData _wikiData;

    public ScanController(ScanProfileFile profiles, WikiData wikiData)
    {
        _profiles = profiles;
        _wikiData = wikiData;
    }

    public async Task<ScanSessionResult> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress> progress,
        CancellationToken cancellationToken)
    {
        var profile = _profiles.Profiles.FirstOrDefault(p => p.Name == options.ProfileName)
            ?? _profiles.Profiles.First();
        var outputDir = AppPaths.CreateScanDirectory();
        using var scanLog = new ScanLog(Path.Combine(outputDir, "scan.log"));
        var results = new ConcurrentBag<DriveDiscExport>();
        var ocrWorkerCount = ResolveOcrWorkerCount(options);
        var ocrIntraOpThreads = ResolveOcrIntraOpThreads(options, ocrWorkerCount);
        var queueCapacity = Math.Max(ocrWorkerCount * Math.Max(1, options.OcrBatchSize) * 4, options.OcrQueueCapacity);
        if (options.StopAtNonLevel15)
        {
            queueCapacity = Math.Min(queueCapacity, Math.Max(ocrWorkerCount * Math.Max(1, options.OcrBatchSize) * 2, 16));
        }
        var queue = new BlockingCollection<DiscCapture>(boundedCapacity: queueCapacity);
        var ocrResults = new BlockingCollection<OcrWorkResult>(boundedCapacity: queueCapacity);
        var counters = new Counters();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var traversalMode = ResolveTraversalMode(options, profile);
        var duplicateGuard = new DuplicateGuard(Math.Max(1, profile.DuplicateRowThreshold));

        scanLog.Write($"Start scan. Profile={profile.Name}, Traversal={traversalMode}, Process={options.ProcessName}, MaxItems={options.MaxItems}, Rarities={string.Join(",", options.Rarities)}, BringToFront={options.BringToFront}, StopAtNonLevel15={options.StopAtNonLevel15}, HighSpeedOcr={options.HighSpeedOcr}, OcrWorkers={ocrWorkerCount}, OcrBatchSize={options.OcrBatchSize}, OcrQueueCapacity={queueCapacity}, OcrIntraOpThreads={ocrIntraOpThreads}");
        using var ocrDiagnostics = new OcrDiagnosticsWriter(Path.Combine(outputDir, "ocr_diagnostics.csv"));
        var resourceMonitor = ResourceMonitor.Start(
            Path.Combine(outputDir, "resource.csv"),
            options.ProcessName,
            () => new ResourceCounterSnapshot(
                Volatile.Read(ref counters.Visited),
                Volatile.Read(ref counters.Queued),
                Volatile.Read(ref counters.Completed),
                Volatile.Read(ref counters.Failed)),
            scanLog.Write);
        Task monitor = Task.CompletedTask;
        Exception? pendingException = null;
        Exception? ocrException = null;

        try
        {
            Report(progress, counters, $"加载窗口：{options.ProcessName}");
            var window = GameWindow.Find(options.ProcessName);
            if (options.BringToFront)
            {
                window.BringToFront();
            }

            Report(progress, counters, $"窗口客户区：{window.ClientScreenRect.Width} x {window.ClientScreenRect.Height}，DPI：{window.Dpi}，坐标倍率：{window.CoordinateScale:F2}");
            scanLog.Write($"Window client={window.ClientScreenRect}, dpi={window.Dpi}, scale={window.CoordinateScale:F2}");

            using var inventoryRecognizer = new PaddleOcrRecognizer(AppPaths.ModelFile, AppPaths.CharacterDictFile, ocrIntraOpThreads);
            var ocrWorkers = StartOcrWorkers(queue, ocrResults, outputDir, options, scanLog, ocrWorkerCount, ocrIntraOpThreads, counters, ocrDiagnostics);
            var resultConsumer = Task.Run(() => ConsumeOcrResults(ocrResults, results, counters, outputDir, progress, scanLog, duplicateGuard, options, linked));
            var captureCompleted = false;

            try
            {
                await PrepareBackpackAsync(window, profile, progress, counters, scanLog, linked.Token);
                monitor = Task.Run(() => MonitorBackpackAsync(window, profile, linked, progress, counters, scanLog), cancellationToken);
                var inventoryCount = TryReadInventoryCount(window, profile, inventoryRecognizer, scanLog);
                await ProduceCapturesAsync(window, profile, queue, options, counters, progress, scanLog, inventoryCount, traversalMode, linked.Token);
                captureCompleted = true;
                Report(progress, counters, "截图采集完成，后台 OCR 正在收尾。");
            }
            catch (OperationCanceledException)
            {
                scanLog.Write("Scan canceled.");
                Report(progress, counters, "扫描已停止。");
            }
            catch (Exception ex)
            {
                pendingException = ex;
                scanLog.Write("Scan failed:");
                scanLog.Write(ex.ToString());
                Report(progress, counters, $"扫描失败：{ex.Message}");
            }
            finally
            {
                queue.CompleteAdding();
                if (!captureCompleted)
                {
                    Interlocked.CompareExchange(ref counters.StopAfterIndex, Math.Max(1, Volatile.Read(ref counters.Queued)), 0);
                    var discarded = DisposeQueuedCaptures(queue);
                    if (discarded > 0)
                    {
                        scanLog.Write($"Discarded {discarded} queued captures after early stop.");
                    }
                }

                linked.Cancel();
            }

            try
            {
                await Task.WhenAll(ocrWorkers);
            }
            catch (Exception ex)
            {
                ocrException = ex;
                scanLog.Write("OCR worker failed:");
                scanLog.Write(ex.ToString());
            }
            finally
            {
                ocrResults.CompleteAdding();
            }

            await resultConsumer;
            await SafeAwait(monitor);
        }
        finally
        {
            await resourceMonitor.StopAsync();
        }

        if (pendingException is not null)
        {
            ExceptionDispatchInfo.Capture(pendingException).Throw();
        }

        if (ocrException is not null)
        {
            ExceptionDispatchInfo.Capture(ocrException).Throw();
        }

        var ordered = results.OrderBy(x => x.Index).ToList();
        var exportFile = Path.Combine(outputDir, "export.json");
        await File.WriteAllTextAsync(exportFile, JsonSerializer.Serialize(ordered, JsonDefaults.Write), cancellationToken);

        Report(progress, counters, $"完成：输出 {ordered.Count} 条，失败 {counters.Failed} 条。");
        return new ScanSessionResult
        {
            OutputDirectory = outputDir,
            ExportFile = exportFile,
            Items = ordered,
            Visited = counters.Visited,
            Failed = counters.Failed
        };
    }

    private static async Task PrepareBackpackAsync(
        GameWindow window,
        ScanProfile profile,
        IProgress<ScanProgress> progress,
        Counters counters,
        ScanLog scanLog,
        CancellationToken token)
    {
        Report(progress, counters, "等待背包驱动盘界面。");
        var dismantlePoint = window.ToScreenPoint(profile.Point("dismantleButton"));
        var dismantleColor = profile.Color("dismantleButton");
        scanLog.Write($"Preflight dismantle point={dismantlePoint}, expected={ColorText(dismantleColor)}, current={ColorText(window.GetPixel(dismantlePoint))}");

        try
        {
            await WaitUntilAsync(
                () => window.GetPixel(dismantlePoint).IsCloseTo(dismantleColor, profile.ColorTolerance),
                TimeSpan.FromSeconds(profile.WaitForBackpackSeconds),
                TimeSpan.FromMilliseconds(200),
                2,
                token);
        }
        catch (TimeoutException ex)
        {
            var current = window.GetPixel(dismantlePoint);
            scanLog.Write($"Preflight failed. Expected dismantle color={ColorText(dismantleColor)}, actual={ColorText(current)}");
            throw new InvalidOperationException("未检测到驱动盘仓库界面。请先打开背包/仓库的驱动盘页，再开始扫描；本次没有继续点击或滚动。", ex);
        }

        window.LeftClick(window.ToScreenPoint(profile.Point("driveDiscTab")));
        await Task.Delay(profile.ClickDelayMs, token);

        await ResetListToTopAsync(window, profile, progress, counters, scanLog, token);
        Report(progress, counters, "已定位到驱动盘列表顶部。");
    }

    private static async Task ResetListToTopAsync(
        GameWindow window,
        ScanProfile profile,
        IProgress<ScanProgress> progress,
        Counters counters,
        ScanLog scanLog,
        CancellationToken token)
    {
        Report(progress, counters, "正在将驱动盘列表拉到最上方。");
        var scrollTop = window.ToScreenPoint(profile.Point("scrollBarTop"));
        var scrollBarColor = profile.Color("scrollBar");
        window.MoveCursor(window.ToScreenPoint(profile.Point("listWheelArea")));
        var resetDelay = Math.Max(80, profile.ResetToTopWheelDelayMs);
        var reachedTop = false;
        for (var i = 0; i < profile.ResetToTopWheelTicks; i++)
        {
            token.ThrowIfCancellationRequested();
            scanLog.WriteEvent("RESET_WHEEL", $"tick={i + 1}/{profile.ResetToTopWheelTicks}, delta=120, cursor={window.ToScreenPoint(profile.Point("listWheelArea"))}");
            window.MouseWheel(120);
            if (resetDelay > 0)
            {
                await Task.Delay(resetDelay, token);
            }

            if (i >= 8 && IsScrollAtTop(window, profile, scrollTop, scrollBarColor))
            {
                scanLog.Write($"Reset reached top after {i + 1} wheel ticks.");
                reachedTop = true;
                break;
            }
        }

        if (!reachedTop)
        {
            scanLog.Write($"Reset top probe did not confirm after {profile.ResetToTopWheelTicks} ticks; continuing with explicit top click.");
        }

        scanLog.WriteEvent("RESET_TOP_CLICK", $"point={scrollTop}");
        window.LeftClick(scrollTop, durationMs: 30);
        await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
    }

    private static async Task ProduceCapturesAsync(
        GameWindow window,
        ScanProfile profile,
        BlockingCollection<DiscCapture> queue,
        ScanOptions options,
        Counters counters,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        int? inventoryCount,
        ScanTraversalMode traversalMode,
        CancellationToken token)
    {
        if (traversalMode == ScanTraversalMode.SafeBandViewport && inventoryCount is > 0)
        {
            await ProduceCapturesSafeBandViewportAsync(window, profile, queue, options, counters, progress, scanLog, inventoryCount.Value, token);
            return;
        }

        if (traversalMode == ScanTraversalMode.SafeBandViewport)
        {
            scanLog.Write("SafeBandViewport requires an inventory count. Inventory count OCR failed; refusing to fall back to LegacyThirdRow.");
            Report(progress, counters, "仓库数量 OCR 失败，安全带扫描无法计算总行数。");
            throw new InvalidOperationException("仓库数量 OCR 失败，安全带扫描无法计算总行数。请确认数量区域可见后重试，或手动选择旧版第3行兼容模式。");
        }

        if (traversalMode == ScanTraversalMode.CalibratedPage && inventoryCount is > 0)
        {
            await ProduceCapturesCalibratedPageAsync(window, profile, queue, options, counters, progress, scanLog, inventoryCount.Value, token);
            return;
        }

        if (traversalMode == ScanTraversalMode.CalibratedPage)
        {
            scanLog.Write("CalibratedPage requires an inventory count. Inventory count OCR failed; refusing to fall back to LegacyThirdRow.");
            Report(progress, counters, "仓库数量 OCR 失败，校准翻页无法计算总行数。");
            throw new InvalidOperationException("仓库数量 OCR 失败，校准翻页无法计算总行数。请确认数量区域可见后重试，或手动选择旧版第3行兼容模式。");
        }

        await ProduceCapturesLegacyThirdRowAsync(window, profile, queue, options, counters, progress, scanLog, inventoryCount, token);
    }

    private static async Task ProduceCapturesSafeBandViewportAsync(
        GameWindow window,
        ScanProfile profile,
        BlockingCollection<DiscCapture> queue,
        ScanOptions options,
        Counters counters,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        int inventoryCount,
        CancellationToken token)
    {
        var offset = profile.Point("driveDiscOffset");
        var step = profile.Point("driveDiscStep");
        var columns = Math.Max(1, profile.VisibleColumns);
        const int visibleRows = 4;
        var totalRows = Math.Max(1, (int)Math.Ceiling(inventoryCount / (double)columns));
        var lastRowColumns = inventoryCount % columns == 0 ? columns : inventoryCount % columns;
        var maxVisibleTop = Math.Max(1, totalRows - visibleRows + 1);
        var panelRect = window.ToScreenRectangle(profile.Rectangle("detailPanel"));
        var panelChangeProbeRect = ProfileRectangleOrFallback(window, profile, "panelChangeProbeRect", panelRect);
        var listGridRect = ProfileRectangleOrFallback(window, profile, "listGridRect", BuildListGridFallback(window, profile, visibleRows, columns));
        var rowSignatureRects = BuildVisualRowSignatureRects(listGridRect, visibleRows);
        var rois = BuildRois(window, profile, panelRect);
        var statOffset = window.ToScreenPoint(profile.Point("statBackgroundOffset"), clientToScreen: false);
        var statRowBackground = profile.Color("statRowBackground");

        scanLog.Write($"Traversal: safe-band viewport. inventoryCount={inventoryCount}, totalRows={totalRows}, visibleRows={visibleRows}, columns={columns}, lastRowColumns={lastRowColumns}, maxVisibleTop={maxVisibleTop}, listGrid={listGridRect}, panelProbe={panelChangeProbeRect}, scrollTickDelta={profile.ScrollTickDelta}.");
        await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);

        var visibleTopLogicalRow = 1;
        for (var logicalRow = 1; logicalRow <= totalRows; logicalRow++)
        {
            token.ThrowIfCancellationRequested();
            visibleTopLogicalRow = await MoveLogicalRowIntoSafeBandAsync(
                window,
                profile,
                listGridRect,
                rowSignatureRects,
                visibleTopLogicalRow,
                maxVisibleTop,
                totalRows,
                logicalRow,
                scanLog,
                token);

            var visualRow = logicalRow - visibleTopLogicalRow + 1;
            var viewportState = ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop);
            var maxColumns = logicalRow == totalRows ? lastRowColumns : columns;
            scanLog.WriteEvent("VIEWPORT_STATE", $"targetLogicalRow={logicalRow}, visualRow={visualRow}, visibleTopLogicalRow={visibleTopLogicalRow}, maxVisibleTop={maxVisibleTop}, state={viewportState}, columns={maxColumns}, visited={counters.Visited}, queued={counters.Queued}");
            Report(progress, counters, $"安全带扫描：逻辑行 {logicalRow}/{totalRows}，视觉第 {visualRow} 行，{viewportState}。");

            var rowResult = await ScanVisualRowAsync(
                window,
                profile,
                queue,
                options,
                counters,
                progress,
                scanLog,
                offset,
                step,
                panelRect,
                rois,
                statOffset,
                statRowBackground,
                logicalRow,
                visualRow,
                isBottom: logicalRow == totalRows,
                maxColumns,
                logicalRow,
                treatBlankAsEnd: false,
                panelChangeProbeRect,
                token,
                enforceSafeBand: true,
                visibleTopLogicalRow: visibleTopLogicalRow,
                maxVisibleTop: maxVisibleTop
                );
            if (rowResult == RowScanResult.Stop)
            {
                return;
            }
        }

        scanLog.Write($"End: safe-band viewport completed. visited={counters.Visited}, expectedInventory={inventoryCount}, queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}.");
        Report(progress, counters, $"已到达安全带扫描末尾：访问 {counters.Visited}/{inventoryCount}。");
    }

    private static async Task ProduceCapturesCalibratedPageAsync(
        GameWindow window,
        ScanProfile profile,
        BlockingCollection<DiscCapture> queue,
        ScanOptions options,
        Counters counters,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        int inventoryCount,
        CancellationToken token)
    {
        var offset = profile.Point("driveDiscOffset");
        var step = profile.Point("driveDiscStep");
        var columns = Math.Max(1, profile.VisibleColumns);
        var visibleRows = Math.Max(1, profile.VisibleRows);
        var totalRows = Math.Max(1, (int)Math.Ceiling(inventoryCount / (double)columns));
        var lastRowColumns = inventoryCount % columns == 0 ? columns : inventoryCount % columns;
        var panelRect = window.ToScreenRectangle(profile.Rectangle("detailPanel"));
        var panelChangeProbeRect = ProfileRectangleOrFallback(window, profile, "panelChangeProbeRect", panelRect);
        var listGridRect = ProfileRectangleOrFallback(window, profile, "listGridRect", BuildListGridFallback(window, profile, visibleRows, columns));
        var rowAlignProbeRect = ProfileRectangleOrFallback(window, profile, "rowAlignProbeRect", listGridRect);
        var rois = BuildRois(window, profile, panelRect);
        var statOffset = window.ToScreenPoint(profile.Point("statBackgroundOffset"), clientToScreen: false);
        var statRowBackground = profile.Color("statRowBackground");
        var calibration = new ScrollCalibrationState(CaptureSignature(window, rowAlignProbeRect));

        scanLog.Write($"Traversal: calibrated-page. inventoryCount={inventoryCount}, totalRows={totalRows}, visibleRows={visibleRows}, columns={columns}, lastRowColumns={lastRowColumns}, listGrid={listGridRect}, rowProbe={rowAlignProbeRect}, panelProbe={panelChangeProbeRect}, scrollTickDelta={profile.ScrollTickDelta}, maxTicksPerRow={profile.ScrollMaxTicksPerRow}, calibrationRows={profile.CalibrationRows}.");
        await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);

        var scannedRows = 0;
        var startVisualRow = 1;
        var page = 1;
        while (scannedRows < totalRows)
        {
            token.ThrowIfCancellationRequested();
            var rowsThisPage = Math.Min(visibleRows - startVisualRow + 1, totalRows - scannedRows);
            var firstLogicalRow = scannedRows + 1;
            var lastLogicalRow = scannedRows + rowsThisPage;
            scanLog.Write($"Page {page}: scan visual rows {startVisualRow}-{startVisualRow + rowsThisPage - 1}, logical rows {firstLogicalRow}-{lastLogicalRow}, visited={counters.Visited}, queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}.");
            Report(progress, counters, $"扫描第 {page} 页：逻辑行 {firstLogicalRow}-{lastLogicalRow}。");

            await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);

            for (var i = 0; i < rowsThisPage; i++)
            {
                var logicalRow = scannedRows + 1;
                var visualRow = startVisualRow + i;
                var maxColumns = logicalRow == totalRows ? lastRowColumns : columns;
                var rowResult = await ScanVisualRowAsync(
                    window,
                    profile,
                    queue,
                    options,
                    counters,
                    progress,
                    scanLog,
                    offset,
                    step,
                    panelRect,
                    rois,
                    statOffset,
                    statRowBackground,
                    page,
                    visualRow,
                    isBottom: logicalRow == totalRows,
                    maxColumns,
                    logicalRow,
                    treatBlankAsEnd: false,
                    panelChangeProbeRect,
                    token
                    );
                if (rowResult == RowScanResult.Stop)
                {
                    return;
                }

                scannedRows++;
            }

            if (scannedRows >= totalRows)
            {
                break;
            }

            var remainingRows = totalRows - scannedRows;
            var scrollRows = Math.Min(visibleRows, remainingRows);
            startVisualRow = visibleRows - scrollRows + 1;
            scanLog.Write($"Scroll page {page}: rows={scrollRows}, nextStartVisualRow={startVisualRow}, remainingRows={remainingRows}.");
            Report(progress, counters, $"翻页 {scrollRows} 行，准备扫描下一页。");

            var scrollResult = await ScrollRowsWithRetryAsync(window, profile, listGridRect, rowAlignProbeRect, calibration, scrollRows, scanLog, token);
            if (!scrollResult.Success)
            {
                throw new InvalidOperationException($"滚动失败：{scrollResult.Message}");
            }

            page++;
        }

        scanLog.Write($"End: calibrated-page completed. visited={counters.Visited}, expectedInventory={inventoryCount}, queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}.");
        Report(progress, counters, $"已到达计算末尾：访问 {counters.Visited}/{inventoryCount}。");
    }

    private static async Task ProduceCapturesLegacyThirdRowAsync(
        GameWindow window,
        ScanProfile profile,
        BlockingCollection<DiscCapture> queue,
        ScanOptions options,
        Counters counters,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        int? inventoryCount,
        CancellationToken token)
    {
        var offset = profile.Point("driveDiscOffset");
        var step = profile.Point("driveDiscStep");
        var scrollBottom = window.ToScreenPoint(profile.Point("scrollBarBottom"));
        var scrollBarColor = profile.Color("scrollBar");
        var panelRect = window.ToScreenRectangle(profile.Rectangle("detailPanel"));
        var panelChangeProbeRect = ProfileRectangleOrFallback(window, profile, "panelChangeProbeRect", panelRect);
        var rois = BuildRois(window, profile, panelRect);
        var statOffset = window.ToScreenPoint(profile.Point("statBackgroundOffset"), clientToScreen: false);
        var statRowBackground = profile.Color("statRowBackground");
        var totalRows = inventoryCount is > 0 ? (int)Math.Ceiling(inventoryCount.Value / 9d) : (int?)null;
        var maxScrollRows = Math.Max(0, (totalRows ?? 69) - 4);
        scanLog.Write($"Traversal: legacy third-row mode. inventoryCount={inventoryCount?.ToString() ?? "unknown"}, totalRows={totalRows?.ToString() ?? "unknown"}, maxScrollRows={maxScrollRows}, scrollBottom={scrollBottom}, wheelDelta={profile.ScrollWheelDelta}.");

        var pass = 0;
        for (var row = 1; row <= 4; row++)
        {
            token.ThrowIfCancellationRequested();
            pass++;
            var bottomBefore = IsScrollAtBottom(window, profile, scrollBottom, scrollBarColor);
            scanLog.Write($"Pass {pass}: scan visual row {row}, bottomBefore={bottomBefore}, queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}");
            Report(progress, counters, $"扫描第 {pass} 轮：可视第 {row} 行{(bottomBefore ? "，已到底" : "")}。");

            var rowResult = await ScanVisualRowAsync(
                window,
                profile,
                queue,
                options,
                counters,
                progress,
                scanLog,
                offset,
                step,
                panelRect,
                rois,
                statOffset,
                statRowBackground,
                pass,
                row,
                bottomBefore,
                Math.Max(1, profile.VisibleColumns),
                logicalRow: null,
                treatBlankAsEnd: true,
                panelChangeProbeRect,
                token
                );
            if (rowResult == RowScanResult.Stop)
            {
                return;
            }

            if (row == 3)
            {
                var bottomAfterRow = IsScrollAtBottom(window, profile, scrollBottom, scrollBarColor);
                if (!bottomAfterRow)
                {
                    scanLog.Write($"Scroll: legacy third-row wheel after pass {pass}, delta={profile.ScrollWheelDelta}.");
                    scanLog.WriteEvent("LEGACY_WHEEL", $"afterPass={pass}, visualRow={row}, delta={profile.ScrollWheelDelta}");
                    window.MouseWheel(profile.ScrollWheelDelta);
                    await Task.Delay(profile.WheelDelayMs, token);
                    row--;
                }
                else
                {
                    scanLog.Write("Bottom reached after visual row 3; scanning final visual row 4 next.");
                }
            }
        }

        scanLog.Write("End: legacy visual row loop completed.");
        Report(progress, counters, "滚动条已到底，扫描结束。");
    }

    private static async Task<RowScanResult> ScanVisualRowAsync(
        GameWindow window,
        ScanProfile profile,
        BlockingCollection<DiscCapture> queue,
        ScanOptions options,
        Counters counters,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        PointF offset,
        PointF step,
        Rectangle panelRect,
        CvRect[] rois,
        System.Drawing.Point statOffset,
        Color statRowBackground,
        int pass,
        int row,
        bool isBottom,
        int maxColumns,
        int? logicalRow,
        bool treatBlankAsEnd,
        Rectangle panelChangeProbeRect,
        CancellationToken token,
        bool enforceSafeBand = false,
        int visibleTopLogicalRow = 1,
        int maxVisibleTop = 1)
    {
        var currentY = offset.Y + step.Y * row;
        ImageSignature[]? previousPanelSignatures = null;
        for (var col = 1; col <= maxColumns; col++)
        {
            token.ThrowIfCancellationRequested();
            if (options.MaxItems > 0 && counters.Queued >= options.MaxItems)
            {
                scanLog.Write($"End: max items reached. MaxItems={options.MaxItems}, queued={counters.Queued}.");
                Report(progress, counters, $"已达到读取上限：{options.MaxItems}");
                return RowScanResult.Stop;
            }

            if (enforceSafeBand && !IsSafeBandClick(row, visibleTopLogicalRow, maxVisibleTop, logicalRow ?? -1))
            {
                scanLog.WriteEvent("EDGE_CLICK_BLOCKED", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopLogicalRow}, maxVisibleTop={maxVisibleTop}, state={ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop)}");
                throw new InvalidOperationException($"安全带保护阻止点击：逻辑行 {logicalRow} 当前位于视觉第 {row} 行。");
            }

            var sw = Stopwatch.StartNew();
            var current = new PointF(offset.X + step.X * col, currentY);
            var clickPoint = window.ToScreenPoint(current);
            scanLog.WriteEvent("CELL_MOVE", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={(enforceSafeBand ? visibleTopLogicalRow.ToString() : "unknown")}, state={(enforceSafeBand ? ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop) : "unknown")}, point={clickPoint}, normalized=({current.X:F5},{current.Y:F5}), queued={counters.Queued}, visited={counters.Visited}");
            window.MoveCursor(clickPoint);

            var rarityProbe = DetectRarityAround(window, profile, clickPoint);
            if (rarityProbe.Rarity is null)
            {
                await Task.Delay(25, token);
                rarityProbe = DetectRarityAround(window, profile, clickPoint);
            }

            var rarity = rarityProbe.Rarity;
            if (rarity is null || options.ShowDebugImages || counters.Visited % 50 == 0)
            {
                scanLog.Write($"Probe pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, point={clickPoint}, rarity={rarity ?? "null"}, best={ColorText(rarityProbe.BestColor)}, bestMatch={rarityProbe.BestCandidate}, delta={rarityProbe.BestDelta}, bottom={isBottom}");
            }
            if (rarity is null)
            {
                if (treatBlankAsEnd)
                {
                    scanLog.Write($"End: blank cell at pass={pass}, visualRow={row}, col={col}. This is treated as list end after retry.");
                    Report(progress, counters, $"第 {pass} 轮第 {row} 行第 {col} 列未检测到驱动盘卡片，扫描结束。详情见本次输出目录的 scan.log。");
                    return RowScanResult.Stop;
                }

                throw new InvalidOperationException($"预期存在驱动盘，但第 {logicalRow} 行第 {col} 列未检测到品质颜色。可能发生漏扫或列表未稳定。");
            }

            counters.Visited++;
            if (!options.Rarities.Contains(rarity))
            {
                Report(progress, counters, $"跳过 {rarity} 级驱动盘。");
                continue;
            }

            scanLog.WriteEvent("CELL_CLICK", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={(enforceSafeBand ? visibleTopLogicalRow.ToString() : "unknown")}, state={(enforceSafeBand ? ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop) : "unknown")}, point={clickPoint}");
            window.LeftClickCurrent();

            using var panelCapture = await CaptureStablePanelAsync(window, profile, panelRect, rois, statOffset, statRowBackground, panelChangeProbeRect, previousPanelSignatures, scanLog, token);
            var panelImage = panelCapture.TakeImage();
            var captureRois = rois.Take(panelCapture.VisibleRoiCount).ToArray();
            var debugImage = options.ShowDebugImages ? (Bitmap)panelImage.Clone() : null;
            previousPanelSignatures = panelCapture.ProbeSignatures;
            var itemIndex = Interlocked.Increment(ref counters.Queued);
            var enqueueWait = Stopwatch.StartNew();
            var enqueued = false;
            try
            {
                queue.Add(new DiscCapture(itemIndex, rarity, panelImage, captureRois), token);
                enqueued = true;
            }
            finally
            {
                enqueueWait.Stop();
                if (!enqueued)
                {
                    panelImage.Dispose();
                }
            }

            if (enqueueWait.ElapsedMilliseconds >= 120)
            {
                scanLog.Write($"OCR queue backpressure: waited {enqueueWait.ElapsedMilliseconds}ms to enqueue #{itemIndex}, queueCount={queue.Count}.");
            }

            scanLog.WriteEvent("CELL_TIMING", $"index={itemIndex}, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, panelWaitMs={panelCapture.WaitMilliseconds:F1}, enqueueWaitMs={enqueueWait.Elapsed.TotalMilliseconds:F1}, fallback={panelCapture.UsedFallback}, visibleRois={panelCapture.VisibleRoiCount}/{rois.Length}, totalMs={sw.ElapsedMilliseconds}");
            var queueMessage = options.ShowDebugImages || itemIndex % 25 == 0
                ? $"入队 #{itemIndex}：{rarity}，{captureRois.Length} 个文本区域，用时 {sw.ElapsedMilliseconds}ms。"
                : "";
            Report(progress, counters, queueMessage, debugImage: debugImage);
        }

        return RowScanResult.Completed;
    }

    private static bool IsScrollAtBottom(GameWindow window, ScanProfile profile, System.Drawing.Point scrollBottom, Color scrollBarColor)
    {
        return window.GetPixel(scrollBottom).IsCloseTo(scrollBarColor, profile.ColorTolerance);
    }

    private static bool IsScrollAtTop(GameWindow window, ScanProfile profile, System.Drawing.Point scrollTop, Color scrollBarColor)
    {
        return window.GetPixel(scrollTop).IsCloseTo(scrollBarColor, profile.ColorTolerance);
    }

    private static ScanTraversalMode ResolveTraversalMode(ScanOptions options, ScanProfile profile)
    {
        if (options.TraversalMode != ScanTraversalMode.FromProfile)
        {
            return options.TraversalMode;
        }

        return Enum.TryParse<ScanTraversalMode>(profile.TraversalMode, ignoreCase: true, out var parsed) && parsed != ScanTraversalMode.FromProfile
            ? parsed
            : ScanTraversalMode.SafeBandViewport;
    }

    private static int ResolveOcrWorkerCount(ScanOptions options)
    {
        if (options.OcrWorkerCount > 0)
        {
            return Math.Clamp(options.OcrWorkerCount, 1, 4);
        }

        return options.HighSpeedOcr ? Math.Min(3, Math.Max(2, Environment.ProcessorCount / 4)) : 1;
    }

    private static int ResolveOcrIntraOpThreads(ScanOptions options, int workerCount)
    {
        var requested = Math.Clamp(options.OcrIntraOpThreads, 1, 8);
        if (!options.HighSpeedOcr || workerCount <= 1)
        {
            return requested;
        }

        return Math.Max(1, Math.Min(requested, Math.Max(2, Environment.ProcessorCount / Math.Max(1, workerCount * 2))));
    }

    private static Rectangle ProfileRectangleOrFallback(GameWindow window, ScanProfile profile, string key, Rectangle fallback)
    {
        return profile.HasRectangle(key) ? window.ToScreenRectangle(profile.Rectangle(key)) : fallback;
    }

    private static Rectangle BuildListGridFallback(GameWindow window, ScanProfile profile, int visibleRows, int columns)
    {
        var offset = profile.Point("driveDiscOffset");
        var step = profile.Point("driveDiscStep");
        var left = offset.X + step.X - step.X * 0.9f;
        var top = offset.Y + step.Y - step.Y * 0.72f;
        var width = step.X * columns + step.X * 0.55f;
        var height = step.Y * visibleRows;
        return window.ToScreenRectangle(new RectangleF(left, top, width, height));
    }

    private static async Task<int> MoveLogicalRowIntoSafeBandAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        int totalRows,
        int logicalRow,
        ScanLog scanLog,
        CancellationToken token)
    {
        var desiredTop = ChooseSafeVisibleTop(logicalRow, totalRows, maxVisibleTop);
        while (visibleTopLogicalRow < desiredTop)
        {
            var result = await ScrollSafeBandOneRowWithRetryAsync(
                window,
                profile,
                listGridRect,
                rowSignatureRects,
                visibleTopLogicalRow,
                maxVisibleTop,
                scanLog,
                token);
            if (!result.Success)
            {
                throw new InvalidOperationException($"安全带逐行滚动失败：{result.Message}");
            }

            var rowsAdvanced = Math.Max(1, result.RowsAdvanced);
            var nextVisibleTop = Math.Min(maxVisibleTop, visibleTopLogicalRow + rowsAdvanced);
            if (rowsAdvanced != 1)
            {
                scanLog.Write($"Safe-band viewport resynced after wheel overshoot: previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, nextTop={nextVisibleTop}, desiredTop={desiredTop}.");
            }

            visibleTopLogicalRow = nextVisibleTop;
        }

        var visualRow = logicalRow - visibleTopLogicalRow + 1;
        if (!IsSafeBandClick(visualRow, visibleTopLogicalRow, maxVisibleTop, logicalRow))
        {
            scanLog.WriteEvent("EDGE_CLICK_BLOCKED", $"logicalRow={logicalRow}, visualRow={visualRow}, visibleTopLogicalRow={visibleTopLogicalRow}, maxVisibleTop={maxVisibleTop}, state={ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop)}");
            throw new InvalidOperationException($"安全带保护阻止点击：逻辑行 {logicalRow} 当前位于视觉第 {visualRow} 行。");
        }

        return visibleTopLogicalRow;
    }

    private static int ChooseSafeVisibleTop(int logicalRow, int totalRows, int maxVisibleTop)
    {
        if (logicalRow <= 3)
        {
            return 1;
        }

        if (logicalRow == totalRows)
        {
            return maxVisibleTop;
        }

        return Math.Clamp(logicalRow - 2, 1, maxVisibleTop);
    }

    private static bool IsSafeBandClick(int visualRow, int visibleTopLogicalRow, int maxVisibleTop, int logicalRow)
    {
        if (visualRow is 2 or 3)
        {
            return true;
        }

        var atTop = visibleTopLogicalRow == 1;
        var atBottom = visibleTopLogicalRow == maxVisibleTop;
        return (visualRow == 1 && atTop && logicalRow == 1) || (visualRow == 4 && atBottom);
    }

    private static string ViewportStateLabel(int visibleTopLogicalRow, int maxVisibleTop)
    {
        if (visibleTopLogicalRow == 1 && visibleTopLogicalRow == maxVisibleTop)
        {
            return "TopAndBottom";
        }

        if (visibleTopLogicalRow == 1)
        {
            return "Top";
        }

        return visibleTopLogicalRow == maxVisibleTop ? "Bottom" : "Middle";
    }

    private static async Task<ScrollRowsResult> ScrollSafeBandOneRowWithRetryAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        ScanLog scanLog,
        CancellationToken token)
    {
        var delay = Math.Max(120, profile.ScrollTickDelayMs * 2);
        ScrollRowsResult last = default;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            last = await ScrollSafeBandOneRowAsync(window, profile, listGridRect, rowSignatureRects, visibleTopLogicalRow, maxVisibleTop, scanLog, token);
            if (last.Success)
            {
                return last;
            }

            if (attempt < 3)
            {
                scanLog.Write($"Safe-band scroll retry {attempt + 1}/3: visibleTopLogicalRow={visibleTopLogicalRow}, failure={last.Message}");
                await Task.Delay(delay, token);
                delay = Math.Min(360, delay + 120);
            }
        }

        scanLog.Write($"Safe-band scroll failed after retries: visibleTopLogicalRow={visibleTopLogicalRow}, failure={last.Message}");
        return last;
    }

    private static async Task<ScrollRowsResult> ScrollSafeBandOneRowAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        ScanLog scanLog,
        CancellationToken token)
    {
        if (visibleTopLogicalRow >= maxVisibleTop)
        {
            return ScrollRowsResult.Fail($"already at bottom visibleTopLogicalRow={visibleTopLogicalRow}");
        }

        window.MoveCursor(window.ToScreenPoint(profile.Point("listWheelArea")));
        var beforeGrid = CaptureSignature(window, listGridRect);
        var beforeRows = CaptureRowSignatures(window, rowSignatureRects);
        scanLog.WriteEvent("ROW_SCROLL_START", $"fromTop={visibleTopLogicalRow}, toTop={visibleTopLogicalRow + 1}, maxVisibleTop={maxVisibleTop}, delta={profile.ScrollTickDelta}");
        scanLog.WriteEvent("ROW_SCROLL_TICK", $"fromTop={visibleTopLogicalRow}, toTop={visibleTopLogicalRow + 1}, delta={profile.ScrollTickDelta}");
        window.MouseWheel(profile.ScrollTickDelta);
        await Task.Delay(Math.Max(80, profile.ScrollTickDelayMs), token);
        await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);

        var afterGrid = CaptureSignature(window, listGridRect);
        var afterRows = CaptureRowSignatures(window, rowSignatureRects);
        var movedDistance = SignatureDistance(beforeGrid, afterGrid);
        var verification = VerifyOneRowDown(beforeRows, afterRows);
        var estimatedRows = EstimateRowsAdvanced(verification, movedDistance);
        var twoRowsClearlyBetter = estimatedRows == 2;
        var oneRowMatched = verification.OneRowScore <= RowShiftMatchTolerance
            || verification.OneRowScore <= verification.NoMoveScore + RowShiftClearMargin
            || verification.OneRowScore <= verification.TwoRowScore + RowShiftClearMargin;
        var success = estimatedRows > 0;
        scanLog.WriteEvent("ROW_SCROLL_VERIFY", $"fromTop={visibleTopLogicalRow}, toTop={visibleTopLogicalRow + 1}, movedDistance={movedDistance}, oneRowScore={verification.OneRowScore}, noMoveScore={verification.NoMoveScore}, twoRowScore={verification.TwoRowScore}, oneRowMatched={oneRowMatched}, twoRowsClearlyBetter={twoRowsClearlyBetter}, estimatedRows={estimatedRows}, success={success}");

        if (success)
        {
            var eventName = estimatedRows == 1 ? "ROW_SCROLL_DONE" : "ROW_SCROLL_OVERSHOT";
            scanLog.WriteEvent(eventName, $"fromTop={visibleTopLogicalRow}, estimatedToTop={Math.Min(maxVisibleTop, visibleTopLogicalRow + estimatedRows)}, requestedToTop={visibleTopLogicalRow + 1}, rowsAdvanced={estimatedRows}, movedDistance={movedDistance}, oneRowScore={verification.OneRowScore}, twoRowScore={verification.TwoRowScore}");
            return ScrollRowsResult.Ok($"safe-band advanced {estimatedRows} row(s) from {visibleTopLogicalRow}", estimatedRows);
        }

        scanLog.WriteEvent("ROW_SCROLL_FAIL", $"fromTop={visibleTopLogicalRow}, expectedToTop={visibleTopLogicalRow + 1}, movedDistance={movedDistance}, oneRowScore={verification.OneRowScore}, noMoveScore={verification.NoMoveScore}, twoRowScore={verification.TwoRowScore}");
        return ScrollRowsResult.Fail($"one-row verification failed, movedDistance={movedDistance}, oneRowScore={verification.OneRowScore}, twoRowScore={verification.TwoRowScore}");
    }

    private static Rectangle[] BuildVisualRowSignatureRects(Rectangle listGridRect, int visibleRows)
    {
        var rects = new Rectangle[Math.Max(1, visibleRows)];
        var rowHeight = listGridRect.Height / (double)rects.Length;
        for (var i = 0; i < rects.Length; i++)
        {
            var top = listGridRect.Top + (int)Math.Round(rowHeight * i);
            var bottom = listGridRect.Top + (int)Math.Round(rowHeight * (i + 1));
            rects[i] = Rectangle.FromLTRB(
                listGridRect.Left + 8,
                top + 6,
                listGridRect.Right - 8,
                Math.Max(top + 7, bottom - 6));
        }

        return rects;
    }

    private static ImageSignature[] CaptureRowSignatures(GameWindow window, IReadOnlyList<Rectangle> rowSignatureRects)
    {
        var signatures = new ImageSignature[rowSignatureRects.Count];
        for (var i = 0; i < signatures.Length; i++)
        {
            signatures[i] = CaptureSignature(window, rowSignatureRects[i]);
        }

        return signatures;
    }

    private static RowScrollVerification VerifyOneRowDown(IReadOnlyList<ImageSignature> beforeRows, IReadOnlyList<ImageSignature> afterRows)
    {
        var noMove = AverageSignatureDistance(beforeRows, afterRows, [(0, 0), (1, 1), (2, 2), (3, 3)]);
        var oneRow = AverageSignatureDistance(beforeRows, afterRows, [(1, 0), (2, 1), (3, 2)]);
        var twoRows = AverageSignatureDistance(beforeRows, afterRows, [(2, 0), (3, 1)]);
        return new RowScrollVerification(oneRow, noMove, twoRows);
    }

    private static int EstimateRowsAdvanced(RowScrollVerification verification, int movedDistance)
    {
        if (movedDistance <= ListMovementTolerance)
        {
            return 0;
        }

        var twoRowsClearlyBest = verification.TwoRowScore + RowShiftClearMargin < verification.OneRowScore
            && verification.TwoRowScore + RowShiftClearMargin < verification.NoMoveScore;
        if (twoRowsClearlyBest)
        {
            return 2;
        }

        var oneRowMatched = verification.OneRowScore <= RowShiftMatchTolerance
            || verification.OneRowScore <= verification.NoMoveScore + RowShiftClearMargin
            || verification.OneRowScore <= verification.TwoRowScore + RowShiftClearMargin;
        if (oneRowMatched)
        {
            return 1;
        }

        return 0;
    }

    private static int AverageSignatureDistance(IReadOnlyList<ImageSignature> beforeRows, IReadOnlyList<ImageSignature> afterRows, IReadOnlyList<(int Before, int After)> pairs)
    {
        var sum = 0;
        var count = 0;
        foreach (var pair in pairs)
        {
            if (pair.Before < beforeRows.Count && pair.After < afterRows.Count)
            {
                sum += SignatureDistance(beforeRows[pair.Before], afterRows[pair.After]);
                count++;
            }
        }

        return count == 0 ? int.MaxValue : sum / count;
    }

    private static async Task<ScrollRowsResult> ScrollRowsWithRetryAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle listGridRect,
        Rectangle rowAlignProbeRect,
        ScrollCalibrationState calibration,
        int rows,
        ScanLog scanLog,
        CancellationToken token)
    {
        var first = await ScrollRowsAsync(window, profile, listGridRect, rowAlignProbeRect, calibration, rows, scanLog, token);
        if (first.Success)
        {
            return first;
        }

        scanLog.Write($"Scroll retry: rows={rows}, firstFailure={first.Message}");
        await Task.Delay(Math.Max(80, profile.ScrollTickDelayMs), token);
        var second = await ScrollRowsAsync(window, profile, listGridRect, rowAlignProbeRect, calibration, rows, scanLog, token);
        if (!second.Success)
        {
            scanLog.Write($"Scroll failed after retry: rows={rows}, secondFailure={second.Message}");
        }

        return second;
    }

    private static async Task<ScrollRowsResult> ScrollRowsAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle listGridRect,
        Rectangle rowAlignProbeRect,
        ScrollCalibrationState calibration,
        int rows,
        ScanLog scanLog,
        CancellationToken token)
    {
        if (rows <= 0)
        {
            return ScrollRowsResult.Ok("no rows requested");
        }

        window.MoveCursor(window.ToScreenPoint(profile.Point("listWheelArea")));
        scanLog.WriteEvent("SCROLL_ROWS_START", $"rows={rows}, calibratedRows={calibration.CalibratedRows}, avgTicks={calibration.AverageTicksPerRow:F2}, listGrid={listGridRect}, rowProbe={rowAlignProbeRect}");
        if (calibration.CalibratedRows >= Math.Max(1, profile.CalibrationRows) && rows > 1 && calibration.AverageTicksPerRow > 0)
        {
            var ticks = Math.Max(1, (int)Math.Round(calibration.AverageTicksPerRow * rows));
            var before = CaptureSignature(window, listGridRect);
            for (var i = 0; i < ticks; i++)
            {
                token.ThrowIfCancellationRequested();
                scanLog.WriteEvent("WHEEL_TICK", $"mode=fast, requestedRows={rows}, tick={i + 1}/{ticks}, delta={profile.ScrollTickDelta}, avgTicks={calibration.AverageTicksPerRow:F2}");
                window.MouseWheel(profile.ScrollTickDelta);
                await Task.Delay(Math.Min(20, Math.Max(1, profile.ScrollTickDelayMs / 4)), token);
            }

            await Task.Delay(Math.Max(40, profile.ScrollTickDelayMs), token);
            var after = CaptureSignature(window, listGridRect);
            var movedDistance = SignatureDistance(before, after);
            await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);
            scanLog.Write($"Scroll fast: rows={rows}, ticks={ticks}, avg={calibration.AverageTicksPerRow:F2}, movedDistance={movedDistance}.");
            return movedDistance > ListMovementTolerance
                ? ScrollRowsResult.Ok($"fast rows={rows}, ticks={ticks}")
                : ScrollRowsResult.Fail($"fast scroll did not move list enough, distance={movedDistance}");
        }

        for (var i = 0; i < rows; i++)
        {
            var result = await ScrollOneRowAsync(window, profile, listGridRect, rowAlignProbeRect, calibration, scanLog, token);
            if (!result.Success)
            {
                return result;
            }
        }

        return ScrollRowsResult.Ok($"slow rows={rows}");
    }

    private static async Task<ScrollRowsResult> ScrollOneRowAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle listGridRect,
        Rectangle rowAlignProbeRect,
        ScrollCalibrationState calibration,
        ScanLog scanLog,
        CancellationToken token)
    {
        var beforeGrid = CaptureSignature(window, listGridRect);
        var beforeAlign = CaptureSignature(window, rowAlignProbeRect);
        var maxTicks = Math.Max(1, profile.ScrollMaxTicksPerRow);
        scanLog.WriteEvent("SCROLL_ONE_ROW_START", $"maxTicks={maxTicks}, delta={profile.ScrollTickDelta}, calibratedRows={calibration.CalibratedRows}, avgTicks={calibration.AverageTicksPerRow:F2}, beforeGrid={beforeGrid.Hash:X16}, beforeAlign={beforeAlign.Hash:X16}");
        for (var tick = 1; tick <= maxTicks; tick++)
        {
            token.ThrowIfCancellationRequested();
            scanLog.WriteEvent("WHEEL_TICK", $"mode=calibrate-one-row, tick={tick}/{maxTicks}, delta={profile.ScrollTickDelta}");
            window.MouseWheel(profile.ScrollTickDelta);
            await Task.Delay(Math.Max(1, profile.ScrollTickDelayMs), token);

            var currentGrid = CaptureSignature(window, listGridRect);
            var currentAlign = CaptureSignature(window, rowAlignProbeRect);
            var gridDistance = SignatureDistance(beforeGrid, currentGrid);
            var alignDistance = SignatureDistance(beforeAlign, currentAlign);
            if (gridDistance > ListMovementTolerance || alignDistance > ListMovementTolerance)
            {
                await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);
                calibration.Record(tick);
                scanLog.WriteEvent("SCROLL_ONE_ROW_DONE", $"ticks={tick}, gridDistance={gridDistance}, alignDistance={alignDistance}, afterGrid={currentGrid.Hash:X16}, afterAlign={currentAlign.Hash:X16}, avg={calibration.AverageTicksPerRow:F2}, calibratedRows={calibration.CalibratedRows}");
                scanLog.Write($"Scroll one row: ticks={tick}, gridDistance={gridDistance}, alignDistance={alignDistance}, avg={calibration.AverageTicksPerRow:F2}, calibratedRows={calibration.CalibratedRows}.");
                return ScrollRowsResult.Ok($"one row ticks={tick}");
            }
        }

        scanLog.WriteEvent("SCROLL_ONE_ROW_FAIL", $"maxTicks={maxTicks}, delta={profile.ScrollTickDelta}");
        return ScrollRowsResult.Fail($"list did not move after {maxTicks} wheel ticks");
    }

    private static async Task WaitForListStableAsync(GameWindow window, ScanProfile profile, Rectangle listGridRect, ScanLog scanLog, CancellationToken token)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Max(180, Math.Min(profile.LoadTimeoutMs, 500)));
        var interval = TimeSpan.FromMilliseconds(Math.Max(15, profile.LoadPollMs));
        var start = DateTime.UtcNow;
        var stableFrames = 0;
        var requiredStableFrames = Math.Max(1, profile.ListStableConfirmFrames);
        var previous = CaptureSignature(window, listGridRect);

        while (DateTime.UtcNow - start < timeout)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(interval, token);
            var current = CaptureSignature(window, listGridRect);
            if (SignatureDistance(previous, current) <= ListStableTolerance)
            {
                stableFrames++;
                if (stableFrames >= requiredStableFrames)
                {
                    return;
                }
            }
            else
            {
                stableFrames = 0;
            }

            previous = current;
        }

        scanLog.Write($"List stable wait timed out after {timeout.TotalMilliseconds:F0}ms; continuing with best effort.");
    }

    private static int? TryReadInventoryCount(GameWindow window, ScanProfile profile, PaddleOcrRecognizer recognizer, ScanLog scanLog)
    {
        try
        {
            var rect = window.ToScreenRectangle(profile.Rectangle("inventoryCount"));
            using var image = window.Capture(rect);
            using var mat = BitmapConverter.ToMat(image);
            var ocr = recognizer.Recognize(mat, [new CvRect(0, 0, mat.Width, mat.Height)]);
            var text = ocr.Count > 0 ? ocr[0].Text : "";
            var match = Regex.Match(text, @"(\d{1,4})\s*/\s*\d{2,5}");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var count))
            {
                scanLog.Write($"Inventory count OCR: '{text}' => {count}.");
                return count;
            }

            var numbers = Regex.Matches(text, @"\d+").Select(m => m.Value).ToArray();
            if (numbers.Length >= 2 && int.TryParse(numbers[0], out count))
            {
                scanLog.Write($"Inventory count OCR fallback: '{text}' => {count}.");
                return count;
            }

            scanLog.Write($"Inventory count OCR failed: '{text}'.");
        }
        catch (Exception ex)
        {
            scanLog.Write($"Inventory count OCR exception: {ex.Message}");
        }

        return null;
    }

    private static RarityProbe DetectRarityAround(GameWindow window, ScanProfile profile, System.Drawing.Point center)
    {
        const int radius = 36;
        const int rarityScoreTolerance = 42;
        using var image = window.Capture(new Rectangle(center.X - radius, center.Y - radius, radius * 2 + 1, radius * 2 + 1));
        var candidates = new[]
        {
            new RarityColor("S", profile.Color("rarityS")),
            new RarityColor("A", profile.Color("rarityA")),
            new RarityColor("B", profile.Color("rarityB")),
        };

        var bestDelta = int.MaxValue;
        var bestCandidate = "";
        var bestColor = Color.Empty;
        for (var y = 0; y < image.Height; y += 2)
        {
            for (var x = 0; x < image.Width; x += 2)
            {
                var color = image.GetPixel(x, y);
                foreach (var candidate in candidates)
                {
                    var delta = RarityColorScore(color, candidate.Color);
                    if (delta < bestDelta)
                    {
                        bestDelta = delta;
                        bestCandidate = candidate.Rarity;
                        bestColor = color;
                    }
                }
            }
        }

        return new RarityProbe(
            bestDelta <= rarityScoreTolerance ? bestCandidate : null,
            bestColor,
            bestCandidate,
            bestDelta);
    }

    private static int RarityColorScore(Color current, Color expected)
    {
        var channelDelta = MaxChannelDelta(current, expected);
        var hueScore = HueScore(current, expected);
        return Math.Min(channelDelta, hueScore);
    }

    private static int HueScore(Color current, Color expected)
    {
        var currentHsv = ToHsv(current);
        if (currentHsv.Saturation < 0.35f || currentHsv.Value < 0.20f)
        {
            return 999;
        }

        var expectedHsv = ToHsv(expected);
        var hueDelta = Math.Abs(currentHsv.Hue - expectedHsv.Hue);
        hueDelta = Math.Min(hueDelta, 360f - hueDelta);
        var saturationPenalty = currentHsv.Saturation < 0.55f ? (0.55f - currentHsv.Saturation) * 80f : 0f;
        var valuePenalty = currentHsv.Value < 0.35f ? (0.35f - currentHsv.Value) * 80f : 0f;
        return (int)Math.Round(hueDelta + saturationPenalty + valuePenalty);
    }

    private static HsvColor ToHsv(Color color)
    {
        var r = color.R / 255f;
        var g = color.G / 255f;
        var b = color.B / 255f;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = 0f;

        if (delta > 0.0001f)
        {
            if (Math.Abs(max - r) < 0.0001f)
            {
                hue = 60f * (((g - b) / delta) % 6f);
            }
            else if (Math.Abs(max - g) < 0.0001f)
            {
                hue = 60f * (((b - r) / delta) + 2f);
            }
            else
            {
                hue = 60f * (((r - g) / delta) + 4f);
            }

            if (hue < 0f)
            {
                hue += 360f;
            }
        }

        var saturation = max <= 0.0001f ? 0f : delta / max;
        return new HsvColor(hue, saturation, max);
    }

    private static int MaxChannelDelta(Color current, Color expected)
    {
        return Math.Max(
            Math.Abs(current.R - expected.R),
            Math.Max(Math.Abs(current.G - expected.G), Math.Abs(current.B - expected.B)));
    }

    private static string ColorText(Color color)
    {
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static ImageSignature CaptureSignature(GameWindow window, Rectangle rect)
    {
        using var image = window.Capture(rect);
        return CreateSignature(image, new Rectangle(System.Drawing.Point.Empty, image.Size));
    }

    private static Rectangle[] BuildPanelStableProbeRects(Rectangle panelProbeRect, Rectangle panelRect, IReadOnlyList<CvRect> rois)
    {
        var imageSize = new System.Drawing.Size(panelRect.Width, panelRect.Height);
        var rects = new List<Rectangle>
        {
            panelProbeRect
        };

        foreach (var index in new[] { 0, 1, 2, 3 })
        {
            if (index >= rois.Count)
            {
                continue;
            }

            var roi = rois[index];
            rects.Add(ClampRectangle(new Rectangle(roi.X, roi.Y, roi.Width, roi.Height), imageSize));
        }

        return rects.ToArray();
    }

    private static ImageSignature[] CreateSignatures(Bitmap image, IReadOnlyList<Rectangle> rects)
    {
        var signatures = new ImageSignature[rects.Count];
        for (var i = 0; i < signatures.Length; i++)
        {
            signatures[i] = CreateSignature(image, rects[i]);
        }

        return signatures;
    }

    private static bool AreProbesStable(IReadOnlyList<ImageSignature> previousSignatures, IReadOnlyList<ImageSignature> currentSignatures)
    {
        var count = Math.Min(previousSignatures.Count, currentSignatures.Count);
        for (var i = 0; i < count; i++)
        {
            if (SignatureDistance(previousSignatures[i], currentSignatures[i]) > PanelStableTolerance)
            {
                return false;
            }
        }

        return count > 0;
    }

    private static bool HasProbeChange(IReadOnlyList<ImageSignature> previousSignatures, IReadOnlyList<ImageSignature> currentSignatures, int tolerance)
    {
        var count = Math.Min(previousSignatures.Count, currentSignatures.Count);
        if (count == 0 || previousSignatures.Count != currentSignatures.Count)
        {
            return true;
        }

        for (var i = 0; i < count; i++)
        {
            if (SignatureDistance(previousSignatures[i], currentSignatures[i]) > tolerance)
            {
                return true;
            }
        }

        return false;
    }

    private static ImageSignature CreateSignature(Bitmap image, Rectangle rect)
    {
        rect = ClampRectangle(rect, image.Size);
        var samples = new int[SignatureColumns * SignatureRows];
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        var index = 0;

        for (var row = 0; row < SignatureRows; row++)
        {
            var y = rect.Top + Math.Min(rect.Height - 1, (int)Math.Round((row + 0.5) * rect.Height / SignatureRows));
            for (var col = 0; col < SignatureColumns; col++)
            {
                var x = rect.Left + Math.Min(rect.Width - 1, (int)Math.Round((col + 0.5) * rect.Width / SignatureColumns));
                var color = image.GetPixel(x, y);
                var luma = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
                samples[index++] = luma;
                hash ^= color.R;
                hash *= prime;
                hash ^= color.G;
                hash *= prime;
                hash ^= color.B;
                hash *= prime;
            }
        }

        return new ImageSignature(hash, samples);
    }

    private static int SignatureDistance(ImageSignature left, ImageSignature right)
    {
        var count = Math.Min(left.Samples.Length, right.Samples.Length);
        if (count == 0)
        {
            return left.Hash == right.Hash ? 0 : int.MaxValue;
        }

        var sum = 0;
        for (var i = 0; i < count; i++)
        {
            sum += Math.Abs(left.Samples[i] - right.Samples[i]);
        }

        return sum / count;
    }

    private static Rectangle RelativeIntersection(Rectangle childScreenRect, Rectangle parentScreenRect, System.Drawing.Size imageSize)
    {
        var intersect = Rectangle.Intersect(childScreenRect, parentScreenRect);
        if (intersect.IsEmpty)
        {
            return new Rectangle(System.Drawing.Point.Empty, imageSize);
        }

        return ClampRectangle(new Rectangle(
            intersect.Left - parentScreenRect.Left,
            intersect.Top - parentScreenRect.Top,
            intersect.Width,
            intersect.Height), imageSize);
    }

    private static Rectangle ClampRectangle(Rectangle rect, System.Drawing.Size bounds)
    {
        var left = Math.Clamp(rect.Left, 0, Math.Max(0, bounds.Width - 1));
        var top = Math.Clamp(rect.Top, 0, Math.Max(0, bounds.Height - 1));
        var right = Math.Clamp(rect.Right, left + 1, bounds.Width);
        var bottom = Math.Clamp(rect.Bottom, top + 1, bounds.Height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static async Task<PanelCapture> CaptureStablePanelAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle panelRect,
        IReadOnlyList<CvRect> rois,
        System.Drawing.Point statOffset,
        Color statRowBackground,
        Rectangle panelChangeProbeRect,
        ImageSignature[]? previousPanelSignatures,
        ScanLog scanLog,
        CancellationToken token)
    {
        const int minimumReadyRois = 6;
        var timeout = TimeSpan.FromMilliseconds(profile.LoadTimeoutMs);
        var interval = TimeSpan.FromMilliseconds(Math.Max(15, profile.LoadPollMs));
        var settleDelay = Math.Clamp(profile.PanelSettleDelayMs, 40, 180);
        await Task.Delay(settleDelay, token);

        var start = DateTime.UtcNow;
        Bitmap? previous = null;
        var previousCount = -1;
        var stableCount = 0;
        var probeRect = RelativeIntersection(panelChangeProbeRect, panelRect, new System.Drawing.Size(panelRect.Width, panelRect.Height));
        var stableProbeRects = BuildPanelStableProbeRects(probeRect, panelRect, rois);
        var sawPanelChange = previousPanelSignatures is null;
        ImageSignature[]? previousProbeSignatures = null;
        var stableProbeFrames = 0;
        var unchangedFallbackDelay = TimeSpan.FromMilliseconds(Math.Clamp(profile.PanelUnchangedFallbackMs, 80, profile.LoadTimeoutMs));

        while (DateTime.UtcNow - start < timeout)
        {
            token.ThrowIfCancellationRequested();
            var image = window.Capture(panelRect);
            var probeSignatures = CreateSignatures(image, stableProbeRects);
            if (previousPanelSignatures is not null
                && HasProbeChange(previousPanelSignatures, probeSignatures, PanelChangeTolerance))
            {
                sawPanelChange = true;
            }

            stableProbeFrames = previousProbeSignatures is not null
                && AreProbesStable(previousProbeSignatures, probeSignatures)
                    ? stableProbeFrames + 1
                    : 0;
            previousProbeSignatures = probeSignatures;

            var visibleCount = CountVisibleRois(image, rois, statOffset, statRowBackground, profile.ColorTolerance);
            if (visibleCount >= minimumReadyRois)
            {
                stableCount = visibleCount == previousCount ? stableCount + 1 : 1;
                var readable = visibleCount == rois.Count || stableCount >= 2;
                if (readable && sawPanelChange && stableProbeFrames >= 1)
                {
                    previous?.Dispose();
                    return new PanelCapture(image, visibleCount, (DateTime.UtcNow - start).TotalMilliseconds, usedFallback: false, probeSignatures);
                }

                if (readable && !sawPanelChange && stableProbeFrames >= 1 && DateTime.UtcNow - start >= unchangedFallbackDelay)
                {
                    scanLog.Write("Panel probes stayed unchanged but readable and stable; accepting fast fallback.");
                    previous?.Dispose();
                    return new PanelCapture(image, visibleCount, (DateTime.UtcNow - start).TotalMilliseconds, usedFallback: true, probeSignatures);
                }
            }
            else
            {
                stableCount = 0;
            }

            previousCount = visibleCount;
            previous?.Dispose();
            previous = image;
            await Task.Delay(interval, token);
        }

        if (previous is not null)
        {
            previous.Dispose();
        }

        scanLog.Write("Panel readable wait timed out.");
        throw new TimeoutException("详情面板截图等待超时。");
    }

    private static CvRect[] BuildRois(GameWindow window, ScanProfile profile, Rectangle panelRect)
    {
        return profile.OrderedRoiKeys()
            .Select(key =>
            {
                var rect = window.ToScreenRectangle(profile.Rectangle(key));
                return new CvRect(rect.X - panelRect.X, rect.Y - panelRect.Y, rect.Width, rect.Height);
            })
            .ToArray();
    }

    private static int CountVisibleRois(Bitmap image, IReadOnlyList<CvRect> rois, System.Drawing.Point statOffset, Color statRowBackground, int tolerance)
    {
        var count = 0;
        var rowPresenceTolerance = Math.Min(tolerance, 10);
        foreach (var roi in rois)
        {
            var x = Math.Clamp(roi.X + statOffset.X, 0, image.Width - 1);
            var y = Math.Clamp(roi.Y + statOffset.Y, 0, image.Height - 1);
            if (count <= 3 || image.GetPixel(x, y).IsCloseTo(statRowBackground, rowPresenceTolerance))
            {
                count++;
            }
            else
            {
                break;
            }
        }

        return count;
    }

    private Task[] StartOcrWorkers(
        BlockingCollection<DiscCapture> queue,
        BlockingCollection<OcrWorkResult> ocrResults,
        string outputDir,
        ScanOptions options,
        ScanLog scanLog,
        int workerCount,
        int ocrIntraOpThreads,
        Counters counters,
        OcrDiagnosticsWriter diagnostics)
    {
        return Enumerable.Range(1, workerCount)
            .Select(workerId => Task.Run(() => ConsumeOcrWorker(queue, ocrResults, outputDir, options, scanLog, workerId, ocrIntraOpThreads, counters, diagnostics)))
            .ToArray();
    }

    private void ConsumeOcrWorker(
        BlockingCollection<DiscCapture> queue,
        BlockingCollection<OcrWorkResult> ocrResults,
        string outputDir,
        ScanOptions options,
        ScanLog scanLog,
        int workerId,
        int ocrIntraOpThreads,
        Counters counters,
        OcrDiagnosticsWriter diagnostics)
    {
        using var recognizer = new PaddleOcrRecognizer(AppPaths.ModelFile, AppPaths.CharacterDictFile, ocrIntraOpThreads);
        var cleaner = new DriveDiscCleaner(_wikiData);
        scanLog.Write($"OCR worker {workerId} started. IntraOpThreads={ocrIntraOpThreads}.");
        while (TryTakeBatch(queue, options.OcrBatchSize, out var batch))
        {
            Mat?[]? mats = null;
            try
            {
                if (Volatile.Read(ref counters.StopAfterIndex) > 0)
                {
                    scanLog.Write($"OCR worker {workerId} discarded batch after stop signal. BatchSize={batch.Count}.");
                    break;
                }

                var bitmapToMatSw = Stopwatch.StartNew();
                mats = new Mat?[batch.Count];
                for (var i = 0; i < batch.Count; i++)
                {
                    var capture = batch[i];
                    var mat = BitmapConverter.ToMat(capture.Image);
                    mats[i] = mat;
                }
                bitmapToMatSw.Stop();

                var inputs = batch
                    .Select((capture, index) => new OcrBatchInput(mats[index]!, capture.Rois))
                    .ToArray();
                var recognition = recognizer.RecognizeBatchDetailed(inputs);
                var ocrBatch = recognition.Results;
                if (Volatile.Read(ref counters.StopAfterIndex) > 0)
                {
                    scanLog.Write($"OCR worker {workerId} discarded recognized batch after stop signal. BatchSize={batch.Count}.");
                    break;
                }

                var cleanSw = Stopwatch.StartNew();
                for (var i = 0; i < batch.Count; i++)
                {
                    var capture = batch[i];
                    var ocr = ocrBatch[i];
                    try
                    {
                        var export = cleaner.Clean(capture.Index, capture.Rarity, ocr);
                        ocrResults.Add(OcrWorkResult.Success(capture.Index, export, BuildOcrDetail(capture, ocr)));
                    }
                    catch (Exception ex)
                    {
                        var detail = BuildErrorDetail(capture, ocr, ex);
                        ocrResults.Add(OcrWorkResult.Failure(capture.Index, detail, ex.Message));
                    }
                }
                cleanSw.Stop();
                var backlog = Math.Max(0, Volatile.Read(ref counters.Queued) - Volatile.Read(ref counters.Completed) - Volatile.Read(ref counters.Failed));
                diagnostics.Write(workerId, batch.Count, recognition.Diagnostics, bitmapToMatSw.Elapsed.TotalMilliseconds, cleanSw.Elapsed.TotalMilliseconds, fallbackCount: 0, backlog);
            }
            catch (Exception ex)
            {
                foreach (var capture in batch)
                {
                    ocrResults.Add(OcrWorkResult.Failure(capture.Index, BuildBatchErrorDetail(capture, ex), ex.Message));
                }

                scanLog.Write($"OCR worker {workerId} batch failed: {ex}");
            }
            finally
            {
                if (mats is not null)
                {
                    foreach (var mat in mats)
                    {
                        mat?.Dispose();
                    }
                }

                foreach (var capture in batch)
                {
                    capture.Dispose();
                }
            }
        }

        scanLog.Write($"OCR worker {workerId} stopped.");
    }

    private static void ConsumeOcrResults(
        BlockingCollection<OcrWorkResult> ocrResults,
        ConcurrentBag<DriveDiscExport> results,
        Counters counters,
        string outputDir,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        DuplicateGuard duplicateGuard,
        ScanOptions options,
        CancellationTokenSource linked)
    {
        var pending = new SortedDictionary<int, OcrWorkResult>();
        var nextIndex = 1;
        foreach (var result in ocrResults.GetConsumingEnumerable())
        {
            if (Volatile.Read(ref counters.StopAfterIndex) > 0)
            {
                continue;
            }

            pending[result.Index] = result;
            nextIndex = FlushOcrResults(pending, nextIndex, allowGaps: false, results, counters, outputDir, progress, scanLog, duplicateGuard, options, linked);
        }

        _ = FlushOcrResults(pending, nextIndex, allowGaps: true, results, counters, outputDir, progress, scanLog, duplicateGuard, options, linked);
    }

    private static int FlushOcrResults(
        SortedDictionary<int, OcrWorkResult> pending,
        int nextIndex,
        bool allowGaps,
        ConcurrentBag<DriveDiscExport> results,
        Counters counters,
        string outputDir,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        DuplicateGuard duplicateGuard,
        ScanOptions options,
        CancellationTokenSource linked)
    {
        if (Volatile.Read(ref counters.StopAfterIndex) > 0)
        {
            pending.Clear();
            return nextIndex;
        }

        while (pending.Count > 0)
        {
            var index = nextIndex;
            if (!pending.TryGetValue(index, out var result))
            {
                if (!allowGaps)
                {
                    break;
                }

                index = pending.Keys.First();
                result = pending[index];
            }

            pending.Remove(index);
            nextIndex = Math.Max(nextIndex, index + 1);
            if (result.Export is not null)
            {
                if (options.StopAtNonLevel15 && result.Export.Level != 15)
                {
                    Interlocked.CompareExchange(ref counters.StopAfterIndex, result.Index, 0);
                    pending.Clear();
                    var detail = result.ErrorDetail ?? BuildExportDetail(result.Export);
                    File.WriteAllText(Path.Combine(outputDir, $"{result.Index:0000}.non15.txt"), detail);
                    scanLog.Write($"Stop at #{result.Index}: detected non-15 drive disc {result.Export.Name} {result.Export.Rarity} {result.Export.Level}/{result.Export.MaxLevel}.");
                    Report(progress, counters, $"检测到非15级驱动盘 #{result.Index}：{result.Export.Level}/{result.Export.MaxLevel}，扫描停止。");
                    linked.Cancel();
                    return nextIndex;
                }

                results.Add(result.Export);
                Interlocked.Increment(ref counters.Completed);
                if (!duplicateGuard.Observe(result.Export, out var duplicateReason))
                {
                    Interlocked.CompareExchange(ref counters.StopAfterIndex, result.Index, 0);
                    scanLog.Write($"Duplicate guard canceled scan at #{result.Index}: {duplicateReason}");
                    Report(progress, counters, $"重复保护触发：{duplicateReason}");
                    linked.Cancel();
                    return nextIndex;
                }

                var message = result.Export.Index % 25 == 0
                    ? $"识别 #{result.Export.Index}：{result.Export.Name} {result.Export.Rarity} {result.Export.Level}/{result.Export.MaxLevel}"
                    : "";
                Report(progress, counters, message, result.Export);
            }
            else
            {
                Interlocked.Increment(ref counters.Failed);
                File.WriteAllText(Path.Combine(outputDir, $"{result.Index:0000}.error.txt"), result.ErrorDetail ?? result.ErrorMessage ?? "Unknown OCR error");
                scanLog.Write($"OCR item failed #{result.Index}: {result.ErrorMessage}");
                Report(progress, counters, $"识别失败 #{result.Index}：{result.ErrorMessage}");
            }
        }

        return nextIndex;
    }

    private static bool TryTakeBatch(BlockingCollection<DiscCapture> queue, int maxBatchSize, out List<DiscCapture> batch)
    {
        batch = new List<DiscCapture>(Math.Max(1, maxBatchSize));
        try
        {
            batch.Add(queue.Take());
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        while (batch.Count < maxBatchSize && queue.TryTake(out var next, millisecondsTimeout: 3))
        {
            batch.Add(next);
        }

        return true;
    }

    private static int DisposeQueuedCaptures(BlockingCollection<DiscCapture> queue)
    {
        var count = 0;
        while (queue.TryTake(out var capture))
        {
            capture.Dispose();
            count++;
        }

        return count;
    }

    private static string BuildOcrDetail(DiscCapture capture, IReadOnlyList<OcrResult> ocr)
    {
        var lines = new List<string>
        {
            $"Index: {capture.Index}",
            $"Rarity: {capture.Rarity}",
            $"Rois: {capture.Rois.Length}",
            $"OcrResults: {ocr.Count}",
            "OCR:",
        };
        lines.AddRange(ocr.Select((item, index) => $"{index:D2}: {item.Score:P1} {item.Text}"));
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildErrorDetail(DiscCapture capture, IReadOnlyList<OcrResult> ocr, Exception ex)
    {
        var lines = new List<string>
        {
            BuildOcrDetail(capture, ocr),
            "Exception:",
            ex.ToString()
        };
        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildExportDetail(DriveDiscExport export)
    {
        return JsonSerializer.Serialize(export, JsonDefaults.Write);
    }

    private static string BuildBatchErrorDetail(DiscCapture capture, Exception ex)
    {
        return string.Join(Environment.NewLine, [
            $"Index: {capture.Index}",
            $"Rarity: {capture.Rarity}",
            $"Rois: {capture.Rois.Length}",
            "Batch OCR exception:",
            ex.ToString()
        ]);
    }

    private static async Task MonitorBackpackAsync(
        GameWindow window,
        ScanProfile profile,
        CancellationTokenSource linked,
        IProgress<ScanProgress> progress,
        Counters counters,
        ScanLog scanLog)
    {
        var dismantlePoint = window.ToScreenPoint(profile.Point("dismantleButton"));
        var dismantleColor = profile.Color("dismantleButton");

        while (!linked.IsCancellationRequested)
        {
            if (!window.GetPixel(dismantlePoint).IsCloseTo(dismantleColor, profile.ColorTolerance))
            {
                scanLog.Write($"Backpack monitor canceled scan. point={dismantlePoint}, current={ColorText(window.GetPixel(dismantlePoint))}, expected={ColorText(dismantleColor)}");
                Report(progress, counters, "检测到背包界面已退出。");
                linked.Cancel();
                return;
            }

            await Task.Delay(150, linked.Token);
        }
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan interval,
        int confirm,
        CancellationToken token)
    {
        var start = DateTime.UtcNow;
        var count = 0;
        while (DateTime.UtcNow - start < timeout)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(interval, token);
            if (condition())
            {
                count++;
                if (count >= confirm)
                {
                    return;
                }
            }
            else
            {
                count = 0;
            }
        }

        throw new TimeoutException("等待界面加载超时。");
    }

    private static async Task SafeAwait(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Report(IProgress<ScanProgress> progress, Counters counters, string message, DriveDiscExport? item = null, Bitmap? debugImage = null)
    {
        progress.Report(new ScanProgress
        {
            Message = string.IsNullOrWhiteSpace(message) ? "" : $"[{DateTime.Now:HH:mm:ss}] {message}",
            Item = item,
            DebugImage = debugImage,
            Visited = counters.Visited,
            Queued = counters.Queued,
            Completed = counters.Completed,
            Failed = counters.Failed
        });
    }

    private sealed class DiscCapture : IDisposable
    {
        public int Index { get; }
        public string Rarity { get; }
        public Bitmap Image { get; }
        public CvRect[] Rois { get; }

        public DiscCapture(int index, string rarity, Bitmap image, CvRect[] rois)
        {
            Index = index;
            Rarity = rarity;
            Image = image;
            Rois = rois;
        }

        public void Dispose()
        {
            Image.Dispose();
        }
    }

    private sealed class PanelCapture : IDisposable
    {
        private bool _imageTaken;

        public PanelCapture(Bitmap image, int visibleRoiCount, double waitMilliseconds, bool usedFallback, ImageSignature[] probeSignatures)
        {
            Image = image;
            VisibleRoiCount = visibleRoiCount;
            WaitMilliseconds = waitMilliseconds;
            UsedFallback = usedFallback;
            ProbeSignatures = probeSignatures;
        }

        public Bitmap Image { get; }
        public int VisibleRoiCount { get; }
        public double WaitMilliseconds { get; }
        public bool UsedFallback { get; }
        public ImageSignature[] ProbeSignatures { get; }

        public Bitmap TakeImage()
        {
            _imageTaken = true;
            return Image;
        }

        public void Dispose()
        {
            if (!_imageTaken)
            {
                Image.Dispose();
            }
        }
    }

    private sealed record OcrWorkResult(int Index, DriveDiscExport? Export, string? ErrorDetail, string? ErrorMessage)
    {
        public static OcrWorkResult Success(int index, DriveDiscExport export, string? detail = null)
        {
            return new OcrWorkResult(index, export, detail, null);
        }

        public static OcrWorkResult Failure(int index, string errorDetail, string errorMessage)
        {
            return new OcrWorkResult(index, null, errorDetail, errorMessage);
        }
    }

    private sealed class Counters
    {
        public int Visited;
        public int Queued;
        public int Completed;
        public int Failed;
        public int StopAfterIndex;
    }

    private enum RowScanResult
    {
        Completed,
        Stop
    }

    private readonly record struct RarityColor(string Rarity, Color Color);

    private readonly record struct RarityProbe(string? Rarity, Color BestColor, string BestCandidate, int BestDelta);

    private readonly record struct HsvColor(float Hue, float Saturation, float Value);

    private sealed class ScanLog : IDisposable
    {
        private readonly object _sync = new();
        private readonly StreamWriter _writer;
        private int _eventId;

        public ScanLog(string path)
        {
            _writer = new StreamWriter(path, append: false) { AutoFlush = false };
        }

        public void Write(string message)
        {
            lock (_sync)
            {
                _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}");
                _writer.Flush();
            }
        }

        public void WriteEvent(string kind, string message)
        {
            lock (_sync)
            {
                _eventId++;
                _writer.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] EVENT #{_eventId:000000} {kind}: {message}");
                if (_eventId % 64 == 0)
                {
                    _writer.Flush();
                }
            }
        }

        public void Dispose()
        {
            _writer.Flush();
            _writer.Dispose();
        }
    }

    private sealed class DuplicateGuard
    {
        private readonly int _threshold;
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private string? _previousFingerprint;
        private int _consecutiveDuplicateCount;

        public DuplicateGuard(int threshold)
        {
            _threshold = Math.Max(1, threshold);
        }

        public bool Observe(DriveDiscExport item, out string? reason)
        {
            reason = null;
            var fingerprint = Fingerprint(item);
            if (_previousFingerprint is not null && string.Equals(_previousFingerprint, fingerprint, StringComparison.Ordinal))
            {
                _consecutiveDuplicateCount++;
            }
            else if (_seen.Contains(fingerprint))
            {
                _consecutiveDuplicateCount = 1;
            }
            else
            {
                _consecutiveDuplicateCount = 0;
            }

            _previousFingerprint = fingerprint;
            _seen.Add(fingerprint);

            if (_consecutiveDuplicateCount >= _threshold)
            {
                reason = $"连续重复达到 {_consecutiveDuplicateCount} 条，疑似翻页错误或列表未对齐。";
                return false;
            }

            return true;
        }

        private static string Fingerprint(DriveDiscExport item)
        {
            static string FormatStat(Dictionary<string, object> values)
            {
                return string.Join("|", values.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={FormatValue(kv.Value)}"));
            }

            static string FormatList(IEnumerable<Dictionary<string, object>> values)
            {
                return string.Join("||", values.Select(FormatStat));
            }

            static string FormatValue(object value)
            {
                return value is JsonElement element ? element.ToString() : value.ToString() ?? "";
            }

            return string.Join("::", [
                item.Name,
                item.Slot.ToString(),
                item.Rarity,
                item.Level.ToString(),
                item.MaxLevel.ToString(),
                FormatStat(item.MainStat),
                FormatList(item.SubStats)
            ]);
        }
    }

    private sealed class ScrollCalibrationState
    {
        public ScrollCalibrationState(ImageSignature initialSignature)
        {
            InitialSignature = initialSignature;
        }

        public ImageSignature InitialSignature { get; }
        public int CalibratedRows { get; private set; }
        public double AverageTicksPerRow { get; private set; }

        public void Record(int ticks)
        {
            CalibratedRows++;
            AverageTicksPerRow = ((AverageTicksPerRow * (CalibratedRows - 1)) + ticks) / CalibratedRows;
        }
    }

    private readonly record struct ImageSignature(ulong Hash, int[] Samples);

    private readonly record struct RowScrollVerification(int OneRowScore, int NoMoveScore, int TwoRowScore);

    private readonly record struct ScrollRowsResult(bool Success, string Message, int RowsAdvanced = 0)
    {
        public static ScrollRowsResult Ok(string message, int rowsAdvanced = 0) => new(true, message, rowsAdvanced);
        public static ScrollRowsResult Fail(string message) => new(false, message);
    }

}
