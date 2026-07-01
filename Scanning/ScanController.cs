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
    private const int RowShiftLooseMargin = 12;
    private const int RowShiftStrongMatchTolerance = 18;
    private const int RowShiftAmbiguousMovementTolerance = 18;
    private const int ScrollMaxSmallTicks = 6;
    private const int OverlapSignatureMatchTolerance = 48;
    private const int OverlapSignatureClearMargin = 12;
    private const int OverlapSignatureStrongMargin = 18;
    private const int OverlapSignatureRecheckFrames = 3;
    private const int ConsecutiveIdenticalDuplicateThreshold = 3;
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
        var requestedFastMode = options.FastMode;
        var fastModeActive = false;
        var fastModeMessage = "";
        var profileName = options.ProfileName;
        if (requestedFastMode)
        {
            if (string.IsNullOrWhiteSpace(profileName) || string.Equals(profileName, ScanOptions.DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            {
                profileName = ScanOptions.FastProfileName;
            }

            if (FastOcrTemplateIndex.TryValidateFastModeIndex(options.FastOcrTemplateIndexFile, out var resolvedIndexFile, out var validationMessage))
            {
                options.FastOcrAssist = true;
                options.FastOcrTemplateIndexFile = resolvedIndexFile;
                fastModeActive = true;
                fastModeMessage = validationMessage;
            }
            else
            {
                options.FastOcrAssist = false;
                if (string.Equals(profileName, ScanOptions.FastProfileName, StringComparison.OrdinalIgnoreCase))
                {
                    profileName = ScanOptions.DefaultProfileName;
                }

                fastModeMessage = validationMessage;
            }
        }

        var profile = _profiles.Profiles.FirstOrDefault(p => p.Name == profileName)
            ?? _profiles.Profiles.First();
        if (requestedFastMode && fastModeActive && string.Equals(profileName, ScanOptions.FastProfileName, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(profile.Name, ScanOptions.FastProfileName, StringComparison.OrdinalIgnoreCase))
        {
            options.FastOcrAssist = false;
            fastModeActive = false;
            fastModeMessage = $"fast scan profile not found: {ScanOptions.FastProfileName}";
            profile = _profiles.Profiles.FirstOrDefault(p => p.Name == ScanOptions.DefaultProfileName)
                ?? _profiles.Profiles.First();
        }

        var outputDir = AppPaths.CreateScanDirectory();
        using var scanLog = new ScanLog(Path.Combine(outputDir, "scan.log"));
        var results = new ConcurrentBag<DriveDiscExport>();
        var ocrWorkerCount = ResolveOcrWorkerCount(options);
        var ocrIntraOpThreads = ResolveOcrIntraOpThreads(options, ocrWorkerCount);
        var requestedQueueCapacity = Math.Max(1, options.OcrQueueCapacity);
        var queueCapacity = Math.Max(ocrWorkerCount * Math.Max(1, options.OcrBatchSize) * 4, requestedQueueCapacity);
        if (options.StopAtNonLevel15)
        {
            queueCapacity = Math.Min(queueCapacity, Math.Max(ocrWorkerCount * Math.Max(1, options.OcrBatchSize) * 2, requestedQueueCapacity));
        }
        var queue = new BlockingCollection<DiscCapture>(boundedCapacity: queueCapacity);
        var ocrResults = new BlockingCollection<OcrWorkResult>(boundedCapacity: queueCapacity);
        var counters = new Counters();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var traversalMode = ResolveTraversalMode(options, profile);
        var duplicateGuard = new DuplicateGuard(Math.Max(1, profile.DuplicateRowThreshold));
        var adaptiveTimingActive = options.AdaptiveTiming ?? requestedFastMode;
        var panelStability = new PanelStabilitySelector(options.PanelStabilityMode);
        var runtimeState = new ScanRuntimeState(
            QuickPanelAcceptEnabled: false,
            adaptiveTimingActive,
            adaptiveTimingActive ? new AdaptiveTimingState() : null,
            adaptiveTimingActive ? new AdaptiveOcrThrottle(queueCapacity) : null,
            panelStability,
            new PanelProbeHealth(),
            new ProfileHealthGate(),
            options.PanelAcceptMode,
            options.PostScrollPanelAcceptMode,
            options.PanelFloorMode,
            Math.Clamp(options.PanelMinAcceptFloorMs, 90, 120),
            Math.Clamp(options.SameRowPanelMinAcceptFloorMs, 100, 120),
            Math.Clamp(options.PostScrollPanelMinAcceptFloorMs, 100, 120),
            EffectiveScrollTickDelay(profile, options.ScrollTickDelayOverrideMs));

        scanLog.Write($"AppVersion={AppInfo.Version}, AssemblyVersion={AppInfo.AssemblyVersion}, FileVersion={AppInfo.FileVersion}, ExecutablePath={AppInfo.ExecutablePath}, ExecutableLastWriteTime={AppInfo.ExecutableLastWriteTime?.ToString("O") ?? "unknown"}, RuntimeDirectory={AppInfo.BaseDirectory}");
        scanLog.Write($"Start scan. Profile={profile.Name}, Traversal={traversalMode}, Process={options.ProcessName}, MaxItems={options.MaxItems}, Rarities={string.Join(",", options.Rarities)}, BringToFront={options.BringToFront}, StopAtNonLevel15={options.StopAtNonLevel15}, HighSpeedOcr={options.HighSpeedOcr}, OcrShadowDataset={options.OcrShadowDataset}, FastOcrShadow={options.FastOcrShadow}, FastOcrAssist={options.FastOcrAssist}, FastModeRequested={requestedFastMode}, FastModeActive={fastModeActive}, AdaptiveTimingRequested={options.AdaptiveTiming?.ToString() ?? "auto"}, AdaptiveTimingActive={adaptiveTimingActive}, PanelStabilityMode={options.PanelStabilityMode}, ScrollAcceptMode={options.ScrollAcceptMode}, PanelAcceptMode={options.PanelAcceptMode}, PostScrollPanelAcceptMode={options.PostScrollPanelAcceptMode}, PanelFloorMode={options.PanelFloorMode}, PanelMinAcceptFloorMs={Math.Clamp(options.PanelMinAcceptFloorMs, 90, 120)}, SameRowPanelMinAcceptFloorMs={Math.Clamp(options.SameRowPanelMinAcceptFloorMs, 100, 120)}, PostScrollPanelMinAcceptFloorMs={Math.Clamp(options.PostScrollPanelMinAcceptFloorMs, 100, 120)}, ScrollTickDelayOverrideMs={(options.ScrollTickDelayOverrideMs <= 0 ? 0 : Math.Clamp(options.ScrollTickDelayOverrideMs, 50, 80))}, EffectiveScrollTickDelayMs={runtimeState.EffectiveScrollTickDelayMs}, OverlapConflictMode={options.OverlapConflictMode}, CaptureModeRequested={options.CaptureMode}, CollectVisualProfile={options.CollectVisualProfile}, VisualProfileId={options.VisualProfileId}, VisualProfileClient={options.VisualProfileClient}, VisualQualityLabel={options.VisualQualityLabel}, ProfileRouting={options.ProfileRouting}, OcrWorkers={ocrWorkerCount}, OcrBatchSize={options.OcrBatchSize}, OcrQueueCapacity={queueCapacity}, OcrIntraOpThreads={ocrIntraOpThreads}");
        if (options.CollectVisualProfile)
        {
            scanLog.Write($"VISUAL_PROFILE_COLLECTION_PROTOCOL client={options.VisualProfileClient}, requestedProfile={options.VisualProfileId}, quality={options.VisualQualityLabel}, capture={options.CaptureMode}, fastOcrAssist={options.FastOcrAssist}, panelAccept={options.PanelAcceptMode}, scrollAccept={options.ScrollAcceptMode}, maxItems={options.MaxItems}");
        }
        if (options.PanelStabilityMode == PanelStabilityMode.Auto)
        {
            scanLog.Write($"Panel stability auto enabled. WarmupItems={PanelStabilitySelector.DefaultWarmupItems}, TextCoreGainMs={PanelStabilitySelector.MinimumTextCoreGainMilliseconds}.");
        }
        if (adaptiveTimingActive)
        {
            scanLog.Write($"Adaptive timing enabled for this scan only. WarmupItems={AdaptiveTimingState.DefaultWarmupItems}, OcrThrottleHigh={Math.Ceiling(queueCapacity * 0.60):F0}, OcrThrottleLow={Math.Floor(queueCapacity * 0.25):F0}.");
        }
        if (requestedFastMode)
        {
            scanLog.Write($"Fast mode {(fastModeActive ? "enabled" : "disabled")}. {fastModeMessage}");
        }
        using var ocrDiagnostics = new OcrDiagnosticsWriter(Path.Combine(outputDir, "ocr_diagnostics.csv"));
        using var ocrShadowDataset = options.OcrShadowDataset
            ? new OcrShadowDatasetWriter(outputDir, profile.OrderedRoiKeys())
            : null;
        if (ocrShadowDataset is not null)
        {
            scanLog.Write($"OCR shadow dataset enabled. Csv={Path.Combine(outputDir, "ocr_shadow.csv")}");
        }
        using var fastOcrShadow = options.FastOcrShadow
            ? FastOcrShadowRecorder.TryCreate(outputDir, options.FastOcrTemplateIndexFile, profile.OrderedRoiKeys(), scanLog.Write)
            : null;
        if (fastOcrShadow is not null)
        {
            scanLog.Write($"Fast OCR shadow enabled. Index={fastOcrShadow.IndexFile}, Templates={fastOcrShadow.TemplateCount}, Csv={Path.Combine(outputDir, "ocr_fast_shadow.csv")}");
        }
        FastOcrAssistEngine? fastOcrAssist = null;
        FastOcrAssistRecorder? fastOcrAssistRecorder = null;
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
            using var window = GameWindow.Find(options.ProcessName);
            if (options.BringToFront)
            {
                window.BringToFront();
            }

            window.ConfigureCaptureMode(options.CaptureMode, scanLog.Write);

            Report(progress, counters, $"窗口客户区：{window.ClientScreenRect.Width} x {window.ClientScreenRect.Height}，DPI：{window.Dpi}，坐标倍率：{window.CoordinateScale:F2}");
            scanLog.Write($"Window client={window.ClientScreenRect}, dpi={window.Dpi}, scale={window.CoordinateScale:F2}, captureModeActive={window.ActiveCaptureMode}");
            var visualProfile = RuntimeVisualProfile.Create(
                options.ProcessName,
                options.VisualProfileId,
                options.VisualQualityLabel,
                options.VisualProfileClient,
                options.CaptureMode,
                window);
            visualProfile.ProfileRoutingDecision = options.ProfileRouting.ToString().ToLowerInvariant();
            visualProfile.Save(outputDir);
            scanLog.Write($"VISUAL_PROFILE_SELECTED id={visualProfile.ProfileId}, requested_label={visualProfile.RequestedProfileId}, detected_profile={visualProfile.DetectedProfileId}, detected_geometry={visualProfile.GeometryKey}, clientKind={visualProfile.ClientKind}, quality={visualProfile.QualityLabel}, size={visualProfile.ClientWidth}x{visualProfile.ClientHeight}, dpi={visualProfile.Dpi}, captureRequested={visualProfile.CaptureModeRequested}, captureActive={visualProfile.CaptureModeActive}, frameBackend={visualProfile.CaptureFrameBackend}, profileRouting={options.ProfileRouting}");
            if (!visualProfile.ProfileId.Equals(visualProfile.DetectedProfileId, StringComparison.OrdinalIgnoreCase))
            {
                scanLog.Write($"VISUAL_PROFILE_LABEL_GEOMETRY_MISMATCH requested_label={visualProfile.ProfileId}, detected_profile={visualProfile.DetectedProfileId}, detected_geometry={visualProfile.GeometryKey}");
            }

            if (options.FastOcrAssist)
            {
                fastOcrAssist = FastOcrAssistEngine.TryCreate(options.FastOcrTemplateIndexFile, profile.OrderedRoiKeys(), visualProfile.ProfileId, options.ProfileRouting, scanLog.Write);
                fastOcrAssistRecorder = fastOcrAssist?.CreateRecorder(outputDir);
                if (fastOcrAssist is not null)
                {
                    scanLog.Write($"Fast OCR assist enabled. Index={fastOcrAssist.IndexFile}, Templates={fastOcrAssist.TemplateCount}, VisualProfile={fastOcrAssist.VisualProfileId}, ProfileRouting={fastOcrAssist.ProfileRoutingMode}, Csv={Path.Combine(outputDir, "ocr_fast_assist.csv")}");
                }
            }

            using var inventoryRecognizer = new PaddleOcrRecognizer(AppPaths.ModelFile, AppPaths.CharacterDictFile, ocrIntraOpThreads);
            var ocrWorkers = StartOcrWorkers(queue, ocrResults, outputDir, options, scanLog, ocrWorkerCount, ocrIntraOpThreads, counters, ocrDiagnostics, ocrShadowDataset, fastOcrShadow, fastOcrAssist, fastOcrAssistRecorder);
            var resultConsumer = Task.Run(() => ConsumeOcrResults(ocrResults, results, counters, outputDir, progress, scanLog, duplicateGuard, options, linked));
            var captureCompleted = false;

            try
            {
                await PrepareBackpackAsync(window, profile, progress, counters, scanLog, linked.Token);
                monitor = Task.Run(() => MonitorBackpackAsync(window, profile, linked, progress, counters, scanLog), cancellationToken);
                var inventoryCount = TryReadInventoryCount(window, profile, inventoryRecognizer, scanLog);
                await ProduceCapturesAsync(window, profile, queue, options, runtimeState, counters, progress, scanLog, inventoryCount, traversalMode, linked.Token);
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
            fastOcrAssistRecorder?.Dispose();
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
            Queued = counters.Queued,
            Completed = counters.Completed,
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
        ScanRuntimeState runtimeState,
        Counters counters,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        int? inventoryCount,
        ScanTraversalMode traversalMode,
        CancellationToken token)
    {
        if (traversalMode == ScanTraversalMode.OverlapSignaturePage && inventoryCount is > 0)
        {
            await ProduceCapturesOverlapSignaturePageAsync(window, profile, queue, options, runtimeState, counters, progress, scanLog, inventoryCount.Value, token);
            return;
        }

        if (traversalMode == ScanTraversalMode.OverlapSignaturePage)
        {
            scanLog.Write("OverlapSignaturePage requires an inventory count. Inventory count OCR failed; refusing to fall back to LegacyThirdRow.");
            Report(progress, counters, "仓库数量 OCR 失败，重叠签名扫描无法计算总行数。");
            throw new InvalidOperationException("仓库数量 OCR 失败，重叠签名扫描无法计算总行数。请确认数量区域可见后重试，或手动选择旧版第3行兼容模式。");
        }

        if (traversalMode == ScanTraversalMode.SafeBandViewport && inventoryCount is > 0)
        {
            await ProduceCapturesSafeBandViewportAsync(window, profile, queue, options, runtimeState, counters, progress, scanLog, inventoryCount.Value, token);
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
            await ProduceCapturesCalibratedPageAsync(window, profile, queue, options, runtimeState, counters, progress, scanLog, inventoryCount.Value, token);
            return;
        }

        if (traversalMode == ScanTraversalMode.CalibratedPage)
        {
            scanLog.Write("CalibratedPage requires an inventory count. Inventory count OCR failed; refusing to fall back to LegacyThirdRow.");
            Report(progress, counters, "仓库数量 OCR 失败，校准翻页无法计算总行数。");
            throw new InvalidOperationException("仓库数量 OCR 失败，校准翻页无法计算总行数。请确认数量区域可见后重试，或手动选择旧版第3行兼容模式。");
        }

        await ProduceCapturesLegacyThirdRowAsync(window, profile, queue, options, runtimeState, counters, progress, scanLog, inventoryCount, token);
    }

    private static async Task ProduceCapturesOverlapSignaturePageAsync(
        GameWindow window,
        ScanProfile profile,
        BlockingCollection<DiscCapture> queue,
        ScanOptions options,
        ScanRuntimeState runtimeState,
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
        var scannedLogicalRows = new HashSet<int>();
        var pendingRowStartColumns = new Dictionary<int, int>();
        var deferredRowCounts = new Dictionary<int, int>();

        scanLog.Write($"Traversal: overlap-signature-page. inventoryCount={inventoryCount}, totalRows={totalRows}, visibleRows={visibleRows}, columns={columns}, lastRowColumns={lastRowColumns}, maxVisibleTop={maxVisibleTop}, listGrid={listGridRect}, panelProbe={panelChangeProbeRect}, scrollTickDelta={profile.ScrollTickDelta}.");
        await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);

        var visibleTopLogicalRow = 1;
        var lastScrollChangedViewport = false;
        var guardIterations = Math.Max(totalRows * 4, 16);
        for (var iteration = 1; scannedLogicalRows.Count < totalRows && iteration <= guardIterations; iteration++)
        {
            token.ThrowIfCancellationRequested();
            await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);
            var currentRows = CaptureRowSignatures(window, rowSignatureRects);
            LogOverlapViewport(scanLog, iteration, visibleTopLogicalRow, maxVisibleTop, totalRows, currentRows);

            var scannedThisViewport = false;
            foreach (var candidate in BuildOverlapScanCandidates(visibleTopLogicalRow, maxVisibleTop, totalRows, visibleRows, scannedLogicalRows))
            {
                token.ThrowIfCancellationRequested();
                var logicalRow = candidate.LogicalRow;
                var visualRow = candidate.VisualRow;
                var viewportState = ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop);
                var maxColumns = logicalRow == totalRows ? lastRowColumns : columns;
                var startColumn = pendingRowStartColumns.TryGetValue(logicalRow, out var pendingStartColumn)
                    ? Math.Clamp(pendingStartColumn, 1, maxColumns)
                    : 1;
                scanLog.WriteEvent("OVERLAP_ROW_CANDIDATE", $"iteration={iteration}, logicalRow={logicalRow}, visualRow={visualRow}, visibleTopLogicalRow={visibleTopLogicalRow}, state={viewportState}, rowHash={currentRows[Math.Clamp(visualRow - 1, 0, currentRows.Length - 1)].Hash:X16}, maxColumns={maxColumns}, startColumn={startColumn}");
                Report(progress, counters, $"重叠签名扫描：逻辑行 {logicalRow}/{totalRows}，视觉第 {visualRow} 行，{viewportState}。");

                RowScanResult rowResult;
                try
                {
                    rowResult = await ScanVisualRowAsync(
                        window,
                        profile,
                        queue,
                        options,
                        runtimeState,
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
                        enforceSafeBand: false,
                        visibleTopLogicalRow: visibleTopLogicalRow,
                        maxVisibleTop: maxVisibleTop,
                        afterScroll: lastScrollChangedViewport,
                        postScrollFirstCell: lastScrollChangedViewport,
                        startColumn: startColumn);
                }
                catch (PanelCellCaptureException ex) when (
                    options.OverlapConflictMode == OverlapConflictMode.Recover
                    && visualRow > 2
                    && logicalRow < totalRows
                    && deferredRowCounts.GetValueOrDefault(logicalRow) < 2)
                {
                    var resumeColumn = Math.Clamp(ex.Column, 1, maxColumns);
                    pendingRowStartColumns[logicalRow] = resumeColumn;
                    deferredRowCounts[logicalRow] = deferredRowCounts.GetValueOrDefault(logicalRow) + 1;
                    scannedThisViewport = true;
                    lastScrollChangedViewport = false;
                    scanLog.WriteEvent("OVERLAP_ROW_DEFERRED_AFTER_PANEL_TIMEOUT", $"iteration={iteration}, logicalRow={logicalRow}, visualRow={visualRow}, resumeColumn={resumeColumn}, maxColumns={maxColumns}, deferCount={deferredRowCounts[logicalRow]}, visibleTopLogicalRow={visibleTopLogicalRow}, state={viewportState}, reason={ex.InnerException?.Message ?? ex.Message}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
                    break;
                }

                lastScrollChangedViewport = false;
                if (rowResult == RowScanResult.Stop)
                {
                    return;
                }

                scannedLogicalRows.Add(logicalRow);
                pendingRowStartColumns.Remove(logicalRow);
                deferredRowCounts.Remove(logicalRow);
                scannedThisViewport = true;
                scanLog.WriteEvent("OVERLAP_ROW_SCANNED", $"iteration={iteration}, logicalRow={logicalRow}, visualRow={visualRow}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
            }

            if (scannedLogicalRows.Count >= totalRows)
            {
                break;
            }

            if (visibleTopLogicalRow >= maxVisibleTop)
            {
                var missing = Enumerable.Range(1, totalRows).Where(row => !scannedLogicalRows.Contains(row)).Take(8).ToArray();
                throw new InvalidOperationException($"重叠签名扫描到达底部但仍有逻辑行未扫：{string.Join(",", missing)}。为避免漏扫，本次停止。");
            }

            var beforeTop = visibleTopLogicalRow;
            var beforeScrollRows = CaptureRowSignatures(window, rowSignatureRects);
            var scroll = await ScrollSafeBandOneRowWithRetryAsync(
                window,
                profile,
                options.ScrollAcceptMode,
                listGridRect,
                rowSignatureRects,
                visibleTopLogicalRow,
                maxVisibleTop,
                scanLog,
                options.ScrollTickDelayOverrideMs,
                token);
            if (!scroll.Success)
            {
                throw new InvalidOperationException($"重叠签名扫描滚动失败：{scroll.Message}");
            }

            ImageSignature[] afterRows;
            if (scroll.AfterRows is not null)
            {
                afterRows = scroll.AfterRows;
            }
            else
            {
                await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);
                afterRows = CaptureRowSignatures(window, rowSignatureRects);
            }
            var signatureEvidence = EstimateVisibleTopAdvanceBySignatures(beforeScrollRows, afterRows);
            var signatureAdvanced = signatureEvidence.AcceptedRows;
            var scrollRowsAdvanced = Math.Clamp(Math.Max(1, scroll.RowsAdvanced), 1, visibleRows - 1);
            if (signatureAdvanced is > 0 && signatureAdvanced.Value != scrollRowsAdvanced)
            {
                scanLog.WriteEvent("OVERLAP_SCROLL_SIGNATURE_MISMATCH", $"iteration={iteration}, fromTop={beforeTop}, requestedToTop={beforeTop + 1}, scrollRows={scroll.RowsAdvanced}, signatureRows={signatureAdvanced.Value}, signatureBestScore={signatureEvidence.BestScore}, signatureSecondScore={signatureEvidence.SecondScore}, signatureMargin={signatureEvidence.Margin}, signatureAmbiguous={signatureEvidence.Ambiguous}, scannedThisViewport={scannedThisViewport}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
                var resolution = await ResolveOverlapScrollConflictAsync(
                    window,
                    profile,
                    listGridRect,
                    rowSignatureRects,
                    beforeScrollRows,
                    beforeTop,
                    scrollRowsAdvanced,
                    signatureEvidence,
                    options.OverlapConflictMode,
                    scannedLogicalRows,
                    totalRows,
                    visibleRows,
                    scanLog,
                    iteration,
                    scannedThisViewport,
                    token);
                if (!resolution.Success)
                {
                    scanLog.WriteEvent("OVERLAP_SCROLL_CONFLICT_HARD_STOP", $"iteration={iteration}, fromTop={beforeTop}, requestedToTop={beforeTop + 1}, scrollRows={scroll.RowsAdvanced}, signatureRows={signatureAdvanced.Value}, mode={options.OverlapConflictMode}, reason={resolution.Reason}, scannedThisViewport={scannedThisViewport}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
                    throw new InvalidOperationException($"重叠签名扫描检测到滚动估计与下一屏签名不一致：previousTop={beforeTop}, scrollRows={scroll.RowsAdvanced}, signatureRows={signatureAdvanced.Value}。为避免重复或漏扫，本次停止。");
                }

                afterRows = resolution.Rows ?? afterRows;
                scrollRowsAdvanced = resolution.RowsAdvanced;
                signatureEvidence = resolution.Evidence;
                signatureAdvanced = signatureEvidence.AcceptedRows;
            }

            if (scrollRowsAdvanced > 1)
            {
                var coverage = VerifyOverlapGapCoverage(scannedLogicalRows, beforeTop, scrollRowsAdvanced, visibleRows, totalRows);
                if (!coverage.Safe)
                {
                    scanLog.WriteEvent("OVERLAP_SCROLL_UNCONFIRMED_OVERSHOT", $"iteration={iteration}, fromTop={beforeTop}, requestedToTop={beforeTop + 1}, scrollRows={scroll.RowsAdvanced}, signatureRows={signatureAdvanced?.ToString() ?? "NA"}, rowsAdvanced={scrollRowsAdvanced}, missingRows={string.Join("|", coverage.MissingRows)}, scannedThisViewport={scannedThisViewport}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
                    throw new InvalidOperationException($"重叠签名扫描检测到未被下一屏签名确认的越行滚动：previousTop={beforeTop}, scrollRows={scroll.RowsAdvanced}, signatureRows={signatureAdvanced?.ToString() ?? "NA"}。为避免重复读取，本次停止。");
                }

                scanLog.WriteEvent("OVERLAP_SCROLL_TWO_ROW_COVERAGE_ACCEPTED", $"iteration={iteration}, fromTop={beforeTop}, toTop={Math.Min(maxVisibleTop, beforeTop + scrollRowsAdvanced)}, rowsAdvanced={scrollRowsAdvanced}, signatureRows={signatureAdvanced?.ToString() ?? "NA"}, signatureBestScore={signatureEvidence.BestScore}, signatureSecondScore={signatureEvidence.SecondScore}, signatureMargin={signatureEvidence.Margin}, coveredRows={string.Join("|", coverage.CoveredRows)}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
            }

            var rowsAdvanced = scrollRowsAdvanced;
            if (signatureAdvanced is > 0 && signatureAdvanced.Value == scrollRowsAdvanced)
            {
                rowsAdvanced = signatureAdvanced.Value;
            }
            visibleTopLogicalRow = Math.Min(maxVisibleTop, visibleTopLogicalRow + rowsAdvanced);
            lastScrollChangedViewport = rowsAdvanced > 0;
            scanLog.WriteEvent("OVERLAP_SCROLL_ACCEPTED", $"iteration={iteration}, fromTop={beforeTop}, toTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, scrollRows={scroll.RowsAdvanced}, signatureRows={signatureAdvanced?.ToString() ?? "NA"}, signatureBestRows={signatureEvidence.BestRows}, signatureBestScore={signatureEvidence.BestScore}, signatureSecondScore={signatureEvidence.SecondScore}, signatureMargin={signatureEvidence.Margin}, signatureAmbiguous={signatureEvidence.Ambiguous}, scannedThisViewport={scannedThisViewport}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
            runtimeState.ProfileHealth.ObserveOverlap(signatureEvidence.Ambiguous, scanLog.Write);
        }

        if (scannedLogicalRows.Count < totalRows)
        {
            var missing = Enumerable.Range(1, totalRows).Where(row => !scannedLogicalRows.Contains(row)).Take(8).ToArray();
            throw new InvalidOperationException($"重叠签名扫描达到保护上限仍未完成：scannedRows={scannedLogicalRows.Count}/{totalRows}, missing={string.Join(",", missing)}。");
        }

        scanLog.Write($"End: overlap-signature-page completed. visited={counters.Visited}, expectedInventory={inventoryCount}, queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}.");
        Report(progress, counters, $"已完成重叠签名扫描：访问 {counters.Visited}/{inventoryCount}。");
    }

    private static async Task ProduceCapturesSafeBandViewportAsync(
        GameWindow window,
        ScanProfile profile,
        BlockingCollection<DiscCapture> queue,
        ScanOptions options,
        ScanRuntimeState runtimeState,
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
            var moveResult = await MoveLogicalRowIntoSafeBandAsync(
                window,
                profile,
                options.ScrollAcceptMode,
                listGridRect,
                rowSignatureRects,
                visibleTopLogicalRow,
                maxVisibleTop,
                totalRows,
                logicalRow,
                scanLog,
                options.ScrollTickDelayOverrideMs,
                token);
            visibleTopLogicalRow = moveResult.VisibleTopLogicalRow;

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
                runtimeState,
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
                maxVisibleTop: maxVisibleTop,
                afterScroll: moveResult.Scrolled,
                postScrollFirstCell: moveResult.Scrolled
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
        ScanRuntimeState runtimeState,
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
                    runtimeState,
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
        ScanRuntimeState runtimeState,
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
                runtimeState,
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
        ScanRuntimeState runtimeState,
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
        int maxVisibleTop = 1,
        bool afterScroll = false,
        bool postScrollFirstCell = false,
        int startColumn = 1)
    {
        var currentY = offset.Y + step.Y * row;
        ImageSignature[]? previousPanelSignatures = counters.Queued > 0
            ? CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois)
            : null;
        startColumn = Math.Clamp(startColumn, 1, maxColumns);
        if (startColumn > 1)
        {
            var refreshColumn = startColumn - 1;
            var refresh = new PointF(offset.X + step.X * refreshColumn, currentY);
            var refreshPoint = window.ToScreenPoint(refresh);
            scanLog.WriteEvent("ROW_RESUME_SELECTION_REFRESH", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, startColumn={startColumn}, refreshColumn={refreshColumn}, point={refreshPoint}, visibleTopLogicalRow={visibleTopLogicalRow}, state={ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop)}");
            window.MoveCursor(refreshPoint);
            window.LeftClickCurrent();
            await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
            previousPanelSignatures = CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois);
        }

        var postScrollFirstCellPending = postScrollFirstCell;
        for (var col = startColumn; col <= maxColumns; col++)
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

            var ocrBacklogBeforeClick = CurrentOcrBacklog(counters);
            var throttleDecision = runtimeState.OcrThrottle?.Observe(ocrBacklogBeforeClick);
            var adaptiveThrottleMs = throttleDecision?.DelayMilliseconds ?? 0;
            if (throttleDecision?.Changed == true)
            {
                scanLog.WriteEvent("ADAPTIVE_OCR_THROTTLE", $"backlog={ocrBacklogBeforeClick}, delayMs={adaptiveThrottleMs}, highThreshold={throttleDecision.HighBacklogThreshold}, lowThreshold={throttleDecision.LowBacklogThreshold}");
            }

            if (adaptiveThrottleMs > 0)
            {
                await Task.Delay(adaptiveThrottleMs, token);
            }

            var sw = Stopwatch.StartNew();
            var currentPostScrollFirstCell = postScrollFirstCellPending;
            var current = new PointF(offset.X + step.X * col, currentY);
            var clickPoint = window.ToScreenPoint(current);
            var viewportKnown = logicalRow.HasValue && maxVisibleTop > 1;
            var visibleTopText = viewportKnown ? visibleTopLogicalRow.ToString() : "unknown";
            var viewportStateText = viewportKnown ? ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop) : "unknown";
            scanLog.WriteEvent("CELL_MOVE", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, point={clickPoint}, normalized=({current.X:F5},{current.Y:F5}), queued={counters.Queued}, visited={counters.Visited}");
            window.MoveCursor(clickPoint);

            var rarityProbeWatch = Stopwatch.StartNew();
            var rarityProbe = DetectRarityAround(window, profile, clickPoint);
            if (rarityProbe.Rarity is null)
            {
                await Task.Delay(25, token);
                rarityProbe = DetectRarityAround(window, profile, clickPoint);
            }
            rarityProbeWatch.Stop();

            var rarity = rarityProbe.Rarity;
            if (rarity is null || options.ShowDebugImages || counters.Visited % 50 == 0)
            {
                scanLog.Write($"Probe pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, point={clickPoint}, rarity={rarity ?? "null"}, best={ColorText(rarityProbe.BestColor)}, bestMatch={rarityProbe.BestCandidate}, delta={rarityProbe.BestDelta}, fullScan={rarityProbe.FullScan}, bottom={isBottom}");
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

            scanLog.WriteEvent("CELL_CLICK", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, point={clickPoint}");
            var selectionProbeRect = SelectionProbeRect(window, clickPoint);
            var selectionProbeWatch = Stopwatch.StartNew();
            var beforeSelectionSignature = CaptureScreenSignature(selectionProbeRect);
            selectionProbeWatch.Stop();
            window.LeftClickCurrent();
            var sceneAdaptivePanelFloorEligible = !afterScroll && !currentPostScrollFirstCell && row != 2;
            System.Drawing.Point? selectionRefreshPoint = null;
            var refreshCol = col > 1
                ? col - 1
                : col < maxColumns
                    ? col + 1
                    : 0;
            if (refreshCol > 0)
            {
                var refresh = new PointF(offset.X + step.X * refreshCol, currentY);
                selectionRefreshPoint = window.ToScreenPoint(refresh);
            }

            using var panelCapture = await CaptureStablePanelWithRetryAsync(
                window,
                profile,
                panelRect,
                rois,
                statOffset,
                statRowBackground,
                panelChangeProbeRect,
                previousPanelSignatures,
                selectionProbeRect,
                beforeSelectionSignature,
                runtimeState,
                scanLog,
                clickPoint,
                selectionRefreshPoint,
                pass,
                row,
                col,
                maxColumns,
                logicalRow,
                visibleTopText,
                viewportStateText,
                token,
                currentPostScrollFirstCell,
                sceneAdaptivePanelFloorEligible);
            var panelImage = panelCapture.TakeImage();
            var captureRois = rois.Take(panelCapture.VisibleRoiCount).ToArray();
            var debugImage = options.ShowDebugImages ? (Bitmap)panelImage.Clone() : null;
            previousPanelSignatures = panelCapture.ProbeSignatures;
            runtimeState.AdaptiveTiming?.ObservePanel(
                panelCapture.ChangeMilliseconds,
                panelCapture.FullRoiMilliseconds,
                panelCapture.StableMilliseconds,
                panelCapture.WaitMilliseconds,
                panelCapture.CaptureMilliseconds,
                panelCapture.FrameLoopMilliseconds,
                currentPostScrollFirstCell);
            runtimeState.ProfileHealth.ObservePanel(panelCapture.WaitMilliseconds, panelCapture.CaptureMilliseconds, scanLog.Write);
            runtimeState.PanelStability.Observe(
                panelCapture.StableMilliseconds,
                panelCapture.PanelTextStableMilliseconds,
                panelCapture.PanelStableSource,
                panelCapture.PanelStabilityReason);
            runtimeState.PanelProbeHealth.Observe(
                panelCapture.ChangeMilliseconds,
                panelCapture.PanelAcceptMode,
                scanLog.Write);
            var ocrBacklogBeforeEnqueue = CurrentOcrBacklog(counters);
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

            scanLog.WriteEvent("CELL_TIMING", $"index={itemIndex}, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, afterScroll={afterScroll}, postScrollFirstCell={currentPostScrollFirstCell}, panelWaitMs={panelCapture.WaitMilliseconds:F1}, enqueueWaitMs={enqueueWait.Elapsed.TotalMilliseconds:F1}, fallback={panelCapture.UsedFallback}, visibleRois={panelCapture.VisibleRoiCount}/{rois.Length}, totalMs={sw.ElapsedMilliseconds}, panelFrames={panelCapture.FrameCount}, changeMs={FormatOptionalMs(panelCapture.ChangeMilliseconds)}, selectionChangeMs={FormatOptionalMs(panelCapture.SelectionChangeMilliseconds)}, fullRoiMs={FormatOptionalMs(panelCapture.FullRoiMilliseconds)}, stableMs={FormatOptionalMs(panelCapture.StableMilliseconds)}, panelTextStableMs={FormatOptionalMs(panelCapture.PanelTextStableMilliseconds)}, panelStableSource={panelCapture.PanelStableSource}, panelStabilityReason={panelCapture.PanelStabilityReason}, rarityProbeMs={rarityProbeWatch.Elapsed.TotalMilliseconds:F1}, selectionProbeMs={selectionProbeWatch.Elapsed.TotalMilliseconds:F1}, captureMs={panelCapture.CaptureMilliseconds:F1}, signatureMs={panelCapture.SignatureMilliseconds:F1}, visibleRoiMs={panelCapture.VisibleRoiMilliseconds:F1}, frameLoopMs={panelCapture.FrameLoopMilliseconds:F1}, frameToBitmapMs={panelCapture.FrameToBitmapMilliseconds:F1}, bitmapCreatedCount={panelCapture.BitmapCreatedCount}, quickAccept={panelCapture.QuickAccept}, quickRejectReason={panelCapture.QuickRejectReason}, adaptiveThrottleMs={adaptiveThrottleMs}, ocrBacklogBeforeEnqueue={ocrBacklogBeforeEnqueue}, adaptivePanelMinMs={panelCapture.MinimumAcceptMilliseconds}, adaptivePanelSamples={panelCapture.AdaptiveSampleCount}, adaptivePanelReason={panelCapture.AdaptiveReason}, panelAcceptMode={panelCapture.PanelAcceptMode}, postScrollAcceptMode={panelCapture.PostScrollPanelAcceptMode}, panelMinFloorMs={panelCapture.PanelMinAcceptFloorMs}, roiCompleteFrames={panelCapture.RoiCompleteFrames}, selectedStableFrames={panelCapture.SelectedStableFrames}, acceptGateReason={panelCapture.AcceptGateReason}, panelFloorMode={panelCapture.PanelFloorMode}, sameRowPanelFloorMs={panelCapture.SameRowPanelFloorMs}, postScrollPanelFloorMs={panelCapture.PostScrollPanelFloorMs}, panelFloorReason={panelCapture.PanelFloorReason}, floorWaitLimitedMs={panelCapture.FloorWaitLimitedMilliseconds:F1}, panelAcceptElapsedVsFloorMs={panelCapture.PanelAcceptElapsedVsFloorMilliseconds:F1}, scrollTickDelayMs={runtimeState.EffectiveScrollTickDelayMs}, accept={panelCapture.AcceptReason}");
            postScrollFirstCellPending = false;
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

        return options.HighSpeedOcr ? Math.Min(2, Math.Max(1, Environment.ProcessorCount / 4)) : 1;
    }

    private static int ResolveOcrIntraOpThreads(ScanOptions options, int workerCount)
    {
        var requested = Math.Clamp(options.OcrIntraOpThreads, 1, 8);
        if (options.HighSpeedOcr && workerCount <= 1)
        {
            return Math.Clamp(Math.Max(requested, Environment.ProcessorCount / 2), 1, 8);
        }

        if (workerCount <= 1)
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

    private static async Task<SafeBandMoveResult> MoveLogicalRowIntoSafeBandAsync(
        GameWindow window,
        ScanProfile profile,
        ScrollAcceptMode scrollAcceptMode,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        int totalRows,
        int logicalRow,
        ScanLog scanLog,
        int scrollTickDelayOverrideMs,
        CancellationToken token)
    {
        var desiredTop = ChooseSafeVisibleTop(logicalRow, totalRows, maxVisibleTop);
        var scrolled = false;
        while (visibleTopLogicalRow < desiredTop)
        {
            var result = await ScrollSafeBandOneRowWithRetryAsync(
                window,
                profile,
                scrollAcceptMode,
                listGridRect,
                rowSignatureRects,
                visibleTopLogicalRow,
                maxVisibleTop,
                scanLog,
                scrollTickDelayOverrideMs,
                token);
            if (!result.Success)
            {
                throw new InvalidOperationException($"安全带逐行滚动失败：{result.Message}");
            }

            var rowsAdvanced = Math.Max(1, result.RowsAdvanced);
            var nextVisibleTop = Math.Min(maxVisibleTop, visibleTopLogicalRow + rowsAdvanced);
            if (rowsAdvanced != 1)
            {
                scanLog.Write($"Safe-band viewport detected wheel overshoot: previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, nextTop={nextVisibleTop}, desiredTop={desiredTop}.");
                if (nextVisibleTop > desiredTop)
                {
                    if (!profile.AllowScrollRecovery)
                    {
                        scanLog.WriteEvent("ROW_SCROLL_STRICT_STOP", $"previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, nextTop={nextVisibleTop}, desiredTop={desiredTop}, recoveryAllowed={profile.AllowScrollRecovery}");
                        throw new InvalidOperationException($"安全带逐行滚动越过目标行：previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, nextTop={nextVisibleTop}, desiredTop={desiredTop}。当前为单向严格模式，已禁止自动上翻恢复；请降低 scrollTickDelta 或增大 scrollTickDelayMs 后重试。");
                    }

                    var recovery = await ScrollSafeBandOneRowUpAsync(
                        window,
                        profile,
                        listGridRect,
                        rowSignatureRects,
                        nextVisibleTop,
                        scanLog,
                        scrollTickDelayOverrideMs,
                        token);
                    if (!recovery.Success)
                    {
                        throw new InvalidOperationException($"安全带逐行滚动越过目标行且回退失败：previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, nextTop={nextVisibleTop}, desiredTop={desiredTop}, recovery={recovery.Message}。为避免读取视觉第2行导致重复，本次停止。");
                    }

                    var rowsRecovered = Math.Max(1, recovery.RowsAdvanced);
                    nextVisibleTop = Math.Max(1, nextVisibleTop - rowsRecovered);
                    if (nextVisibleTop > desiredTop)
                    {
                        throw new InvalidOperationException($"安全带逐行滚动越过目标行且回退后仍越过目标行：previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, rowsRecovered={rowsRecovered}, recoveredTop={nextVisibleTop}, desiredTop={desiredTop}。为避免读取视觉第2行导致重复，本次停止。");
                    }

                    scanLog.WriteEvent("ROW_SCROLL_RECOVERY_ACCEPTED", $"previousTop={visibleTopLogicalRow}, desiredTop={desiredTop}, recoveredTop={nextVisibleTop}, rowsAdvanced={rowsAdvanced}, rowsRecovered={rowsRecovered}");
                }
            }

            visibleTopLogicalRow = nextVisibleTop;
            scrolled = true;
        }

        var visualRow = logicalRow - visibleTopLogicalRow + 1;
        if (!IsSafeBandClick(visualRow, visibleTopLogicalRow, maxVisibleTop, logicalRow))
        {
            scanLog.WriteEvent("EDGE_CLICK_BLOCKED", $"logicalRow={logicalRow}, visualRow={visualRow}, visibleTopLogicalRow={visibleTopLogicalRow}, maxVisibleTop={maxVisibleTop}, state={ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop)}");
            throw new InvalidOperationException($"安全带保护阻止点击：逻辑行 {logicalRow} 当前位于视觉第 {visualRow} 行。");
        }

        return new SafeBandMoveResult(visibleTopLogicalRow, scrolled);
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
        if (visualRow == 3)
        {
            return true;
        }

        var atTop = visibleTopLogicalRow == 1;
        var atBottom = visibleTopLogicalRow == maxVisibleTop;
        return (visualRow == 1 && atTop && logicalRow == 1)
            || (visualRow == 2 && atTop && logicalRow == 2)
            || (visualRow == 4 && atBottom);
    }

    private static IEnumerable<OverlapRowCandidate> BuildOverlapScanCandidates(
        int visibleTopLogicalRow,
        int maxVisibleTop,
        int totalRows,
        int visibleRows,
        ISet<int> scannedLogicalRows)
    {
        var atTop = visibleTopLogicalRow == 1;
        var atBottom = visibleTopLogicalRow == maxVisibleTop;
        var preferredVisualRows = atTop
            ? new[] { 1, 2, 3 }
            : atBottom
                ? new[] { 2, 3, 4 }
                : new[] { 2, 3 };

        foreach (var visualRow in preferredVisualRows)
        {
            if (visualRow < 1 || visualRow > visibleRows)
            {
                continue;
            }

            var logicalRow = visibleTopLogicalRow + visualRow - 1;
            if (logicalRow < 1 || logicalRow > totalRows || scannedLogicalRows.Contains(logicalRow))
            {
                continue;
            }

            yield return new OverlapRowCandidate(logicalRow, visualRow);
        }
    }

    private static RowAdvanceEvidence EstimateVisibleTopAdvanceBySignatures(
        IReadOnlyList<ImageSignature> beforeRows,
        IReadOnlyList<ImageSignature> afterRows)
    {
        var bestRows = 0;
        var bestScore = int.MaxValue;
        var secondScore = int.MaxValue;
        var scores = new int[3];
        for (var rows = 0; rows <= 2; rows++)
        {
            var score = AverageSignatureDistanceForShift(beforeRows, afterRows, rows);
            scores[rows] = score;
            if (score < bestScore)
            {
                secondScore = bestScore;
                bestScore = score;
                bestRows = rows;
            }
            else if (score < secondScore)
            {
                secondScore = score;
            }
        }

        var margin = secondScore == int.MaxValue ? 0 : secondScore - bestScore;
        var acceptedRows = bestRows > 0
            && bestScore <= OverlapSignatureMatchTolerance
            && margin >= OverlapSignatureClearMargin
            ? bestRows
            : (int?)null;
        var ambiguous = bestRows > 0
            && bestScore <= OverlapSignatureMatchTolerance
            && margin < OverlapSignatureClearMargin;
        var strongRows = bestRows > 0
            && bestScore <= OverlapSignatureMatchTolerance
            && margin >= OverlapSignatureStrongMargin
            ? bestRows
            : (int?)null;
        return new RowAdvanceEvidence(bestRows, bestScore, secondScore, margin, acceptedRows, strongRows, ambiguous, scores[0], scores[1], scores[2]);
    }

    private static async Task<OverlapConflictResolution> ResolveOverlapScrollConflictAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        IReadOnlyList<ImageSignature> beforeScrollRows,
        int beforeTop,
        int scrollRowsAdvanced,
        RowAdvanceEvidence initialEvidence,
        OverlapConflictMode mode,
        IReadOnlySet<int> scannedLogicalRows,
        int totalRows,
        int visibleRows,
        ScanLog scanLog,
        int iteration,
        bool scannedThisViewport,
        CancellationToken token)
    {
        if (mode == OverlapConflictMode.Strict)
        {
            return OverlapConflictResolution.Fail("strict_mode");
        }

        RowAdvanceEvidence latestEvidence = initialEvidence;
        ImageSignature[]? latestRows = null;
        var strongTwoRowFrames = initialEvidence.StrongRows == 2 ? 1 : 0;
        var ambiguousTwoRowFrames = initialEvidence.BestRows == 2 && initialEvidence.Ambiguous ? 1 : 0;
        var pollMs = Math.Max(10, profile.LoadPollMs);
        for (var frame = 1; frame <= OverlapSignatureRecheckFrames; frame++)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(pollMs, token);
            latestRows = CaptureRowSignatures(window, rowSignatureRects);
            latestEvidence = EstimateVisibleTopAdvanceBySignatures(beforeScrollRows, latestRows);
            scanLog.WriteEvent("OVERLAP_SCROLL_SIGNATURE_RECHECK_FRAME", $"iteration={iteration}, frame={frame}/{OverlapSignatureRecheckFrames}, fromTop={beforeTop}, scrollRows={scrollRowsAdvanced}, bestRows={latestEvidence.BestRows}, acceptedRows={latestEvidence.AcceptedRows?.ToString() ?? "NA"}, strongRows={latestEvidence.StrongRows?.ToString() ?? "NA"}, bestScore={latestEvidence.BestScore}, secondScore={latestEvidence.SecondScore}, margin={latestEvidence.Margin}, ambiguous={latestEvidence.Ambiguous}, scores=0:{latestEvidence.NoMoveScore}|1:{latestEvidence.OneRowScore}|2:{latestEvidence.TwoRowScore}, scannedThisViewport={scannedThisViewport}, scannedRows={scannedLogicalRows.Count}/{totalRows}");

            if (latestEvidence.AcceptedRows == scrollRowsAdvanced)
            {
                scanLog.WriteEvent("OVERLAP_SCROLL_SIGNATURE_RECHECK_RECOVERED", $"iteration={iteration}, fromTop={beforeTop}, rowsAdvanced={scrollRowsAdvanced}, frame={frame}, bestScore={latestEvidence.BestScore}, secondScore={latestEvidence.SecondScore}, margin={latestEvidence.Margin}");
                return OverlapConflictResolution.Ok(scrollRowsAdvanced, latestEvidence, latestRows, "recheck_matched_scroll");
            }

            if (latestEvidence.StrongRows == 2)
            {
                strongTwoRowFrames++;
            }

            if (latestEvidence.BestRows == 2 && latestEvidence.Ambiguous)
            {
                ambiguousTwoRowFrames++;
            }
        }

        if (mode == OverlapConflictMode.Recover && strongTwoRowFrames >= 2)
        {
            var coverage = VerifyOverlapGapCoverage(scannedLogicalRows, beforeTop, rowsAdvanced: 2, visibleRows, totalRows);
            if (coverage.Safe)
            {
                scanLog.WriteEvent("OVERLAP_SCROLL_SIGNATURE_TWO_ROW_RECOVERED", $"iteration={iteration}, fromTop={beforeTop}, toTop={Math.Min(beforeTop + 2, Math.Max(1, totalRows - visibleRows + 1))}, rowsAdvanced=2, strongFrames={strongTwoRowFrames}, bestScore={latestEvidence.BestScore}, secondScore={latestEvidence.SecondScore}, margin={latestEvidence.Margin}, coveredRows={string.Join("|", coverage.CoveredRows)}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
                var acceptedEvidence = latestEvidence with { AcceptedRows = 2, StrongRows = 2, Ambiguous = false };
                return OverlapConflictResolution.Ok(2, acceptedEvidence, latestRows, "strong_two_row_coverage");
            }

            return OverlapConflictResolution.Fail($"strong_two_row_missing_coverage={string.Join("|", coverage.MissingRows)}");
        }

        if (mode == OverlapConflictMode.Recover && scrollRowsAdvanced == 1)
        {
            scanLog.WriteEvent("OVERLAP_SCROLL_SIGNATURE_AMBIGUOUS_ACCEPTED", $"iteration={iteration}, fromTop={beforeTop}, rowsAdvanced={scrollRowsAdvanced}, bestRows={latestEvidence.BestRows}, bestScore={latestEvidence.BestScore}, secondScore={latestEvidence.SecondScore}, margin={latestEvidence.Margin}, ambiguousFrames={ambiguousTwoRowFrames}, strongTwoRowFrames={strongTwoRowFrames}, reason=no_confirmed_two_row, scannedRows={scannedLogicalRows.Count}/{totalRows}");
            var acceptedEvidence = latestEvidence with { AcceptedRows = scrollRowsAdvanced, StrongRows = null, Ambiguous = true };
            return OverlapConflictResolution.Ok(scrollRowsAdvanced, acceptedEvidence, latestRows, "unconfirmed_signature_accepted_as_scroll");
        }

        return OverlapConflictResolution.Fail($"unresolved bestRows={latestEvidence.BestRows}, acceptedRows={latestEvidence.AcceptedRows?.ToString() ?? "NA"}, strongRows={latestEvidence.StrongRows?.ToString() ?? "NA"}, margin={latestEvidence.Margin}");
    }

    private static OverlapGapCoverage VerifyOverlapGapCoverage(
        IReadOnlySet<int> scannedLogicalRows,
        int beforeTop,
        int rowsAdvanced,
        int visibleRows,
        int totalRows)
    {
        if (rowsAdvanced <= 1)
        {
            return new OverlapGapCoverage(true, [], []);
        }

        var leavingRows = Enumerable.Range(beforeTop, Math.Min(rowsAdvanced, visibleRows))
            .Where(row => row >= 1 && row <= totalRows)
            .ToArray();
        var missingRows = leavingRows
            .Where(row => !scannedLogicalRows.Contains(row))
            .ToArray();
        return new OverlapGapCoverage(missingRows.Length == 0, leavingRows, missingRows);
    }

    private static int AverageSignatureDistanceForShift(
        IReadOnlyList<ImageSignature> beforeRows,
        IReadOnlyList<ImageSignature> afterRows,
        int rowsAdvanced)
    {
        var sum = 0;
        var count = 0;
        for (var beforeIndex = rowsAdvanced; beforeIndex < beforeRows.Count; beforeIndex++)
        {
            var afterIndex = beforeIndex - rowsAdvanced;
            if (afterIndex < 0 || afterIndex >= afterRows.Count)
            {
                continue;
            }

            sum += SignatureDistance(beforeRows[beforeIndex], afterRows[afterIndex]);
            count++;
        }

        return count == 0 ? int.MaxValue : sum / count;
    }

    private static void LogOverlapViewport(
        ScanLog scanLog,
        int iteration,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        int totalRows,
        IReadOnlyList<ImageSignature> rowSignatures)
    {
        var state = ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop);
        var rows = string.Join(",", rowSignatures.Select((signature, index) =>
        {
            var visualRow = index + 1;
            var logicalRow = visibleTopLogicalRow + index;
            var inRange = logicalRow <= totalRows;
            return $"v{visualRow}:l{(inRange ? logicalRow.ToString() : "NA")}:{signature.Hash:X16}";
        }));
        scanLog.WriteEvent("OVERLAP_VIEWPORT", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, state={state}, rows=[{rows}]");
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
        ScrollAcceptMode scrollAcceptMode,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        ScanLog scanLog,
        int scrollTickDelayOverrideMs,
        CancellationToken token)
    {
        var delay = Math.Max(120, EffectiveScrollTickDelay(profile, scrollTickDelayOverrideMs) * 2);
        ScrollRowsResult last = default;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            last = await ScrollSafeBandOneRowAsync(window, profile, scrollAcceptMode, listGridRect, rowSignatureRects, visibleTopLogicalRow, maxVisibleTop, scanLog, scrollTickDelayOverrideMs, token);
            if (last.Success)
            {
                return last;
            }

            if (!last.Retryable)
            {
                scanLog.Write($"Safe-band scroll stopped without retry: visibleTopLogicalRow={visibleTopLogicalRow}, failure={last.Message}");
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
        ScrollAcceptMode scrollAcceptMode,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        ScanLog scanLog,
        int scrollTickDelayOverrideMs,
        CancellationToken token)
    {
        if (visibleTopLogicalRow >= maxVisibleTop)
        {
            return ScrollRowsResult.Fail($"already at bottom visibleTopLogicalRow={visibleTopLogicalRow}");
        }

        var wheelPoint = window.ToScreenPoint(profile.Point("listWheelArea"));
        var neutralPoint = ListNeutralPoint(window, listGridRect);
        window.MoveCursor(neutralPoint);
        var beforeViewport = CaptureViewportSignatures(window, listGridRect, rowSignatureRects);
        var beforeGrid = beforeViewport.Grid;
        var beforeRows = beforeViewport.Rows;
        var maxTicks = ScrollSmallTickLimit(profile);
        var effectiveScrollTickDelayMs = EffectiveScrollTickDelay(profile, scrollTickDelayOverrideMs);
        scanLog.WriteEvent("ROW_SCROLL_START", $"fromTop={visibleTopLogicalRow}, toTop={visibleTopLogicalRow + 1}, maxVisibleTop={maxVisibleTop}, delta={profile.ScrollTickDelta}, maxTicks={maxTicks}, scrollTickDelayMs={effectiveScrollTickDelayMs}, wheelPoint={wheelPoint}, neutralPoint={neutralPoint}");
        for (var tick = 1; tick <= maxTicks; tick++)
        {
            scanLog.WriteEvent("ROW_SCROLL_TICK", $"fromTop={visibleTopLogicalRow}, toTop={visibleTopLogicalRow + 1}, tick={tick}/{maxTicks}, delta={profile.ScrollTickDelta}");
            window.MoveCursor(wheelPoint);
            window.MouseWheel(profile.ScrollTickDelta);
            window.MoveCursor(neutralPoint);
            var scrollTickWait = Stopwatch.StartNew();
            await Task.Delay(effectiveScrollTickDelayMs, token);
            scrollTickWait.Stop();

            var viewportSignature = Stopwatch.StartNew();
            var settled = await WaitForOneRowDownSettledAsync(window, profile, scrollAcceptMode, listGridRect, rowSignatureRects, beforeGrid, beforeRows, token);
            viewportSignature.Stop();
            var movedDistance = settled.MovedDistance;
            var verification = settled.Verification;
            var estimatedRows = settled.EstimatedRows;
            var twoRowsClearlyBetter = estimatedRows == 2;
            var oneRowMatched = verification.OneRowScore <= RowShiftMatchTolerance
                || verification.OneRowScore <= verification.NoMoveScore + RowShiftClearMargin
                || verification.OneRowScore <= verification.TwoRowScore + RowShiftClearMargin;
            var success = settled.Success;
            scanLog.WriteEvent("ROW_SCROLL_TIMING", $"fromTop={visibleTopLogicalRow}, toTop={visibleTopLogicalRow + 1}, tick={tick}/{maxTicks}, scroll_tick_wait_ms={scrollTickWait.Elapsed.TotalMilliseconds:F1}, list_stable_ms={settled.WaitMilliseconds:F1}, row_signature_ms={settled.SignatureMilliseconds:F1}, post_scroll_viewport_ms={viewportSignature.Elapsed.TotalMilliseconds:F1}");
            scanLog.WriteEvent("ROW_SCROLL_VERIFY", $"fromTop={visibleTopLogicalRow}, toTop={visibleTopLogicalRow + 1}, tick={tick}/{maxTicks}, movedDistance={movedDistance}, oneRowScore={verification.OneRowScore}, noMoveScore={verification.NoMoveScore}, twoRowScore={verification.TwoRowScore}, oneRowMatched={oneRowMatched}, twoRowsClearlyBetter={twoRowsClearlyBetter}, estimatedRows={estimatedRows}, success={success}");

            if (success)
            {
                scanLog.WriteEvent("ROW_SCROLL_DONE", $"fromTop={visibleTopLogicalRow}, estimatedToTop={visibleTopLogicalRow + 1}, requestedToTop={visibleTopLogicalRow + 1}, rowsAdvanced=1, movedDistance={movedDistance}, oneRowScore={verification.OneRowScore}, twoRowScore={verification.TwoRowScore}, ticks={tick}");
                return ScrollRowsResult.Ok($"safe-band advanced 1 row from {visibleTopLogicalRow}", 1, settled.AfterViewport.Rows);
            }

            if (estimatedRows > 1)
            {
                scanLog.WriteEvent("ROW_SCROLL_OVERSHOT_BLOCKED", $"fromTop={visibleTopLogicalRow}, estimatedToTop={Math.Min(maxVisibleTop, visibleTopLogicalRow + estimatedRows)}, requestedToTop={visibleTopLogicalRow + 1}, rowsAdvanced={estimatedRows}, movedDistance={movedDistance}, oneRowScore={verification.OneRowScore}, twoRowScore={verification.TwoRowScore}, ticks={tick}");
                return ScrollRowsResult.Fail($"scroll advanced {estimatedRows} rows while only one row was requested, movedDistance={movedDistance}, oneRowScore={verification.OneRowScore}, twoRowScore={verification.TwoRowScore}", retryable: false);
            }

            if (movedDistance > RowShiftAmbiguousMovementTolerance)
            {
                scanLog.WriteEvent("ROW_SCROLL_AMBIGUOUS", $"fromTop={visibleTopLogicalRow}, expectedToTop={visibleTopLogicalRow + 1}, tick={tick}/{maxTicks}, movedDistance={movedDistance}, oneRowScore={verification.OneRowScore}, noMoveScore={verification.NoMoveScore}, twoRowScore={verification.TwoRowScore}, action=continue_downward");
            }
        }

        await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);
        var finalViewport = CaptureViewportSignatures(window, listGridRect, rowSignatureRects);
        var finalGrid = finalViewport.Grid;
        var finalRows = finalViewport.Rows;
        var finalMovedDistance = SignatureDistance(beforeGrid, finalGrid);
        var finalVerification = VerifyOneRowDown(beforeRows, finalRows);
        scanLog.WriteEvent("ROW_SCROLL_FAIL", $"fromTop={visibleTopLogicalRow}, expectedToTop={visibleTopLogicalRow + 1}, ticks={maxTicks}, movedDistance={finalMovedDistance}, oneRowScore={finalVerification.OneRowScore}, noMoveScore={finalVerification.NoMoveScore}, twoRowScore={finalVerification.TwoRowScore}");
        return ScrollRowsResult.Fail($"one-row verification failed after {maxTicks} tick(s), movedDistance={finalMovedDistance}, oneRowScore={finalVerification.OneRowScore}, twoRowScore={finalVerification.TwoRowScore}");
    }

    private static async Task<ScrollRowsResult> ScrollSafeBandOneRowUpAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        int visibleTopLogicalRow,
        ScanLog scanLog,
        int scrollTickDelayOverrideMs,
        CancellationToken token)
    {
        if (visibleTopLogicalRow <= 1)
        {
            return ScrollRowsResult.Fail($"already at top visibleTopLogicalRow={visibleTopLogicalRow}");
        }

        var delta = -profile.ScrollTickDelta;
        var maxTicks = ScrollSmallTickLimit(profile);
        window.MoveCursor(window.ToScreenPoint(profile.Point("listWheelArea")));
        var beforeViewport = CaptureViewportSignatures(window, listGridRect, rowSignatureRects);
        var beforeGrid = beforeViewport.Grid;
        var beforeRows = beforeViewport.Rows;
        scanLog.WriteEvent("ROW_SCROLL_RECOVERY_START", $"fromTop={visibleTopLogicalRow}, toTop={visibleTopLogicalRow - 1}, delta={delta}, maxTicks={maxTicks}");
        for (var tick = 1; tick <= maxTicks; tick++)
        {
            scanLog.WriteEvent("ROW_SCROLL_RECOVERY_TICK", $"fromTop={visibleTopLogicalRow}, toTop={visibleTopLogicalRow - 1}, tick={tick}/{maxTicks}, delta={delta}");
            window.MouseWheel(delta);
            await Task.Delay(EffectiveScrollTickDelay(profile, scrollTickDelayOverrideMs), token);
            await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);

            var afterViewport = CaptureViewportSignatures(window, listGridRect, rowSignatureRects);
            var afterGrid = afterViewport.Grid;
            var afterRows = afterViewport.Rows;
            var movedDistance = SignatureDistance(beforeGrid, afterGrid);
            var verification = VerifyOneRowUp(beforeRows, afterRows);
            var estimatedRows = EstimateRowsAdvanced(verification, movedDistance);
            var twoRowsClearlyBetter = estimatedRows == 2;
            var oneRowMatched = verification.OneRowScore <= RowShiftMatchTolerance
                || verification.OneRowScore <= verification.NoMoveScore + RowShiftClearMargin
                || verification.OneRowScore <= verification.TwoRowScore + RowShiftClearMargin;
            var success = estimatedRows > 0;
            scanLog.WriteEvent("ROW_SCROLL_RECOVERY_VERIFY", $"fromTop={visibleTopLogicalRow}, toTop={visibleTopLogicalRow - 1}, tick={tick}/{maxTicks}, movedDistance={movedDistance}, oneRowScore={verification.OneRowScore}, noMoveScore={verification.NoMoveScore}, twoRowScore={verification.TwoRowScore}, oneRowMatched={oneRowMatched}, twoRowsClearlyBetter={twoRowsClearlyBetter}, estimatedRows={estimatedRows}, success={success}");

            if (success)
            {
                var eventName = estimatedRows == 1 ? "ROW_SCROLL_RECOVERED" : "ROW_SCROLL_RECOVERY_OVERSHOT";
                scanLog.WriteEvent(eventName, $"fromTop={visibleTopLogicalRow}, estimatedToTop={Math.Max(1, visibleTopLogicalRow - estimatedRows)}, requestedToTop={visibleTopLogicalRow - 1}, rowsRecovered={estimatedRows}, movedDistance={movedDistance}, oneRowScore={verification.OneRowScore}, twoRowScore={verification.TwoRowScore}, ticks={tick}");
                return ScrollRowsResult.Ok($"safe-band recovered {estimatedRows} row(s) from {visibleTopLogicalRow}", estimatedRows);
            }

            if (movedDistance > RowShiftAmbiguousMovementTolerance)
            {
                scanLog.WriteEvent("ROW_SCROLL_RECOVERY_AMBIGUOUS", $"fromTop={visibleTopLogicalRow}, expectedToTop={visibleTopLogicalRow - 1}, tick={tick}/{maxTicks}, movedDistance={movedDistance}, oneRowScore={verification.OneRowScore}, noMoveScore={verification.NoMoveScore}, twoRowScore={verification.TwoRowScore}");
                return ScrollRowsResult.Fail($"ambiguous recovery movement after {tick} tick(s), movedDistance={movedDistance}, oneRowScore={verification.OneRowScore}, twoRowScore={verification.TwoRowScore}", retryable: false);
            }
        }

        await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);
        var finalViewport = CaptureViewportSignatures(window, listGridRect, rowSignatureRects);
        var finalGrid = finalViewport.Grid;
        var finalRows = finalViewport.Rows;
        var finalMovedDistance = SignatureDistance(beforeGrid, finalGrid);
        var finalVerification = VerifyOneRowUp(beforeRows, finalRows);
        scanLog.WriteEvent("ROW_SCROLL_RECOVERY_FAIL", $"fromTop={visibleTopLogicalRow}, expectedToTop={visibleTopLogicalRow - 1}, ticks={maxTicks}, movedDistance={finalMovedDistance}, oneRowScore={finalVerification.OneRowScore}, noMoveScore={finalVerification.NoMoveScore}, twoRowScore={finalVerification.TwoRowScore}");
        return ScrollRowsResult.Fail($"one-row recovery failed after {maxTicks} tick(s), movedDistance={finalMovedDistance}, oneRowScore={finalVerification.OneRowScore}, twoRowScore={finalVerification.TwoRowScore}");
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

    private static System.Drawing.Point ListNeutralPoint(GameWindow window, Rectangle listGridRect)
    {
        var bounds = window.ClientScreenRect;
        return new System.Drawing.Point(
            Math.Clamp(listGridRect.Right + 24, bounds.Left + 1, bounds.Right - 2),
            Math.Clamp(listGridRect.Top + (listGridRect.Height / 2), bounds.Top + 1, bounds.Bottom - 2));
    }

    private static Rectangle SelectionProbeRect(GameWindow window, System.Drawing.Point clickPoint)
    {
        var bounds = window.ClientScreenRect;
        const int halfWidth = 72;
        const int halfHeight = 84;
        var left = Math.Clamp(clickPoint.X - halfWidth, bounds.Left, Math.Max(bounds.Left, bounds.Right - 1));
        var top = Math.Clamp(clickPoint.Y - halfHeight, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - 1));
        var right = Math.Clamp(clickPoint.X + halfWidth, left + 1, bounds.Right);
        var bottom = Math.Clamp(clickPoint.Y + halfHeight, top + 1, bounds.Bottom);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static ImageSignature[] CaptureRowSignatures(GameWindow window, IReadOnlyList<Rectangle> rowSignatureRects)
    {
        var signatures = new ImageSignature[rowSignatureRects.Count];
        if (rowSignatureRects.Count == 0)
        {
            return signatures;
        }

        var bounds = rowSignatureRects[0];
        for (var i = 1; i < rowSignatureRects.Count; i++)
        {
            bounds = Rectangle.Union(bounds, rowSignatureRects[i]);
        }

        using var image = window.CaptureFrame(bounds);
        for (var i = 0; i < signatures.Length; i++)
        {
            var rect = rowSignatureRects[i];
            signatures[i] = CreateSignature(image, new Rectangle(rect.Left - bounds.Left, rect.Top - bounds.Top, rect.Width, rect.Height));
        }

        return signatures;
    }

    private static ViewportSignatures CaptureViewportSignatures(GameWindow window, Rectangle listGridRect, IReadOnlyList<Rectangle> rowSignatureRects)
    {
        using var image = window.CaptureFrame(listGridRect);
        var rows = new ImageSignature[rowSignatureRects.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            var rect = rowSignatureRects[i];
            rows[i] = CreateSignature(image, new Rectangle(rect.Left - listGridRect.Left, rect.Top - listGridRect.Top, rect.Width, rect.Height));
        }

        return new ViewportSignatures(
            CreateSignature(image, new Rectangle(System.Drawing.Point.Empty, image.Size)),
            rows);
    }

    private static async Task<ScrollSettleResult> WaitForOneRowDownSettledAsync(
        GameWindow window,
        ScanProfile profile,
        ScrollAcceptMode scrollAcceptMode,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        ImageSignature beforeGrid,
        IReadOnlyList<ImageSignature> beforeRows,
        CancellationToken token)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Max(
            Math.Max(1, profile.MinListStableTimeoutMs),
            Math.Min(profile.LoadTimeoutMs, Math.Max(1, profile.ListStableTimeoutMs))));
        var interval = TimeSpan.FromMilliseconds(Math.Max(10, profile.LoadPollMs));
        var requiredStableFrames = Math.Max(1, profile.ListStableConfirmFrames);
        var start = DateTime.UtcNow;
        ViewportSignatures? previousOneRowCandidate = null;
        var stableFrames = 0;
        var polls = 0;
        var signatureMilliseconds = 0.0;
        ScrollSettleResult? last = null;

        while (DateTime.UtcNow - start < timeout)
        {
            token.ThrowIfCancellationRequested();
            var signatureWatch = Stopwatch.StartNew();
            var afterViewport = CaptureViewportSignatures(window, listGridRect, rowSignatureRects);
            signatureWatch.Stop();
            signatureMilliseconds += signatureWatch.Elapsed.TotalMilliseconds;
            polls++;

            var movedDistance = SignatureDistance(beforeGrid, afterViewport.Grid);
            var verification = VerifyOneRowDown(beforeRows, afterViewport.Rows);
            var estimatedRows = EstimateRowsAdvanced(verification, movedDistance);
            var success = false;

            if (estimatedRows == 1)
            {
                if (scrollAcceptMode == ScrollAcceptMode.EarlyOneRow)
                {
                    success = true;
                }
                else
                {
                    if (previousOneRowCandidate is { } previous
                        && SignatureDistance(previous.Grid, afterViewport.Grid) <= ListStableTolerance)
                    {
                        stableFrames++;
                    }
                    else
                    {
                        stableFrames = 0;
                    }

                    previousOneRowCandidate = afterViewport;
                    success = stableFrames >= requiredStableFrames;
                }
            }
            else
            {
                previousOneRowCandidate = null;
                stableFrames = 0;
            }

            last = new ScrollSettleResult(
                afterViewport,
                verification,
                movedDistance,
                estimatedRows,
                success,
                polls,
                (DateTime.UtcNow - start).TotalMilliseconds,
                signatureMilliseconds);

            if (success || estimatedRows > 1)
            {
                return last.Value;
            }

            await Task.Delay(interval, token);
        }

        if (last is { } final)
        {
            return final;
        }

        var fallbackViewport = CaptureViewportSignatures(window, listGridRect, rowSignatureRects);
        var fallbackMovedDistance = SignatureDistance(beforeGrid, fallbackViewport.Grid);
        var fallbackVerification = VerifyOneRowDown(beforeRows, fallbackViewport.Rows);
        return new ScrollSettleResult(
            fallbackViewport,
            fallbackVerification,
            fallbackMovedDistance,
            EstimateRowsAdvanced(fallbackVerification, fallbackMovedDistance),
            Success: false,
            Polls: polls,
            WaitMilliseconds: timeout.TotalMilliseconds,
            SignatureMilliseconds: signatureMilliseconds);
    }

    private static RowScrollVerification VerifyOneRowDown(IReadOnlyList<ImageSignature> beforeRows, IReadOnlyList<ImageSignature> afterRows)
    {
        var noMove = AverageSignatureDistance(beforeRows, afterRows, [(0, 0), (1, 1), (2, 2), (3, 3)]);
        var oneRow = AverageSignatureDistance(beforeRows, afterRows, [(1, 0), (2, 1), (3, 2)]);
        var twoRows = AverageSignatureDistance(beforeRows, afterRows, [(2, 0), (3, 1)]);
        return new RowScrollVerification(oneRow, noMove, twoRows);
    }

    private static RowScrollVerification VerifyOneRowUp(IReadOnlyList<ImageSignature> beforeRows, IReadOnlyList<ImageSignature> afterRows)
    {
        var noMove = AverageSignatureDistance(beforeRows, afterRows, [(0, 0), (1, 1), (2, 2), (3, 3)]);
        var oneRow = AverageSignatureDistance(beforeRows, afterRows, [(0, 1), (1, 2), (2, 3)]);
        var twoRows = AverageSignatureDistance(beforeRows, afterRows, [(0, 2), (1, 3)]);
        return new RowScrollVerification(oneRow, noMove, twoRows);
    }

    private static int EstimateRowsAdvanced(RowScrollVerification verification, int movedDistance)
    {
        if (movedDistance <= ListMovementTolerance)
        {
            return 0;
        }

        var oneRowMatched = verification.OneRowScore <= RowShiftMatchTolerance
            || verification.OneRowScore <= verification.NoMoveScore + RowShiftClearMargin
            || verification.OneRowScore <= verification.TwoRowScore + RowShiftClearMargin
            || (verification.TwoRowScore > RowShiftStrongMatchTolerance
                && verification.OneRowScore <= verification.NoMoveScore + RowShiftLooseMargin);
        if (oneRowMatched)
        {
            return 1;
        }

        var twoRowsClearlyBest = verification.TwoRowScore <= RowShiftStrongMatchTolerance
            && verification.TwoRowScore + (RowShiftClearMargin * 2) < verification.OneRowScore
            && verification.TwoRowScore + (RowShiftClearMargin * 2) < verification.NoMoveScore;
        if (twoRowsClearlyBest)
        {
            return 2;
        }

        return 0;
    }

    private static int ScrollTickDelay(ScanProfile profile)
    {
        return Math.Max(Math.Max(1, profile.MinScrollTickDelayMs), profile.ScrollTickDelayMs);
    }

    private static int EffectiveScrollTickDelay(ScanProfile profile, int overrideMilliseconds)
    {
        return overrideMilliseconds > 0
            ? Math.Clamp(overrideMilliseconds, 50, 80)
            : ScrollTickDelay(profile);
    }

    private static int ScrollSmallTickLimit(ScanProfile profile)
    {
        return Math.Max(1, Math.Min(ScrollMaxSmallTicks, Math.Abs(-120 / Math.Max(1, Math.Abs(profile.ScrollTickDelta))) + 2));
    }

    private static int ScrollSingleTickDelay(ScanProfile profile)
    {
        return ScrollTickDelay(profile);
    }

    private static string FormatOptionalMs(double? value)
    {
        return value.HasValue ? value.Value.ToString("F1") : "NA";
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
        await Task.Delay(ScrollTickDelay(profile), token);
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
        var timeout = TimeSpan.FromMilliseconds(Math.Max(
            Math.Max(1, profile.MinListStableTimeoutMs),
            Math.Min(profile.LoadTimeoutMs, Math.Max(1, profile.ListStableTimeoutMs))));
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

        var best = ProbeBestRarity(image, candidates, FixedRaritySamplePoints(image.Width, image.Height));
        if (best.BestDelta <= rarityScoreTolerance)
        {
            return best with { Rarity = best.BestCandidate, FullScan = false };
        }

        best = ProbeBestRarity(image, candidates, CenterRaritySamplePoints(image.Width, image.Height));
        if (best.BestDelta <= rarityScoreTolerance)
        {
            return best with { Rarity = best.BestCandidate, FullScan = false };
        }

        best = ProbeBestRarity(image, candidates, FullRaritySamplePoints(image.Width, image.Height));
        return best with
        {
            Rarity = best.BestDelta <= rarityScoreTolerance ? best.BestCandidate : null,
            FullScan = true
        };
    }

    private static RarityProbe ProbeBestRarity(Bitmap image, IReadOnlyList<RarityColor> candidates, IEnumerable<System.Drawing.Point> points)
    {
        var bestDelta = int.MaxValue;
        var bestCandidate = "";
        var bestColor = Color.Empty;
        foreach (var point in points)
        {
            var x = Math.Clamp(point.X, 0, image.Width - 1);
            var y = Math.Clamp(point.Y, 0, image.Height - 1);
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

        return new RarityProbe(null, bestColor, bestCandidate, bestDelta, FullScan: false);
    }

    private static IEnumerable<System.Drawing.Point> FixedRaritySamplePoints(int width, int height)
    {
        var cx = width / 2;
        var cy = height / 2;
        var inner = Math.Max(4, Math.Min(width, height) / 5);
        yield return new System.Drawing.Point(cx, cy);
        yield return new System.Drawing.Point(cx - inner, cy);
        yield return new System.Drawing.Point(cx + inner, cy);
        yield return new System.Drawing.Point(cx, cy - inner);
        yield return new System.Drawing.Point(cx, cy + inner);
        yield return new System.Drawing.Point(cx - inner, cy - inner);
        yield return new System.Drawing.Point(cx + inner, cy - inner);
        yield return new System.Drawing.Point(cx - inner, cy + inner);
        yield return new System.Drawing.Point(cx + inner, cy + inner);
    }

    private static IEnumerable<System.Drawing.Point> CenterRaritySamplePoints(int width, int height)
    {
        var cx = width / 2;
        var cy = height / 2;
        for (var y = cy - 12; y <= cy + 12; y += 4)
        {
            for (var x = cx - 12; x <= cx + 12; x += 4)
            {
                yield return new System.Drawing.Point(x, y);
            }
        }
    }

    private static IEnumerable<System.Drawing.Point> FullRaritySamplePoints(int width, int height)
    {
        for (var y = 0; y < height; y += 2)
        {
            for (var x = 0; x < width; x += 2)
            {
                yield return new System.Drawing.Point(x, y);
            }
        }
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

    private static ImageSignature[] CaptureCurrentPanelSignatures(
        GameWindow window,
        Rectangle panelRect,
        Rectangle panelChangeProbeRect,
        IReadOnlyList<CvRect> rois)
    {
        var probeScreenRect = PanelProbeScreenRect(panelRect, panelChangeProbeRect);
        using var image = window.CaptureFrame(probeScreenRect);
        var probeRects = BuildPanelChangeProbeRectsForProbe(probeScreenRect, panelRect, rois);
        return CreateSignatures(image, probeRects);
    }

    private static ImageSignature CaptureSignature(GameWindow window, Rectangle rect)
    {
        using var image = window.CaptureFrame(rect);
        return CreateSignature(image, new Rectangle(System.Drawing.Point.Empty, image.Size));
    }

    private static ImageSignature CaptureScreenSignature(Rectangle rect)
    {
        using var image = new Bitmap(rect.Width, rect.Height);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.CopyFromScreen(rect.Location, System.Drawing.Point.Empty, image.Size);
        }

        return CreateSignature(image, new Rectangle(System.Drawing.Point.Empty, image.Size));
    }

    private static Rectangle[] BuildPanelChangeProbeRects(Rectangle panelProbeRect, Rectangle panelRect, IReadOnlyList<CvRect> rois)
    {
        var imageSize = new System.Drawing.Size(panelRect.Width, panelRect.Height);
        var rects = new List<Rectangle>(Math.Max(1, rois.Count));

        foreach (var roi in rois)
        {
            rects.Add(ClampRectangle(new Rectangle(roi.X, roi.Y, roi.Width, roi.Height), imageSize));
        }

        if (rects.Count == 0)
        {
            rects.Add(panelProbeRect);
        }

        return rects.ToArray();
    }

    private static Rectangle[] BuildPanelChangeProbeRectsForProbe(Rectangle probeScreenRect, Rectangle panelRect, IReadOnlyList<CvRect> rois)
    {
        var imageSize = new System.Drawing.Size(probeScreenRect.Width, probeScreenRect.Height);
        var rects = new List<Rectangle>(Math.Max(1, rois.Count));

        foreach (var roi in rois)
        {
            var roiScreenRect = new Rectangle(panelRect.Left + roi.X, panelRect.Top + roi.Y, roi.Width, roi.Height);
            var intersection = Rectangle.Intersect(roiScreenRect, probeScreenRect);
            if (intersection.IsEmpty)
            {
                continue;
            }

            rects.Add(ClampRectangle(new Rectangle(
                intersection.Left - probeScreenRect.Left,
                intersection.Top - probeScreenRect.Top,
                intersection.Width,
                intersection.Height), imageSize));
        }

        if (rects.Count == 0)
        {
            rects.Add(new Rectangle(System.Drawing.Point.Empty, imageSize));
        }

        return rects.ToArray();
    }

    private static Rectangle PanelProbeScreenRect(Rectangle panelRect, Rectangle panelChangeProbeRect)
    {
        var probe = Rectangle.Intersect(panelRect, panelChangeProbeRect);
        return probe.IsEmpty ? panelRect : probe;
    }

    private static Rectangle[] TranslateProbeRectsToPanel(IReadOnlyList<Rectangle> probeRects, Rectangle probeScreenRect, Rectangle panelRect)
    {
        var imageSize = new System.Drawing.Size(panelRect.Width, panelRect.Height);
        var dx = probeScreenRect.Left - panelRect.Left;
        var dy = probeScreenRect.Top - panelRect.Top;
        var translated = new Rectangle[probeRects.Count];
        for (var i = 0; i < translated.Length; i++)
        {
            var rect = probeRects[i];
            translated[i] = ClampRectangle(new Rectangle(rect.Left + dx, rect.Top + dy, rect.Width, rect.Height), imageSize);
        }

        return translated;
    }

    private static Rectangle[] BuildPanelStableProbeRects(Rectangle panelRect, IReadOnlyList<CvRect> rois)
    {
        var imageSize = new System.Drawing.Size(panelRect.Width, panelRect.Height);
        var stableRoiCount = Math.Min(rois.Count, 4);
        var rects = new List<Rectangle>(stableRoiCount);

        for (var i = 0; i < stableRoiCount; i++)
        {
            var roi = rois[i];
            rects.Add(ClampRectangle(new Rectangle(roi.X, roi.Y, roi.Width, roi.Height), imageSize));
        }

        return rects.ToArray();
    }

    private static Rectangle[] BuildTextCoreStableProbeRects(Rectangle panelRect, IReadOnlyList<CvRect> rois)
    {
        var imageSize = new System.Drawing.Size(panelRect.Width, panelRect.Height);
        var rects = new List<Rectangle>(rois.Count);

        foreach (var roi in rois)
        {
            var cropX = (int)Math.Round(roi.Width * 0.06);
            var cropY = (int)Math.Round(roi.Height * 0.18);
            var left = roi.X + cropX;
            var top = roi.Y + cropY;
            var width = Math.Max(4, roi.Width - cropX * 2);
            var height = Math.Max(4, roi.Height - cropY * 2);

            rects.Add(ClampRectangle(new Rectangle(left, top, width, height), imageSize));
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

    private static ImageSignature[] CreateSignatures(CapturedFrame image, IReadOnlyList<Rectangle> rects)
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

    private static ImageSignature CreateSignature(CapturedFrame image, Rectangle rect)
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

    private static async Task<PanelCapture> CaptureStablePanelWithRetryAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle panelRect,
        IReadOnlyList<CvRect> rois,
        System.Drawing.Point statOffset,
        Color statRowBackground,
        Rectangle panelChangeProbeRect,
        ImageSignature[]? previousPanelSignatures,
        Rectangle selectionProbeRect,
        ImageSignature beforeSelectionSignature,
        ScanRuntimeState runtimeState,
        ScanLog scanLog,
        System.Drawing.Point clickPoint,
        System.Drawing.Point? selectionRefreshPoint,
        int pass,
        int row,
        int col,
        int maxColumns,
        int? logicalRow,
        string visibleTopText,
        string viewportStateText,
        CancellationToken token,
        bool postScrollFirstCell,
        bool sceneAdaptivePanelFloorEligible)
    {
        const int maxAttempts = 3;
        var activePreviousPanelSignatures = previousPanelSignatures;
        var activeSelectionProbeRect = selectionProbeRect;
        var activeBeforeSelectionSignature = beforeSelectionSignature;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                return await CaptureStablePanelAsync(window, profile, panelRect, rois, statOffset, statRowBackground, panelChangeProbeRect, activePreviousPanelSignatures, activeSelectionProbeRect, activeBeforeSelectionSignature, runtimeState, scanLog, token, postScrollFirstCell, sceneAdaptivePanelFloorEligible && attempt == 1);
            }
            catch (StalePanelException) when (attempt < maxAttempts)
            {
                runtimeState.PanelStability.MarkSafetyFallback();
                scanLog.WriteEvent("PANEL_STALE_RETRY", $"attempt={attempt}/{maxAttempts - 1}, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, point={clickPoint}");
                await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
                RefreshSelectionForPanelRetry(window, profile, panelRect, rois, panelChangeProbeRect, scanLog, clickPoint, selectionRefreshPoint, pass, row, col, maxColumns, logicalRow, visibleTopText, viewportStateText, token, out activePreviousPanelSignatures, out activeSelectionProbeRect, out activeBeforeSelectionSignature);
            }
            catch (TimeoutException ex) when (attempt < maxAttempts)
            {
                runtimeState.PanelStability.MarkSafetyFallback();
                scanLog.WriteEvent("PANEL_CAPTURE_RETRY", $"attempt={attempt}/{maxAttempts - 1}, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, point={clickPoint}, reason={ex.Message}");
                await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
                RefreshSelectionForPanelRetry(window, profile, panelRect, rois, panelChangeProbeRect, scanLog, clickPoint, selectionRefreshPoint, pass, row, col, maxColumns, logicalRow, visibleTopText, viewportStateText, token, out activePreviousPanelSignatures, out activeSelectionProbeRect, out activeBeforeSelectionSignature);
            }
            catch (TimeoutException ex)
            {
                throw new PanelCellCaptureException(pass, row, col, maxColumns, logicalRow, ex);
            }
        }

        throw new UnreachableException("Panel capture retry loop exhausted without returning or throwing.");
    }

    private static void RefreshSelectionForPanelRetry(
        GameWindow window,
        ScanProfile profile,
        Rectangle panelRect,
        IReadOnlyList<CvRect> rois,
        Rectangle panelChangeProbeRect,
        ScanLog scanLog,
        System.Drawing.Point clickPoint,
        System.Drawing.Point? selectionRefreshPoint,
        int pass,
        int row,
        int col,
        int maxColumns,
        int? logicalRow,
        string visibleTopText,
        string viewportStateText,
        CancellationToken token,
        out ImageSignature[]? refreshedPanelSignatures,
        out Rectangle refreshedSelectionProbeRect,
        out ImageSignature refreshedSelectionSignature)
    {
        token.ThrowIfCancellationRequested();
        var delayMs = Math.Max(80, profile.ClickDelayMs);
        if (selectionRefreshPoint is { } refreshPoint && refreshPoint != clickPoint)
        {
            scanLog.WriteEvent("PANEL_SELECTION_REFRESH", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, refreshPoint={refreshPoint}, targetPoint={clickPoint}");
            window.MoveCursor(refreshPoint);
            window.LeftClickCurrent();
            Thread.Sleep(delayMs);
            refreshedPanelSignatures = CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois);
            refreshedSelectionProbeRect = SelectionProbeRect(window, clickPoint);
            refreshedSelectionSignature = CaptureScreenSignature(refreshedSelectionProbeRect);
        }
        else
        {
            refreshedPanelSignatures = CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois);
            refreshedSelectionProbeRect = SelectionProbeRect(window, clickPoint);
            refreshedSelectionSignature = CaptureScreenSignature(refreshedSelectionProbeRect);
        }

        token.ThrowIfCancellationRequested();
        window.MoveCursor(clickPoint);
        window.LeftClickCurrent();
    }

    private static Rectangle ClampRectangle(Rectangle rect, System.Drawing.Size bounds)
    {
        var left = Math.Clamp(rect.Left, 0, Math.Max(0, bounds.Width - 1));
        var top = Math.Clamp(rect.Top, 0, Math.Max(0, bounds.Height - 1));
        var right = Math.Clamp(rect.Right, left + 1, bounds.Width);
        var bottom = Math.Clamp(rect.Bottom, top + 1, bounds.Height);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    private static AdaptivePanelTimingDecision ResolveDefaultPanelTiming(
        ScanProfile profile,
        string captureMode,
        int panelMinAcceptFloorMs = 120,
        PanelFloorMode panelFloorMode = PanelFloorMode.Static,
        int sameRowPanelFloorMs = 105,
        int postScrollPanelFloorMs = 110)
    {
        var captureModeMinimum = string.Equals(captureMode, "dxgi", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(panelMinAcceptFloorMs, 90, 120)
            : 60;
        return new AdaptivePanelTimingDecision(
            Math.Clamp(Math.Max(profile.PanelChangedMinimumAcceptMs, captureModeMinimum), 60, profile.LoadTimeoutMs),
            string.Equals(captureMode, "dxgi", StringComparison.OrdinalIgnoreCase) ? 2 : 1,
            0,
            WarmupComplete: false,
            AppliedAdaptiveMinimum: false,
            Reason: "disabled",
            PanelAcceptMode.Safe,
            PostScrollPanelAcceptMode.Safe,
            panelFloorMode,
            captureModeMinimum,
            Math.Clamp(sameRowPanelFloorMs, 100, 120),
            Math.Clamp(postScrollPanelFloorMs, 100, 120),
            "disabled");
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
        Rectangle selectionProbeRect,
        ImageSignature beforeSelectionSignature,
        ScanRuntimeState runtimeState,
        ScanLog scanLog,
        CancellationToken token,
        bool postScrollFirstCell,
        bool sceneAdaptivePanelFloorEligible)
    {
        var timeout = TimeSpan.FromMilliseconds(profile.LoadTimeoutMs);
        var interval = TimeSpan.FromMilliseconds(Math.Max(5, profile.LoadPollMs));
        var panelAcceptMode = runtimeState.PanelProbeHealth.ForceSafe || runtimeState.ProfileHealth.ForceSafePanel
            ? PanelAcceptMode.Safe
            : runtimeState.PanelAcceptMode;
        var postScrollPanelAcceptMode = runtimeState.PanelProbeHealth.ForceSafe || runtimeState.ProfileHealth.ForceSafePanel
            ? PostScrollPanelAcceptMode.Safe
            : runtimeState.PostScrollPanelAcceptMode;
        var panelTiming = runtimeState.AdaptiveTiming?.ResolvePanelTiming(
                profile,
                window.ActiveCaptureMode,
                panelAcceptMode,
                postScrollFirstCell,
                postScrollPanelAcceptMode,
                runtimeState.PanelMinAcceptFloorMs,
                runtimeState.PanelFloorMode,
                sceneAdaptivePanelFloorEligible,
                runtimeState.SameRowPanelMinAcceptFloorMs,
                runtimeState.PostScrollPanelMinAcceptFloorMs)
            ?? ResolveDefaultPanelTiming(profile, window.ActiveCaptureMode, runtimeState.PanelMinAcceptFloorMs, runtimeState.PanelFloorMode, runtimeState.SameRowPanelMinAcceptFloorMs, runtimeState.PostScrollPanelMinAcceptFloorMs);
        if (postScrollFirstCell && panelTiming.EffectivePostScrollPanelAcceptMode == PostScrollPanelAcceptMode.Safe)
        {
            panelTiming = ResolveDefaultPanelTiming(profile, window.ActiveCaptureMode, runtimeState.PanelMinAcceptFloorMs, runtimeState.PanelFloorMode, runtimeState.SameRowPanelMinAcceptFloorMs, runtimeState.PostScrollPanelMinAcceptFloorMs);
        }
        var stabilityDecision = runtimeState.PanelStability.Resolve();
        var settleDelay = Math.Clamp(profile.PanelSettleDelayMs, Math.Max(1, profile.MinPanelSettleDelayMs), 180);
        var changedMinimumAcceptMs = panelTiming.MinimumAcceptMilliseconds;
        var panelFloorReason = panelTiming.PanelFloorReason;
        var quickMinimumAcceptMs = string.Equals(window.ActiveCaptureMode, "dxgi", StringComparison.OrdinalIgnoreCase)
            ? Math.Min(changedMinimumAcceptMs, 90)
            : Math.Min(changedMinimumAcceptMs, 60);
        var requiredStableFrames = panelTiming.RequiredStableFrames;
        await Task.Delay(settleDelay, token);

        var start = DateTime.UtcNow;
        var roiCompleteFrames = 0;
        var probeScreenRect = PanelProbeScreenRect(panelRect, panelChangeProbeRect);
        var probeCaptureRects = BuildPanelChangeProbeRectsForProbe(probeScreenRect, panelRect, rois);
        var fullPanelProbeRects = TranslateProbeRectsToPanel(probeCaptureRects, probeScreenRect, panelRect);
        var stableProbeRects = BuildPanelStableProbeRects(panelRect, rois);
        var measureTextCoreStability = stabilityDecision.Source == PanelStabilitySource.TextCore
            || stabilityDecision.Reason == "panel_fallback_warmup";
        var textCoreStableProbeRects = measureTextCoreStability
            ? BuildTextCoreStableProbeRects(panelRect, rois)
            : Array.Empty<Rectangle>();
        var sawPanelChange = previousPanelSignatures is null;
        var selectionChanged = previousPanelSignatures is null;
        ImageSignature[]? previousStableProbeSignatures = null;
        ImageSignature[]? previousTextCoreStableProbeSignatures = null;
        var stableProbeFrames = 0;
        var textCoreStableProbeFrames = 0;
        var frameCount = 0;
        var captureMilliseconds = 0.0;
        var signatureMilliseconds = 0.0;
        var visibleRoiMilliseconds = 0.0;
        var frameLoopMilliseconds = 0.0;
        var frameToBitmapMilliseconds = 0.0;
        var bitmapCreatedCount = 0;
        double? changeMilliseconds = previousPanelSignatures is null ? 0 : null;
        double? selectionChangeMilliseconds = null;
        double? fullRoiMilliseconds = null;
        double? stableMilliseconds = null;
        double? textCoreStableMilliseconds = null;
        var acceptReason = "changed_stable_full_roi";
        var acceptGateReason = "waiting_for_panel_change";
        var quickRejectReason = runtimeState.QuickPanelAcceptEnabled ? "waiting_for_panel_change" : "disabled";
        var unchangedFallbackDelay = TimeSpan.FromMilliseconds(Math.Clamp(
            profile.PanelUnchangedFallbackMs,
            Math.Max(1, profile.MinPanelUnchangedFallbackMs),
            profile.LoadTimeoutMs));
        var useProbeOnlyBeforeChange = true;

        bool ObserveSelectionChange(double elapsed)
        {
            if (selectionChanged)
            {
                return true;
            }

            var currentSelectionSignature = CaptureScreenSignature(selectionProbeRect);
            if (SignatureDistance(beforeSelectionSignature, currentSelectionSignature) > PanelChangeTolerance)
            {
                selectionChanged = true;
                selectionChangeMilliseconds ??= elapsed;
                return true;
            }

            return false;
        }

        bool PromoteSelectionChange(double elapsed)
        {
            if (!ObserveSelectionChange(elapsed))
            {
                return false;
            }

            sawPanelChange = true;
            acceptReason = "selection_changed_stable_full_roi";
            changeMilliseconds ??= selectionChangeMilliseconds ?? elapsed;
            if (panelTiming.PanelFloorMode == PanelFloorMode.SceneAdaptive && changedMinimumAcceptMs < 120)
            {
                changedMinimumAcceptMs = 120;
                panelFloorReason = "scene_selection_fallback";
            }
            scanLog.Write("Panel probes stayed unchanged, but target cell selection changed; treating as identical-detail transition.");
            return true;
        }

        while (DateTime.UtcNow - start < timeout)
        {
            token.ThrowIfCancellationRequested();
            var frameLoop = Stopwatch.StartNew();
            var elapsedMilliseconds = (DateTime.UtcNow - start).TotalMilliseconds;

            if (useProbeOnlyBeforeChange && !sawPanelChange && previousPanelSignatures is not null)
            {
                frameCount++;
                var probeCaptureWatch = Stopwatch.StartNew();
                using var probeImage = window.CaptureFrame(probeScreenRect);
                probeCaptureWatch.Stop();
                captureMilliseconds += probeCaptureWatch.Elapsed.TotalMilliseconds;

                var probeSignatureWatch = Stopwatch.StartNew();
                var probeSignatures = CreateSignatures(probeImage, probeCaptureRects);
                if (HasProbeChange(previousPanelSignatures, probeSignatures, PanelChangeTolerance))
                {
                    sawPanelChange = true;
                    changeMilliseconds ??= elapsedMilliseconds;
                    quickRejectReason = runtimeState.QuickPanelAcceptEnabled ? "waiting_for_roi" : quickRejectReason;
                }
                probeSignatureWatch.Stop();
                signatureMilliseconds += probeSignatureWatch.Elapsed.TotalMilliseconds;

                if (!sawPanelChange)
                {
                    ObserveSelectionChange(elapsedMilliseconds);
                    if (DateTime.UtcNow - start >= unchangedFallbackDelay)
                    {
                        if (!PromoteSelectionChange(elapsedMilliseconds))
                        {
                            scanLog.Write("Panel probes stayed unchanged past fallback delay; refusing to capture stale detail panel.");
                            throw new StalePanelException("详情面板未检测到变化，已拒绝复用旧面板。");
                        }
                    }

                    if (!sawPanelChange)
                    {
                        frameLoop.Stop();
                        frameLoopMilliseconds += frameLoop.Elapsed.TotalMilliseconds;
                        await Task.Delay(interval, token);
                        continue;
                    }
                }
            }

            frameCount++;
            var captureWatch = Stopwatch.StartNew();
            using var image = window.CaptureFrame(panelRect);
            captureWatch.Stop();
            captureMilliseconds += captureWatch.Elapsed.TotalMilliseconds;
            elapsedMilliseconds = (DateTime.UtcNow - start).TotalMilliseconds;

            var signatureWatch = Stopwatch.StartNew();
            var changeProbeSignatures = CreateSignatures(image, fullPanelProbeRects);
            if (!sawPanelChange
                && previousPanelSignatures is not null
                && HasProbeChange(previousPanelSignatures, changeProbeSignatures, PanelChangeTolerance))
            {
                sawPanelChange = true;
                changeMilliseconds ??= elapsedMilliseconds;
            }

            var stableProbeSignatures = CreateSignatures(image, stableProbeRects);
            stableProbeFrames = previousStableProbeSignatures is not null
                && AreProbesStable(previousStableProbeSignatures, stableProbeSignatures)
                    ? stableProbeFrames + 1
                    : 0;
            previousStableProbeSignatures = stableProbeSignatures;
            if (stableProbeFrames >= requiredStableFrames)
            {
                stableMilliseconds ??= elapsedMilliseconds;
            }

            if (measureTextCoreStability)
            {
                var textCoreStableProbeSignatures = CreateSignatures(image, textCoreStableProbeRects);
                textCoreStableProbeFrames = previousTextCoreStableProbeSignatures is not null
                    && AreProbesStable(previousTextCoreStableProbeSignatures, textCoreStableProbeSignatures)
                        ? textCoreStableProbeFrames + 1
                        : 0;
                previousTextCoreStableProbeSignatures = textCoreStableProbeSignatures;
                if (textCoreStableProbeFrames >= requiredStableFrames)
                {
                    textCoreStableMilliseconds ??= elapsedMilliseconds;
                }
            }
            signatureWatch.Stop();
            signatureMilliseconds += signatureWatch.Elapsed.TotalMilliseconds;

            var visibleWatch = Stopwatch.StartNew();
            var visibleCount = CountVisibleRois(image, rois, statOffset, statRowBackground, profile.ColorTolerance);
            visibleWatch.Stop();
            visibleRoiMilliseconds += visibleWatch.Elapsed.TotalMilliseconds;
            if (visibleCount == rois.Count)
            {
                fullRoiMilliseconds ??= elapsedMilliseconds;
                roiCompleteFrames++;
            }
            else
            {
                roiCompleteFrames = 0;
            }

            if (visibleCount > 0)
            {
                var fullPanelReadable = visibleCount == rois.Count && roiCompleteFrames >= 1;
                var panelSelectedStableFrames = stabilityDecision.Source == PanelStabilitySource.TextCore
                    ? textCoreStableProbeFrames
                    : stableProbeFrames;
                var effectiveRequiredStableFrames = panelTiming.EffectivePanelAcceptMode == PanelAcceptMode.AdaptiveEarlyFullRoi
                    ? 1
                    : requiredStableFrames;
                var requiredRoiCompleteFrames = 1;
                var selectedStableFrames = panelTiming.EffectivePanelAcceptMode == PanelAcceptMode.AdaptiveEarlyFullRoi
                    ? roiCompleteFrames
                    : panelSelectedStableFrames;
                var stableEnough = selectedStableFrames >= effectiveRequiredStableFrames;
                var roiEnough = visibleCount == rois.Count && roiCompleteFrames >= requiredRoiCompleteFrames;
                acceptGateReason = !sawPanelChange
                    ? "waiting_for_panel_change"
                    : visibleCount != rois.Count
                        ? "waiting_for_full_roi"
                        : roiCompleteFrames < requiredRoiCompleteFrames
                            ? "waiting_for_roi_complete_frames"
                            : !stableEnough
                                ? "waiting_for_stable_frame"
                                : elapsedMilliseconds < changedMinimumAcceptMs
                                    ? "before_min_accept"
                                    : "ready";
                if (runtimeState.QuickPanelAcceptEnabled
                    && panelTiming.WarmupComplete
                    && previousPanelSignatures is not null
                    && fullPanelReadable
                    && sawPanelChange)
                {
                    if (selectedStableFrames >= 1 && elapsedMilliseconds >= quickMinimumAcceptMs)
                    {
                        var bitmapWatch = Stopwatch.StartNew();
                        var acceptedImage = image.ToBitmap();
                        bitmapWatch.Stop();
                        frameToBitmapMilliseconds += bitmapWatch.Elapsed.TotalMilliseconds;
                        bitmapCreatedCount++;
                        frameLoop.Stop();
                        frameLoopMilliseconds += frameLoop.Elapsed.TotalMilliseconds;
                        acceptReason = "quick_changed_stable_full_roi";
                        var waitMilliseconds = (DateTime.UtcNow - start).TotalMilliseconds;
                        return new PanelCapture(acceptedImage, visibleCount, waitMilliseconds, usedFallback: false, changeProbeSignatures, frameCount, changeMilliseconds, selectionChangeMilliseconds, fullRoiMilliseconds, stableMilliseconds, textCoreStableMilliseconds, stabilityDecision.SourceName, stabilityDecision.Reason, captureMilliseconds, signatureMilliseconds, visibleRoiMilliseconds, frameLoopMilliseconds, frameToBitmapMilliseconds, bitmapCreatedCount, changedMinimumAcceptMs, requiredStableFrames, panelTiming.SampleCount, panelTiming.Reason, acceptReason, quickAccept: true, quickRejectReason: "accepted", panelTiming.EffectivePanelAcceptMode, panelTiming.EffectivePostScrollPanelAcceptMode, panelTiming.PanelFloorMode, panelTiming.PanelMinAcceptFloorMs, panelTiming.SameRowPanelFloorMs, panelTiming.PostScrollPanelFloorMs, panelFloorReason, CalculateFloorWaitLimitedMilliseconds(changedMinimumAcceptMs, changeMilliseconds, fullRoiMilliseconds, stableMilliseconds), waitMilliseconds - changedMinimumAcceptMs, roiCompleteFrames, selectedStableFrames, "quick_ready");
                    }

                    quickRejectReason = selectedStableFrames < 1 ? "waiting_for_stable_frame" : "before_quick_min_accept";
                }
                else if (runtimeState.QuickPanelAcceptEnabled)
                {
                    quickRejectReason = !panelTiming.WarmupComplete
                        ? "before_warmup_complete"
                        : sawPanelChange ? "waiting_for_full_roi" : "waiting_for_panel_change";
                }

                if (roiEnough
                    && sawPanelChange
                    && elapsedMilliseconds >= changedMinimumAcceptMs
                    && stableEnough)
                {
                    var bitmapWatch = Stopwatch.StartNew();
                    var acceptedImage = image.ToBitmap();
                    bitmapWatch.Stop();
                    frameToBitmapMilliseconds += bitmapWatch.Elapsed.TotalMilliseconds;
                    bitmapCreatedCount++;
                    frameLoop.Stop();
                    frameLoopMilliseconds += frameLoop.Elapsed.TotalMilliseconds;
                    var finalAcceptReason = stabilityDecision.Source == PanelStabilitySource.TextCore
                        ? acceptReason.Replace("stable", "text_core_stable", StringComparison.Ordinal)
                        : acceptReason;
                    if (panelTiming.EffectivePanelAcceptMode == PanelAcceptMode.AdaptiveEarlyFullRoi)
                    {
                        finalAcceptReason = panelTiming.EffectivePostScrollPanelAcceptMode == PostScrollPanelAcceptMode.AdaptiveAfterScroll
                            ? "adaptive_after_scroll"
                            : "adaptive_early_full_roi";
                    }

                    var waitMilliseconds = (DateTime.UtcNow - start).TotalMilliseconds;
                    return new PanelCapture(acceptedImage, visibleCount, waitMilliseconds, usedFallback: false, changeProbeSignatures, frameCount, changeMilliseconds, selectionChangeMilliseconds, fullRoiMilliseconds, stableMilliseconds, textCoreStableMilliseconds, stabilityDecision.SourceName, stabilityDecision.Reason, captureMilliseconds, signatureMilliseconds, visibleRoiMilliseconds, frameLoopMilliseconds, frameToBitmapMilliseconds, bitmapCreatedCount, changedMinimumAcceptMs, requiredStableFrames, panelTiming.SampleCount, panelTiming.Reason, finalAcceptReason, quickAccept: false, quickRejectReason, panelTiming.EffectivePanelAcceptMode, panelTiming.EffectivePostScrollPanelAcceptMode, panelTiming.PanelFloorMode, panelTiming.PanelMinAcceptFloorMs, panelTiming.SameRowPanelFloorMs, panelTiming.PostScrollPanelFloorMs, panelFloorReason, CalculateFloorWaitLimitedMilliseconds(changedMinimumAcceptMs, changeMilliseconds, fullRoiMilliseconds, stableMilliseconds), waitMilliseconds - changedMinimumAcceptMs, roiCompleteFrames, selectedStableFrames, acceptGateReason);
                }

                if (fullPanelReadable && !sawPanelChange && stableProbeFrames >= requiredStableFrames && DateTime.UtcNow - start >= unchangedFallbackDelay)
                {
                    if (!PromoteSelectionChange(elapsedMilliseconds))
                    {
                        scanLog.Write("Panel probes stayed unchanged past fallback delay; refusing to capture stale detail panel.");
                        throw new StalePanelException("详情面板未检测到变化，已拒绝复用旧面板。");
                    }
                }
            }
            else
            {
                roiCompleteFrames = 0;
            }

            frameLoop.Stop();
            frameLoopMilliseconds += frameLoop.Elapsed.TotalMilliseconds;
            await Task.Delay(interval, token);
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

    private static int CountVisibleRois(CapturedFrame image, IReadOnlyList<CvRect> rois, System.Drawing.Point statOffset, Color statRowBackground, int tolerance)
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
        OcrDiagnosticsWriter diagnostics,
        OcrShadowDatasetWriter? shadowDataset,
        FastOcrShadowRecorder? fastOcrShadow,
        FastOcrAssistEngine? fastOcrAssist,
        FastOcrAssistRecorder? fastOcrAssistRecorder)
    {
        return Enumerable.Range(1, workerCount)
            .Select(workerId => Task.Run(() => ConsumeOcrWorker(queue, ocrResults, outputDir, options, scanLog, workerId, ocrIntraOpThreads, counters, diagnostics, shadowDataset, fastOcrShadow, fastOcrAssist, fastOcrAssistRecorder)))
            .ToArray();
    }

    private static double CalculateFloorWaitLimitedMilliseconds(
        int minimumAcceptMilliseconds,
        double? changeMilliseconds,
        double? fullRoiMilliseconds,
        double? stableMilliseconds)
    {
        var readyMilliseconds = new[] { changeMilliseconds, fullRoiMilliseconds, stableMilliseconds }
            .Where(value => value is > 0)
            .Select(value => value!.Value)
            .DefaultIfEmpty(0)
            .Max();
        return Math.Max(0, minimumAcceptMilliseconds - readyMilliseconds);
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
        OcrDiagnosticsWriter diagnostics,
        OcrShadowDatasetWriter? shadowDataset,
        FastOcrShadowRecorder? fastOcrShadow,
        FastOcrAssistEngine? fastOcrAssist,
        FastOcrAssistRecorder? fastOcrAssistRecorder)
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

                FastOcrAssistPlan?[]? assistPlans = null;
                var fastMatchMs = 0.0;
                var fastAcceptedCount = 0;
                var fastRejectedCount = 0;
                if (fastOcrAssist is not null)
                {
                    assistPlans = new FastOcrAssistPlan?[batch.Count];
                    for (var i = 0; i < batch.Count; i++)
                    {
                        var capture = batch[i];
                        var plan = fastOcrAssist.Plan(capture.Index, capture.Rarity, capture.Image, capture.Rois);
                        assistPlans[i] = plan;
                        fastMatchMs += plan.FastMatchMs;
                        fastAcceptedCount += plan.FastAcceptedCount;
                        fastRejectedCount += plan.FastRejectedCount;
                    }
                }

                var inputs = batch
                    .Select((capture, index) => new OcrBatchInput(mats[index]!, assistPlans?[index]?.PpOcrRois ?? capture.Rois))
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
                    var assistPlan = assistPlans?[i];
                    var ocr = assistPlan?.Merge(ocrBatch[i]) ?? ocrBatch[i];
                    try
                    {
                        var export = cleaner.Clean(capture.Index, capture.Rarity, ocr);
                        TryWriteFastOcrAssist(fastOcrAssistRecorder, assistPlan, ocr, scanLog);
                        TryWriteOcrShadowDataset(shadowDataset, capture, ocr, export, scanLog);
                        TryWriteFastOcrShadow(fastOcrShadow, capture, ocr, export, scanLog);
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
                diagnostics.Write(
                    workerId,
                    batch.Count,
                    recognition.Diagnostics,
                    bitmapToMatSw.Elapsed.TotalMilliseconds,
                    cleanSw.Elapsed.TotalMilliseconds,
                    fallbackCount: 0,
                    backlog,
                    fastMatchMs,
                    fastAcceptedCount,
                    fastRejectedCount,
                    recognition.Diagnostics.RoiCount);
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

                if (!duplicateGuard.Observe(result.Export, out var duplicateReason))
                {
                    Interlocked.CompareExchange(ref counters.StopAfterIndex, result.Index, 0);
                    scanLog.Write($"Duplicate guard canceled scan at #{result.Index}: {duplicateReason}");
                    Report(progress, counters, $"重复保护触发：{duplicateReason}");
                    linked.Cancel();
                    return nextIndex;
                }

                results.Add(result.Export);
                Interlocked.Increment(ref counters.Completed);
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

    private static void TryWriteOcrShadowDataset(
        OcrShadowDatasetWriter? shadowDataset,
        DiscCapture capture,
        IReadOnlyList<OcrResult> ocr,
        DriveDiscExport export,
        ScanLog scanLog)
    {
        if (shadowDataset is null)
        {
            return;
        }

        try
        {
            shadowDataset.Write(capture.Index, capture.Rarity, capture.Image, capture.Rois, ocr, export);
        }
        catch (Exception ex)
        {
            scanLog.Write($"OCR shadow dataset write failed for index={capture.Index}: {ex.Message}");
        }
    }

    private static void TryWriteFastOcrShadow(
        FastOcrShadowRecorder? fastOcrShadow,
        DiscCapture capture,
        IReadOnlyList<OcrResult> ocr,
        DriveDiscExport export,
        ScanLog scanLog)
    {
        if (fastOcrShadow is null)
        {
            return;
        }

        try
        {
            fastOcrShadow.Write(capture.Index, capture.Rarity, capture.Image, capture.Rois, ocr, export);
        }
        catch (Exception ex)
        {
            scanLog.Write($"Fast OCR shadow write failed for index={capture.Index}: {ex.Message}");
        }
    }

    private static void TryWriteFastOcrAssist(
        FastOcrAssistRecorder? fastOcrAssistRecorder,
        FastOcrAssistPlan? assistPlan,
        IReadOnlyList<OcrResult> ocr,
        ScanLog scanLog)
    {
        if (fastOcrAssistRecorder is null || assistPlan is null)
        {
            return;
        }

        try
        {
            fastOcrAssistRecorder.Write(assistPlan.Decisions, ocr);
        }
        catch (Exception ex)
        {
            scanLog.Write($"Fast OCR assist write failed: {ex.Message}");
        }
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

    private static int CurrentOcrBacklog(Counters counters)
    {
        return Math.Max(
            0,
            Volatile.Read(ref counters.Queued)
            - Volatile.Read(ref counters.Completed)
            - Volatile.Read(ref counters.Failed));
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

    private sealed record ScanRuntimeState(
        bool QuickPanelAcceptEnabled,
        bool AdaptiveTimingActive,
        AdaptiveTimingState? AdaptiveTiming,
        AdaptiveOcrThrottle? OcrThrottle,
        PanelStabilitySelector PanelStability,
        PanelProbeHealth PanelProbeHealth,
        ProfileHealthGate ProfileHealth,
        PanelAcceptMode PanelAcceptMode,
        PostScrollPanelAcceptMode PostScrollPanelAcceptMode,
        PanelFloorMode PanelFloorMode,
        int PanelMinAcceptFloorMs,
        int SameRowPanelMinAcceptFloorMs,
        int PostScrollPanelMinAcceptFloorMs,
        int EffectiveScrollTickDelayMs);

    private enum PanelStabilitySource
    {
        Panel,
        TextCore
    }

    private sealed record PanelStabilityDecision(PanelStabilitySource Source, string SourceName, string Reason);

    private sealed class PanelProbeHealth
    {
        private const int WarmupItems = 12;
        private const int ZeroChangeLimit = 3;
        private int _items;
        private int _zeroChangeItems;
        private bool _logged;

        public bool ForceSafe { get; private set; }

        public void Observe(double? changeMilliseconds, PanelAcceptMode effectivePanelAcceptMode, Action<string> log)
        {
            if (ForceSafe || _items >= WarmupItems)
            {
                return;
            }

            if (effectivePanelAcceptMode != PanelAcceptMode.AdaptiveEarlyFullRoi)
            {
                return;
            }

            _items++;
            if (changeMilliseconds is <= 0.1)
            {
                _zeroChangeItems++;
            }

            if (_zeroChangeItems < ZeroChangeLimit)
            {
                return;
            }

            ForceSafe = true;
            if (!_logged)
            {
                _logged = true;
                log($"PANEL_PROBE_DEGRADED_SAFE_MODE warmupItems={_items}, zeroChangeItems={_zeroChangeItems}, threshold={ZeroChangeLimit}, action=force_panel_accept_safe_for_scan");
                log($"PROFILE_HEALTH_DEGRADED source=panel_probe, warmupItems={_items}, zeroChangeItems={_zeroChangeItems}, threshold={ZeroChangeLimit}, action=force_panel_accept_safe_for_scan");
            }
        }
    }

    private sealed class ProfileHealthGate
    {
        private const int PanelWarmupItems = 12;
        private const int OverlapWarmupScrolls = 12;
        private const double PanelWaitAverageThresholdMs = 260;
        private const double CaptureAverageThresholdMs = 90;
        private const double AmbiguousOverlapThresholdRate = 0.50;

        private int _panelItems;
        private double _panelWaitTotalMs;
        private double _captureTotalMs;
        private int _overlapScrolls;
        private int _overlapAmbiguous;
        private bool _panelLogged;
        private bool _overlapLogged;

        public bool ForceSafePanel { get; private set; }

        public void ObservePanel(double panelWaitMilliseconds, double captureMilliseconds, Action<string> log)
        {
            if (_panelItems >= PanelWarmupItems)
            {
                return;
            }

            _panelItems++;
            _panelWaitTotalMs += panelWaitMilliseconds;
            _captureTotalMs += captureMilliseconds;
            if (_panelItems < PanelWarmupItems)
            {
                return;
            }

            var panelWaitAverage = _panelWaitTotalMs / _panelItems;
            var captureAverage = _captureTotalMs / _panelItems;
            if (panelWaitAverage > PanelWaitAverageThresholdMs || captureAverage > CaptureAverageThresholdMs)
            {
                ForceSafePanel = true;
                if (!_panelLogged)
                {
                    _panelLogged = true;
                    log($"PROFILE_HEALTH_DEGRADED source=panel_capture, warmupItems={_panelItems}, panelWaitAvgMs={panelWaitAverage:F1}, captureAvgMs={captureAverage:F1}, panelWaitThresholdMs={PanelWaitAverageThresholdMs:F1}, captureThresholdMs={CaptureAverageThresholdMs:F1}, action=force_panel_accept_safe_for_scan");
                }
            }
            else
            {
                log($"PROFILE_HEALTH_OK source=panel_capture, warmupItems={_panelItems}, panelWaitAvgMs={panelWaitAverage:F1}, captureAvgMs={captureAverage:F1}, panelWaitThresholdMs={PanelWaitAverageThresholdMs:F1}, captureThresholdMs={CaptureAverageThresholdMs:F1}");
            }
        }

        public void ObserveOverlap(bool ambiguous, Action<string> log)
        {
            if (_overlapScrolls >= OverlapWarmupScrolls)
            {
                return;
            }

            _overlapScrolls++;
            if (ambiguous)
            {
                _overlapAmbiguous++;
            }

            if (_overlapScrolls < OverlapWarmupScrolls)
            {
                return;
            }

            var rate = _overlapAmbiguous / (double)_overlapScrolls;
            if (rate > AmbiguousOverlapThresholdRate)
            {
                ForceSafePanel = true;
                if (!_overlapLogged)
                {
                    _overlapLogged = true;
                    log($"PROFILE_HEALTH_DEGRADED source=overlap_signature, warmupScrolls={_overlapScrolls}, ambiguous={_overlapAmbiguous}, ambiguousRate={rate:F3}, threshold={AmbiguousOverlapThresholdRate:F3}, action=force_panel_accept_safe_for_scan");
                }
            }
            else
            {
                log($"PROFILE_HEALTH_OK source=overlap_signature, warmupScrolls={_overlapScrolls}, ambiguous={_overlapAmbiguous}, ambiguousRate={rate:F3}, threshold={AmbiguousOverlapThresholdRate:F3}");
            }
        }
    }

    private sealed class PanelStabilitySelector
    {
        public const int DefaultWarmupItems = 12;
        public const int MinimumTextCoreGainMilliseconds = 15;

        private readonly PanelStabilityMode _requestedMode;
        private readonly int _warmupItems;
        private readonly List<PanelStabilitySample> _samples = new();
        private bool _safetyFallback;

        public PanelStabilitySelector(PanelStabilityMode requestedMode, int warmupItems = DefaultWarmupItems)
        {
            _requestedMode = requestedMode;
            _warmupItems = Math.Max(1, warmupItems);
        }

        public PanelStabilityDecision Resolve()
        {
            if (_requestedMode == PanelStabilityMode.Panel)
            {
                return new PanelStabilityDecision(PanelStabilitySource.Panel, "panel", "configured_panel");
            }

            if (_requestedMode == PanelStabilityMode.TextCore)
            {
                return new PanelStabilityDecision(PanelStabilitySource.TextCore, "text-core", "configured_text_core");
            }

            if (_safetyFallback)
            {
                return new PanelStabilityDecision(PanelStabilitySource.Panel, "panel", "panel_fallback_safety");
            }

            if (_samples.Count < _warmupItems)
            {
                return new PanelStabilityDecision(PanelStabilitySource.Panel, "panel", "panel_fallback_warmup");
            }

            var panelAverage = _samples.Average(sample => sample.PanelStableMilliseconds);
            var textAverage = _samples.Average(sample => sample.TextCoreStableMilliseconds);
            var panelP90 = Percentile(_samples.Select(sample => sample.PanelStableMilliseconds), 0.90);
            var textP90 = Percentile(_samples.Select(sample => sample.TextCoreStableMilliseconds), 0.90);
            if (textAverage + MinimumTextCoreGainMilliseconds < panelAverage && textP90 <= panelP90)
            {
                return new PanelStabilityDecision(PanelStabilitySource.TextCore, "text-core", "text_core_selected");
            }

            return new PanelStabilityDecision(PanelStabilitySource.Panel, "panel", "panel_fallback_no_gain");
        }

        public void Observe(double? panelStableMilliseconds, double? textCoreStableMilliseconds, string sourceName, string reason)
        {
            if (_requestedMode != PanelStabilityMode.Auto)
            {
                return;
            }

            if (reason == "panel_fallback_safety")
            {
                _safetyFallback = true;
                return;
            }

            if (string.Equals(sourceName, "text-core", StringComparison.OrdinalIgnoreCase)
                && (panelStableMilliseconds is null || panelStableMilliseconds <= 0))
            {
                return;
            }

            if (panelStableMilliseconds is not > 0 || textCoreStableMilliseconds is not > 0)
            {
                return;
            }

            _samples.Add(new PanelStabilitySample(panelStableMilliseconds.Value, textCoreStableMilliseconds.Value));
            if (_samples.Count > 96)
            {
                _samples.RemoveAt(0);
            }
        }

        public void MarkSafetyFallback()
        {
            if (_requestedMode == PanelStabilityMode.Auto)
            {
                _safetyFallback = true;
            }
        }

        private static double Percentile(IEnumerable<double> values, double percentile)
        {
            var sorted = values
                .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
                .OrderBy(value => value)
                .ToArray();
            if (sorted.Length == 0)
            {
                return 0;
            }

            var index = Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1);
            return sorted[index];
        }

        private readonly record struct PanelStabilitySample(double PanelStableMilliseconds, double TextCoreStableMilliseconds);
    }

    private sealed class PanelCapture : IDisposable
    {
        private bool _imageTaken;

        public PanelCapture(
            Bitmap image,
            int visibleRoiCount,
            double waitMilliseconds,
            bool usedFallback,
            ImageSignature[] probeSignatures,
            int frameCount,
            double? changeMilliseconds,
            double? selectionChangeMilliseconds,
            double? fullRoiMilliseconds,
            double? stableMilliseconds,
            double? panelTextStableMilliseconds,
            string panelStableSource,
            string panelStabilityReason,
            double captureMilliseconds,
            double signatureMilliseconds,
            double visibleRoiMilliseconds,
            double frameLoopMilliseconds,
            double frameToBitmapMilliseconds,
            int bitmapCreatedCount,
            int minimumAcceptMilliseconds,
            int requiredStableFrames,
            int adaptiveSampleCount,
            string adaptiveReason,
            string acceptReason,
            bool quickAccept,
            string quickRejectReason,
            PanelAcceptMode panelAcceptMode,
            PostScrollPanelAcceptMode postScrollPanelAcceptMode,
            PanelFloorMode panelFloorMode,
            int panelMinAcceptFloorMs,
            int sameRowPanelFloorMs,
            int postScrollPanelFloorMs,
            string panelFloorReason,
            double floorWaitLimitedMilliseconds,
            double panelAcceptElapsedVsFloorMilliseconds,
            int roiCompleteFrames,
            int selectedStableFrames,
            string acceptGateReason)
        {
            Image = image;
            VisibleRoiCount = visibleRoiCount;
            WaitMilliseconds = waitMilliseconds;
            UsedFallback = usedFallback;
            ProbeSignatures = probeSignatures;
            FrameCount = frameCount;
            ChangeMilliseconds = changeMilliseconds;
            SelectionChangeMilliseconds = selectionChangeMilliseconds;
            FullRoiMilliseconds = fullRoiMilliseconds;
            StableMilliseconds = stableMilliseconds;
            PanelTextStableMilliseconds = panelTextStableMilliseconds;
            PanelStableSource = panelStableSource;
            PanelStabilityReason = panelStabilityReason;
            CaptureMilliseconds = captureMilliseconds;
            SignatureMilliseconds = signatureMilliseconds;
            VisibleRoiMilliseconds = visibleRoiMilliseconds;
            FrameLoopMilliseconds = frameLoopMilliseconds;
            FrameToBitmapMilliseconds = frameToBitmapMilliseconds;
            BitmapCreatedCount = bitmapCreatedCount;
            MinimumAcceptMilliseconds = minimumAcceptMilliseconds;
            RequiredStableFrames = requiredStableFrames;
            AdaptiveSampleCount = adaptiveSampleCount;
            AdaptiveReason = adaptiveReason;
            AcceptReason = acceptReason;
            QuickAccept = quickAccept;
            QuickRejectReason = quickRejectReason;
            PanelAcceptMode = panelAcceptMode;
            PostScrollPanelAcceptMode = postScrollPanelAcceptMode;
            PanelFloorMode = panelFloorMode;
            PanelMinAcceptFloorMs = panelMinAcceptFloorMs;
            SameRowPanelFloorMs = sameRowPanelFloorMs;
            PostScrollPanelFloorMs = postScrollPanelFloorMs;
            PanelFloorReason = panelFloorReason;
            FloorWaitLimitedMilliseconds = floorWaitLimitedMilliseconds;
            PanelAcceptElapsedVsFloorMilliseconds = panelAcceptElapsedVsFloorMilliseconds;
            RoiCompleteFrames = roiCompleteFrames;
            SelectedStableFrames = selectedStableFrames;
            AcceptGateReason = acceptGateReason;
        }

        public Bitmap Image { get; }
        public int VisibleRoiCount { get; }
        public double WaitMilliseconds { get; }
        public bool UsedFallback { get; }
        public ImageSignature[] ProbeSignatures { get; }
        public int FrameCount { get; }
        public double? ChangeMilliseconds { get; }
        public double? SelectionChangeMilliseconds { get; }
        public double? FullRoiMilliseconds { get; }
        public double? StableMilliseconds { get; }
        public double? PanelTextStableMilliseconds { get; }
        public string PanelStableSource { get; }
        public string PanelStabilityReason { get; }
        public double CaptureMilliseconds { get; }
        public double SignatureMilliseconds { get; }
        public double VisibleRoiMilliseconds { get; }
        public double FrameLoopMilliseconds { get; }
        public double FrameToBitmapMilliseconds { get; }
        public int BitmapCreatedCount { get; }
        public int MinimumAcceptMilliseconds { get; }
        public int RequiredStableFrames { get; }
        public int AdaptiveSampleCount { get; }
        public string AdaptiveReason { get; }
        public string AcceptReason { get; }
        public bool QuickAccept { get; }
        public string QuickRejectReason { get; }
        public PanelAcceptMode PanelAcceptMode { get; }
        public PostScrollPanelAcceptMode PostScrollPanelAcceptMode { get; }
        public PanelFloorMode PanelFloorMode { get; }
        public int PanelMinAcceptFloorMs { get; }
        public int SameRowPanelFloorMs { get; }
        public int PostScrollPanelFloorMs { get; }
        public string PanelFloorReason { get; }
        public double FloorWaitLimitedMilliseconds { get; }
        public double PanelAcceptElapsedVsFloorMilliseconds { get; }
        public int RoiCompleteFrames { get; }
        public int SelectedStableFrames { get; }
        public string AcceptGateReason { get; }

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

    private sealed class StalePanelException : TimeoutException
    {
        public StalePanelException(string message)
            : base(message)
        {
        }
    }

    private sealed class PanelCellCaptureException : TimeoutException
    {
        public PanelCellCaptureException(int pass, int visualRow, int column, int maxColumns, int? logicalRow, Exception innerException)
            : base($"详情面板截图等待超时：logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={visualRow}, col={column}/{maxColumns}。", innerException)
        {
            Pass = pass;
            VisualRow = visualRow;
            Column = column;
            MaxColumns = maxColumns;
            LogicalRow = logicalRow;
        }

        public int Pass { get; }
        public int VisualRow { get; }
        public int Column { get; }
        public int MaxColumns { get; }
        public int? LogicalRow { get; }
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

    private readonly record struct OverlapRowCandidate(int LogicalRow, int VisualRow);

    private readonly record struct RarityProbe(string? Rarity, Color BestColor, string BestCandidate, int BestDelta, bool FullScan);

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
        private int _consecutiveIdenticalCount;
        private int _consecutiveSeenDuplicateCount;

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
                _consecutiveIdenticalCount++;
            }
            else
            {
                _consecutiveIdenticalCount = 0;
            }

            if (_seen.Contains(fingerprint))
            {
                _consecutiveSeenDuplicateCount++;
            }
            else
            {
                _consecutiveSeenDuplicateCount = 0;
            }

            _previousFingerprint = fingerprint;
            _seen.Add(fingerprint);

            if (_consecutiveIdenticalCount + 1 >= ConsecutiveIdenticalDuplicateThreshold)
            {
                reason = $"同一驱动盘连续重复达到 {_consecutiveIdenticalCount + 1} 条，疑似详情面板未切换。";
                return false;
            }

            if (_consecutiveSeenDuplicateCount >= _threshold)
            {
                reason = $"连续 {_consecutiveSeenDuplicateCount} 条都已在本轮扫描中出现，疑似整行重复或滚动位移误判。";
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

    private readonly record struct ViewportSignatures(ImageSignature Grid, ImageSignature[] Rows);

    private readonly record struct SafeBandMoveResult(int VisibleTopLogicalRow, bool Scrolled);

    private readonly record struct ScrollSettleResult(
        ViewportSignatures AfterViewport,
        RowScrollVerification Verification,
        int MovedDistance,
        int EstimatedRows,
        bool Success,
        int Polls,
        double WaitMilliseconds,
        double SignatureMilliseconds);

    private readonly record struct RowScrollVerification(int OneRowScore, int NoMoveScore, int TwoRowScore);

    private readonly record struct RowAdvanceEvidence(
        int BestRows,
        int BestScore,
        int SecondScore,
        int Margin,
        int? AcceptedRows,
        int? StrongRows,
        bool Ambiguous,
        int NoMoveScore,
        int OneRowScore,
        int TwoRowScore);

    private readonly record struct OverlapConflictResolution(
        bool Success,
        int RowsAdvanced,
        RowAdvanceEvidence Evidence,
        ImageSignature[]? Rows,
        string Reason)
    {
        public static OverlapConflictResolution Ok(int rowsAdvanced, RowAdvanceEvidence evidence, ImageSignature[]? rows, string reason) =>
            new(true, rowsAdvanced, evidence, rows, reason);

        public static OverlapConflictResolution Fail(string reason) =>
            new(false, 0, default, null, reason);
    }

    private readonly record struct OverlapGapCoverage(bool Safe, int[] CoveredRows, int[] MissingRows);

    private readonly record struct ScrollRowsResult(bool Success, string Message, int RowsAdvanced = 0, bool Retryable = true, ImageSignature[]? AfterRows = null)
    {
        public static ScrollRowsResult Ok(string message, int rowsAdvanced = 0, ImageSignature[]? afterRows = null) => new(true, message, rowsAdvanced, AfterRows: afterRows);
        public static ScrollRowsResult Fail(string message, bool retryable = true) => new(false, message, Retryable: retryable);
    }

}
