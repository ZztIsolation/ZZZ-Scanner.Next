using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;
using ZZZScannerNext.Ocr;
using CvRect = System.Drawing.Rectangle;
using OcrBatchInput = ZZZScannerNext.Ocr.PaddleOcrRecognizer.OcrBatchInput;

namespace ZZZScannerNext.Scanning;

public sealed partial class ScanController
{
    private const int PanelChangeTolerance = 8;
    private const int PanelStrongChangeTolerance = PanelChangeTolerance * 2;
    private const int PanelStableTolerance = 4;
    private const double MinReliablePanelChangeMs = 25.0;
    private const int ListMovementTolerance = 6;
    private const int ListStableTolerance = 4;
    private const int ScrollMaxSmallTicks = 6;
    private const int VerifiedScrollTransactionLimit = 2;
    private const int VerifiedScrollDetentBurstCount = 1;
    private const int VerifiedScrollDetentBurstIntervalMs = 20;
    private const int VerifiedScrollHoverSettleMilliseconds = 40;
    private const int VerifiedScrollReleaseSamples = 6;
    private const int VerifiedScrollReleaseStableFrames = 2;
    private const int ConsecutiveIdenticalDuplicateThreshold = 3;
    private const int SignatureColumns = 8;
    private const int SignatureRows = 4;
    private const int NativeEdgeClickTimeoutMilliseconds = 800;
    private const int NativeEdgeClickNoMoveDecisionMilliseconds = 300;
    private const int NativeEdgeClickStableFrames = 2;

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
        ScanModeDefaults.ApplyFastSafety(options);
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

        var profile = _profiles.Find(profileName);
        if (profile is null
            && requestedFastMode
            && fastModeActive
            && string.Equals(profileName, ScanOptions.FastProfileName, StringComparison.OrdinalIgnoreCase))
        {
            options.FastOcrAssist = false;
            fastModeActive = false;
            fastModeMessage = $"fast scan profile not found: {ScanOptions.FastProfileName}";
            profileName = ScanOptions.DefaultProfileName;
            profile = _profiles.ResolveRequired(profileName);
        }

        profile ??= _profiles.ResolveRequired(profileName);

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
        using var queue = new BlockingCollection<DiscCapture>(boundedCapacity: queueCapacity);
        using var ocrResults = new BlockingCollection<OcrWorkResult>(boundedCapacity: queueCapacity);
        var counters = new Counters();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var traversalMode = ResolveTraversalMode(options, profile);
        var rowAdvanceMode = ResolveRowAdvanceMode(options, profile);
        options.RowAdvanceMode = rowAdvanceMode;
        if (rowAdvanceMode == RowAdvanceMode.NativeEdgeClick
            && traversalMode != ScanTraversalMode.Gamepad
            && traversalMode != ScanTraversalMode.OverlapSignaturePage)
        {
            throw new ArgumentException("NativeEdgeClick row advance requires OverlapSignaturePage traversal.");
        }
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
        scanLog.Write($"Start scan. Profile={profile.Name}, Traversal={traversalMode}, RowAdvanceMode={rowAdvanceMode}, Process={options.ProcessName}, MaxItems={options.MaxItems}, Rarities={string.Join(",", options.Rarities)}, BringToFront={options.BringToFront}, StopAtNonLevel15={options.StopAtNonLevel15}, HighSpeedOcr={options.HighSpeedOcr}, OcrShadowDataset={options.OcrShadowDataset}, FastOcrShadow={options.FastOcrShadow}, FastOcrAssist={options.FastOcrAssist}, FastModeRequested={requestedFastMode}, FastModeActive={fastModeActive}, AdaptiveTimingRequested={options.AdaptiveTiming?.ToString() ?? "auto"}, AdaptiveTimingActive={adaptiveTimingActive}, PanelStabilityMode={options.PanelStabilityMode}, ScrollAcceptMode={options.ScrollAcceptMode}, PanelAcceptMode={options.PanelAcceptMode}, PostScrollPanelAcceptMode={options.PostScrollPanelAcceptMode}, PanelFloorMode={options.PanelFloorMode}, PanelMinAcceptFloorMs={Math.Clamp(options.PanelMinAcceptFloorMs, 90, 120)}, SameRowPanelMinAcceptFloorMs={Math.Clamp(options.SameRowPanelMinAcceptFloorMs, 100, 120)}, PostScrollPanelMinAcceptFloorMs={Math.Clamp(options.PostScrollPanelMinAcceptFloorMs, 100, 120)}, ScrollTickDelayOverrideMs={(options.ScrollTickDelayOverrideMs <= 0 ? 0 : Math.Clamp(options.ScrollTickDelayOverrideMs, 50, 80))}, EffectiveScrollTickDelayMs={runtimeState.EffectiveScrollTickDelayMs}, OverlapConflictMode={options.OverlapConflictMode}, CaptureModeRequested={options.CaptureMode}, CollectVisualProfile={options.CollectVisualProfile}, VisualProfileId={options.VisualProfileId}, VisualProfileClient={options.VisualProfileClient}, VisualQualityLabel={options.VisualQualityLabel}, ProfileRouting={options.ProfileRouting}, OcrWorkers={ocrWorkerCount}, OcrBatchSize={options.OcrBatchSize}, OcrQueueCapacity={queueCapacity}, OcrIntraOpThreads={ocrIntraOpThreads}");
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
        Exception? pendingException = null;
        Exception? ocrException = null;
        ScanSessionDiagnostics? sessionDiagnostics = null;
        var drainingQueuedOcrAfterFailure = false;

        try
        {
            Report(progress, counters, $"加载窗口：{options.ProcessName}");
            using var window = GameWindow.Find(options.ProcessName,
                requiresMouseInput: traversalMode != ScanTraversalMode.Gamepad || options.OpenInventoryFromWorld);
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
            runtimeState.VisualProfileId = visualProfile.ProfileId;
            visualProfile.Save(outputDir);
            scanLog.Write($"VISUAL_PROFILE_SELECTED id={visualProfile.ProfileId}, trainingProfile={visualProfile.TrainingProfileId}, profileFamily={visualProfile.ProfileFamilyId}, geometryStatus={visualProfile.ProfileGeometryStatus}, requested_label={visualProfile.RequestedProfileId}, detected_profile={visualProfile.DetectedProfileId}, detected_geometry={visualProfile.GeometryKey}, clientKind={visualProfile.ClientKind}, quality={visualProfile.QualityLabel}, size={visualProfile.ClientWidth}x{visualProfile.ClientHeight}, dpi={visualProfile.Dpi}, captureRequested={visualProfile.CaptureModeRequested}, captureActive={visualProfile.CaptureModeActive}, frameBackend={visualProfile.CaptureFrameBackend}, profileRouting={options.ProfileRouting}");
            if (!visualProfile.ProfileId.Equals(visualProfile.DetectedProfileId, StringComparison.OrdinalIgnoreCase))
            {
                scanLog.Write($"VISUAL_PROFILE_LABEL_GEOMETRY_MISMATCH requested_label={visualProfile.ProfileId}, detected_profile={visualProfile.DetectedProfileId}, detected_geometry={visualProfile.GeometryKey}");
            }

            using var inventoryRecognizer = new PaddleOcrRecognizer(AppPaths.ModelFile, AppPaths.CharacterDictFile, ocrIntraOpThreads);
            if (options.OpenInventoryFromWorld)
                await OpenInventoryFromWorldAsync(window, profile, inventoryRecognizer, outputDir, scanLog, linked.Token);
            Task[] ocrWorkers = [];
            var resultConsumer = Task.CompletedTask;
            var captureCompleted = false;
            using var gamepad = traversalMode == ScanTraversalMode.Gamepad ? ConnectGamepad(scanLog) : null;

            try
            {
                if (gamepad is not null) window.EnsureGamepadTarget();
                var preflight = await PrepareBackpackAsync(
                    window,
                    profile,
                    inventoryRecognizer,
                    visualProfile.ProfileId,
                    progress,
                    counters,
                    scanLog,
                    linked.Token);
                var transformClass = VisualProbeEvaluator.TransformClassName(preflight.Anchor.TransformClass);
                sessionDiagnostics = new ScanSessionDiagnostics
                {
                    ClientWidth = visualProfile.ClientWidth,
                    ClientHeight = visualProfile.ClientHeight,
                    Dpi = visualProfile.Dpi,
                    CaptureMode = visualProfile.CaptureModeActive,
                    VisualProfileId = visualProfile.ProfileId,
                    PreflightState = "accepted",
                    VisualTransformClass = transformClass,
                    AnchorScore = preflight.Anchor.Score,
                    GridScore = preflight.GridStructureScore,
                    WarehouseHeaderDetected = preflight.HeaderDetected,
                    HeaderScore = preflight.HeaderScore,
                    GridStructureScore = preflight.GridStructureScore,
                    LayoutScore = preflight.LayoutScore,
                    InventoryCountDetected = preflight.InventoryCount.HasValue,
                    CountConsensusFrames = preflight.CountConsensusFrames,
                    HueDelta = preflight.Anchor.HueDelta,
                    SaturationDeltaPct = preflight.Anchor.SaturationDeltaPercent,
                    ValueDeltaPct = preflight.Anchor.ValueDeltaPercent
                };

                var shiftedVisualEnvironment = preflight.Anchor.TransformClass != VisualTransformClass.Neutral;
                if (shiftedVisualEnvironment && options.FastOcrAssist)
                {
                    options.FastOcrAssist = false;
                    scanLog.Write($"Fast OCR assist disabled for shifted visual environment. visualTransformClass={transformClass}; PP-OCR remains authoritative.");
                }

                if (options.FastOcrAssist)
                {
                    var requiredFastOcrLabels = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["mainStat"] = _wikiData.StatRules.SlotMainStats.Values
                            .SelectMany(stats => stats)
                            .Where(stat => !string.IsNullOrWhiteSpace(stat))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .OrderBy(stat => stat, StringComparer.OrdinalIgnoreCase)
                            .ToArray()
                    };
                    fastOcrAssist = FastOcrAssistEngine.TryCreate(
                        options.FastOcrTemplateIndexFile,
                        profile.OrderedRoiKeys(),
                        visualProfile.ProfileId,
                        options.ProfileRouting,
                        requiredFastOcrLabels,
                        scanLog.Write);
                    fastOcrAssistRecorder = fastOcrAssist?.CreateRecorder(outputDir);
                    if (fastOcrAssist is not null)
                    {
                        scanLog.Write($"Fast OCR assist enabled. Index={fastOcrAssist.IndexFile}, Templates={fastOcrAssist.TemplateCount}, VisualProfile={fastOcrAssist.VisualProfileId}, ProfileRouting={fastOcrAssist.ProfileRoutingMode}, Csv={Path.Combine(outputDir, "ocr_fast_assist.csv")}");
                    }
                }

                var warehousePolicy = (profile.VisualProbes ?? new VisualProbeOptions()).WarehousePreflight ?? new WarehousePreflightPolicy();
                var contextGuard = new WarehouseContextGuard(
                    window,
                    preflight.MonitorPlan,
                    warehousePolicy,
                    inventoryRecognizer,
                    scanLog);
                window.ConfigureInputGuard(() =>
                {
                    if (options.OpenInventoryFromWorld) { linked.Token.ThrowIfCancellationRequested(); window.EnsureGamepadTarget(); }
                    contextGuard.EnsureHealthy();
                });
                if (gamepad is null)
                {
                    window.LeftClick(window.ToScreenPoint(profile.Point("driveDiscTab")));
                    await Task.Delay(profile.ClickDelayMs, linked.Token);
                    await ResetListToTopAsync(window, profile, progress, counters, scanLog, linked.Token);
                    Report(progress, counters, "已定位到驱动盘列表顶部。");
                }
                else
                {
                    if (options.OpenInventoryFromWorld)
                        await SelectFirstDiscForGamepadAsync(window, profile, outputDir, progress, counters, scanLog, linked.Token);
                    Report(progress, counters, "手柄扫描：确认背包首盘后开始，切出游戏即可停止输入。");
                }

                ocrWorkers = StartOcrWorkers(queue, ocrResults, outputDir, options, scanLog, ocrWorkerCount, ocrIntraOpThreads, counters, ocrDiagnostics, ocrShadowDataset, fastOcrShadow, fastOcrAssist, fastOcrAssistRecorder, shiftedVisualEnvironment, linked);
                resultConsumer = Task.Run(() =>
                {
                    try { ConsumeOcrResults(ocrResults, results, counters, outputDir, progress, scanLog, duplicateGuard, options, linked); }
                    catch { linked.Cancel(); throw; }
                });
                if (gamepad is not null)
                    await ProduceGamepadCapturesAsync(window, profile, queue, options, runtimeState, counters, progress, scanLog,
                        preflight.InventoryCount, gamepad, contextGuard.EnsureHealthy, outputDir, linked.Token);
                else
                    await ProduceCapturesAsync(window, profile, queue, options, runtimeState, counters, progress, scanLog, preflight.InventoryCount, traversalMode, linked.Token);
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
                if (ex is IScannerFailureException scannerFailure)
                {
                    Interlocked.CompareExchange(ref counters.StopReason, scannerFailure.Code, null);
                }
                scanLog.Write("Scan failed:");
                scanLog.Write(ex.ToString());
                Report(progress, counters, $"扫描失败：{ex.Message}");
            }
            finally
            {
                queue.CompleteAdding();
                var earlyStopDisposition = ScanEarlyStopPolicy.Resolve(
                    captureCompleted,
                    pendingException is not null,
                    cancellationToken.IsCancellationRequested,
                    !string.IsNullOrWhiteSpace(Volatile.Read(ref counters.StopReason)));
                if (earlyStopDisposition == ScanEarlyStopDisposition.DrainQueuedOcr)
                {
                    drainingQueuedOcrAfterFailure = true;
                    var backlog = Math.Max(0, Volatile.Read(ref counters.Queued) - Volatile.Read(ref counters.Completed) - Volatile.Read(ref counters.Failed));
                    scanLog.WriteEvent(
                        "OCR_DRAIN_START",
                        $"reason=capture_failure, queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}, backlog={backlog}");
                }
                else if (earlyStopDisposition == ScanEarlyStopDisposition.DiscardQueuedOcr)
                {
                    Interlocked.CompareExchange(ref counters.StopAfterIndex, Math.Max(1, Volatile.Read(ref counters.Queued)), 0);
                    var discarded = DisposeQueuedCaptures(queue);
                    if (discarded > 0)
                    {
                        scanLog.Write($"Discarded {discarded} queued captures after early stop.");
                    }

                    linked.Cancel();
                }
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
                // Workers can terminate before consuming the remaining bitmaps.
                DisposeQueuedCaptures(queue);
            }

            await resultConsumer;
            if (drainingQueuedOcrAfterFailure)
            {
                var remaining = Math.Max(0, Volatile.Read(ref counters.Queued) - Volatile.Read(ref counters.Completed) - Volatile.Read(ref counters.Failed));
                scanLog.WriteEvent(
                    "OCR_DRAIN_DONE",
                    $"queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}, remaining={remaining}, discarded=0");
            }

            linked.Cancel();
        }
        catch (Exception ex)
        {
            pendingException ??= ex;
            scanLog.Write("Scan session failed before terminal export:");
            scanLog.Write(ex.ToString());
        }
        finally
        {
            fastOcrAssistRecorder?.Dispose();
            await resourceMonitor.StopAsync();
        }

        var ordered = results.OrderBy(x => x.Index).ToList();
        var terminationCode = Volatile.Read(ref counters.StopReason) ?? "";
        var partial = pendingException is not null
            || ocrException is not null
            || cancellationToken.IsCancellationRequested
            || counters.Failed > 0
            || !string.IsNullOrWhiteSpace(terminationCode);
        var exportFile = Path.Combine(outputDir, partial ? "export.partial.json" : "export.json");
        await File.WriteAllTextAsync(exportFile, JsonSerializer.Serialize(ordered, JsonDefaults.Write), CancellationToken.None);
        scanLog.WriteEvent(
            "SCAN_TERMINAL",
            $"visited={counters.Visited}, queued={counters.Queued}, completed={counters.Completed}, failed={counters.Failed}, partial={partial}, terminationCode={terminationCode}, exportFile={Path.GetFileName(exportFile)}");
        var terminalSnapshot = new ScanTerminalSnapshot(
            outputDir,
            exportFile,
            ordered.Count,
            counters.Visited,
            counters.Queued,
            counters.Completed,
            counters.Failed,
            partial,
            terminationCode);

        if (pendingException is not null)
        {
            pendingException = new ScanSessionDiagnosticException(pendingException, sessionDiagnostics, terminalSnapshot);

            ExceptionDispatchInfo.Capture(pendingException).Throw();
        }

        if (ocrException is not null)
        {
            throw new ScanSessionDiagnosticException(new ScannerFailureException(
                "ocr_worker_failed",
                "OCR 识别进程失败",
                "后台 OCR 工作线程发生异常。",
                "请重新扫描；如果持续发生，请打开日志并提供诊断信息。",
                innerException: ocrException), sessionDiagnostics, terminalSnapshot);
        }

        Report(progress, counters, $"完成：输出 {ordered.Count} 条，失败 {counters.Failed} 条。");
        return new ScanSessionResult
        {
            OutputDirectory = outputDir,
            ExportFile = exportFile,
            Items = ordered,
            Visited = counters.Visited,
            Queued = counters.Queued,
            Completed = counters.Completed,
            Failed = counters.Failed,
            Partial = partial,
            TerminationCode = terminationCode,
            Diagnostics = sessionDiagnostics
        };
    }

    public async Task<EdgeScrollProbeResult> ProbeNativeEdgeScrollAsync(
        ScanOptions options,
        int runs,
        CancellationToken token)
    {
        runs = Math.Clamp(runs, 1, 100);
        var profile = _profiles.ResolveRequired(options.ProfileName);
        var outputDir = AppPaths.CreateScanDirectory();
        using var scanLog = new ScanLog(Path.Combine(outputDir, "scan.log"));
        using var window = GameWindow.Find(options.ProcessName);
        if (options.BringToFront)
        {
            window.BringToFront();
        }

        window.ConfigureCaptureMode(options.CaptureMode, scanLog.Write);
        using var inventoryRecognizer = new PaddleOcrRecognizer(AppPaths.ModelFile, AppPaths.CharacterDictFile, 1);
        var progress = new Progress<ScanProgress>(_ => { });
        var counters = new Counters();
        var preflight = await PrepareBackpackAsync(
            window,
            profile,
            inventoryRecognizer,
            "edge-scroll-probe",
            progress,
            counters,
            scanLog,
            token);
        var warehousePolicy = (profile.VisualProbes ?? new VisualProbeOptions()).WarehousePreflight ?? new WarehousePreflightPolicy();
        var contextGuard = new WarehouseContextGuard(
            window,
            preflight.MonitorPlan,
            warehousePolicy,
            inventoryRecognizer,
            scanLog);
        window.ConfigureInputGuard(contextGuard.EnsureHealthy);
        window.LeftClick(window.ToScreenPoint(profile.Point("driveDiscTab")));
        await Task.Delay(profile.ClickDelayMs, token);

        var offset = profile.Point("driveDiscOffset");
        var step = profile.Point("driveDiscStep");
        var columns = Math.Max(1, profile.VisibleColumns);
        var visibleRows = Math.Max(1, profile.VisibleRows);
        var totalRows = preflight.InventoryCount is > 0
            ? Math.Max(1, (int)Math.Ceiling(preflight.InventoryCount.Value / (double)columns))
            : (int?)null;
        var maxVisibleTop = totalRows.HasValue
            ? Math.Max(1, totalRows.Value - visibleRows + 1)
            : int.MaxValue;
        var listGridRect = ProfileRectangleOrFallback(
            window,
            profile,
            "listGridRect",
            BuildListGridFallback(window, profile, visibleRows, columns));
        var rowSignatureRects = BuildVisualRowSignatureRects(listGridRect, visibleRows);
        var changedRuns = 0;
        var bottomRuns = 0;
        var failedRuns = 0;
        scanLog.WriteEvent(
            "EDGE_CLICK_PROBE_START",
            $"runs={runs}, inventoryCount={preflight.InventoryCount?.ToString() ?? "unknown"}, totalRows={totalRows?.ToString() ?? "unknown"}, visibleRows={visibleRows}, columns={columns}, captureMode={window.ActiveCaptureMode}, client={window.ClientScreenRect.Width}x{window.ClientScreenRect.Height}, dpi={window.Dpi}");

        for (var run = 1; run <= runs; run++)
        {
            token.ThrowIfCancellationRequested();
            await ResetListToTopAsync(window, profile, progress, counters, scanLog, token);
            await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);
            var armClientPoint = new PointF(
                offset.X + step.X * columns,
                offset.Y + step.Y * 3);
            var armPoint = window.ToScreenPoint(armClientPoint);
            window.MoveCursor(armPoint);
            window.LeftClickCurrent();
            await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
            await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);
            scanLog.WriteEvent(
                "EDGE_CLICK_PROBE_ARM",
                $"run={run}/{runs}, visualRow=3, column={columns}, point={armPoint}, reason=match_scan_pre_advance_selection");
            var result = await AdvanceNativeEdgeClickAsync(
                window,
                profile,
                listGridRect,
                rowSignatureRects,
                offset,
                step,
                visibleTopLogicalRow: 1,
                maxVisibleTop,
                preflight.InventoryCount ?? 0,
                scannedRows: 0,
                scanLog,
                token);
            var decision = NativeEdgeClickPolicy.ResolveSettle(result);
            if (decision == NativeEdgeClickDecision.AdvanceAssumedOne)
            {
                changedRuns++;
            }
            else if (decision == NativeEdgeClickDecision.HashFallback && totalRows is <= 4)
            {
                bottomRuns++;
            }
            else
            {
                failedRuns++;
            }

            scanLog.WriteEvent(
                "EDGE_CLICK_PROBE_RESULT",
                $"run={run}/{runs}, decision={decision}, settled={result.Settled}, changed={result.Changed}, movementDistance={result.MovementDistance}, stableFrames={result.StableFrames}, samples={result.Samples}, elapsedMs={result.ElapsedMilliseconds:F1}, expectedMovement={totalRows is > 4}, reason={result.Reason}");
        }

        var probeResult = new EdgeScrollProbeResult
        {
            Success = failedRuns == 0 && changedRuns + bottomRuns == runs,
            OutputDirectory = outputDir,
            RequestedRuns = runs,
            ChangedRuns = changedRuns,
            BottomRuns = bottomRuns,
            FailedRuns = failedRuns,
            InventoryCount = preflight.InventoryCount,
            ClientWidth = window.ClientScreenRect.Width,
            ClientHeight = window.ClientScreenRect.Height,
            Dpi = window.Dpi,
            CaptureMode = window.ActiveCaptureMode.ToString()
        };
        scanLog.WriteEvent(
            "EDGE_CLICK_PROBE_DONE",
            $"success={probeResult.Success}, runs={runs}, changed={changedRuns}, bottom={bottomRuns}, failed={failedRuns}");
        return probeResult;
    }

    private static async Task<VisualPreflightResult> PrepareBackpackAsync(
        GameWindow window,
        ScanProfile profile,
        PaddleOcrRecognizer inventoryRecognizer,
        string visualProfileId,
        IProgress<ScanProgress> progress,
        Counters counters,
        ScanLog scanLog,
        CancellationToken token)
    {
        Report(progress, counters, "等待背包驱动盘界面。");
        var visualOptions = profile.VisualProbes ?? new VisualProbeOptions();
        var warehousePolicy = visualOptions.WarehousePreflight ?? new WarehousePreflightPolicy();
        var aspectRatio = window.ClientScreenRect.Height <= 0
            ? 0
            : window.ClientScreenRect.Width / (double)window.ClientScreenRect.Height;
        if (Math.Abs(aspectRatio - (16d / 9d)) > 0.03)
        {
            throw VisualPreflightException.Create(
                "unsupported_display_layout",
                "游戏客户区不是受支持的 16:9 布局，本次没有继续点击或滚动。",
                default,
                headerDetected: false,
                headerScore: 0,
                gridStructureScore: 0,
                layoutScore: 0,
                inventoryCountDetected: false,
                countConsensusFrames: 0,
                stableFrames: 0,
                warehousePolicy.RequiredStableFrames,
                window,
                visualProfileId);
        }

        var headerRect = window.ToScreenRectangle(profile.Rectangle("inventoryCount"));
        var listGridRect = ProfileRectangleOrFallback(
            window,
            profile,
            "listGridRect",
            BuildListGridFallback(window, profile, Math.Max(1, profile.VisibleRows), Math.Max(1, profile.VisibleColumns)));
        var detailPanelRect = window.ToScreenRectangle(profile.Rectangle("detailPanel"));
        var probeBounds = Rectangle.Union(Rectangle.Union(headerRect, listGridRect), detailPanelRect);
        probeBounds = Rectangle.Intersect(window.ClientScreenRect, probeBounds);
        var driveDiscOffset = window.ToScreenPoint(profile.Point("driveDiscOffset"));
        var driveDiscStepNormalized = profile.Point("driveDiscStep");
        var driveDiscStep = window.ToClientSize(new SizeF(driveDiscStepNormalized.X, driveDiscStepNormalized.Y));
        var requiredStableFrames = Math.Max(1, warehousePolicy.RequiredStableFrames);
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(warehousePolicy.PollMilliseconds, 100, 1000));
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(1, profile.WaitForBackpackSeconds));
        var gate = new VisualPreflightGate(requiredStableFrames);
        var lastHealth = new CaptureHealthResult(false, 0, 0, 0, 100, 0, 0);
        var lastHeader = new WarehouseHeaderProbeResult(false, 0, 4, 0, null, null, false, string.Empty);
        var lastStructure = new WarehouseStructureProbeResult(0, 0, 0, 0, 0, 0, 0);

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            using var frame = window.Capture(probeBounds);
            lastHealth = WarehousePreflightEvaluator.EvaluateCaptureHealth(frame);
            var localHeaderRect = ToLocalRectangle(headerRect, probeBounds, frame.Size);
            var localListGridRect = ToLocalRectangle(listGridRect, probeBounds, frame.Size);
            var localDetailPanelRect = ToLocalRectangle(detailPanelRect, probeBounds, frame.Size);
            lastHeader = lastHealth.Passed
                ? ReadWarehouseHeader(frame, localHeaderRect, inventoryRecognizer, warehousePolicy, scanLog)
                : new WarehouseHeaderProbeResult(false, 0, 4, 0, null, null, false, string.Empty);
            lastStructure = lastHealth.Passed
                ? WarehousePreflightEvaluator.EvaluateStructure(
                    frame,
                    localListGridRect,
                    localDetailPanelRect,
                    new Point(driveDiscOffset.X - probeBounds.Left, driveDiscOffset.Y - probeBounds.Top),
                    driveDiscStep)
                : new WarehouseStructureProbeResult(0, 0, 0, 0, 0, 0, 0);
            var gridPassed = lastStructure.GridStructureScore >= warehousePolicy.GridMinimumScore;
            var layoutPassed = lastStructure.LayoutScore >= warehousePolicy.LayoutMinimumScore;
            var accepted = gate.Observe(lastHealth.Passed, lastHeader.HeaderDetected, gridPassed, layoutPassed);
            scanLog.WriteEvent(
                "VISUAL_PREFLIGHT",
                $"captureHealthy={lastHealth.Passed}, captureScore={lastHealth.Score}, meanLuma={lastHealth.MeanLuminance}, lumaStdDev={lastHealth.LuminanceStandardDeviation}, darkPct={lastHealth.DarkPixelPercent}, brightPct={lastHealth.BrightPixelPercent}, edgeDensityPermille={lastHealth.EdgeDensityPermille}, headerDetected={lastHeader.HeaderDetected}, headerScore={lastHeader.HeaderScore}, titleEditDistance={lastHeader.TitleEditDistance}, headerConfidence={lastHeader.Confidence:F3}, normalizedRetry={lastHeader.UsedNormalizedImage}, inventoryCountDetected={lastHeader.InventoryCountDetected}, gridCells={lastStructure.RecognizedGridCells}/6, gridStructureScore={lastStructure.GridStructureScore}, gridEdgeDensityPermille={lastStructure.GridEdgeDensityPermille}, layoutScore={lastStructure.LayoutScore}, layoutEdgeDensityPermille={lastStructure.LayoutEdgeDensityPermille}, verticalLineScore={lastStructure.VerticalLineScore}, horizontalLineScore={lastStructure.HorizontalLineScore}, stableFrames={gate.StableFrames}/{requiredStableFrames}, capture={window.ActiveCaptureMode}");

            if (accepted)
            {
                scanLog.Write($"Warehouse page confirmed without input. headerScore={lastHeader.HeaderScore}, gridStructureScore={lastStructure.GridStructureScore}, layoutScore={lastStructure.LayoutScore}.");
                var consensus = await ReadInventoryCountConsensusAsync(
                    window,
                    profile,
                    inventoryRecognizer,
                    warehousePolicy,
                    scanLog,
                    token);
                if (!consensus.InventoryCount.HasValue)
                {
                    throw InventoryCountOcrFailure(
                        "已确认驱动盘仓库，但仓库数量未能在独立画面中形成一致结果；本次没有继续点击或滚动。",
                        ScanDiagnosticDetails.Preflight(
                            "inventory_count_ocr_failed",
                            "unknown",
                            anchorScore: 0,
                            lastStructure.GridStructureScore,
                            lastHeader.HeaderDetected,
                            lastHeader.HeaderScore,
                            lastStructure.GridStructureScore,
                            lastStructure.LayoutScore,
                            inventoryCountDetected: false,
                            consensus.ConsensusFrames,
                            hueDelta: 0,
                            saturationDeltaPct: 0,
                            valueDeltaPct: 0,
                            gate.StableFrames,
                            requiredStableFrames,
                            window.ClientScreenRect.Width,
                            window.ClientScreenRect.Height,
                            window.Dpi,
                            window.ActiveCaptureMode,
                            visualProfileId));
                }

                var monitorPlan = CreateWarehouseMonitorPlan(window, profile);
                var backpackPolicy = visualOptions.BackpackReady ?? new ChromaticProbePolicy();
                var center = window.ToScreenPoint(profile.Point("dismantleButton"));
                var radiusX = Math.Max(4, (int)Math.Round(backpackPolicy.Radius * window.ClientScreenRect.Width / (double)profile.StandardScreen[0]));
                var radiusY = Math.Max(4, (int)Math.Round(backpackPolicy.Radius * window.ClientScreenRect.Height / (double)profile.StandardScreen[1]));
                var anchorRect = Rectangle.Intersect(
                    window.ClientScreenRect,
                    Rectangle.FromLTRB(center.X - radiusX, center.Y - radiusY, center.X + radiusX + 1, center.Y + radiusY + 1));
                using var anchorImage = window.Capture(anchorRect);
                var anchor = VisualProbeEvaluator.EvaluateChromaticAnchor(anchorImage, profile.Color("dismantleButton"), backpackPolicy);
                scanLog.Write($"Post-confirmation color diagnostic. passed={anchor.Passed}, score={anchor.Score}, transform={VisualProbeEvaluator.TransformClassName(anchor.TransformClass)}. This result does not affect warehouse readiness.");
                return new VisualPreflightResult(
                    anchor,
                    lastHeader.HeaderDetected,
                    lastHeader.HeaderScore,
                    lastStructure.GridStructureScore,
                    lastStructure.LayoutScore,
                    consensus.InventoryCount,
                    consensus.InventoryCapacity,
                    consensus.ConsensusFrames,
                    monitorPlan);
            }

            await Task.Delay(poll, token);
        }

        var structuralEvidence = lastStructure.GridStructureScore >= warehousePolicy.GridMinimumScore
            || lastStructure.LayoutScore >= warehousePolicy.LayoutMinimumScore;
        var partialWarehouseEvidence = lastHeader.HeaderDetected || structuralEvidence;
        var reason = !lastHealth.Passed
            ? "capture_unavailable"
            : partialWarehouseEvidence
                ? "inventory_screen_unreadable"
                : "inventory_screen_not_detected";
        scanLog.Write($"Visual preflight failed. reason={reason}, captureScore={lastHealth.Score}, headerScore={lastHeader.HeaderScore}, gridStructureScore={lastStructure.GridStructureScore}, layoutScore={lastStructure.LayoutScore}, stableFrames={gate.StableFrames}/{requiredStableFrames}.");
        throw VisualPreflightException.Create(
            reason,
            "未能安全确认驱动盘仓库界面。请保持游戏可见并确认仓库布局正常；本次没有继续点击或滚动。",
            default,
            lastHeader.HeaderDetected,
            lastHeader.HeaderScore,
            lastStructure.GridStructureScore,
            lastStructure.LayoutScore,
            lastHeader.InventoryCountDetected,
            countConsensusFrames: 0,
            gate.StableFrames,
            requiredStableFrames,
            window,
            visualProfileId);
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
        var scrollBottom = window.ToScreenPoint(profile.Point("scrollBarBottom"));
        var wheelPoint = window.ToScreenPoint(profile.Point("listWheelArea"));
        var expectedColor = profile.Color("scrollBar");
        var maximumWheelTicks = Math.Min(
            Math.Max(0, profile.ResetToTopWheelTicks),
            ScrollTopResetCoordinator.MaximumWheelTicks);
        window.MoveCursor(wheelPoint);
        var resetDelay = Math.Clamp(profile.ResetToTopWheelDelayMs, 20, 80);
        var fallbackResetAction = "not_run";
        var lastResetThumb = default(ScrollbarThumbProbe);

        void Trace(ScrollTopResetTrace trace)
        {
            switch (trace.Kind)
            {
                case ScrollTopResetTraceKind.Probe:
                    scanLog.WriteEvent(
                        "RESET_TOP_COLOR_PROBE",
                        $"phase={trace.Phase}, batch={trace.Batch}, sample={trace.Sample}/{ScrollTopResetCoordinator.ProbeSampleCount}, tick={trace.WheelTicks}/{maximumWheelTicks}, actual={ColorText(trace.ActualColor)}, expected={ColorText(trace.ExpectedColor)}, delta=({Math.Abs(trace.ActualColor.R - trace.ExpectedColor.R)},{Math.Abs(trace.ActualColor.G - trace.ExpectedColor.G)},{Math.Abs(trace.ActualColor.B - trace.ExpectedColor.B)}), tolerance={trace.Tolerance}, matched={trace.Matched}, matchReason={trace.MatchReason}, stableMatches={trace.StableMatches}/{ScrollTopResetCoordinator.RequiredStableMatches}, thumbFound={lastResetThumb.Found}, thumbX={lastResetThumb.CenterX}, thumb={lastResetThumb.StartY}-{lastResetThumb.EndY}, elapsedMs={trace.ElapsedMilliseconds}");
                    break;
                case ScrollTopResetTraceKind.Wheel:
                    scanLog.WriteEvent(
                        "RESET_WHEEL",
                        $"batch={trace.Batch}, tick={trace.WheelTicks}/{maximumWheelTicks}, delta=120, cursor={wheelPoint}, elapsedMs={trace.ElapsedMilliseconds}");
                    break;
                case ScrollTopResetTraceKind.Settle:
                    scanLog.WriteEvent(
                        "RESET_TOP_SETTLE",
                        $"phase={trace.Phase}, batch={trace.Batch}, tick={trace.WheelTicks}/{maximumWheelTicks}, delayMs={trace.DelayMilliseconds}, elapsedMs={trace.ElapsedMilliseconds}");
                    break;
                case ScrollTopResetTraceKind.Click:
                    if (fallbackResetAction.StartsWith("thumb_drag", StringComparison.Ordinal))
                    {
                        scanLog.WriteEvent(
                            "RESET_TOP_THUMB_DRAG",
                            $"{fallbackResetAction}, attempt={trace.TopClicks}, tick={trace.WheelTicks}/{maximumWheelTicks}, elapsedMs={trace.ElapsedMilliseconds}");
                    }
                    else
                    {
                        scanLog.WriteEvent(
                            "RESET_TOP_CLICK",
                            $"point={scrollTop}, click={trace.TopClicks}, tick={trace.WheelTicks}/{maximumWheelTicks}, action={fallbackResetAction}, elapsedMs={trace.ElapsedMilliseconds}");
                    }
                    break;
                case ScrollTopResetTraceKind.Confirmed:
                    scanLog.WriteEvent(
                        "RESET_TOP_CONFIRMED",
                        $"phase={trace.Phase}, batch={trace.Batch}, tick={trace.WheelTicks}/{maximumWheelTicks}, clicks={trace.TopClicks}, actual={ColorText(trace.ActualColor)}, expected={ColorText(trace.ExpectedColor)}, stableMatches={trace.StableMatches}/{ScrollTopResetCoordinator.RequiredStableMatches}, elapsedMs={trace.ElapsedMilliseconds}");
                    break;
                case ScrollTopResetTraceKind.Failed:
                    scanLog.WriteEvent(
                        "RESET_TOP_FAILED",
                        $"reason=scroll_top_color_unconfirmed, phase={trace.Phase}, batch={trace.Batch}, tick={trace.WheelTicks}/{maximumWheelTicks}, clicks={trace.TopClicks}, actual={ColorText(trace.ActualColor)}, expected={ColorText(trace.ExpectedColor)}, tolerance={trace.Tolerance}, elapsedMs={trace.ElapsedMilliseconds}");
                    break;
            }
        }

        var result = await ScrollTopResetCoordinator.RunAsync(
            maximumWheelTicks,
            resetDelay,
            profile.ClickDelayMs,
            expectedColor,
            profile.ColorTolerance,
            () =>
            {
                lastResetThumb = CaptureScrollbarThumbProbe(window, profile);
                return window.GetPixel(scrollTop);
            },
            () => window.MouseWheel(120),
            () =>
            {
                var thumb = CaptureScrollbarThumbProbe(window, profile);
                if (thumb.Found)
                {
                    var targetCenterY = ScrollbarTopResetPlanner.GetTopCenterY(
                        scrollTop.Y,
                        scrollBottom.Y,
                        thumb.StartY,
                        thumb.EndY);
                    var start = new Point(thumb.CenterX, thumb.CenterY);
                    var end = new Point(thumb.CenterX, targetCenterY);
                    fallbackResetAction = $"thumb_drag, start={start}, end={end}, thumb={thumb.StartY}-{thumb.EndY}";
                    window.LeftDrag(start, end, durationMs: 160);
                }
                else
                {
                    fallbackResetAction = "track_click_no_thumb";
                    window.LeftClick(scrollTop, durationMs: 30);
                }

                window.MoveCursor(wheelPoint);
            },
            Trace,
            token,
            captureTopPosition: () =>
                lastResetThumb.Found
                && ScrollbarTopResetPlanner.IsAtTop(
                    scrollTop.Y,
                    scrollBottom.Y,
                    lastResetThumb.StartY,
                    lastResetThumb.EndY));
        if (!result.Confirmed)
        {
            throw NavigationFailure(
                "驱动盘列表顶部颜色探针在有界滚轮和顶部点击后仍未确认。为避免继续上滑或从错误位置扫描，本次已停止。",
                new Dictionary<string, object?>
                {
                    ["phase"] = "reset_to_top",
                    ["acceptGateReason"] = "scroll_top_color_unconfirmed",
                    ["wheelTicks"] = result.WheelTicks,
                    ["topClicks"] = result.TopClicks,
                    ["fallbackResetAction"] = fallbackResetAction,
                    ["probeSamples"] = result.ProbeSamples,
                    ["actualColor"] = ColorText(result.LastActualColor),
                    ["expectedColor"] = ColorText(expectedColor),
                    ["colorTolerance"] = profile.ColorTolerance,
                    ["elapsedMs"] = result.ElapsedMilliseconds
                });
        }

        scanLog.Write($"Reset confirmed the scrollbar thumb at the top via {result.Phase}; wheelTicks={result.WheelTicks}, clicks={result.TopClicks}, elapsedMs={result.ElapsedMilliseconds}.");
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
            throw InventoryCountOcrFailure("仓库数量 OCR 失败，重叠签名扫描无法计算总行数。请确认数量区域可见后重试。");
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
            throw InventoryCountOcrFailure("仓库数量 OCR 失败，安全带扫描无法计算总行数。请确认数量区域可见后重试。");
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
            throw InventoryCountOcrFailure("仓库数量 OCR 失败，校准翻页无法计算总行数。请确认数量区域可见后重试。");
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
        var nativeEdgeClick = options.RowAdvanceMode == RowAdvanceMode.NativeEdgeClick;
        string? lastNativeMiddleRowHash = null;

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

                var rowFingerprint = nativeEdgeClick && visualRow == 3
                    ? new RowDetailFingerprintBuilder()
                    : null;
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
                        startColumn: startColumn,
                        rowFingerprint: rowFingerprint);
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
                if (rowFingerprint is not null)
                {
                    if (rowFingerprint.Count != maxColumns)
                    {
                        throw NavigationFailure($"原生底行点击模式无法建立完整的视觉第 3 行指纹：logicalRow={logicalRow}, captured={rowFingerprint.Count}, expected={maxColumns}。");
                    }

                    lastNativeMiddleRowHash = rowFingerprint.Complete();
                    scanLog.WriteEvent("EDGE_CLICK_ROW_HASH", $"phase=baseline, iteration={iteration}, logicalRow={logicalRow}, visualRow={visualRow}, columns={rowFingerprint.Count}, rowHash={lastNativeMiddleRowHash}");
                }
            }

            if (scannedLogicalRows.Count >= totalRows)
            {
                break;
            }

            if (visibleTopLogicalRow >= maxVisibleTop)
            {
                var missing = Enumerable.Range(1, totalRows).Where(row => !scannedLogicalRows.Contains(row)).Take(8).ToArray();
                throw NavigationFailure($"重叠签名扫描到达底部但仍有逻辑行未扫：{string.Join(",", missing)}。为避免漏扫，本次停止。");
            }

            var beforeTop = visibleTopLogicalRow;
            if (nativeEdgeClick)
            {
                var edgeClick = await AdvanceNativeEdgeClickAsync(
                    window,
                    profile,
                    listGridRect,
                    rowSignatureRects,
                    offset,
                    step,
                    visibleTopLogicalRow,
                    maxVisibleTop,
                    inventoryCount,
                    scannedLogicalRows.Count,
                    scanLog,
                    token);
                var edgeDecision = NativeEdgeClickPolicy.ResolveSettle(edgeClick);
                if (edgeDecision == NativeEdgeClickDecision.AdvanceAssumedOne)
                {
                    visibleTopLogicalRow = Math.Min(maxVisibleTop, visibleTopLogicalRow + 1);
                    await ArmNativeEdgeFollowupSelectionAsync(
                        window,
                        profile,
                        listGridRect,
                        offset,
                        step,
                        columns,
                        visibleTopLogicalRow,
                        scanLog,
                        token);
                    lastScrollChangedViewport = true;
                    scanLog.WriteEvent("EDGE_CLICK_CHANGED", $"iteration={iteration}, fromTop={beforeTop}, toTop={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, assumedRows=1, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, samples={edgeClick.Samples}, elapsedMs={edgeClick.ElapsedMilliseconds:F1}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={lastNativeMiddleRowHash ?? "unknown"}, currentRowHash=unknown, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, reason=native_edge_click_changed_assume_one");
                    scanLog.WriteEvent("OVERLAP_SCROLL_ACCEPTED", $"iteration={iteration}, fromTop={beforeTop}, toTop={visibleTopLogicalRow}, rowsAdvanced=1, decision=AssumedOne, reason=native_edge_click_changed_assume_one, scannedThisViewport={scannedThisViewport}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
                    continue;
                }

                if (edgeDecision == NativeEdgeClickDecision.Stop)
                {
                    scanLog.WriteEvent("EDGE_CLICK_STOP", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, samples={edgeClick.Samples}, elapsedMs={edgeClick.ElapsedMilliseconds:F1}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={lastNativeMiddleRowHash ?? "unknown"}, currentRowHash=unknown, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, reason={edgeClick.Reason}");
                    throw NavigationFailure($"底行点击后列表未在 {NativeEdgeClickTimeoutMilliseconds}ms 内稳定：{edgeClick.Reason}。");
                }

                if (string.IsNullOrWhiteSpace(lastNativeMiddleRowHash))
                {
                    scanLog.WriteEvent("EDGE_CLICK_STOP", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash=unknown, currentRowHash=unknown, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, reason=missing_previous_row_hash");
                    throw NavigationFailure("底行点击未观察到列表变化，且缺少上一视觉第 3 行指纹，无法判断是否到底。");
                }

                await ArmNativeEdgeFollowupSelectionAsync(
                    window,
                    profile,
                    listGridRect,
                    offset,
                    step,
                    columns,
                    visibleTopLogicalRow,
                    scanLog,
                    token);

                using var bufferedRow = new BufferedRowCapture();
                var fallbackFingerprint = new RowDetailFingerprintBuilder();
                var fallbackLogicalRow = visibleTopLogicalRow + 3;
                RowScanResult fallbackResult;
                try
                {
                    fallbackResult = await ScanVisualRowAsync(
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
                        pass: fallbackLogicalRow,
                        row: 3,
                        isBottom: false,
                        maxColumns: columns,
                        logicalRow: fallbackLogicalRow,
                        treatBlankAsEnd: false,
                        panelChangeProbeRect,
                        token,
                        enforceSafeBand: false,
                        visibleTopLogicalRow: visibleTopLogicalRow + 1,
                        maxVisibleTop: maxVisibleTop,
                        afterScroll: true,
                        postScrollFirstCell: true,
                        rowFingerprint: fallbackFingerprint,
                        bufferedRow: bufferedRow);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var failureCode = ex is IScannerFailureException scannerFailure
                        ? scannerFailure.Code
                        : ex.GetType().Name;
                    scanLog.WriteEvent("EDGE_CLICK_STOP", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={lastNativeMiddleRowHash}, currentRowHash=incomplete, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, reason=hash_fallback_capture_failed, failureCode={failureCode}");
                    throw;
                }
                if (fallbackResult != RowScanResult.Completed || fallbackFingerprint.Count != columns)
                {
                    scanLog.WriteEvent("EDGE_CLICK_STOP", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={lastNativeMiddleRowHash}, currentRowHash=incomplete, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, reason=incomplete_hash_fallback, captured={fallbackFingerprint.Count}, expected={columns}");
                    throw NavigationFailure("底行点击行哈希兜底未能捕获完整的视觉第 3 行。");
                }

                var fallbackHash = fallbackFingerprint.Complete();
                var previousMiddleRowHash = lastNativeMiddleRowHash;
                var fallbackDecision = NativeEdgeClickPolicy.ResolveFallback(previousMiddleRowHash, fallbackHash);
                scanLog.WriteEvent("EDGE_CLICK_HASH_FALLBACK", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={previousMiddleRowHash}, currentRowHash={fallbackHash}, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, capturedColumns={fallbackFingerprint.Count}, decision={fallbackDecision}");
                if (fallbackDecision == NativeEdgeClickDecision.AdvanceAssumedOne)
                {
                    visibleTopLogicalRow = Math.Min(maxVisibleTop, visibleTopLogicalRow + 1);
                    var bufferedLogicalRow = visibleTopLogicalRow + 2;
                    var commitResult = CommitBufferedRow(
                        bufferedRow,
                        queue,
                        options,
                        counters,
                        progress,
                        scanLog,
                        bufferedLogicalRow,
                        token);
                    scannedLogicalRows.Add(bufferedLogicalRow);
                    lastNativeMiddleRowHash = fallbackHash;
                    lastScrollChangedViewport = false;
                    scanLog.WriteEvent("EDGE_CLICK_CHANGED", $"iteration={iteration}, fromTop={beforeTop}, toTop={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, assumedRows=1, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={previousMiddleRowHash}, currentRowHash={fallbackHash}, inventoryCount={inventoryCount}, reason=native_edge_click_hash_changed_assume_one, bufferedLogicalRow={bufferedLogicalRow}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
                    scanLog.WriteEvent("OVERLAP_SCROLL_ACCEPTED", $"iteration={iteration}, fromTop={beforeTop}, toTop={visibleTopLogicalRow}, rowsAdvanced=1, decision=AssumedOne, reason=native_edge_click_hash_changed_assume_one, scannedThisViewport=True, scannedRows={scannedLogicalRows.Count}/{totalRows}");
                    if (commitResult == RowScanResult.Stop)
                    {
                        return;
                    }

                    continue;
                }

                if (fallbackDecision != NativeEdgeClickDecision.Bottom)
                {
                    scanLog.WriteEvent("EDGE_CLICK_STOP", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={previousMiddleRowHash}, currentRowHash={fallbackHash}, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, reason=invalid_hash_fallback");
                    throw NavigationFailure("底行点击行哈希兜底没有得到可提交结论。");
                }

                scanLog.WriteEvent("EDGE_CLICK_BOTTOM_DETECTED", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={previousMiddleRowHash}, currentRowHash={fallbackHash}, inventoryCount={inventoryCount}, visited={counters.Visited}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
                var observedBottomLogicalRow = visibleTopLogicalRow + 3;
                RowScanResult bottomResult;
                try
                {
                    bottomResult = await ScanVisualRowAsync(
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
                        pass: observedBottomLogicalRow,
                        row: 4,
                        isBottom: true,
                        maxColumns: columns,
                        logicalRow: observedBottomLogicalRow,
                        treatBlankAsEnd: true,
                        panelChangeProbeRect,
                        token,
                        enforceSafeBand: false,
                        visibleTopLogicalRow: visibleTopLogicalRow,
                        maxVisibleTop: visibleTopLogicalRow,
                        afterScroll: false,
                        postScrollFirstCell: false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    var failureCode = ex is IScannerFailureException scannerFailure
                        ? scannerFailure.Code
                        : ex.GetType().Name;
                    scanLog.WriteEvent("EDGE_CLICK_STOP", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={previousMiddleRowHash}, currentRowHash={fallbackHash}, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, reason=observed_bottom_capture_failed, failureCode={failureCode}");
                    throw;
                }
                if ((options.MaxItems <= 0 || counters.Queued < options.MaxItems)
                    && counters.Visited != inventoryCount)
                {
                    Interlocked.CompareExchange(
                        ref counters.StopReason,
                        "inventory_count_mismatch_at_observed_bottom",
                        null);
                    scanLog.WriteEvent("EDGE_CLICK_STOP", $"iteration={iteration}, visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={edgeClick.TargetPoint}, movementDistance={edgeClick.MovementDistance}, stableFrames={edgeClick.StableFrames}, beforeListHash={edgeClick.BeforeListHash}, afterListHash={edgeClick.AfterListHash}, previousRowHash={previousMiddleRowHash}, currentRowHash={fallbackHash}, reason=inventory_count_mismatch_at_observed_bottom, inventoryCount={inventoryCount}, scannedRows={scannedLogicalRows.Count}/{totalRows}, visited={counters.Visited}, queued={counters.Queued}, bottomResult={bottomResult}");
                }

                return;
            }

            var scroll = await ScrollSafeBandOneRowWithRetryAsync(
                window,
                profile,
                options.ScrollAcceptMode,
                listGridRect,
                rowSignatureRects,
                visibleTopLogicalRow,
                maxVisibleTop,
                allowTwoRows: false,
                scanLog,
                options.ScrollTickDelayOverrideMs,
                token);
            if (!scroll.Success)
            {
                throw NavigationFailure($"重叠签名扫描滚动失败：{scroll.Message}", scroll.DiagnosticDetails);
            }

            var signatureEvidence = scroll.Evidence;
            var rowsAdvanced = scroll.RowsAdvanced;
            if (rowsAdvanced != 1)
            {
                throw NavigationFailure(
                    $"重叠签名扫描收到无效滚动结论：rowsAdvanced={rowsAdvanced}。",
                    scroll.DiagnosticDetails);
            }

            visibleTopLogicalRow = Math.Min(maxVisibleTop, visibleTopLogicalRow + rowsAdvanced);
            lastScrollChangedViewport = true;
            scanLog.WriteEvent("OVERLAP_SCROLL_ACCEPTED", $"iteration={iteration}, fromTop={beforeTop}, toTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, decision={signatureEvidence.Decision}, signatureBestRows={signatureEvidence.BestRows}, signatureBestScore={signatureEvidence.BestScore}, signatureSecondScore={signatureEvidence.SecondScore}, signatureMargin={signatureEvidence.Margin}, reason={signatureEvidence.Reason}, scannedThisViewport={scannedThisViewport}, scannedRows={scannedLogicalRows.Count}/{totalRows}");
            runtimeState.ProfileHealth.ObserveOverlap(false, scanLog.Write);
        }

        if (scannedLogicalRows.Count < totalRows)
        {
            var missing = Enumerable.Range(1, totalRows).Where(row => !scannedLogicalRows.Contains(row)).Take(8).ToArray();
            throw NavigationFailure($"重叠签名扫描达到保护上限仍未完成：scannedRows={scannedLogicalRows.Count}/{totalRows}, missing={string.Join(",", missing)}。");
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
                throw NavigationFailure($"滚动失败：{scrollResult.Message}");
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
        var panelRect = window.ToScreenRectangle(profile.Rectangle("detailPanel"));
        var panelChangeProbeRect = ProfileRectangleOrFallback(window, profile, "panelChangeProbeRect", panelRect);
        var listGridRect = ProfileRectangleOrFallback(window, profile, "listGridRect", BuildListGridFallback(window, profile, 4, Math.Max(1, profile.VisibleColumns)));
        var rois = BuildRois(window, profile, panelRect);
        var statOffset = window.ToScreenPoint(profile.Point("statBackgroundOffset"), clientToScreen: false);
        var statRowBackground = profile.Color("statRowBackground");
        var totalRows = inventoryCount is > 0 ? (int)Math.Ceiling(inventoryCount.Value / 9d) : (int?)null;
        var maxScrollRows = Math.Max(0, (totalRows ?? 69) - 4);
        scanLog.Write($"Traversal: legacy third-row mode. inventoryCount={inventoryCount?.ToString() ?? "unknown"}, totalRows={totalRows?.ToString() ?? "unknown"}, maxScrollRows={maxScrollRows}, listGrid={listGridRect}, wheelDelta={profile.ScrollWheelDelta}.");

        var pass = 0;
        var atBottom = false;
        var scrollRows = 0;
        for (var row = 1; row <= 4; row++)
        {
            token.ThrowIfCancellationRequested();
            pass++;
            var bottomBefore = atBottom;
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
                if (!atBottom && scrollRows < maxScrollRows)
                {
                    var before = CaptureSignature(window, listGridRect);
                    scanLog.Write($"Scroll: legacy third-row wheel after pass {pass}, delta={profile.ScrollWheelDelta}.");
                    scanLog.WriteEvent("LEGACY_WHEEL", $"afterPass={pass}, visualRow={row}, delta={profile.ScrollWheelDelta}");
                    window.MouseWheel(profile.ScrollWheelDelta);
                    await Task.Delay(profile.WheelDelayMs, token);
                    var after = CaptureSignature(window, listGridRect);
                    var distance = SignatureDistance(before, after);
                    atBottom = distance <= ListStableTolerance;
                    scanLog.WriteEvent("LEGACY_BOTTOM_MOTION", $"afterPass={pass}, signatureDistance={distance}, atBottom={atBottom}");
                    if (!atBottom)
                    {
                        scrollRows++;
                        row--;
                    }
                }
                else
                {
                    atBottom = true;
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
        int startColumn = 1,
        RowDetailFingerprintBuilder? rowFingerprint = null,
        BufferedRowCapture? bufferedRow = null,
        ImageSignature[]? initialPanelSignatures = null,
        bool firstCellHasNeighborBaseline = false)
    {
        var currentY = offset.Y + step.Y * row;
        ImageSignature[]? previousPanelSignatures = initialPanelSignatures ?? CaptureCurrentPanelSignatures(
            window,
            panelRect,
            panelChangeProbeRect,
            rois);
        startColumn = Math.Clamp(startColumn, 1, maxColumns);
        if (startColumn > 1)
        {
            var refreshColumn = startColumn - 1;
            var refresh = new PointF(offset.X + step.X * refreshColumn, currentY);
            var refreshPoint = window.ToScreenPoint(refresh);
            scanLog.WriteEvent("ROW_RESUME_SELECTION_REFRESH", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, startColumn={startColumn}, refreshColumn={refreshColumn}, point={refreshPoint}, visibleTopLogicalRow={visibleTopLogicalRow}, state={ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop)}");
            window.MoveCursor(refreshPoint);
            await window.SelectDiscAsync(refreshPoint, token);
            await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
            previousPanelSignatures = CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois);
        }

        var postScrollFirstCellPending = postScrollFirstCell;
        var bootstrappedColumns = new HashSet<int>();
        for (var col = startColumn; col <= maxColumns; col++)
        {
            token.ThrowIfCancellationRequested();
            if (bootstrappedColumns.Remove(col))
            {
                scanLog.WriteEvent("FIRST_PAIR_CONSUMED_CELL_SKIPPED", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}");
                continue;
            }

            if (bufferedRow is null && options.MaxItems > 0 && counters.Queued >= options.MaxItems)
            {
                scanLog.Write($"End: max items reached. MaxItems={options.MaxItems}, queued={counters.Queued}.");
                Report(progress, counters, $"已达到读取上限：{options.MaxItems}");
                return RowScanResult.Stop;
            }

            if (enforceSafeBand && !IsSafeBandClick(row, visibleTopLogicalRow, maxVisibleTop, logicalRow ?? -1))
            {
                scanLog.WriteEvent("EDGE_CLICK_BLOCKED", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopLogicalRow}, maxVisibleTop={maxVisibleTop}, state={ViewportStateLabel(visibleTopLogicalRow, maxVisibleTop)}");
                throw NavigationFailure($"安全带保护阻止点击：逻辑行 {logicalRow} 当前位于视觉第 {row} 行。");
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
            var gridCellHash = rowFingerprint is null
                ? 0UL
                : CaptureGridCellFingerprint(window, clickPoint, step);
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
                scanLog.Write($"Probe pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, point={clickPoint}, rarity={rarity ?? "null"}, best={ColorText(rarityProbe.BestColor)}, bestMatch={rarityProbe.BestCandidate}, score={rarityProbe.BestScore}, secondScore={rarityProbe.SecondScore}, margin={rarityProbe.Margin}, fullScan={rarityProbe.FullScan}, bottom={isBottom}");
            }
            if (rarity is null)
            {
                if (treatBlankAsEnd)
                {
                    scanLog.Write($"End: blank cell at pass={pass}, visualRow={row}, col={col}. This is treated as list end after retry.");
                    Report(progress, counters, $"第 {pass} 轮第 {row} 行第 {col} 列未检测到驱动盘卡片，扫描结束。详情见本次输出目录的 scan.log。");
                    return RowScanResult.Stop;
                }

                throw NavigationFailure($"预期存在驱动盘，但第 {logicalRow} 行第 {col} 列未检测到品质颜色。可能发生漏扫或列表未稳定。");
            }

            if (bufferedRow is null)
            {
                counters.Visited++;
            }

            var selectedForExport = options.Rarities.Contains(rarity);
            if (!selectedForExport)
            {
                rowFingerprint?.AddGridCell(col, rarity, gridCellHash);
                if (bufferedRow is null)
                {
                    Report(progress, counters, $"跳过 {rarity} 级驱动盘。");
                }
                else
                {
                    bufferedRow.AddSkipped(col, rarity);
                }

                continue;
            }

            var firstQueuedItem = bufferedRow is null && selectedForExport && counters.Queued == 0;
            // The gamepad startup already selected the neighbor and supplied its
            // panel baseline. Returning to cell 1 still uses all original change,
            // selection and stable-ROI gates; no synthetic acceptance is granted.
            var preparedFirstCell = firstQueuedItem && col == 1 && logicalRow == 1
                && firstCellHasNeighborBaseline && initialPanelSignatures is not null
                && window.DiscSelectionInput is not null;
            var secondColumn = col + 1;
            if (bufferedRow is null && firstQueuedItem && !preparedFirstCell && secondColumn <= maxColumns)
            {
                var secondClientPoint = new PointF(offset.X + step.X * secondColumn, currentY);
                var secondClickPoint = window.ToScreenPoint(secondClientPoint);
                var secondRarityProbe = DetectRarityAround(window, profile, secondClickPoint);
                if (secondRarityProbe.Rarity is null)
                {
                    await Task.Delay(25, token);
                    secondRarityProbe = DetectRarityAround(window, profile, secondClickPoint);
                }

                var secondRarity = secondRarityProbe.Rarity;
                if (secondRarity is not null && options.Rarities.Contains(secondRarity))
                {
                    using var bootstrap = await CaptureFirstPairAsync(
                        window,
                        profile,
                        panelRect,
                        rois,
                        statOffset,
                        statRowBackground,
                        panelChangeProbeRect,
                        runtimeState,
                        scanLog,
                        offset,
                        step,
                        pass,
                        row,
                        col,
                        secondColumn,
                        maxColumns,
                        logicalRow,
                        visibleTopText,
                        viewportStateText,
                        token);

                    var pair = new[]
                    {
                        new FirstPairCommit(col, rarity, bootstrap.First),
                        new FirstPairCommit(secondColumn, secondRarity, bootstrap.Second)
                    };
                    var commitCount = options.MaxItems > 0
                        ? Math.Min(pair.Length, options.MaxItems - counters.Queued)
                        : pair.Length;
                    for (var pairIndex = 0; pairIndex < commitCount; pairIndex++)
                    {
                        var commit = pair[pairIndex];
                        if (pairIndex > 0)
                        {
                            Interlocked.Increment(ref counters.Visited);
                        }

                        ObserveAcceptedPanel(runtimeState, commit.Capture, scanLog, postScrollFirstCell: false);
                        var pairPanelImage = commit.Capture.TakeImage();
                        var pairCaptureRois = rois.Take(commit.Capture.VisibleRoiCount).ToArray();
                        rowFingerprint?.AddDetailPanel(commit.Column, commit.Rarity, pairPanelImage, pairCaptureRois);
                        var pairDebugImage = options.ShowDebugImages ? (Bitmap)pairPanelImage.Clone() : null;
                        var pairItemIndex = Interlocked.Increment(ref counters.Queued);
                        var pairEnqueued = false;
                        try
                        {
                            queue.Add(new DiscCapture(
                                pairItemIndex,
                                commit.Rarity,
                                pairPanelImage,
                                pairCaptureRois,
                                commit.Capture.TargetVerificationKind), token);
                            pairEnqueued = true;
                        }
                        finally
                        {
                            if (!pairEnqueued)
                            {
                                pairPanelImage.Dispose();
                            }
                        }

                        Report(progress, counters, options.ShowDebugImages ? $"首对识别入队 #{pairItemIndex}。" : "", debugImage: pairDebugImage);
                    }

                    previousPanelSignatures = bootstrap.CurrentPanelSignatures;
                    postScrollFirstCellPending = false;
                    if (commitCount > 1)
                    {
                        bootstrappedColumns.Add(secondColumn);
                    }

                    scanLog.WriteEvent(
                        "FIRST_PAIR_COMMIT",
                        $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, firstCol={col}, secondCol={secondColumn}, committed={commitCount}, queued={counters.Queued}, currentPanel={bootstrap.CurrentPanelLabel}");
                    continue;
                }
            }

            if (firstQueuedItem)
            {
                scanLog.WriteEvent(
                    "FIRST_CELL_BASELINE_CAPTURED",
                    $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, probes={previousPanelSignatures?.Length ?? 0}");
            }

            scanLog.WriteEvent("CELL_CLICK", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, point={clickPoint}");
            var selectionProbeRect = SelectionProbeRect(window, clickPoint);
            var selectionProbeWatch = Stopwatch.StartNew();
            var beforeSelectionSignature = CaptureSelectionSignature(selectionProbeRect);
            selectionProbeWatch.Stop();
            await window.SelectDiscAsync(clickPoint, token);
            var verifiedRowStart = col == 1 && logicalRow is > 1
                && previousPanelSignatures is not null
                && window.DiscSelectionInput is Gamepad.DiscSelectionInput gamepadSelection
                && gamepadSelection.ConsumeVerifiedRowStart(clickPoint);
            if (verifiedRowStart)
                scanLog.WriteEvent("GAMEPAD_ROW_START_EVIDENCE", $"logicalRow={logicalRow}, col={col}, source=verified_down, scope=initial_capture_only, detailGate=unchanged");
            var selectionRoundTripReady = false;
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
            else
            {
                var refreshRow = row > 1
                    ? row - 1
                    : row < Math.Max(1, profile.VisibleRows)
                        ? row + 1
                        : 0;
                var refreshLogicalRow = logicalRow is null || refreshRow <= 0
                    ? -1
                    : logicalRow.Value + refreshRow - row;
                var refreshRowSafe = refreshRow > 0
                    && (!enforceSafeBand
                        || !viewportKnown
                        || IsSafeBandClick(refreshRow, visibleTopLogicalRow, maxVisibleTop, refreshLogicalRow));
                if (refreshRowSafe)
                {
                    var refresh = new PointF(offset.X + step.X, offset.Y + step.Y * refreshRow);
                    selectionRefreshPoint = window.ToScreenPoint(refresh);
                }
            }

            if (!preparedFirstCell && PanelCaptureGate.RequiresFirstCellNeighborRoundTrip(firstQueuedItem))
            {
                scanLog.WriteEvent("FIRST_CELL_REFRESH_REQUIRED", $"attempt=preflight, reason=deterministic_neighbor_round_trip, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}");
                await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
                var refresh = await RefreshSelectionForPanelRetryAsync(window, profile, panelRect, rois, panelChangeProbeRect, scanLog, clickPoint, selectionRefreshPoint, pass, row, col, maxColumns, logicalRow, visibleTopText, viewportStateText, token);
                if (!refresh.RefreshReady)
                {
                    throw new PanelCellCaptureException(
                        pass,
                        row,
                        col,
                        maxColumns,
                        logicalRow,
                        1,
                        window,
                        runtimeState.VisualProfileId,
                        new StalePanelException("首件邻格刷新无法证明详情面板变化。"));
                }

                scanLog.WriteEvent("FIRST_CELL_REFRESH_READY", $"attempt=preflight, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}");
                selectionRoundTripReady = refresh.SelectionRoundTripReady;
                previousPanelSignatures = refresh.PanelSignatures;
                selectionProbeRect = refresh.SelectionProbeRect;
                beforeSelectionSignature = refresh.SelectionSignature;
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
                sceneAdaptivePanelFloorEligible,
                firstQueuedItem,
                selectionRoundTripReady,
                initialVerifiedRowStart: verifiedRowStart);
            var panelImage = panelCapture.TakeImage();
            var captureRois = rois.Take(panelCapture.VisibleRoiCount).ToArray();
            previousPanelSignatures = panelCapture.ProbeSignatures;
            ObserveAcceptedPanel(runtimeState, panelCapture, scanLog, currentPostScrollFirstCell);
            rowFingerprint?.AddDetailPanel(col, rarity, panelImage, captureRois);
            if (!selectedForExport)
            {
                panelImage.Dispose();
                if (bufferedRow is null)
                {
                    Report(progress, counters, $"跳过 {rarity} 级驱动盘。");
                }
                else
                {
                    bufferedRow.AddSkipped(col, rarity);
                }

                postScrollFirstCellPending = false;
                continue;
            }

            if (bufferedRow is not null)
            {
                bufferedRow.AddCapture(
                    col,
                    rarity,
                    panelImage,
                    captureRois,
                    panelCapture.TargetVerificationKind);
                postScrollFirstCellPending = false;
                scanLog.WriteEvent("EDGE_CLICK_BUFFERED_CELL", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, rarity={rarity}, rowHashColumns={rowFingerprint?.Count ?? 0}");
                continue;
            }

            var debugImage = options.ShowDebugImages ? (Bitmap)panelImage.Clone() : null;
            var ocrBacklogBeforeEnqueue = CurrentOcrBacklog(counters);
            var itemIndex = Interlocked.Increment(ref counters.Queued);
            var enqueueWait = Stopwatch.StartNew();
            var enqueued = false;
            try
            {
                queue.Add(new DiscCapture(
                    itemIndex,
                    rarity,
                    panelImage,
                    captureRois,
                    panelCapture.TargetVerificationKind), token);
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

            scanLog.WriteEvent("CELL_TIMING", $"index={itemIndex}, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, afterScroll={afterScroll}, postScrollFirstCell={currentPostScrollFirstCell}, panelWaitMs={panelCapture.WaitMilliseconds:F1}, enqueueWaitMs={enqueueWait.Elapsed.TotalMilliseconds:F1}, fallback={panelCapture.UsedFallback}, visibleRois={panelCapture.VisibleRoiCount}/{rois.Length}, totalMs={sw.ElapsedMilliseconds}, panelFrames={panelCapture.FrameCount}, changeMs={FormatOptionalMs(panelCapture.ChangeMilliseconds)}, selectionChangeMs={FormatOptionalMs(panelCapture.SelectionChangeMilliseconds)}, fullRoiMs={FormatOptionalMs(panelCapture.FullRoiMilliseconds)}, stableMs={FormatOptionalMs(panelCapture.StableMilliseconds)}, panelTextStableMs={FormatOptionalMs(panelCapture.PanelTextStableMilliseconds)}, panelStableSource={panelCapture.PanelStableSource}, panelStabilityReason={panelCapture.PanelStabilityReason}, targetSelectionStableFrames={panelCapture.TargetSelectionStableFrames}, targetVerificationKind={panelCapture.TargetVerificationKind}, rarityProbeMs={rarityProbeWatch.Elapsed.TotalMilliseconds:F1}, selectionProbeMs={selectionProbeWatch.Elapsed.TotalMilliseconds:F1}, captureMs={panelCapture.CaptureMilliseconds:F1}, signatureMs={panelCapture.SignatureMilliseconds:F1}, visibleRoiMs={panelCapture.VisibleRoiMilliseconds:F1}, frameLoopMs={panelCapture.FrameLoopMilliseconds:F1}, frameToBitmapMs={panelCapture.FrameToBitmapMilliseconds:F1}, bitmapCreatedCount={panelCapture.BitmapCreatedCount}, quickAccept={panelCapture.QuickAccept}, quickRejectReason={panelCapture.QuickRejectReason}, adaptiveThrottleMs={adaptiveThrottleMs}, ocrBacklogBeforeEnqueue={ocrBacklogBeforeEnqueue}, adaptivePanelMinMs={panelCapture.MinimumAcceptMilliseconds}, adaptivePanelSamples={panelCapture.AdaptiveSampleCount}, adaptivePanelReason={panelCapture.AdaptiveReason}, panelAcceptMode={panelCapture.PanelAcceptMode}, postScrollAcceptMode={panelCapture.PostScrollPanelAcceptMode}, panelMinFloorMs={panelCapture.PanelMinAcceptFloorMs}, roiCompleteFrames={panelCapture.RoiCompleteFrames}, selectedStableFrames={panelCapture.SelectedStableFrames}, acceptGateReason={panelCapture.AcceptGateReason}, panelFloorMode={panelCapture.PanelFloorMode}, sameRowPanelFloorMs={panelCapture.SameRowPanelFloorMs}, postScrollPanelFloorMs={panelCapture.PostScrollPanelFloorMs}, panelFloorReason={panelCapture.PanelFloorReason}, floorWaitLimitedMs={panelCapture.FloorWaitLimitedMilliseconds:F1}, panelAcceptElapsedVsFloorMs={panelCapture.PanelAcceptElapsedVsFloorMilliseconds:F1}, scrollTickDelayMs={runtimeState.EffectiveScrollTickDelayMs}, accept={panelCapture.AcceptReason}");
            postScrollFirstCellPending = false;
            var queueMessage = options.ShowDebugImages || itemIndex % 25 == 0
                ? $"入队 #{itemIndex}：{rarity}，{captureRois.Length} 个文本区域，用时 {sw.ElapsedMilliseconds}ms。"
                : "";
            Report(progress, counters, queueMessage, debugImage: debugImage);
        }

        return RowScanResult.Completed;
    }

    private static RowScanResult CommitBufferedRow(
        BufferedRowCapture bufferedRow,
        BlockingCollection<DiscCapture> queue,
        ScanOptions options,
        Counters counters,
        IProgress<ScanProgress> progress,
        ScanLog scanLog,
        int logicalRow,
        CancellationToken token)
    {
        foreach (var cell in bufferedRow.Cells.OrderBy(cell => cell.Column))
        {
            token.ThrowIfCancellationRequested();
            if (options.MaxItems > 0 && counters.Queued >= options.MaxItems)
            {
                scanLog.Write($"End: max items reached while committing buffered row. MaxItems={options.MaxItems}, queued={counters.Queued}.");
                return RowScanResult.Stop;
            }

            Interlocked.Increment(ref counters.Visited);
            if (!cell.HasCapture)
            {
                Report(progress, counters, $"跳过 {cell.Rarity} 级驱动盘。");
                continue;
            }

            var image = cell.TakeImage();
            var itemIndex = Interlocked.Increment(ref counters.Queued);
            var enqueued = false;
            try
            {
                queue.Add(new DiscCapture(
                    itemIndex,
                    cell.Rarity,
                    image,
                    cell.Rois,
                    cell.TargetVerificationKind), token);
                enqueued = true;
            }
            finally
            {
                if (!enqueued)
                {
                    image.Dispose();
                }
            }

            scanLog.WriteEvent("EDGE_CLICK_BUFFER_COMMIT", $"logicalRow={logicalRow}, col={cell.Column}, index={itemIndex}, rarity={cell.Rarity}, queued={counters.Queued}, visited={counters.Visited}");
            Report(progress, counters, options.ShowDebugImages ? $"缓冲行入队 #{itemIndex}。" : "");
        }

        return RowScanResult.Completed;
    }

    internal static ScanTraversalMode ResolveTraversalMode(ScanOptions options, ScanProfile profile)
    {
        if (options.TraversalMode != ScanTraversalMode.FromProfile)
        {
            return options.TraversalMode;
        }

        // Existing UI/API requests using FromProfile now enter controller navigation.
        // Legacy algorithms remain explicitly selectable for regression comparison.
        return ScanTraversalMode.Gamepad;
    }

    private static RowAdvanceMode ResolveRowAdvanceMode(ScanOptions options, ScanProfile profile)
    {
        if (options.RowAdvanceMode.HasValue)
        {
            return options.RowAdvanceMode.Value;
        }

        var normalized = (profile.RowAdvanceMode ?? "").Replace("-", "", StringComparison.OrdinalIgnoreCase);
        return Enum.TryParse<RowAdvanceMode>(normalized, ignoreCase: true, out var parsed)
            ? parsed
            : RowAdvanceMode.WheelVerified;
    }

    private static async Task<NativeEdgeClickSettleResult> AdvanceNativeEdgeClickAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        PointF offset,
        PointF step,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        int inventoryCount,
        int scannedRows,
        ScanLog scanLog,
        CancellationToken token)
    {
        if (rowSignatureRects.Count < 4)
        {
            return new NativeEdgeClickSettleResult(
                false,
                false,
                0,
                0,
                0,
                0,
                "edge_row_geometry_missing",
                Point.Empty,
                "unknown",
                "unknown");
        }

        var targetClientPoint = new PointF(offset.X + step.X, offset.Y + step.Y * 4);
        var targetPoint = window.ToScreenPoint(targetClientPoint);
        window.MoveCursor(targetPoint);
        var rarity = DetectRarityAround(window, profile, targetPoint).Rarity;
        if (rarity is null)
        {
            await Task.Delay(25, token);
            rarity = DetectRarityAround(window, profile, targetPoint).Rarity;
        }

        if (rarity is null)
        {
            return new NativeEdgeClickSettleResult(
                false,
                false,
                0,
                0,
                0,
                0,
                "edge_target_missing",
                targetPoint,
                "unknown",
                "unknown");
        }

        var beforeRows = CaptureRowSignatures(window, rowSignatureRects);
        var beforeListHash = FormatNativeEdgeListHash(beforeRows);
        var previousRows = beforeRows;
        var watch = Stopwatch.StartNew();
        var samples = 0;
        var stableFrames = 0;
        var movementDistance = 0;
        var sawMovement = false;
        var pollMilliseconds = Math.Max(10, profile.LoadPollMs);
        scanLog.WriteEvent(
            "EDGE_CLICK_START",
            $"visibleTopLogicalRow={visibleTopLogicalRow}, maxVisibleTop={maxVisibleTop}, targetVisualRow=4, targetColumn=1, targetPoint={targetPoint}, targetRarity={rarity}, stableFrames=0, movementDistance=0, beforeListHash={beforeListHash}, afterListHash=pending, previousRowHash=unknown, currentRowHash=unknown, inventoryCount={inventoryCount}, scannedRows={scannedRows}, timeoutMs={NativeEdgeClickTimeoutMilliseconds}");
        window.LeftClickCurrent();

        while (watch.ElapsedMilliseconds < NativeEdgeClickTimeoutMilliseconds)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(pollMilliseconds, token);
            samples++;
            var currentRows = CaptureRowSignatures(window, rowSignatureRects);
            var currentMovementDistance = AverageSignatureDistance(
                beforeRows,
                currentRows,
                [(0, 0), (1, 1), (2, 2)]);
            movementDistance = Math.Max(movementDistance, currentMovementDistance);
            var frameDistance = AverageSignatureDistance(
                previousRows,
                currentRows,
                [(0, 0), (1, 1), (2, 2)]);
            previousRows = currentRows;
            sawMovement |= currentMovementDistance > ListMovementTolerance;
            stableFrames = frameDistance <= ListStableTolerance
                ? stableFrames + 1
                : 0;
            var mayDecideNoMove = watch.ElapsedMilliseconds >= NativeEdgeClickNoMoveDecisionMilliseconds;
            if (stableFrames >= NativeEdgeClickStableFrames && (sawMovement || mayDecideNoMove))
            {
                watch.Stop();
                var result = new NativeEdgeClickSettleResult(
                    true,
                    sawMovement,
                    movementDistance,
                    stableFrames,
                    samples,
                    watch.Elapsed.TotalMilliseconds,
                    sawMovement ? "native_edge_click_changed_assume_one" : "native_edge_click_stable_unchanged",
                    targetPoint,
                    beforeListHash,
                    FormatNativeEdgeListHash(currentRows));
                scanLog.WriteEvent(
                    "EDGE_CLICK_SETTLED",
                    $"visibleTopLogicalRow={visibleTopLogicalRow}, targetPoint={targetPoint}, settled=True, changed={result.Changed}, movementDistance={result.MovementDistance}, frameDistance={frameDistance}, stableFrames={result.StableFrames}, samples={result.Samples}, elapsedMs={result.ElapsedMilliseconds:F1}, beforeListHash={result.BeforeListHash}, afterListHash={result.AfterListHash}, previousRowHash=unknown, currentRowHash=unknown, inventoryCount={inventoryCount}, scannedRows={scannedRows}, reason={result.Reason}");
                return result;
            }
        }

        watch.Stop();
        return new NativeEdgeClickSettleResult(
            false,
            sawMovement,
            movementDistance,
            stableFrames,
            samples,
            watch.Elapsed.TotalMilliseconds,
            "native_edge_click_settle_timeout",
            targetPoint,
            beforeListHash,
            FormatNativeEdgeListHash(previousRows));
    }

    private static async Task ArmNativeEdgeFollowupSelectionAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle listGridRect,
        PointF offset,
        PointF step,
        int columns,
        int visibleTopLogicalRow,
        ScanLog scanLog,
        CancellationToken token)
    {
        var armClientPoint = new PointF(
            offset.X + step.X * Math.Max(1, columns),
            offset.Y + step.Y * 2);
        var armPoint = window.ToScreenPoint(armClientPoint);
        window.MoveCursor(armPoint);
        window.LeftClickCurrent();
        await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
        await WaitForListStableAsync(window, profile, listGridRect, scanLog, token);
        scanLog.WriteEvent(
            "EDGE_CLICK_SELECTION_ARMED",
            $"visibleTopLogicalRow={visibleTopLogicalRow}, visualRow=2, column={columns}, point={armPoint}, reason=move_selection_away_from_advanced_row");
    }

    private static string FormatNativeEdgeListHash(IEnumerable<RowVisualSignature> signatures) =>
        string.Join("-", signatures.Take(3).Select(signature => signature.Hash.ToString("X16")));

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
                allowTwoRows: false,
                scanLog,
                scrollTickDelayOverrideMs,
                token);
            if (!result.Success)
            {
                throw NavigationFailure($"安全带逐行滚动失败：{result.Message}", result.DiagnosticDetails);
            }

            var rowsAdvanced = result.RowsAdvanced;
            if (rowsAdvanced <= 0)
            {
                throw NavigationFailure("安全带逐行滚动没有得到可提交的正向位移。", result.DiagnosticDetails);
            }
            var nextVisibleTop = Math.Min(maxVisibleTop, visibleTopLogicalRow + rowsAdvanced);
            if (rowsAdvanced != 1)
            {
                scanLog.Write($"Safe-band viewport detected wheel overshoot: previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, nextTop={nextVisibleTop}, desiredTop={desiredTop}.");
                if (nextVisibleTop > desiredTop)
                {
                    if (!profile.AllowScrollRecovery)
                    {
                        scanLog.WriteEvent("ROW_SCROLL_STRICT_STOP", $"previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, nextTop={nextVisibleTop}, desiredTop={desiredTop}, recoveryAllowed={profile.AllowScrollRecovery}");
                        throw NavigationFailure($"安全带逐行滚动越过目标行：previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, nextTop={nextVisibleTop}, desiredTop={desiredTop}。当前为单向严格模式，已禁止自动上翻恢复；请重试。");
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
                        throw NavigationFailure($"安全带逐行滚动越过目标行且回退失败：previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, nextTop={nextVisibleTop}, desiredTop={desiredTop}, recovery={recovery.Message}。为避免重复读取，本次停止。");
                    }

                    var rowsRecovered = recovery.RowsAdvanced;
                    if (rowsRecovered <= 0)
                    {
                        throw NavigationFailure("安全带回退没有得到可提交的位移。", recovery.DiagnosticDetails);
                    }
                    nextVisibleTop = Math.Max(1, nextVisibleTop - rowsRecovered);
                    if (nextVisibleTop > desiredTop)
                    {
                        throw NavigationFailure($"安全带逐行滚动越过目标行且回退后仍越过目标行：previousTop={visibleTopLogicalRow}, rowsAdvanced={rowsAdvanced}, rowsRecovered={rowsRecovered}, recoveredTop={nextVisibleTop}, desiredTop={desiredTop}。为避免重复读取，本次停止。");
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
            throw NavigationFailure($"安全带保护阻止点击：逻辑行 {logicalRow} 当前位于视觉第 {visualRow} 行。");
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
        var preferredVisualRows = atTop && atBottom
            ? new[] { 1, 2, 3, 4 }
            : atTop
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

    private static void LogOverlapViewport(
        ScanLog scanLog,
        int iteration,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        int totalRows,
        IReadOnlyList<RowVisualSignature> rowSignatures)
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
        bool allowTwoRows,
        ScanLog scanLog,
        int scrollTickDelayOverrideMs,
        CancellationToken token)
    {
        var delay = Math.Max(120, EffectiveScrollTickDelay(profile, scrollTickDelayOverrideMs) * 2);
        ScrollRowsResult last = default;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            last = await ScrollSafeBandOneRowAsync(window, profile, scrollAcceptMode, listGridRect, rowSignatureRects, visibleTopLogicalRow, maxVisibleTop, allowTwoRows, scanLog, scrollTickDelayOverrideMs, token);
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
        bool allowTwoRows,
        ScanLog scanLog,
        int scrollTickDelayOverrideMs,
        CancellationToken token)
    {
        if (visibleTopLogicalRow >= maxVisibleTop)
        {
            return ScrollRowsResult.Fail($"already at bottom visibleTopLogicalRow={visibleTopLogicalRow}", retryable: false);
        }

        return await ScrollVerifiedRowShiftAsync(
            window,
            profile,
            scrollAcceptMode,
            listGridRect,
            rowSignatureRects,
            visibleTopLogicalRow,
            maxVisibleTop,
            upward: false,
            allowTwoRows,
            scanLog,
            scrollTickDelayOverrideMs,
            token);
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
            return ScrollRowsResult.Fail($"already at top visibleTopLogicalRow={visibleTopLogicalRow}", retryable: false);
        }

        return await ScrollVerifiedRowShiftAsync(
            window,
            profile,
            ScrollAcceptMode.Safe,
            listGridRect,
            rowSignatureRects,
            visibleTopLogicalRow,
            maxVisibleTop: visibleTopLogicalRow,
            upward: true,
            allowTwoRows: false,
            scanLog,
            scrollTickDelayOverrideMs,
            token);
    }

    private static async Task<ScrollRowsResult> ScrollVerifiedRowShiftAsync(
        GameWindow window,
        ScanProfile profile,
        ScrollAcceptMode scrollAcceptMode,
        Rectangle listGridRect,
        IReadOnlyList<Rectangle> rowSignatureRects,
        int visibleTopLogicalRow,
        int maxVisibleTop,
        bool upward,
        bool allowTwoRows,
        ScanLog scanLog,
        int scrollTickDelayOverrideMs,
        CancellationToken token)
    {
        // The game consumes standard wheel detents reliably. Fractional -30 pulses can be
        // animated without being committed by the list after the 2026-07 game update.
        var configuredDelta = profile.ScrollWheelDelta != 0 ? profile.ScrollWheelDelta : profile.ScrollTickDelta;
        var delta = upward ? -configuredDelta : configuredDelta;
        var targetTop = upward ? visibleTopLogicalRow - 1 : visibleTopLogicalRow + 1;
        var direction = upward ? "up" : "down";
        var startEvent = upward ? "ROW_SCROLL_RECOVERY_START" : "ROW_SCROLL_START";
        var tickEvent = upward ? "ROW_SCROLL_RECOVERY_TICK" : "ROW_SCROLL_TICK";
        var doneEvent = upward ? "ROW_SCROLL_RECOVERED" : "ROW_SCROLL_DONE";
        var maxTicks = Math.Min(VerifiedScrollTransactionLimit, ScrollSmallTickLimit(profile));
        var wheelPoint = window.ToScreenPoint(profile.Point("listWheelArea"));
        var neutralPoint = ListNeutralPoint(window, listGridRect);
        var effectiveScrollTickDelayMs = EffectiveScrollTickDelay(profile, scrollTickDelayOverrideMs);
        var pollMilliseconds = Math.Max(10, profile.LoadPollMs);
        window.MoveCursor(neutralPoint);
        var beforeViewport = CaptureViewportSignatures(window, listGridRect, rowSignatureRects);
        var beforeGrid = beforeViewport.Grid;
        var beforeRows = beforeViewport.Rows;
        var beforeScrollbar = CaptureScrollbarThumbProbe(window, profile);
        var scrollbarPixelsPerRow = ScrollbarPixelsPerLogicalRow(window, profile, beforeScrollbar, maxVisibleTop);
        var latestViewport = beforeViewport;
        var previousObservedGrid = beforeGrid;
        var totalWatch = Stopwatch.StartNew();
        var wheelWaitMilliseconds = 0.0;
        var signatureMilliseconds = 0.0;
        var settleStartedTicks = new HashSet<int>();
        scanLog.WriteEvent(startEvent, $"direction={direction}, fromTop={visibleTopLogicalRow}, toTop={targetTop}, maxVisibleTop={maxVisibleTop}, delta={delta}, inputMode=standard_detent_hold, detentsPerTick={VerifiedScrollDetentBurstCount}, detentIntervalMs={VerifiedScrollDetentBurstIntervalMs}, hoverSettleMs={VerifiedScrollHoverSettleMilliseconds}, releaseSamples={VerifiedScrollReleaseSamples}, configuredScrollWheelDelta={profile.ScrollWheelDelta}, configuredScrollTickDelta={profile.ScrollTickDelta}, maxTicks={maxTicks}, samplesPerTick={RowScrollCoordinator.MaximumSamplesPerTick}, scrollTickDelayMs={effectiveScrollTickDelayMs}, wheelPoint={wheelPoint}, neutralPoint={neutralPoint}");

        var result = await RowScrollCoordinator.RunAsync(
            maxTicks,
            scrollAcceptMode,
            pollMilliseconds,
            async (tick, cancellationToken) =>
            {
                scanLog.WriteEvent(tickEvent, $"direction={direction}, fromTop={visibleTopLogicalRow}, toTop={targetTop}, tick={tick}/{maxTicks}, delta={delta}, detents={VerifiedScrollDetentBurstCount}, intervalMs={VerifiedScrollDetentBurstIntervalMs}");
                window.MoveCursor(wheelPoint);
                await Task.Delay(VerifiedScrollHoverSettleMilliseconds, cancellationToken);
                for (var detent = 1; detent <= VerifiedScrollDetentBurstCount; detent++)
                {
                    window.MouseWheel(delta);
                    if (detent < VerifiedScrollDetentBurstCount)
                    {
                        await Task.Delay(VerifiedScrollDetentBurstIntervalMs, cancellationToken);
                    }
                }

                // Keep the cursor over the list while the game consumes the wheel gesture.
                // Moving it away immediately can leave the new smooth-scrolling list below
                // its snap threshold and make it spring back to the previous row.
                var wait = Stopwatch.StartNew();
                await Task.Delay(effectiveScrollTickDelayMs, cancellationToken);
                wait.Stop();
                wheelWaitMilliseconds += VerifiedScrollHoverSettleMilliseconds
                    + wait.Elapsed.TotalMilliseconds
                    + Math.Max(0, VerifiedScrollDetentBurstCount - 1) * VerifiedScrollDetentBurstIntervalMs;
            },
            async (tick, sample, phase, cancellationToken) =>
            {
                if (phase == RowScrollPhase.Settle || sample > 1)
                {
                    await Task.Delay(pollMilliseconds, cancellationToken);
                }

                var signatureWatch = Stopwatch.StartNew();
                latestViewport = CaptureViewportSignatures(window, listGridRect, rowSignatureRects);
                signatureWatch.Stop();
                signatureMilliseconds += signatureWatch.Elapsed.TotalMilliseconds;
                var movedDistance = SignatureDistance(beforeGrid, latestViewport.Grid);
                var frameDistance = SignatureDistance(previousObservedGrid, latestViewport.Grid);
                previousObservedGrid = latestViewport.Grid;
                var verification = upward
                    ? VerifyOneRowUp(beforeRows, latestViewport.Rows)
                    : VerifyOneRowDown(beforeRows, latestViewport.Rows);
                var structuralEvidence = RowAdvanceEvaluator.Evaluate(
                    verification.NoMoveScore,
                    verification.OneRowScore,
                    verification.TwoRowScore,
                    movedDistance);
                var structuralObservation = new RowScrollObservation(structuralEvidence, frameDistance);
                var scrollbar = structuralObservation.Stable || sample == 1
                    ? CaptureScrollbarThumbProbe(window, profile)
                    : default;
                var directionalScrollbarDelta = DirectionalScrollbarDelta(beforeScrollbar, scrollbar, upward);
                var evidence = directionalScrollbarDelta is int pixelDelta
                    ? RowAdvanceEvaluator.ReconcileScrollbarPosition(structuralEvidence, pixelDelta, scrollbarPixelsPerRow)
                    : structuralEvidence;
                var estimatedScrollbarRows = directionalScrollbarDelta is int overshotPixelDelta && scrollbarPixelsPerRow >= 1
                    ? Math.Max(0, overshotPixelDelta) / scrollbarPixelsPerRow
                    : (double?)null;
                if (estimatedScrollbarRows > 1.5)
                {
                    evidence = evidence with
                    {
                        Decision = RowAdvanceDecision.TwoRows,
                        BestRows = Math.Max(2, (int)Math.Round(estimatedScrollbarRows.Value, MidpointRounding.AwayFromZero)),
                        Strong = true,
                        Reason = "scrollbar_overshot_above_single_row_limit"
                    };
                }

                var observation = new RowScrollObservation(evidence, frameDistance, directionalScrollbarDelta);
                var sampleLimit = phase == RowScrollPhase.FastProbe
                    ? RowScrollCoordinator.MaximumSamplesPerTick.ToString()
                    : "settle";
                scanLog.WriteEvent("ROW_SCROLL_EVIDENCE", $"direction={direction}, phase={phase}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, tick={tick}/{maxTicks}, sample={sample}/{sampleLimit}, decision={evidence.Decision}, bestRows={evidence.BestRows}, bestScore={evidence.BestScore}, secondScore={evidence.SecondScore}, margin={evidence.Margin}, strong={evidence.Strong}, movedDistance={evidence.MovedDistance}, frameDistance={frameDistance}, stable={observation.Stable}, scrollbarFound={scrollbar.Found}, scrollbarCenter={scrollbar.CenterY}, scrollbarDelta={ScrollbarDelta(beforeScrollbar, scrollbar)}, directionalScrollbarDelta={directionalScrollbarDelta}, scrollbarPixelsPerRow={scrollbarPixelsPerRow:F3}, scrollbarRun={scrollbar.StartY}-{scrollbar.EndY}, scores=0:{evidence.NoMoveScore}|1:{evidence.OneRowScore}|2:{evidence.TwoRowScore}, reason={evidence.Reason}");
                return observation;
            },
            trace =>
            {
                var traceEvidence = trace.Observation.Evidence;
                if (trace.Action.StartsWith("enter_settle", StringComparison.Ordinal) && settleStartedTicks.Add(trace.Tick))
                {
                    scanLog.WriteEvent("ROW_SCROLL_SETTLE_START", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, tick={trace.Tick}/{maxTicks}, triggerDecision={traceEvidence.Decision}, triggerAction={trace.Action}, frameDistance={trace.Observation.FrameDistance}, timeoutMs={RowScrollCoordinator.MaximumSettleMilliseconds}, pollMs={pollMilliseconds}, reason={traceEvidence.Reason}");
                }

                if (trace.Action == "accept_settled")
                {
                    scanLog.WriteEvent("ROW_SCROLL_SETTLE_ACCEPT", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, tick={trace.Tick}/{maxTicks}, sample={trace.Sample}, decision={traceEvidence.Decision}, bestScore={traceEvidence.BestScore}, margin={traceEvidence.Margin}, movedDistance={traceEvidence.MovedDistance}, frameDistance={trace.Observation.FrameDistance}, reason={traceEvidence.Reason}");
                }
                else if (trace.Action == "stop_ambiguous")
                {
                    scanLog.WriteEvent("ROW_SCROLL_SETTLE_TIMEOUT", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, tick={trace.Tick}/{maxTicks}, sample={trace.Sample}, decision={traceEvidence.Decision}, bestScore={traceEvidence.BestScore}, margin={traceEvidence.Margin}, movedDistance={traceEvidence.MovedDistance}, frameDistance={trace.Observation.FrameDistance}, timeoutMs={RowScrollCoordinator.MaximumSettleMilliseconds}, reason={traceEvidence.Reason}");
                }
                else if (trace.Action == "retry_after_full_no_move_window")
                {
                    scanLog.WriteEvent("ROW_SCROLL_SECOND_TRANSACTION_ALLOWED", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, tick={trace.Tick}/{maxTicks}, sample={trace.Sample}, decision={traceEvidence.Decision}, movedDistance={traceEvidence.MovedDistance}, frameDistance={trace.Observation.FrameDistance}, scrollbarDelta={trace.Observation.ScrollbarPixelDelta}, stableNoMoveFrames=3, timeoutMs={RowScrollCoordinator.MaximumSettleMilliseconds}, reason={traceEvidence.Reason}");
                }

                if (traceEvidence.Decision == RowAdvanceDecision.NoMove)
                {
                    scanLog.WriteEvent("ROW_SCROLL_NO_MOVE_CONFIRMED", $"direction={direction}, phase={trace.Phase}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, tick={trace.Tick}/{maxTicks}, sample={trace.Sample}, action={trace.Action}, bestScore={traceEvidence.BestScore}, margin={traceEvidence.Margin}, movedDistance={traceEvidence.MovedDistance}, frameDistance={trace.Observation.FrameDistance}, reason={traceEvidence.Reason}");
                }
            },
            token);

        var settleTimeoutProvisional = RowScrollReleasePolicy.TryCreateSettleTimeoutProvisional(
            result,
            out var provisionalEvidence);
        var provisionalAccepted = result.Accepted || settleTimeoutProvisional;
        if (settleTimeoutProvisional)
        {
            scanLog.WriteEvent("ROW_SCROLL_SETTLE_RELEASE_VERIFY", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, tick={result.Tick}/{maxTicks}, sample={result.Sample}, provisionalDecision={provisionalEvidence.Decision}, bestRows={provisionalEvidence.BestRows}, bestScore={provisionalEvidence.BestScore}, secondScore={provisionalEvidence.SecondScore}, margin={provisionalEvidence.Margin}, movedDistance={provisionalEvidence.MovedDistance}, frameDistance={result.Observation.FrameDistance}, reason={provisionalEvidence.Reason}");
        }

        var finalObservation = result.Observation;
        var releaseConfirmed = false;
        window.MoveCursor(neutralPoint);
        if (provisionalAccepted)
        {
            RowAdvanceDecision? releaseDecision = null;
            var releaseStableFrames = 0;
            var previousReleaseGrid = latestViewport.Grid;
            for (var releaseSample = 1; releaseSample <= VerifiedScrollReleaseSamples; releaseSample++)
            {
                await Task.Delay(pollMilliseconds, token);
                var signatureWatch = Stopwatch.StartNew();
                var releasedViewport = CaptureViewportSignatures(window, listGridRect, rowSignatureRects);
                signatureWatch.Stop();
                signatureMilliseconds += signatureWatch.Elapsed.TotalMilliseconds;
                var movedDistance = SignatureDistance(beforeGrid, releasedViewport.Grid);
                var frameDistance = SignatureDistance(previousReleaseGrid, releasedViewport.Grid);
                previousReleaseGrid = releasedViewport.Grid;
                latestViewport = releasedViewport;
                var verification = upward
                    ? VerifyOneRowUp(beforeRows, releasedViewport.Rows)
                    : VerifyOneRowDown(beforeRows, releasedViewport.Rows);
                var releaseStructuralEvidence = RowAdvanceEvaluator.Evaluate(
                    verification.NoMoveScore,
                    verification.OneRowScore,
                    verification.TwoRowScore,
                    movedDistance);
                var releaseScrollbar = CaptureScrollbarThumbProbe(window, profile);
                var releaseScrollbarDelta = DirectionalScrollbarDelta(beforeScrollbar, releaseScrollbar, upward);
                var releaseEvidence = releaseScrollbarDelta is int releasePixelDelta
                    ? RowAdvanceEvaluator.ReconcileScrollbarPosition(releaseStructuralEvidence, releasePixelDelta, scrollbarPixelsPerRow)
                    : releaseStructuralEvidence;
                finalObservation = new RowScrollObservation(releaseEvidence, frameDistance, releaseScrollbarDelta);

                if (finalObservation.Stable
                    && releaseEvidence.Decision is RowAdvanceDecision.OneRow or RowAdvanceDecision.TwoRows)
                {
                    if (releaseDecision == releaseEvidence.Decision)
                    {
                        releaseStableFrames++;
                    }
                    else
                    {
                        releaseDecision = releaseEvidence.Decision;
                        releaseStableFrames = 1;
                    }
                }
                else
                {
                    releaseDecision = null;
                    releaseStableFrames = 0;
                }

                scanLog.WriteEvent("ROW_SCROLL_RELEASE_EVIDENCE", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, sample={releaseSample}/{VerifiedScrollReleaseSamples}, provisionalDecision={provisionalEvidence.Decision}, decision={releaseEvidence.Decision}, bestRows={releaseEvidence.BestRows}, bestScore={releaseEvidence.BestScore}, secondScore={releaseEvidence.SecondScore}, margin={releaseEvidence.Margin}, movedDistance={releaseEvidence.MovedDistance}, frameDistance={frameDistance}, stable={finalObservation.Stable}, stableMatches={releaseStableFrames}/{VerifiedScrollReleaseStableFrames}, scrollbarFound={releaseScrollbar.Found}, scrollbarCenter={releaseScrollbar.CenterY}, directionalScrollbarDelta={releaseScrollbarDelta}, scrollbarPixelsPerRow={scrollbarPixelsPerRow:F3}, scores=0:{releaseEvidence.NoMoveScore}|1:{releaseEvidence.OneRowScore}|2:{releaseEvidence.TwoRowScore}, reason={releaseEvidence.Reason}");
                if (releaseStableFrames >= VerifiedScrollReleaseStableFrames)
                {
                    releaseConfirmed = true;
                    break;
                }
            }
        }

        totalWatch.Stop();

        var evidence = result.Outcome == RowScrollCoordinatorOutcome.NoMovementStop
            ? result.Evidence with { Reason = "scroll_no_movement_after_max_ticks" }
            : finalObservation.Evidence;
        if (provisionalAccepted && !releaseConfirmed)
        {
            evidence = evidence with
            {
                Decision = RowAdvanceDecision.Ambiguous,
                Reason = "cursor_release_unconfirmed"
            };
        }
        else if (provisionalAccepted && evidence.Decision != provisionalEvidence.Decision)
        {
            scanLog.WriteEvent("ROW_SCROLL_RELEASE_RECLASSIFIED", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, provisionalDecision={provisionalEvidence.Decision}, finalDecision={evidence.Decision}, movedDistance={evidence.MovedDistance}, frameDistance={finalObservation.FrameDistance}, scores=0:{evidence.NoMoveScore}|1:{evidence.OneRowScore}|2:{evidence.TwoRowScore}, reason={evidence.Reason}");
        }

        var observationPhase = provisionalAccepted ? "PostCursorRelease" : result.Phase.ToString();
        var details = ScanDiagnosticDetails.RowScroll(
            direction,
            result.Tick,
            maxTicks,
            result.Sample,
            evidence.Decision,
            evidence.NoMoveScore,
            evidence.OneRowScore,
            evidence.TwoRowScore,
            evidence.BestScore,
            evidence.SecondScore,
            evidence.Margin,
            evidence.MovedDistance,
            finalObservation.FrameDistance,
            observationPhase,
            result.SettleSamples,
            result.SettleElapsedMilliseconds,
            evidence.Reason);
        scanLog.WriteEvent("ROW_SCROLL_TIMING", $"direction={direction}, phase={observationPhase}, fromTop={visibleTopLogicalRow}, toTop={targetTop}, tick={result.Tick}/{maxTicks}, scroll_tick_wait_ms={wheelWaitMilliseconds:F1}, list_stable_ms={Math.Max(0, totalWatch.Elapsed.TotalMilliseconds - wheelWaitMilliseconds - signatureMilliseconds):F1}, row_signature_ms={signatureMilliseconds:F1}, settle_samples={result.SettleSamples}, settle_elapsed_ms={result.SettleElapsedMilliseconds:F1}, post_scroll_viewport_ms={totalWatch.Elapsed.TotalMilliseconds:F1}");

        if (!provisionalAccepted)
        {
            if (result.Outcome == RowScrollCoordinatorOutcome.NoMovementStop)
            {
                scanLog.WriteEvent("ROW_SCROLL_FAIL", $"direction={direction}, fromTop={visibleTopLogicalRow}, expectedToTop={targetTop}, ticks={result.Tick}/{maxTicks}, decision={evidence.Decision}, movedDistance={evidence.MovedDistance}, scores=0:{evidence.NoMoveScore}|1:{evidence.OneRowScore}|2:{evidence.TwoRowScore}, reason=scroll_no_movement_after_max_ticks");
                return ScrollRowsResult.Fail(
                    $"scroll produced no confirmed movement after {result.Tick} tick(s)",
                    retryable: false,
                    evidence,
                    details);
            }

            scanLog.WriteEvent("ROW_SCROLL_AMBIGUOUS_STOP", $"direction={direction}, phase={result.Phase}, fromTop={visibleTopLogicalRow}, expectedToTop={targetTop}, tick={result.Tick}/{maxTicks}, sample={result.Sample}, settleSamples={result.SettleSamples}, settleElapsedMs={result.SettleElapsedMilliseconds:F1}, decision={evidence.Decision}, bestRows={evidence.BestRows}, bestScore={evidence.BestScore}, secondScore={evidence.SecondScore}, margin={evidence.Margin}, movedDistance={evidence.MovedDistance}, frameDistance={result.Observation.FrameDistance}, scores=0:{evidence.NoMoveScore}|1:{evidence.OneRowScore}|2:{evidence.TwoRowScore}, reason={evidence.Reason}");
            return ScrollRowsResult.Fail(
                $"scroll evidence remained ambiguous after {result.Sample} sample(s): {evidence.Reason}",
                retryable: false,
                evidence,
                details);
        }

        if (!releaseConfirmed)
        {
            scanLog.WriteEvent("ROW_SCROLL_RELEASE_UNCONFIRMED", $"direction={direction}, fromTop={visibleTopLogicalRow}, expectedToTop={targetTop}, provisionalDecision={provisionalEvidence.Decision}, decision={evidence.Decision}, movedDistance={evidence.MovedDistance}, frameDistance={finalObservation.FrameDistance}, scores=0:{evidence.NoMoveScore}|1:{evidence.OneRowScore}|2:{evidence.TwoRowScore}, reason={evidence.Reason}");
            return ScrollRowsResult.Fail(
                "scroll movement did not remain confirmed after releasing the list hover",
                retryable: false,
                evidence,
                details);
        }

        var rowsAdvanced = evidence.AcceptedRows ?? 0;
        if (rowsAdvanced <= 0)
        {
            return ScrollRowsResult.Fail("row-scroll coordinator accepted a non-positive decision", retryable: false, evidence, details);
        }

        if (rowsAdvanced == 2 && !allowTwoRows)
        {
            scanLog.WriteEvent("ROW_SCROLL_OVERSHOT_BLOCKED", $"direction={direction}, fromTop={visibleTopLogicalRow}, requestedToTop={targetTop}, rowsAdvanced=2, tick={result.Tick}/{maxTicks}, sample={result.Sample}, bestScore={evidence.BestScore}, margin={evidence.Margin}, movedDistance={evidence.MovedDistance}, reason=strict_single_row_path");
            return ScrollRowsResult.Fail(
                "scroll advanced two rows while the current traversal requires exactly one row",
                retryable: false,
                evidence,
                details);
        }

        var estimatedTop = upward
            ? Math.Max(1, visibleTopLogicalRow - rowsAdvanced)
            : Math.Min(maxVisibleTop, visibleTopLogicalRow + rowsAdvanced);
        scanLog.WriteEvent(doneEvent, $"direction={direction}, phase={observationPhase}, fromTop={visibleTopLogicalRow}, estimatedToTop={estimatedTop}, requestedToTop={targetTop}, rowsAdvanced={rowsAdvanced}, tick={result.Tick}/{maxTicks}, sample={result.Sample}, settleSamples={result.SettleSamples}, settleElapsedMs={result.SettleElapsedMilliseconds:F1}, decision={evidence.Decision}, bestScore={evidence.BestScore}, margin={evidence.Margin}, movedDistance={evidence.MovedDistance}, frameDistance={finalObservation.FrameDistance}, reason={evidence.Reason}");
        return ScrollRowsResult.Ok(
            $"confirmed {direction} movement of {rowsAdvanced} row(s) from {visibleTopLogicalRow}",
            rowsAdvanced,
            latestViewport.Rows,
            evidence,
            details);
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

    private static RowVisualSignature[] CaptureRowSignatures(GameWindow window, IReadOnlyList<Rectangle> rowSignatureRects)
    {
        var signatures = new RowVisualSignature[rowSignatureRects.Count];
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
            signatures[i] = RowVisualSignatureExtractor.Create(image, new Rectangle(rect.Left - bounds.Left, rect.Top - bounds.Top, rect.Width, rect.Height));
        }

        return signatures;
    }

    private static ViewportSignatures CaptureViewportSignatures(GameWindow window, Rectangle listGridRect, IReadOnlyList<Rectangle> rowSignatureRects)
    {
        using var image = window.CaptureFrame(listGridRect);
        var rows = new RowVisualSignature[rowSignatureRects.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            var rect = rowSignatureRects[i];
            rows[i] = RowVisualSignatureExtractor.Create(image, new Rectangle(rect.Left - listGridRect.Left, rect.Top - listGridRect.Top, rect.Width, rect.Height));
        }

        return new ViewportSignatures(
            CreateSignature(image, new Rectangle(System.Drawing.Point.Empty, image.Size)),
            rows);
    }

    private static RowScrollVerification VerifyOneRowDown(IReadOnlyList<RowVisualSignature> beforeRows, IReadOnlyList<RowVisualSignature> afterRows)
    {
        var noMove = AverageSignatureDistance(beforeRows, afterRows, [(0, 0), (1, 1), (2, 2), (3, 3)]);
        var oneRow = AverageSignatureDistance(beforeRows, afterRows, [(1, 0), (2, 1), (3, 2)]);
        var twoRows = AverageSignatureDistance(beforeRows, afterRows, [(2, 0), (3, 1)]);
        return new RowScrollVerification(oneRow, noMove, twoRows);
    }

    private static RowScrollVerification VerifyOneRowUp(IReadOnlyList<RowVisualSignature> beforeRows, IReadOnlyList<RowVisualSignature> afterRows)
    {
        var noMove = AverageSignatureDistance(beforeRows, afterRows, [(0, 0), (1, 1), (2, 2), (3, 3)]);
        var oneRow = AverageSignatureDistance(beforeRows, afterRows, [(0, 1), (1, 2), (2, 3)]);
        var twoRows = AverageSignatureDistance(beforeRows, afterRows, [(0, 2), (1, 3)]);
        return new RowScrollVerification(oneRow, noMove, twoRows);
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

    private static string FormatOptionalInt(int? value)
    {
        return value.HasValue ? value.Value.ToString() : "NA";
    }

    private static int AverageSignatureDistance(IReadOnlyList<RowVisualSignature> beforeRows, IReadOnlyList<RowVisualSignature> afterRows, IReadOnlyList<(int Before, int After)> pairs)
    {
        var sum = 0;
        var count = 0;
        foreach (var pair in pairs)
        {
            if (pair.Before < beforeRows.Count && pair.After < afterRows.Count)
            {
                sum += RowVisualSignatureExtractor.Distance(beforeRows[pair.Before], afterRows[pair.After]);
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

    private static WarehouseHeaderProbeResult ReadWarehouseHeader(
        Bitmap source,
        Rectangle headerRect,
        PaddleOcrRecognizer recognizer,
        WarehousePreflightPolicy policy,
        ScanLog scanLog)
    {
        headerRect = ClampRectangle(headerRect, source.Size);
        if (headerRect.IsEmpty)
        {
            return new WarehouseHeaderProbeResult(false, 0, 4, 0, null, null, false, string.Empty);
        }

        try
        {
            using var header = source.Clone(headerRect, source.PixelFormat);
            var rawOcr = recognizer.Recognize(header, [new CvRect(0, 0, header.Width, header.Height)]);
            var raw = rawOcr.Count > 0
                ? WarehousePreflightEvaluator.EvaluateHeader(rawOcr[0].Text, rawOcr[0].Score, policy)
                : WarehousePreflightEvaluator.EvaluateHeader(string.Empty, 0, policy);
            if (raw.HeaderDetected && raw.InventoryCountDetected)
            {
                scanLog.Write($"Warehouse header OCR raw accepted. text='{SanitizeLogValue(raw.NormalizedText)}', confidence={raw.Confidence:F3}, headerScore={raw.HeaderScore}, inventoryCountDetected=True.");
                return raw;
            }

            using var normalizedImage = VisualProbeEvaluator.NormalizeLuminance(header);
            var normalizedOcr = recognizer.Recognize(normalizedImage, [new CvRect(0, 0, normalizedImage.Width, normalizedImage.Height)]);
            var normalized = normalizedOcr.Count > 0
                ? WarehousePreflightEvaluator.EvaluateHeader(normalizedOcr[0].Text, normalizedOcr[0].Score, policy, usedNormalizedImage: true)
                : WarehousePreflightEvaluator.EvaluateHeader(string.Empty, 0, policy, usedNormalizedImage: true);
            var selected = WarehousePreflightEvaluator.ChooseHeaderResult(raw, normalized);
            scanLog.Write($"Warehouse header OCR evaluated. rawText='{SanitizeLogValue(raw.NormalizedText)}', rawConfidence={raw.Confidence:F3}, normalizedText='{SanitizeLogValue(normalized.NormalizedText)}', normalizedConfidence={normalized.Confidence:F3}, selected={(selected.UsedNormalizedImage ? "normalized" : "raw")}, headerDetected={selected.HeaderDetected}, headerScore={selected.HeaderScore}, inventoryCountDetected={selected.InventoryCountDetected}.");
            return selected;
        }
        catch (Exception ex)
        {
            scanLog.Write($"Warehouse header OCR exception: {ex.GetType().Name}: {SanitizeLogValue(ex.Message)}");
            return new WarehouseHeaderProbeResult(false, 0, 4, 0, null, null, false, string.Empty);
        }
    }

    private static async Task<InventoryCountConsensusResult> ReadInventoryCountConsensusAsync(
        GameWindow window,
        ScanProfile profile,
        PaddleOcrRecognizer recognizer,
        WarehousePreflightPolicy policy,
        ScanLog scanLog,
        CancellationToken token)
    {
        var required = Math.Clamp(policy.CountConsensusFrames, 2, 3);
        var maximumAttempts = Math.Clamp(policy.CountMaximumAttempts, required, 5);
        var poll = TimeSpan.FromMilliseconds(Math.Clamp(policy.PollMilliseconds, 100, 1000));
        var headerRect = window.ToScreenRectangle(profile.Rectangle("inventoryCount"));
        var votes = new Dictionary<(int Current, int Capacity), int>();
        var bestConsensus = 0;

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            using var headerImage = window.Capture(headerRect);
            var result = ReadWarehouseHeader(
                headerImage,
                new Rectangle(Point.Empty, headerImage.Size),
                recognizer,
                policy,
                scanLog);
            if (result.HeaderDetected && result.InventoryCount is int current && result.InventoryCapacity is int capacity)
            {
                var key = (current, capacity);
                var count = votes.TryGetValue(key, out var existing) ? existing + 1 : 1;
                votes[key] = count;
                bestConsensus = Math.Max(bestConsensus, count);
                scanLog.WriteEvent("INVENTORY_COUNT_CONSENSUS", $"attempt={attempt}/{maximumAttempts}, accepted=True, countDetected=True, consensusFrames={count}/{required}, normalizedRetry={result.UsedNormalizedImage}, headerScore={result.HeaderScore}");
                if (count >= required)
                {
                    return new InventoryCountConsensusResult(current, capacity, count, attempt);
                }
            }
            else
            {
                scanLog.WriteEvent("INVENTORY_COUNT_CONSENSUS", $"attempt={attempt}/{maximumAttempts}, accepted=False, countDetected={result.InventoryCountDetected}, consensusFrames={bestConsensus}/{required}, normalizedRetry={result.UsedNormalizedImage}, headerScore={result.HeaderScore}");
            }

            if (attempt < maximumAttempts)
            {
                await Task.Delay(poll, token);
            }
        }

        return new InventoryCountConsensusResult(null, null, bestConsensus, maximumAttempts);
    }

    private static WarehouseMonitorPlan CreateWarehouseMonitorPlan(GameWindow window, ScanProfile profile)
    {
        var headerRect = window.ToScreenRectangle(profile.Rectangle("inventoryCount"));
        var listGridRect = ProfileRectangleOrFallback(
            window,
            profile,
            "listGridRect",
            BuildListGridFallback(window, profile, Math.Max(1, profile.VisibleRows), Math.Max(1, profile.VisibleColumns)));
        var detailRect = window.ToScreenRectangle(profile.Rectangle("detailPanel"));
        var confirmationBounds = Rectangle.Intersect(
            window.ClientScreenRect,
            Rectangle.Union(Rectangle.Union(headerRect, listGridRect), detailRect));
        var driveDiscOffset = window.ToScreenPoint(profile.Point("driveDiscOffset"));
        var driveDiscStepNormalized = profile.Point("driveDiscStep");
        var driveDiscStep = window.ToClientSize(new SizeF(driveDiscStepNormalized.X, driveDiscStepNormalized.Y));
        using var headerImage = window.Capture(headerRect);
        var baseline = WarehousePreflightEvaluator.CreateMonitorSignature(
            headerImage,
            [new Rectangle(Point.Empty, headerImage.Size)]);
        return new WarehouseMonitorPlan(
            headerRect,
            baseline,
            confirmationBounds,
            ToLocalRectangle(headerRect, confirmationBounds, confirmationBounds.Size),
            ToLocalRectangle(listGridRect, confirmationBounds, confirmationBounds.Size),
            ToLocalRectangle(detailRect, confirmationBounds, confirmationBounds.Size),
            new Point(driveDiscOffset.X - confirmationBounds.Left, driveDiscOffset.Y - confirmationBounds.Top),
            driveDiscStep);
    }

    private static WarehouseFastProbe CaptureWarehouseFastProbe(
        GameWindow window,
        WarehouseMonitorPlan plan,
        WarehouseMonitorSignature baseline)
    {
        using var image = window.Capture(plan.FastScreenBounds);
        var health = WarehousePreflightEvaluator.EvaluateCaptureHealth(image);
        var signature = health.Passed
            ? WarehousePreflightEvaluator.CreateMonitorSignature(
                image,
                [new Rectangle(Point.Empty, image.Size)])
            : new WarehouseMonitorSignature([]);
        var score = health.Passed
            ? WarehousePreflightEvaluator.CompareMonitorSignature(baseline, signature)
            : 0;
        return new WarehouseFastProbe(health.Passed, score, health, signature);
    }

    private static WarehouseStrongProbe CaptureWarehouseStrongProbe(
        GameWindow window,
        WarehouseMonitorPlan plan,
        PaddleOcrRecognizer recognizer,
        WarehousePreflightPolicy policy,
        ScanLog scanLog)
    {
        using var image = window.Capture(plan.ConfirmationScreenBounds);
        var health = WarehousePreflightEvaluator.EvaluateCaptureHealth(image);
        var header = health.Passed
            ? ReadWarehouseHeader(image, plan.ConfirmationHeaderRect, recognizer, policy, scanLog)
            : default;
        var structure = health.Passed
            ? WarehousePreflightEvaluator.EvaluateStructure(
                image,
                plan.ConfirmationListGridRect,
                plan.ConfirmationDetailPanelRect,
                plan.ConfirmationDriveDiscOffset,
                plan.DriveDiscStep)
            : default;
        var headerSignature = health.Passed
            ? WarehousePreflightEvaluator.CreateMonitorSignature(image, [plan.ConfirmationHeaderRect])
            : new WarehouseMonitorSignature([]);
        return new WarehouseStrongProbe(
            WarehousePreflightEvaluator.IsStrongConfirmationAccepted(health, header, structure, policy),
            health,
            header,
            structure,
            headerSignature);
    }

    private static Rectangle ToLocalRectangle(Rectangle screenRect, Rectangle captureBounds, Size imageSize) =>
        RelativeIntersection(screenRect, captureBounds, imageSize);

    private static RarityProbe DetectRarityAround(GameWindow window, ScanProfile profile, System.Drawing.Point center)
    {
        const int radius = 36;
        using var image = window.Capture(new Rectangle(center.X - radius, center.Y - radius, radius * 2 + 1, radius * 2 + 1));
        var candidates = new[]
        {
            new VisualRarityCandidate("S", profile.Color("rarityS")),
            new VisualRarityCandidate("A", profile.Color("rarityA")),
            new VisualRarityCandidate("B", profile.Color("rarityB")),
        };

        var rarityPolicy = (profile.VisualProbes ?? new VisualProbeOptions()).Rarity ?? new RarityProbePolicy();
        var best = ProbeBestRarity(image, candidates, FixedRaritySamplePoints(image.Width, image.Height), rarityPolicy);
        if (best.Rarity is not null)
        {
            return best with { FullScan = false };
        }

        best = ProbeBestRarity(image, candidates, CenterRaritySamplePoints(image.Width, image.Height), rarityPolicy);
        if (best.Rarity is not null)
        {
            return best with { FullScan = false };
        }

        return ProbeBestRarity(image, candidates, FullRaritySamplePoints(image.Width, image.Height), rarityPolicy) with { FullScan = true };
    }

    private static RarityProbe ProbeBestRarity(
        Bitmap image,
        IReadOnlyList<VisualRarityCandidate> candidates,
        IEnumerable<System.Drawing.Point> points,
        RarityProbePolicy policy)
    {
        var result = VisualProbeEvaluator.EvaluateRarity(image, candidates, points, policy);
        return new RarityProbe(result.Rarity, result.BestColor, result.BestCandidate, result.BestScore, result.SecondScore, result.Margin, FullScan: false);
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

    private static ulong CaptureGridCellFingerprint(GameWindow window, Point clickPoint, PointF step)
    {
        var bounds = window.ClientScreenRect;
        var halfWidth = Math.Clamp((int)Math.Round(Math.Abs(step.X) * 0.22), 16, 32);
        var halfHeight = Math.Clamp((int)Math.Round(Math.Abs(step.Y) * 0.22), 20, 40);
        var rect = Rectangle.Intersect(
            Rectangle.FromLTRB(
                clickPoint.X - halfWidth,
                clickPoint.Y - halfHeight,
                clickPoint.X + halfWidth,
                clickPoint.Y + halfHeight),
            bounds);
        using var image = window.CaptureFrame(rect);
        return RowVisualSignatureExtractor.Create(
            image,
            new Rectangle(Point.Empty, image.Size)).Hash;
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

    private static ImageSignature CaptureSelectionSignature(Rectangle rect)
    {
        using var image = new Bitmap(rect.Width, rect.Height);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.CopyFromScreen(rect.Location, System.Drawing.Point.Empty, image.Size);
        }

        return new ImageSignature(0, SelectionVisualProbe.CreateSamples(image), EmphasizeLocalizedChanges: true);
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
        return ProbeChangeDistance(previousSignatures, currentSignatures) > tolerance;
    }

    private static int ProbeChangeDistance(IReadOnlyList<ImageSignature> previousSignatures, IReadOnlyList<ImageSignature> currentSignatures)
    {
        var count = Math.Min(previousSignatures.Count, currentSignatures.Count);
        if (count == 0 || previousSignatures.Count != currentSignatures.Count)
        {
            return int.MaxValue;
        }

        var maxDistance = 0;
        for (var i = 0; i < count; i++)
        {
            maxDistance = Math.Max(maxDistance, SignatureDistance(previousSignatures[i], currentSignatures[i]));
        }

        return maxDistance;
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
        if (left.Samples.Length == 0 || right.Samples.Length == 0)
        {
            return left.Hash == right.Hash ? 0 : int.MaxValue;
        }

        return left.EmphasizeLocalizedChanges || right.EmphasizeLocalizedChanges
            ? SelectionVisualProbe.MeasureLocalizedMovement(left.Samples, right.Samples)
            : VisualProbeEvaluator.MeasureLuminanceMovement(left.Samples, right.Samples);
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

    private static void ObserveAcceptedPanel(
        ScanRuntimeState runtimeState,
        PanelCapture panelCapture,
        ScanLog scanLog,
        bool postScrollFirstCell)
    {
        runtimeState.AdaptiveTiming?.ObservePanel(
            panelCapture.ChangeMilliseconds,
            panelCapture.FullRoiMilliseconds,
            panelCapture.StableMilliseconds,
            panelCapture.WaitMilliseconds,
            panelCapture.CaptureMilliseconds,
            panelCapture.FrameLoopMilliseconds,
            postScrollFirstCell);
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
    }

    private static async Task<FirstPairBootstrapResult> CaptureFirstPairAsync(
        GameWindow window,
        ScanProfile profile,
        Rectangle panelRect,
        IReadOnlyList<CvRect> rois,
        System.Drawing.Point statOffset,
        Color statRowBackground,
        Rectangle panelChangeProbeRect,
        ScanRuntimeState runtimeState,
        ScanLog scanLog,
        PointF offset,
        PointF step,
        int pass,
        int visualRow,
        int firstColumn,
        int secondColumn,
        int maxColumns,
        int? logicalRow,
        string visibleTopText,
        string viewportStateText,
        CancellationToken token)
    {
        var firstPoint = window.ToScreenPoint(new PointF(offset.X + step.X * firstColumn, offset.Y + step.Y * visualRow));
        var secondPoint = window.ToScreenPoint(new PointF(offset.X + step.X * secondColumn, offset.Y + step.Y * visualRow));
        var timeoutMs = FirstPairBootstrapTiming.ResolveMaximumWaitMilliseconds(profile.LoadTimeoutMs);
        scanLog.WriteEvent(
            "FIRST_PAIR_BOOTSTRAP_START",
            $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={visualRow}, firstCol={firstColumn}, secondCol={secondColumn}, timeoutMs={timeoutMs}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}");

        window.MoveCursor(firstPoint);
        await window.SelectDiscAsync(firstPoint, token);
        await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
        var provisionalFirstSignatures = CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois);

        PanelCapture? directSecond = null;
        PanelCapture? directFirst = null;
        try
        {
            directSecond = await CaptureFirstPairTargetAsync(
                "second",
                secondPoint,
                provisionalFirstSignatures,
                window,
                profile,
                panelRect,
                rois,
                statOffset,
                statRowBackground,
                panelChangeProbeRect,
                runtimeState,
                scanLog,
                token,
                timeoutMs);
            directFirst = await CaptureFirstPairTargetAsync(
                "first_return",
                firstPoint,
                directSecond.ProbeSignatures,
                window,
                profile,
                panelRect,
                rois,
                statOffset,
                statRowBackground,
                panelChangeProbeRect,
                runtimeState,
                scanLog,
                token,
                timeoutMs);

            var result = new FirstPairBootstrapResult(directFirst, directSecond, directFirst.ProbeSignatures, "first");
            directFirst = null;
            directSecond = null;
            return result;
        }
        catch (TimeoutException ex)
        {
            scanLog.WriteEvent("FIRST_PAIR_RECOVERED", $"phase=direct_pair, reason={SanitizeLogValue(ex.Message)}, action=search_witness");
            await window.ResynchronizeDiscSelectionAsync(token);
        }
        finally
        {
            directFirst?.Dispose();
            directSecond?.Dispose();
        }

        var witnessAttempts = 0;
        foreach (var candidate in BuildFirstPairWitnessCandidates(window, profile, offset, step, visualRow, firstColumn, secondColumn, maxColumns))
        {
            token.ThrowIfCancellationRequested();
            var rarityProbe = DetectRarityAround(window, profile, candidate.Point);
            if (rarityProbe.Rarity is null)
            {
                await Task.Delay(25, token);
                rarityProbe = DetectRarityAround(window, profile, candidate.Point);
            }

            if (rarityProbe.Rarity is null)
            {
                continue;
            }

            witnessAttempts++;
            scanLog.WriteEvent("FIRST_PAIR_WITNESS_ATTEMPT", $"attempt={witnessAttempts}, visualRow={candidate.VisualRow}, col={candidate.Column}, rarity={rarityProbe.Rarity}, point={candidate.Point}");
            var activeSignatures = CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois);
            PanelCapture? witness = null;
            PanelCapture? confirmedFirst = null;
            PanelCapture? witnessReturn = null;
            PanelCapture? confirmedSecond = null;
            try
            {
                witness = await CaptureFirstPairTargetAsync(
                    $"witness_{candidate.VisualRow}_{candidate.Column}", candidate.Point, activeSignatures,
                    window, profile, panelRect, rois, statOffset, statRowBackground, panelChangeProbeRect,
                    runtimeState, scanLog, token, timeoutMs);
                scanLog.WriteEvent("FIRST_PAIR_WITNESS_READY", $"attempt={witnessAttempts}, visualRow={candidate.VisualRow}, col={candidate.Column}, waitMs={witness.WaitMilliseconds:F1}");

                confirmedFirst = await CaptureFirstPairTargetAsync(
                    "first_from_witness", firstPoint, witness.ProbeSignatures,
                    window, profile, panelRect, rois, statOffset, statRowBackground, panelChangeProbeRect,
                    runtimeState, scanLog, token, timeoutMs);
                witnessReturn = await CaptureFirstPairTargetAsync(
                    $"witness_return_{candidate.VisualRow}_{candidate.Column}", candidate.Point, confirmedFirst.ProbeSignatures,
                    window, profile, panelRect, rois, statOffset, statRowBackground, panelChangeProbeRect,
                    runtimeState, scanLog, token, timeoutMs);
                confirmedSecond = await CaptureFirstPairTargetAsync(
                    "second_from_witness", secondPoint, witnessReturn.ProbeSignatures,
                    window, profile, panelRect, rois, statOffset, statRowBackground, panelChangeProbeRect,
                    runtimeState, scanLog, token, timeoutMs);

                scanLog.WriteEvent("FIRST_PAIR_RECOVERED", $"phase=witness_round_trip, attempt={witnessAttempts}, witnessRow={candidate.VisualRow}, witnessCol={candidate.Column}");
                var result = new FirstPairBootstrapResult(confirmedFirst, confirmedSecond, confirmedSecond.ProbeSignatures, "second");
                confirmedFirst = null;
                confirmedSecond = null;
                return result;
            }
            catch (TimeoutException ex)
            {
                scanLog.WriteEvent("FIRST_PAIR_WITNESS_SKIPPED", $"attempt={witnessAttempts}, visualRow={candidate.VisualRow}, col={candidate.Column}, reason={SanitizeLogValue(ex.Message)}");
                await window.ResynchronizeDiscSelectionAsync(token);
            }
            finally
            {
                witness?.Dispose();
                confirmedFirst?.Dispose();
                witnessReturn?.Dispose();
                confirmedSecond?.Dispose();
            }
        }

        scanLog.WriteEvent("FIRST_PAIR_FAILED", $"reason=first_pair_witness_unavailable, attempts={witnessAttempts}, firstCol={firstColumn}, secondCol={secondColumn}");
        var timeout = new PanelCaptureTimeoutException(
            0, rois.Count, null, null, "first_pair_witness_unavailable",
            false, false, 0, 2, 0);
        throw new PanelCellCaptureException(
            pass, visualRow, firstColumn, maxColumns, logicalRow, Math.Max(1, witnessAttempts),
            window, runtimeState.VisualProfileId, timeout);
    }

    private static async Task<PanelCapture> CaptureFirstPairTargetAsync(
        string label,
        System.Drawing.Point clickPoint,
        ImageSignature[] previousPanelSignatures,
        GameWindow window,
        ScanProfile profile,
        Rectangle panelRect,
        IReadOnlyList<CvRect> rois,
        System.Drawing.Point statOffset,
        Color statRowBackground,
        Rectangle panelChangeProbeRect,
        ScanRuntimeState runtimeState,
        ScanLog scanLog,
        CancellationToken token,
        int timeoutMs)
    {
        token.ThrowIfCancellationRequested();
        var selectionProbeRect = SelectionProbeRect(window, clickPoint);
        var beforeSelectionSignature = CaptureSelectionSignature(selectionProbeRect);
        window.MoveCursor(clickPoint);
        await window.SelectDiscAsync(clickPoint, token);
        var capture = await CaptureStablePanelAsync(
            window, profile, panelRect, rois, statOffset, statRowBackground, panelChangeProbeRect,
            previousPanelSignatures, selectionProbeRect, beforeSelectionSignature, runtimeState, scanLog, token,
            postScrollFirstCell: false,
            sceneAdaptivePanelFloorEligible: false,
            allowSelectionOnlyFallback: false,
            selectionRoundTripReady: false,
            timeoutOverrideMs: timeoutMs);
        scanLog.WriteEvent(
            "FIRST_PAIR_CAPTURED",
            $"target={label}, point={clickPoint}, waitMs={capture.WaitMilliseconds:F1}, visibleRois={capture.VisibleRoiCount}/{rois.Count}, changeMs={FormatOptionalMs(capture.ChangeMilliseconds)}, stableFrames={capture.SelectedStableFrames}/{capture.RequiredStableFrames}, accept={capture.AcceptReason}");
        return capture;
    }

    private static IReadOnlyList<FirstPairWitnessCandidate> BuildFirstPairWitnessCandidates(
        GameWindow window,
        ScanProfile profile,
        PointF offset,
        PointF step,
        int firstVisualRow,
        int firstColumn,
        int secondColumn,
        int maxColumns)
    {
        return FirstPairWitnessPlanner.Build(
                firstVisualRow,
                firstColumn,
                secondColumn,
                maxColumns,
                profile.VisibleRows,
                profile.VisibleColumns)
            .Select(candidate => new FirstPairWitnessCandidate(
                candidate.VisualRow,
                candidate.Column,
                window.ToScreenPoint(new PointF(
                    offset.X + step.X * candidate.Column,
                    offset.Y + step.Y * candidate.VisualRow))))
            .Where(candidate => window.DiscSelectionInput?.Contains(candidate.Point) ?? true)
            .ToArray();
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
        bool sceneAdaptivePanelFloorEligible,
        bool firstQueuedItem,
        bool initialSelectionRoundTripReady,
        bool initialVerifiedRowStart = false)
    {
        const int maxAttempts = 2;
        var activePreviousPanelSignatures = previousPanelSignatures;
        var activeSelectionProbeRect = selectionProbeRect;
        var activeBeforeSelectionSignature = beforeSelectionSignature;
        var selectionRoundTripReady = initialSelectionRoundTripReady;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var allowSelectionEvidence = selectionRoundTripReady;
                return await CaptureStablePanelAsync(window, profile, panelRect, rois, statOffset, statRowBackground, panelChangeProbeRect, activePreviousPanelSignatures, activeSelectionProbeRect, activeBeforeSelectionSignature, runtimeState, scanLog, token, postScrollFirstCell, sceneAdaptivePanelFloorEligible && attempt == 1, allowSelectionEvidence, selectionRoundTripReady, verifiedRowStart: initialVerifiedRowStart && attempt == 1);
            }
            catch (StalePanelException) when (attempt < maxAttempts && !selectionRoundTripReady)
            {
                runtimeState.PanelStability.MarkSafetyFallback();
                scanLog.WriteEvent("PANEL_STALE_RETRY", $"attempt={attempt}/{maxAttempts - 1}, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, point={clickPoint}");
                if (firstQueuedItem)
                {
                    scanLog.WriteEvent("FIRST_CELL_REFRESH_REQUIRED", $"attempt={attempt}/{maxAttempts - 1}, reason=stale_panel, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}");
                }
                await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
                var refresh = await RefreshSelectionForPanelRetryAsync(window, profile, panelRect, rois, panelChangeProbeRect, scanLog, clickPoint, selectionRefreshPoint, pass, row, col, maxColumns, logicalRow, visibleTopText, viewportStateText, token);
                if (firstQueuedItem && refresh.RefreshReady)
                {
                    scanLog.WriteEvent("FIRST_CELL_REFRESH_READY", $"attempt={attempt}/{maxAttempts - 1}, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}");
                }
                selectionRoundTripReady = refresh.SelectionRoundTripReady;
                activePreviousPanelSignatures = refresh.PanelSignatures;
                activeSelectionProbeRect = refresh.SelectionProbeRect;
                activeBeforeSelectionSignature = refresh.SelectionSignature;
            }
            catch (TimeoutException ex) when (attempt < maxAttempts && !selectionRoundTripReady)
            {
                runtimeState.PanelStability.MarkSafetyFallback();
                scanLog.WriteEvent("PANEL_CAPTURE_RETRY", $"attempt={attempt}/{maxAttempts - 1}, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, point={clickPoint}, reason={ex.Message}");
                if (firstQueuedItem)
                {
                    scanLog.WriteEvent("FIRST_CELL_REFRESH_REQUIRED", $"attempt={attempt}/{maxAttempts - 1}, reason=capture_timeout, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}");
                }
                await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
                var refresh = await RefreshSelectionForPanelRetryAsync(window, profile, panelRect, rois, panelChangeProbeRect, scanLog, clickPoint, selectionRefreshPoint, pass, row, col, maxColumns, logicalRow, visibleTopText, viewportStateText, token);
                if (firstQueuedItem && refresh.RefreshReady)
                {
                    scanLog.WriteEvent("FIRST_CELL_REFRESH_READY", $"attempt={attempt}/{maxAttempts - 1}, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}");
                }
                selectionRoundTripReady = refresh.SelectionRoundTripReady;
                activePreviousPanelSignatures = refresh.PanelSignatures;
                activeSelectionProbeRect = refresh.SelectionProbeRect;
                activeBeforeSelectionSignature = refresh.SelectionSignature;
            }
            catch (TimeoutException ex)
            {
                throw new PanelCellCaptureException(
                    pass,
                    row,
                    col,
                    maxColumns,
                    logicalRow,
                    attempt,
                    window,
                    runtimeState.VisualProfileId,
                    ex);
            }
        }

        throw new UnreachableException("Panel capture retry loop exhausted without returning or throwing.");
    }

    private static async Task<SelectionRefreshCapture> RefreshSelectionForPanelRetryAsync(
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
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        await window.ResynchronizeDiscSelectionAsync(token);
        var targetPanelSignatures = CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois);
        ImageSignature[] refreshedPanelSignatures = targetPanelSignatures;
        var refreshReady = false;
        var selectionRoundTripReady = false;
        if (selectionRefreshPoint is { } refreshPoint && refreshPoint != clickPoint)
        {
            scanLog.WriteEvent("PANEL_SELECTION_REFRESH", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, refreshPoint={refreshPoint}, targetPoint={clickPoint}");
            var targetSelectionProbeRect = SelectionProbeRect(window, clickPoint);
            window.MoveCursor(refreshPoint);
            await Task.Delay(Math.Max((int)PanelTargetEvidenceGate.MinimumReliableChangeMilliseconds, profile.LoadPollMs), token);
            var targetSelectedSignature = CaptureSelectionSignature(targetSelectionProbeRect);
            await window.SelectDiscAsync(refreshPoint, token);

            var maximumWaitMs = SelectionRefreshTiming.ResolveMaximumWaitMilliseconds(profile.LoadTimeoutMs);
            var pollMs = Math.Max(5, profile.LoadPollMs);
            ImageSignature[]? latestObservedSignatures = null;
            ImageSignature? previousObservedTargetSelection = null;
            var latestTargetSelectionDistance = 0;
            var result = await SelectionRefreshWaiter.WaitAsync(
                () =>
                {
                    var currentSignatures = CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois);
                    latestObservedSignatures = currentSignatures;
                    var currentTargetSelection = CaptureSelectionSignature(targetSelectionProbeRect);
                    latestTargetSelectionDistance = SignatureDistance(targetSelectedSignature, currentTargetSelection);
                    var changedFromTarget = latestTargetSelectionDistance > PanelChangeTolerance;
                    var stableWithPrevious = previousObservedTargetSelection is not null
                        && SignatureDistance(previousObservedTargetSelection.Value, currentTargetSelection) <= ListStableTolerance;
                    previousObservedTargetSelection = currentTargetSelection;
                    return new SelectionRefreshObservation(changedFromTarget, stableWithPrevious);
                },
                maximumWaitMs,
                pollMs,
                token);
            if (result.Ready && latestObservedSignatures is not null)
            {
                refreshReady = true;
                selectionRoundTripReady = true;
                refreshedPanelSignatures = latestObservedSignatures;
                scanLog.WriteEvent(
                    "PANEL_SELECTION_REFRESH_READY",
                    $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, elapsedMs={result.ElapsedMilliseconds:F1}, selectionDistance={latestTargetSelectionDistance}, threshold={PanelChangeTolerance}, stableFrames={result.StableFrames}/2, frameCount={result.FrameCount}");
                scanLog.WriteEvent(
                    "PANEL_NEIGHBOR_ROUNDTRIP",
                    $"phase=away_ready, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, elapsedMs={result.ElapsedMilliseconds:F1}, stableFrames={result.StableFrames}/2, refreshPoint={refreshPoint}, targetPoint={clickPoint}");
            }
            else
            {
                scanLog.WriteEvent(
                    "PANEL_SELECTION_REFRESH_TIMEOUT",
                    $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, elapsedMs={result.ElapsedMilliseconds:F1}, timeoutMs={maximumWaitMs}, changedFromTarget={result.ChangedFromTarget}, selectionDistance={latestTargetSelectionDistance}, threshold={PanelChangeTolerance}, stableFrames={result.StableFrames}/2, frameCount={result.FrameCount}, baseline=target_snapshot");
                scanLog.WriteEvent(
                    "PANEL_NEIGHBOR_ROUNDTRIP",
                    $"phase=away_failed, pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, elapsedMs={result.ElapsedMilliseconds:F1}, changedFromTarget={result.ChangedFromTarget}, stableFrames={result.StableFrames}/2, refreshPoint={refreshPoint}, targetPoint={clickPoint}");
            }
        }

        token.ThrowIfCancellationRequested();
        var refreshedSelectionProbeRect = SelectionProbeRect(window, clickPoint);
        window.MoveCursor(clickPoint);
        await Task.Delay(Math.Max((int)PanelTargetEvidenceGate.MinimumReliableChangeMilliseconds, profile.LoadPollMs), token);
        var refreshedSelectionSignature = CaptureSelectionSignature(refreshedSelectionProbeRect);
        await window.SelectDiscAsync(clickPoint, token);
        return new SelectionRefreshCapture(
            refreshedPanelSignatures,
            refreshedSelectionProbeRect,
            refreshedSelectionSignature,
            refreshReady,
            selectionRoundTripReady);
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
            : PanelCaptureTimingPolicy.GdiSafeMinimumAcceptMilliseconds;
        return new AdaptivePanelTimingDecision(
            PanelCaptureTimingPolicy.ResolveSafeMinimumAcceptMilliseconds(
                profile.PanelChangedMinimumAcceptMs,
                panelMinAcceptFloorMs,
                profile.LoadTimeoutMs,
                captureMode),
            PanelCaptureTimingPolicy.SafeRequiredStableFrames,
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
        bool sceneAdaptivePanelFloorEligible,
        bool allowSelectionOnlyFallback,
        bool selectionRoundTripReady,
        int? timeoutOverrideMs = null,
        bool verifiedRowStart = false)
    {
        var timeout = TimeSpan.FromMilliseconds(Math.Clamp(timeoutOverrideMs ?? profile.LoadTimeoutMs, 1, profile.LoadTimeoutMs));
        var interval = TimeSpan.FromMilliseconds(PanelCaptureTimingPolicy.ResolvePollDelayMilliseconds(
            Math.Max(5, profile.LoadPollMs),
            window.ActiveFrameBackend));
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
        var stableProbeRects = stabilityDecision.Source == PanelStabilitySource.TextCore
            ? Array.Empty<Rectangle>()
            : BuildPanelStableProbeRects(panelRect, rois);
        const bool measureTextCoreStability = true;
        var textCoreStableProbeRects = measureTextCoreStability
            ? BuildTextCoreStableProbeRects(panelRect, rois)
            : Array.Empty<Rectangle>();
        var initialGate = PanelCaptureGate.Initialize(previousPanelSignatures is not null);
        var sawPanelChange = initialGate.SawPanelChange;
        var panelChangedFromBaseline = false;
        var selectionChanged = initialGate.SelectionChanged;
        var selectionAttestedPanel = false;
        var selectionEvidenceGate = new PanelTargetEvidenceGate(verifiedRowStart);
        ImageSignature? previousSelectionSignature = null;
        var selectionObservedFrame = -1;
        var selectionStableForObservedFrame = false;
        var targetEvidenceLogged = false;
        ImageSignature[]? previousStableProbeSignatures = null;
        ImageSignature[]? previousTextCoreStableProbeSignatures = null;
        var stableProbeFrames = 0;
        var textCoreStableProbeFrames = 0;
        var weakPanelChange = false;
        var weakPanelChangeDistance = 0;
        var frameCount = 0;
        var captureMilliseconds = 0.0;
        var signatureMilliseconds = 0.0;
        var visibleRoiMilliseconds = 0.0;
        var frameLoopMilliseconds = 0.0;
        var frameToBitmapMilliseconds = 0.0;
        var bitmapCreatedCount = 0;
        double? changeMilliseconds = initialGate.ChangeMilliseconds;
        double? weakPanelChangeMilliseconds = null;
        double? selectionChangeMilliseconds = null;
        double? fullRoiMilliseconds = null;
        double? stableMilliseconds = null;
        double? textCoreStableMilliseconds = null;
        var acceptReason = "changed_stable_full_roi";
        var acceptGateReason = "waiting_for_panel_change";
        var lastVisibleCount = 0;
        var lastReadableRoiCount = -1;
        var roiKeys = profile.OrderedRoiKeys();
        var lastRoiVisibility = new VisibleRoiEvaluation(0, roiKeys.FirstOrDefault(), null, false, "not_sampled");
        var quickRejectReason = runtimeState.QuickPanelAcceptEnabled ? "waiting_for_panel_change" : "disabled";
        var unchangedFallbackDelay = TimeSpan.FromMilliseconds(Math.Clamp(
            profile.PanelUnchangedFallbackMs,
            Math.Max(1, profile.MinPanelUnchangedFallbackMs),
            profile.LoadTimeoutMs));
        var useProbeOnlyBeforeChange = true;

        bool ObserveSelectionChange(double elapsed)
        {
            if (selectionEvidenceGate.Stable)
            {
                return true;
            }

            if (selectionObservedFrame == frameCount)
            {
                return selectionStableForObservedFrame;
            }

            var currentSelectionSignature = CaptureSelectionSignature(selectionProbeRect);
            var changed = SignatureDistance(beforeSelectionSignature, currentSelectionSignature) > PanelChangeTolerance;
            var stableWithPrevious = previousSelectionSignature is not null
                && SignatureDistance(previousSelectionSignature.Value, currentSelectionSignature) <= ListStableTolerance;
            previousSelectionSignature = currentSelectionSignature;
            var stable = selectionEvidenceGate.Observe(changed, stableWithPrevious, elapsed);
            selectionObservedFrame = frameCount;
            selectionStableForObservedFrame = stable;
            selectionChanged = selectionEvidenceGate.Changed;
            selectionChangeMilliseconds = selectionEvidenceGate.ChangeMilliseconds;
            return stable;
        }

        bool PromoteSelectionChange(double elapsed, out string blockedReason)
        {
            blockedReason = "";
            if (!ObserveSelectionChange(elapsed))
            {
                return false;
            }

            if (!selectionEvidenceGate.CanPromote(
                    allowSelectionOnlyFallback,
                    postScrollFirstCell,
                    selectionRoundTripReady,
                    out blockedReason))
            {
                return false;
            }

            selectionAttestedPanel = true;
            sawPanelChange = true;
            panelChangedFromBaseline = true;
            acceptReason = selectionRoundTripReady
                ? "neighbor_roundtrip_stable_full_roi"
                : "selection_changed_stable_full_roi";
            blockedReason = "";
            if (!targetEvidenceLogged)
            {
                targetEvidenceLogged = true;
                scanLog.WriteEvent(
                    "PANEL_TARGET_EVIDENCE",
                    $"evidence={(selectionRoundTripReady ? "neighbor_roundtrip" : "selection_change")}, elapsedMs={elapsed:F1}, selectionChangeMs={FormatOptionalMs(selectionChangeMilliseconds)}, selectionStableFrames={selectionEvidenceGate.StableFrames}/{PanelTargetEvidenceGate.RequiredStableFrames}, weakPanelChange={weakPanelChange}, weakPanelChangeDistance={weakPanelChangeDistance}, postScrollFirstCell={postScrollFirstCell}");
                if (selectionRoundTripReady)
                {
                    scanLog.WriteEvent(
                        "PANEL_NEIGHBOR_ROUNDTRIP",
                        $"phase=target_ready, elapsedMs={elapsed:F1}, selectionChangeMs={FormatOptionalMs(selectionChangeMilliseconds)}, selectionStableFrames={selectionEvidenceGate.StableFrames}/{PanelTargetEvidenceGate.RequiredStableFrames}, postScrollFirstCell={postScrollFirstCell}");
                }
            }

            return true;
        }

        void WriteSelectionOnlyBlocked(string reason, double elapsed)
        {
            scanLog.WriteEvent(
                "PANEL_SELECTION_ONLY_BLOCKED",
                $"selection_only_blocked_reason={reason}, post_scroll_panel_change_required={postScrollFirstCell}, elapsedMs={elapsed:F1}, selectionChangeMs={FormatOptionalMs(selectionChangeMilliseconds)}, allowSelectionOnlyFallback={allowSelectionOnlyFallback}");
        }

        void WriteWeakPanelChangeBlocked(string reason, double elapsed)
        {
            scanLog.WriteEvent(
                "PANEL_WEAK_CHANGE_BLOCKED",
                $"weak_panel_change_blocked_reason={reason}, weakPanelChangeMs={FormatOptionalMs(weakPanelChangeMilliseconds)}, weakPanelChangeDistance={weakPanelChangeDistance}, strongTolerance={PanelStrongChangeTolerance}, elapsedMs={elapsed:F1}, selectionChangeMs={FormatOptionalMs(selectionChangeMilliseconds)}");
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
                var probeChangeDistance = ProbeChangeDistance(previousPanelSignatures, probeSignatures);
                panelChangedFromBaseline = PanelCaptureGate.IsStrongChangeCurrentFrame(
                    probeChangeDistance,
                    elapsedMilliseconds,
                    PanelStrongChangeTolerance,
                    MinReliablePanelChangeMs);
                if (panelChangedFromBaseline)
                {
                    sawPanelChange = true;
                    changeMilliseconds ??= elapsedMilliseconds;
                    quickRejectReason = runtimeState.QuickPanelAcceptEnabled ? "waiting_for_roi" : quickRejectReason;
                }
                else if (probeChangeDistance > PanelChangeTolerance)
                {
                    weakPanelChange = true;
                    weakPanelChangeDistance = Math.Max(weakPanelChangeDistance, probeChangeDistance);
                    weakPanelChangeMilliseconds ??= elapsedMilliseconds;
                    ObserveSelectionChange(elapsedMilliseconds);
                    quickRejectReason = runtimeState.QuickPanelAcceptEnabled ? "waiting_for_strong_panel_change" : quickRejectReason;
                }
                probeSignatureWatch.Stop();
                signatureMilliseconds += probeSignatureWatch.Elapsed.TotalMilliseconds;

                if (!sawPanelChange)
                {
                    ObserveSelectionChange(elapsedMilliseconds);
                    if (DateTime.UtcNow - start >= unchangedFallbackDelay)
                    {
                        if (!PromoteSelectionChange(elapsedMilliseconds, out var blockedReason))
                        {
                            if (!string.IsNullOrWhiteSpace(blockedReason))
                            {
                                WriteSelectionOnlyBlocked(blockedReason, elapsedMilliseconds);
                            }

                            if (weakPanelChange)
                            {
                                WriteWeakPanelChangeBlocked(blockedReason.Length == 0 ? "no_selection_change" : blockedReason, elapsedMilliseconds);
                            }

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
            ImageSignature[]? changeProbeSignatures = null;
            if (!sawPanelChange && previousPanelSignatures is not null)
            {
                changeProbeSignatures = CreateSignatures(image, fullPanelProbeRects);
                var fullPanelChangeDistance = ProbeChangeDistance(previousPanelSignatures, changeProbeSignatures);
                var strongPanelChange = PanelCaptureGate.LatchStrongChange(
                    panelChangedFromBaseline,
                    fullPanelChangeDistance,
                    elapsedMilliseconds,
                    PanelStrongChangeTolerance,
                    MinReliablePanelChangeMs);
                if (strongPanelChange)
                {
                    sawPanelChange = true;
                    panelChangedFromBaseline = true;
                    changeMilliseconds ??= elapsedMilliseconds;
                }
                else if (fullPanelChangeDistance > PanelChangeTolerance)
                {
                    weakPanelChange = true;
                    weakPanelChangeDistance = Math.Max(weakPanelChangeDistance, fullPanelChangeDistance);
                    weakPanelChangeMilliseconds ??= elapsedMilliseconds;
                    ObserveSelectionChange(elapsedMilliseconds);
                }
            }

            if (stableProbeRects.Length > 0)
            {
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
                    if (stabilityDecision.Source == PanelStabilitySource.TextCore)
                    {
                        stableMilliseconds ??= elapsedMilliseconds;
                    }
                }
            }
            ObserveSelectionChange(elapsedMilliseconds);
            signatureWatch.Stop();
            signatureMilliseconds += signatureWatch.Elapsed.TotalMilliseconds;

            ImageSignature[] EnsureCurrentChangeProbeSignatures()
            {
                if (changeProbeSignatures is not null)
                {
                    return changeProbeSignatures;
                }

                var baselineWatch = Stopwatch.StartNew();
                changeProbeSignatures = CreateSignatures(image, fullPanelProbeRects);
                baselineWatch.Stop();
                signatureMilliseconds += baselineWatch.Elapsed.TotalMilliseconds;
                return changeProbeSignatures;
            }

            var visibleWatch = Stopwatch.StartNew();
            var roiVisibility = EvaluateVisibleRois(
                image,
                rois,
                roiKeys,
                statOffset,
                (profile.VisualProbes ?? new VisualProbeOptions()).RowPresence ?? new RowPresenceProbePolicy());
            var visibleCount = roiVisibility.Count;
            lastVisibleCount = visibleCount;
            lastRoiVisibility = roiVisibility;
            visibleWatch.Stop();
            visibleRoiMilliseconds += visibleWatch.Elapsed.TotalMilliseconds;
            if (roiVisibility.ValidBoundary)
            {
                roiCompleteFrames = lastReadableRoiCount == visibleCount
                    ? roiCompleteFrames + 1
                    : 1;
                lastReadableRoiCount = visibleCount;
                var requiredLayoutFrames = RequiredRoiBoundaryFrames(visibleCount, rois.Count, requiredStableFrames);
                if (roiCompleteFrames >= requiredLayoutFrames)
                {
                    fullRoiMilliseconds ??= elapsedMilliseconds;
                }
            }
            else
            {
                roiCompleteFrames = 0;
                lastReadableRoiCount = -1;
            }

            if (visibleCount > 0)
            {
                var requiredRoiCompleteFrames = RequiredRoiBoundaryFrames(visibleCount, rois.Count, requiredStableFrames);
                var panelReadable = roiVisibility.ValidBoundary && roiCompleteFrames >= requiredRoiCompleteFrames;
                var fullPanelReadable = visibleCount == rois.Count && panelReadable;
                var panelSelectedStableFrames = textCoreStableProbeFrames;
                var adaptiveEarlyEvidence = panelTiming.EffectivePanelAcceptMode == PanelAcceptMode.AdaptiveEarlyFullRoi
                    && !selectionAttestedPanel;
                var effectiveRequiredStableFrames = adaptiveEarlyEvidence
                    ? 1
                    : requiredStableFrames;
                var selectedStableFrames = adaptiveEarlyEvidence
                    ? roiCompleteFrames
                    : panelSelectedStableFrames;
                var stableEnough = selectedStableFrames >= effectiveRequiredStableFrames
                    && selectionEvidenceGate.Stable;
                var roiEnough = panelReadable;
                acceptGateReason = !panelChangedFromBaseline
                    ? "waiting_for_panel_change"
                    : !selectionEvidenceGate.Stable
                        ? "waiting_for_target_selection_stability"
                        : !roiVisibility.ValidBoundary
                        ? roiVisibility.InvalidReason
                        : roiCompleteFrames < requiredRoiCompleteFrames
                            ? "waiting_for_variable_roi_stability"
                            : !stableEnough
                                ? "waiting_for_stable_frame"
                                : elapsedMilliseconds < changedMinimumAcceptMs
                                    ? "before_min_accept"
                                    : "ready";
                if (runtimeState.QuickPanelAcceptEnabled
                    && !selectionAttestedPanel
                    && panelTiming.WarmupComplete
                    && previousPanelSignatures is not null
                    && fullPanelReadable
                    && panelChangedFromBaseline)
                {
                    if (selectedStableFrames >= 1 && elapsedMilliseconds >= quickMinimumAcceptMs)
                    {
                        var acceptedProbeSignatures = EnsureCurrentChangeProbeSignatures();
                        var bitmapWatch = Stopwatch.StartNew();
                        var acceptedImage = image.ToBitmap();
                        bitmapWatch.Stop();
                        frameToBitmapMilliseconds += bitmapWatch.Elapsed.TotalMilliseconds;
                        bitmapCreatedCount++;
                        frameLoop.Stop();
                        frameLoopMilliseconds += frameLoop.Elapsed.TotalMilliseconds;
                        acceptReason = "quick_changed_stable_full_roi";
                        var waitMilliseconds = (DateTime.UtcNow - start).TotalMilliseconds;
                        return new PanelCapture(acceptedImage, visibleCount, waitMilliseconds, usedFallback: false, acceptedProbeSignatures, frameCount, changeMilliseconds, selectionChangeMilliseconds, fullRoiMilliseconds, stableMilliseconds, textCoreStableMilliseconds, stabilityDecision.SourceName, stabilityDecision.Reason, captureMilliseconds, signatureMilliseconds, visibleRoiMilliseconds, frameLoopMilliseconds, frameToBitmapMilliseconds, bitmapCreatedCount, changedMinimumAcceptMs, requiredStableFrames, panelTiming.SampleCount, panelTiming.Reason, acceptReason, quickAccept: true, quickRejectReason: "accepted", panelTiming.EffectivePanelAcceptMode, panelTiming.EffectivePostScrollPanelAcceptMode, panelTiming.PanelFloorMode, panelTiming.PanelMinAcceptFloorMs, panelTiming.SameRowPanelFloorMs, panelTiming.PostScrollPanelFloorMs, panelFloorReason, CalculateFloorWaitLimitedMilliseconds(changedMinimumAcceptMs, changeMilliseconds, fullRoiMilliseconds, stableMilliseconds), waitMilliseconds - changedMinimumAcceptMs, roiCompleteFrames, selectedStableFrames, selectionEvidenceGate.StableFrames, TargetVerificationKind.ChangedText, "quick_ready");
                    }

                    quickRejectReason = selectedStableFrames < 1 ? "waiting_for_stable_frame" : "before_quick_min_accept";
                }
                else if (runtimeState.QuickPanelAcceptEnabled)
                {
                    quickRejectReason = !panelTiming.WarmupComplete
                        ? "before_warmup_complete"
                        : panelChangedFromBaseline
                            ? visibleCount == rois.Count ? "waiting_for_full_roi" : "variable_roi_requires_stability"
                            : "waiting_for_panel_change";
                }

                if (roiEnough
                    && panelChangedFromBaseline
                    && elapsedMilliseconds >= changedMinimumAcceptMs
                    && stableEnough)
                {
                    var acceptedProbeSignatures = EnsureCurrentChangeProbeSignatures();
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
                    if (adaptiveEarlyEvidence)
                    {
                        finalAcceptReason = panelTiming.EffectivePostScrollPanelAcceptMode == PostScrollPanelAcceptMode.AdaptiveAfterScroll
                            ? "adaptive_after_scroll"
                            : "adaptive_early_full_roi";
                    }

                    var waitMilliseconds = (DateTime.UtcNow - start).TotalMilliseconds;
                    var targetVerificationKind = selectionAttestedPanel
                        ? TargetVerificationKind.IdenticalNeighborRoundTrip
                        : TargetVerificationKind.ChangedText;
                    return new PanelCapture(acceptedImage, visibleCount, waitMilliseconds, usedFallback: false, acceptedProbeSignatures, frameCount, changeMilliseconds, selectionChangeMilliseconds, fullRoiMilliseconds, stableMilliseconds, textCoreStableMilliseconds, stabilityDecision.SourceName, stabilityDecision.Reason, captureMilliseconds, signatureMilliseconds, visibleRoiMilliseconds, frameLoopMilliseconds, frameToBitmapMilliseconds, bitmapCreatedCount, changedMinimumAcceptMs, requiredStableFrames, panelTiming.SampleCount, panelTiming.Reason, finalAcceptReason, quickAccept: false, quickRejectReason, panelTiming.EffectivePanelAcceptMode, panelTiming.EffectivePostScrollPanelAcceptMode, panelTiming.PanelFloorMode, panelTiming.PanelMinAcceptFloorMs, panelTiming.SameRowPanelFloorMs, panelTiming.PostScrollPanelFloorMs, panelFloorReason, CalculateFloorWaitLimitedMilliseconds(changedMinimumAcceptMs, changeMilliseconds, fullRoiMilliseconds, stableMilliseconds), waitMilliseconds - changedMinimumAcceptMs, roiCompleteFrames, selectedStableFrames, selectionEvidenceGate.StableFrames, targetVerificationKind, acceptGateReason);
                }

                var unchangedStableFrames = stabilityDecision.Source == PanelStabilitySource.TextCore
                    ? textCoreStableProbeFrames
                    : stableProbeFrames;
                if (panelReadable && !panelChangedFromBaseline && unchangedStableFrames >= requiredStableFrames && DateTime.UtcNow - start >= unchangedFallbackDelay)
                {
                    if (!PromoteSelectionChange(elapsedMilliseconds, out var blockedReason))
                    {
                        if (!string.IsNullOrWhiteSpace(blockedReason))
                        {
                            WriteSelectionOnlyBlocked(blockedReason, elapsedMilliseconds);
                        }

                        if (weakPanelChange)
                        {
                            WriteWeakPanelChangeBlocked(blockedReason.Length == 0 ? "no_selection_change" : blockedReason, elapsedMilliseconds);
                        }

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

        var timeoutException = new PanelCaptureTimeoutException(
            lastVisibleCount,
            rois.Count,
            lastRoiVisibility.FirstMissingRoi,
            lastRoiVisibility.FirstMissingProbe,
            acceptGateReason,
            sawPanelChange,
            selectionChanged,
            Math.Max(stableProbeFrames, textCoreStableProbeFrames),
            requiredStableFrames,
            frameCount);
        scanLog.WriteEvent(
            "PANEL_CAPTURE_TIMEOUT",
            $"visibleRois={timeoutException.VisibleRois}/{timeoutException.TotalRois}, firstMissingRoi={timeoutException.FirstMissingRoi ?? "none"}, referenceLuma={FormatOptionalInt(timeoutException.ReferenceLuma)}, candidateLuma={FormatOptionalInt(timeoutException.CandidateLuma)}, lumaDelta={FormatOptionalInt(timeoutException.LumaDelta)}, allowedLumaDelta={FormatOptionalInt(timeoutException.AllowedLumaDelta)}, edgeDensityPermille={FormatOptionalInt(timeoutException.EdgeDensityPermille)}, minimumEdgeDensityPermille={FormatOptionalInt(timeoutException.MinimumEdgeDensityPermille)}, acceptGateReason={timeoutException.AcceptGateReason}, sawPanelChange={timeoutException.SawPanelChange}, selectionChanged={timeoutException.SelectionChanged}, stableFrames={timeoutException.StableFrames}/{timeoutException.RequiredStableFrames}, frameCount={timeoutException.FrameCount}, captureMode={window.ActiveCaptureMode}");
        throw timeoutException;
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

    internal sealed record VisibleRoiEvaluation(
        int Count,
        string? FirstMissingRoi,
        RowPresenceProbeResult? FirstMissingProbe,
        bool ValidBoundary,
        string InvalidReason);

    internal static VisibleRoiEvaluation EvaluateVariableRoiLayout(
        IReadOnlyList<bool> presence,
        IReadOnlyList<string> roiKeys,
        IReadOnlyList<RowPresenceProbeResult?>? probes = null)
    {
        const int requiredCoreRois = 4;
        if (presence.Count < requiredCoreRois || roiKeys.Count != presence.Count)
        {
            return new VisibleRoiEvaluation(0, roiKeys.FirstOrDefault(), null, false, "invalid_roi_layout");
        }

        for (var index = 0; index < requiredCoreRois; index++)
        {
            if (!presence[index])
            {
                return new VisibleRoiEvaluation(
                    index,
                    roiKeys[index],
                    probes?.ElementAtOrDefault(index),
                    false,
                    "required_core_missing");
            }
        }

        var readableCount = requiredCoreRois;
        var boundaryFound = false;
        string? firstMissing = null;
        RowPresenceProbeResult? firstMissingProbe = null;
        for (var index = requiredCoreRois; index + 1 < presence.Count; index += 2)
        {
            var namePresent = presence[index];
            var valuePresent = presence[index + 1];
            if (namePresent != valuePresent)
            {
                var missingIndex = namePresent ? index + 1 : index;
                return new VisibleRoiEvaluation(
                    readableCount,
                    roiKeys[missingIndex],
                    probes?.ElementAtOrDefault(missingIndex),
                    false,
                    "incomplete_substat_pair");
            }

            if (!namePresent)
            {
                boundaryFound = true;
                firstMissing ??= roiKeys[index];
                firstMissingProbe ??= probes?.ElementAtOrDefault(index);
                continue;
            }

            if (boundaryFound)
            {
                return new VisibleRoiEvaluation(
                    readableCount,
                    firstMissing,
                    firstMissingProbe,
                    false,
                    "substat_gap");
            }

            readableCount += 2;
        }

        return new VisibleRoiEvaluation(readableCount, firstMissing, firstMissingProbe, true, "");
    }

    internal static int RequiredRoiBoundaryFrames(int visibleCount, int totalCount, int configuredStableFrames) =>
        visibleCount == totalCount ? 1 : Math.Max(3, configuredStableFrames);

    private static VisibleRoiEvaluation EvaluateVisibleRois(
        Bitmap image,
        IReadOnlyList<CvRect> rois,
        IReadOnlyList<string> roiKeys,
        System.Drawing.Point statOffset,
        RowPresenceProbePolicy policy)
    {
        var referenceRoi = rois.Count > 2 ? rois[2] : rois[0];
        var presence = new bool[rois.Count];
        var probes = new RowPresenceProbeResult?[rois.Count];
        for (var index = 0; index < rois.Count; index++)
        {
            RowPresenceProbeResult? probe = index <= 3
                ? null
                : VisualProbeEvaluator.EvaluateRelativeTextRowPresence(image, referenceRoi, rois[index], statOffset, policy);
            probes[index] = probe;
            presence[index] = probe is null || probe.Value.Present;
        }

        return EvaluateVariableRoiLayout(presence, roiKeys, probes);
    }

    private static VisibleRoiEvaluation EvaluateVisibleRois(
        CapturedFrame image,
        IReadOnlyList<CvRect> rois,
        IReadOnlyList<string> roiKeys,
        System.Drawing.Point statOffset,
        RowPresenceProbePolicy policy)
    {
        var referenceRoi = rois.Count > 2 ? rois[2] : rois[0];
        var presence = new bool[rois.Count];
        var probes = new RowPresenceProbeResult?[rois.Count];
        for (var index = 0; index < rois.Count; index++)
        {
            RowPresenceProbeResult? probe = index <= 3
                ? null
                : VisualProbeEvaluator.EvaluateRelativeTextRowPresence(image, referenceRoi, rois[index], statOffset, policy);
            probes[index] = probe;
            presence[index] = probe is null || probe.Value.Present;
        }

        return EvaluateVariableRoiLayout(presence, roiKeys, probes);
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
        FastOcrAssistRecorder? fastOcrAssistRecorder,
        bool normalizedRetryEnabled,
        CancellationTokenSource linked)
    {
        return Enumerable.Range(1, workerCount)
            .Select(workerId => Task.Run(() =>
            {
                try { ConsumeOcrWorker(queue, ocrResults, outputDir, options, scanLog, workerId, ocrIntraOpThreads, counters, diagnostics, shadowDataset, fastOcrShadow, fastOcrAssist, fastOcrAssistRecorder, normalizedRetryEnabled, linked.Token); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
                catch { linked.Cancel(); throw; }
            }))
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
        FastOcrAssistRecorder? fastOcrAssistRecorder,
        bool normalizedRetryEnabled,
        CancellationToken pipelineToken)
    {
        using var recognizer = new PaddleOcrRecognizer(AppPaths.ModelFile, AppPaths.CharacterDictFile, ocrIntraOpThreads);
        var cleaner = new DriveDiscCleaner(_wikiData);
        scanLog.Write($"OCR worker {workerId} started. IntraOpThreads={ocrIntraOpThreads}.");
        while (TryTakeBatch(queue, options.OcrBatchSize, out var batch))
        {
            try
            {
                if (Volatile.Read(ref counters.StopAfterIndex) > 0)
                {
                    scanLog.Write($"OCR worker {workerId} discarded batch after stop signal. BatchSize={batch.Count}.");
                    break;
                }

                var bitmapInputSw = Stopwatch.StartNew();

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
                    .Select((capture, index) => new OcrBatchInput(capture.Image, assistPlans?[index]?.PpOcrRois ?? capture.Rois))
                    .ToArray();
                bitmapInputSw.Stop();
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
                        ocrResults.Add(OcrWorkResult.Success(
                            capture.Index,
                            export,
                            capture.TargetVerificationKind,
                            BuildOcrDetail(capture, ocr)), pipelineToken);
                    }
                    catch (Exception ex) when (!pipelineToken.IsCancellationRequested)
                    {
                        if (normalizedRetryEnabled && NeedsNormalizedOcrRetry(ocr))
                        {
                            try
                            {
                                using var normalized = VisualProbeEvaluator.NormalizeLuminance(capture.Image);
                                var retryOcr = recognizer.Recognize(normalized, capture.Rois);
                                var retryExport = cleaner.Clean(capture.Index, capture.Rarity, retryOcr);
                                scanLog.WriteEvent("OCR_LUMINANCE_RETRY_ACCEPTED", $"index={capture.Index}, originalError={SanitizeLogValue(ex.Message)}, emptyOrLowConfidence={NeedsNormalizedOcrRetry(ocr)}");
                                TryWriteOcrShadowDataset(shadowDataset, capture, retryOcr, retryExport, scanLog);
                                TryWriteFastOcrShadow(fastOcrShadow, capture, retryOcr, retryExport, scanLog);
                                ocrResults.Add(OcrWorkResult.Success(
                                    capture.Index,
                                    retryExport,
                                    capture.TargetVerificationKind,
                                    BuildOcrDetail(capture, retryOcr)), pipelineToken);
                                continue;
                            }
                            catch (Exception retryException) when (!pipelineToken.IsCancellationRequested)
                            {
                                scanLog.WriteEvent("OCR_LUMINANCE_RETRY_REJECTED", $"index={capture.Index}, originalError={SanitizeLogValue(ex.Message)}, retryError={SanitizeLogValue(retryException.Message)}");
                            }
                        }

                        var detail = BuildErrorDetail(capture, ocr, ex);
                        ocrResults.Add(OcrWorkResult.Failure(
                            capture.Index,
                            capture.TargetVerificationKind,
                            detail,
                            ex.Message), pipelineToken);
                    }
                }
                cleanSw.Stop();
                var backlog = Math.Max(0, Volatile.Read(ref counters.Queued) - Volatile.Read(ref counters.Completed) - Volatile.Read(ref counters.Failed));
                diagnostics.Write(
                    workerId,
                    batch.Count,
                    recognition.Diagnostics,
                    bitmapInputSw.Elapsed.TotalMilliseconds,
                    cleanSw.Elapsed.TotalMilliseconds,
                    fallbackCount: 0,
                    backlog,
                    fastMatchMs,
                    fastAcceptedCount,
                    fastRejectedCount,
                    recognition.Diagnostics.RoiCount);
            }
            catch (Exception ex) when (!pipelineToken.IsCancellationRequested)
            {
                foreach (var capture in batch)
                {
                    ocrResults.Add(OcrWorkResult.Failure(
                        capture.Index,
                        capture.TargetVerificationKind,
                        BuildBatchErrorDetail(capture, ex),
                        ex.Message), pipelineToken);
                }

                scanLog.Write($"OCR worker {workerId} batch failed: {ex}");
            }
            finally
            {
                foreach (var capture in batch)
                {
                    capture.Dispose();
                }
            }
        }

        scanLog.Write($"OCR worker {workerId} stopped.");
    }

    internal static void ConsumeOcrResults(
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
                    Interlocked.CompareExchange(ref counters.StopReason, "non_level_15_stop", null);
                    pending.Clear();
                    var detail = result.ErrorDetail ?? BuildExportDetail(result.Export);
                    File.WriteAllText(Path.Combine(outputDir, $"{result.Index:0000}.non15.txt"), detail);
                    scanLog.Write($"Stop at #{result.Index}: detected non-15 drive disc {result.Export.Name} {result.Export.Rarity} {result.Export.Level}/{result.Export.MaxLevel}.");
                    Report(progress, counters, $"检测到非15级驱动盘 #{result.Index}：{result.Export.Level}/{result.Export.MaxLevel}，扫描停止。");
                    linked.Cancel();
                    return nextIndex;
                }

                if (!duplicateGuard.Observe(result.Export, result.TargetVerificationKind, out var duplicateReason))
                {
                    Interlocked.CompareExchange(ref counters.StopAfterIndex, result.Index, 0);
                    Interlocked.CompareExchange(ref counters.StopReason, "duplicate_guard", null);
                    scanLog.Write($"Duplicate guard canceled scan at #{result.Index}: {duplicateReason}");
                    Report(progress, counters, $"重复保护触发：{duplicateReason}");
                    linked.Cancel();
                    return nextIndex;
                }

                scanLog.WriteEvent(
                    "ITEM_TARGET_VERIFICATION",
                    $"index={result.Index}, kind={result.TargetVerificationKind}");

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

    private static int CurrentOcrBacklog(Counters counters)
    {
        return Math.Max(
            0,
            Volatile.Read(ref counters.Queued)
            - Volatile.Read(ref counters.Completed)
            - Volatile.Read(ref counters.Failed));
    }

    private static ScannerFailureException InventoryCountOcrFailure(
        string message,
        IReadOnlyDictionary<string, object?>? details = null) => new(
        "inventory_count_ocr_failed",
        "仓库数量识别失败",
        message,
        "请确认仓库数量区域完整可见，并关闭遮挡或缩放后重试。",
        details);

    private static ScannerFailureException NavigationFailure(
        string message,
        IReadOnlyDictionary<string, object?>? details = null) => new(
        "scan_navigation_failed",
        "驱动盘列表滚动失败",
        message,
        "请保持游戏前台且不要操作鼠标滚轮，然后重新扫描。",
        details);

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
        public TargetVerificationKind TargetVerificationKind { get; }

        public DiscCapture(
            int index,
            string rarity,
            Bitmap image,
            CvRect[] rois,
            TargetVerificationKind targetVerificationKind)
        {
            Index = index;
            Rarity = rarity;
            Image = image;
            Rois = rois;
            TargetVerificationKind = targetVerificationKind;
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
        int EffectiveScrollTickDelayMs)
    {
        public string VisualProfileId { get; set; } = "";
    }

    private static bool NeedsNormalizedOcrRetry(IReadOnlyList<OcrResult> ocr)
    {
        return ocr.Any(result => string.IsNullOrWhiteSpace(result.Text) || result.Score < 0.45f);
    }

    private static string SanitizeLogValue(string value)
    {
        return value.Replace('\r', ' ').Replace('\n', ' ').Replace(',', ';');
    }

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

    private sealed record FirstPairCommit(int Column, string Rarity, PanelCapture Capture);

    private sealed record FirstPairWitnessCandidate(
        int VisualRow,
        int Column,
        System.Drawing.Point Point);

    private sealed class FirstPairBootstrapResult : IDisposable
    {
        public FirstPairBootstrapResult(
            PanelCapture first,
            PanelCapture second,
            ImageSignature[] currentPanelSignatures,
            string currentPanelLabel)
        {
            First = first;
            Second = second;
            CurrentPanelSignatures = currentPanelSignatures;
            CurrentPanelLabel = currentPanelLabel;
        }

        public PanelCapture First { get; }
        public PanelCapture Second { get; }
        public ImageSignature[] CurrentPanelSignatures { get; }
        public string CurrentPanelLabel { get; }

        public void Dispose()
        {
            First.Dispose();
            Second.Dispose();
        }
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
            int targetSelectionStableFrames,
            TargetVerificationKind targetVerificationKind,
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
            TargetSelectionStableFrames = targetSelectionStableFrames;
            TargetVerificationKind = targetVerificationKind;
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
        public int TargetSelectionStableFrames { get; }
        public TargetVerificationKind TargetVerificationKind { get; }
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

    private sealed record SelectionRefreshCapture(
        ImageSignature[] PanelSignatures,
        Rectangle SelectionProbeRect,
        ImageSignature SelectionSignature,
        bool RefreshReady,
        bool SelectionRoundTripReady);

    private sealed class StalePanelException : TimeoutException
    {
        public StalePanelException(string message)
            : base(message)
        {
        }
    }

    private sealed class PanelCaptureTimeoutException : TimeoutException
    {
        public PanelCaptureTimeoutException(
            int visibleRois,
            int totalRois,
            string? firstMissingRoi,
            RowPresenceProbeResult? firstMissingProbe,
            string acceptGateReason,
            bool sawPanelChange,
            bool selectionChanged,
            int stableFrames,
            int requiredStableFrames,
            int frameCount)
            : base("详情面板截图等待超时。")
        {
            VisibleRois = visibleRois;
            TotalRois = totalRois;
            FirstMissingRoi = firstMissingRoi;
            ReferenceLuma = firstMissingProbe?.ReferenceLuma;
            CandidateLuma = firstMissingProbe?.CandidateLuma;
            LumaDelta = firstMissingProbe?.LumaDelta;
            AllowedLumaDelta = firstMissingProbe?.AllowedLumaDelta;
            EdgeDensityPermille = firstMissingProbe?.EdgeDensityPermille;
            MinimumEdgeDensityPermille = firstMissingProbe?.MinimumEdgeDensityPermille;
            AcceptGateReason = acceptGateReason;
            SawPanelChange = sawPanelChange;
            SelectionChanged = selectionChanged;
            StableFrames = stableFrames;
            RequiredStableFrames = requiredStableFrames;
            FrameCount = frameCount;
        }

        public int VisibleRois { get; }
        public int TotalRois { get; }
        public string? FirstMissingRoi { get; }
        public int? ReferenceLuma { get; }
        public int? CandidateLuma { get; }
        public int? LumaDelta { get; }
        public int? AllowedLumaDelta { get; }
        public int? EdgeDensityPermille { get; }
        public int? MinimumEdgeDensityPermille { get; }
        public string AcceptGateReason { get; }
        public bool SawPanelChange { get; }
        public bool SelectionChanged { get; }
        public int StableFrames { get; }
        public int RequiredStableFrames { get; }
        public int FrameCount { get; }
    }

    private sealed class PanelCellCaptureException : TimeoutException, IScannerFailureException
    {
        public PanelCellCaptureException(
            int pass,
            int visualRow,
            int column,
            int maxColumns,
            int? logicalRow,
            int attempts,
            GameWindow window,
            string visualProfileId,
            Exception innerException)
            : base($"详情面板截图等待超时：logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={visualRow}, col={column}/{maxColumns}。", innerException)
        {
            Pass = pass;
            VisualRow = visualRow;
            Column = column;
            MaxColumns = maxColumns;
            LogicalRow = logicalRow;
            var timeout = innerException as PanelCaptureTimeoutException;
            DiagnosticDetails = ScanDiagnosticDetails.PanelCapture(
                logicalRow,
                visualRow,
                column,
                maxColumns,
                timeout?.VisibleRois ?? 0,
                timeout?.TotalRois ?? 0,
                timeout?.FirstMissingRoi,
                timeout?.ReferenceLuma,
                timeout?.CandidateLuma,
                timeout?.LumaDelta,
                timeout?.AllowedLumaDelta,
                timeout?.EdgeDensityPermille,
                timeout?.MinimumEdgeDensityPermille,
                timeout?.AcceptGateReason ?? "unknown",
                timeout?.SawPanelChange ?? false,
                timeout?.SelectionChanged ?? false,
                timeout?.StableFrames ?? 0,
                timeout?.RequiredStableFrames ?? 0,
                attempts,
                timeout?.FrameCount ?? 0,
                window.ClientScreenRect.Width,
                window.ClientScreenRect.Height,
                window.Dpi,
                window.ActiveCaptureMode,
                visualProfileId);
        }

        public int Pass { get; }
        public int VisualRow { get; }
        public int Column { get; }
        public int MaxColumns { get; }
        public int? LogicalRow { get; }
        public IReadOnlyDictionary<string, object?> DiagnosticDetails { get; }
        public string Code => "panel_capture_timeout";
        public string Title => "驱动盘详情读取超时";
        public string Remedy => "请保持游戏前台且无遮挡后重试；持续发生时请打开日志。";
        public bool Retryable => true;
    }

    internal sealed record OcrWorkResult(
        int Index,
        DriveDiscExport? Export,
        TargetVerificationKind TargetVerificationKind,
        string? ErrorDetail,
        string? ErrorMessage)
    {
        public static OcrWorkResult Success(
            int index,
            DriveDiscExport export,
            TargetVerificationKind targetVerificationKind,
            string? detail = null)
        {
            return new OcrWorkResult(index, export, targetVerificationKind, detail, null);
        }

        public static OcrWorkResult Failure(
            int index,
            TargetVerificationKind targetVerificationKind,
            string errorDetail,
            string errorMessage)
        {
            return new OcrWorkResult(index, null, targetVerificationKind, errorDetail, errorMessage);
        }
    }

    internal sealed class Counters
    {
        public int Visited;
        public int Queued;
        public int Completed;
        public int Failed;
        public int StopAfterIndex;
        public string? StopReason;
    }

    private enum RowScanResult
    {
        Completed,
        Stop
    }

    private readonly record struct OverlapRowCandidate(int LogicalRow, int VisualRow);

    private readonly record struct RarityProbe(
        string? Rarity,
        Color BestColor,
        string BestCandidate,
        int BestScore,
        int SecondScore,
        int Margin,
        bool FullScan);

    internal sealed class ScanLog : IDisposable
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

    internal sealed class DuplicateGuard
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

        public bool Observe(
            DriveDiscExport item,
            TargetVerificationKind targetVerificationKind,
            out string? reason)
        {
            reason = null;
            var fingerprint = Fingerprint(item);
            var adjacentIdentical = _previousFingerprint is not null
                && string.Equals(_previousFingerprint, fingerprint, StringComparison.Ordinal);
            if (adjacentIdentical)
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

            if (adjacentIdentical)
            {
                if (TargetVerificationPolicy.AllowsAdjacentIdentical(targetVerificationKind))
                {
                    _consecutiveSeenDuplicateCount = 0;
                    return true;
                }

                reason = "相邻驱动盘指纹完全相同，但目标格没有邻格往返验证证据，已拒绝继续扫描。";
                return false;
            }

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

    private sealed class WarehouseContextGuard
    {
        private readonly object _sync = new();
        private readonly GameWindow _window;
        private readonly WarehouseMonitorPlan _plan;
        private readonly WarehousePreflightPolicy _policy;
        private readonly PaddleOcrRecognizer _recognizer;
        private readonly ScanLog _scanLog;
        private readonly WarehouseInputGuardState _state;
        private WarehouseFastProbe _lastFastProbe;
        private WarehouseStrongProbe _lastStrongProbe;

        public WarehouseContextGuard(
            GameWindow window,
            WarehouseMonitorPlan plan,
            WarehousePreflightPolicy policy,
            PaddleOcrRecognizer recognizer,
            ScanLog scanLog)
        {
            _window = window;
            _plan = plan;
            _policy = policy;
            _recognizer = recognizer;
            _scanLog = scanLog;
            PollMilliseconds = Math.Clamp(policy.MonitorPollMilliseconds, 100, 1000);
            RequiredFailureFrames = Math.Clamp(policy.MonitorFailureFrames, 2, 5);
            MinimumScore = Math.Clamp(policy.MonitorMinimumScore, 1, 100);
            _state = new WarehouseInputGuardState(
                Math.Clamp(policy.MonitorMaximumAgeMilliseconds, 100, 2000),
                RequiredFailureFrames,
                plan.FastBaseline);
            _lastFastProbe = new WarehouseFastProbe(true, 100, default, plan.FastBaseline);
        }

        public int PollMilliseconds { get; }
        public int RequiredFailureFrames { get; }
        public int MinimumScore { get; }

        public void EnsureHealthy()
        {
            lock (_sync)
            {
                if (_state.IsFresh())
                {
                    return;
                }

                _lastFastProbe = CaptureWarehouseFastProbe(_window, _plan, _state.FastBaseline);
                if (_state.AcceptFast(
                    _lastFastProbe.CaptureHealthy,
                    _lastFastProbe.Score,
                    MinimumScore))
                {
                    return;
                }

                _scanLog.WriteEvent(
                    "WAREHOUSE_INPUT_GUARD_FAST",
                    $"passed=False, score={_lastFastProbe.Score}, requiredScore={MinimumScore}, captureHealthy={_lastFastProbe.CaptureHealthy}, captureScore={_lastFastProbe.Health.Score}");
                _state.BeginStrongConfirmation();
                while (true)
                {
                    _lastStrongProbe = CaptureWarehouseStrongProbe(
                        _window,
                        _plan,
                        _recognizer,
                        _policy,
                        _scanLog);
                    if (_state.AcceptStrong(
                        _lastStrongProbe.Passed,
                        _lastStrongProbe.HeaderSignature))
                    {
                        _scanLog.WriteEvent(
                            "WAREHOUSE_INPUT_GUARD_CONFIRM",
                            $"passed=True, confirmationFailures=0/{RequiredFailureFrames}, captureHealthy={_lastStrongProbe.Health.Passed}, headerDetected={_lastStrongProbe.Header.HeaderDetected}, headerScore={_lastStrongProbe.Header.HeaderScore}, gridStructureScore={_lastStrongProbe.Structure.GridStructureScore}, layoutScore={_lastStrongProbe.Structure.LayoutScore}, baselineRebuilt=True");
                        return;
                    }

                    _scanLog.WriteEvent(
                        "WAREHOUSE_INPUT_GUARD_CONFIRM",
                        $"passed=False, confirmationFailures={_state.StrongFailures}/{RequiredFailureFrames}, captureHealthy={_lastStrongProbe.Health.Passed}, headerDetected={_lastStrongProbe.Header.HeaderDetected}, headerScore={_lastStrongProbe.Header.HeaderScore}, gridStructureScore={_lastStrongProbe.Structure.GridStructureScore}, layoutScore={_lastStrongProbe.Structure.LayoutScore}");
                    if (_state.ShouldBlock)
                    {
                        _scanLog.Write($"Warehouse input guard blocked input after {_state.StrongFailures} strong confirmation failures.");
                        throw CreateFailureCore();
                    }

                    Thread.Sleep(PollMilliseconds);
                }
            }
        }

        private ScannerFailureException CreateFailureCore()
        {
            var details = new Dictionary<string, object?>
            {
                ["preflightState"] = "warehouse_context_lost",
                ["fastScore"] = Math.Clamp(_lastFastProbe.Score, 0, 100),
                ["headerScore"] = Math.Clamp(_lastStrongProbe.Header.HeaderScore, 0, 100),
                ["gridStructureScore"] = Math.Clamp(_lastStrongProbe.Structure.GridStructureScore, 0, 100),
                ["layoutScore"] = Math.Clamp(_lastStrongProbe.Structure.LayoutScore, 0, 100),
                ["confirmationFailures"] = _state.StrongFailures,
                ["stableFrames"] = 0,
                ["requiredStableFrames"] = RequiredFailureFrames,
                ["captureMode"] = _window.ActiveCaptureMode,
                ["clientWidth"] = _window.ClientScreenRect.Width,
                ["clientHeight"] = _window.ClientScreenRect.Height,
                ["dpi"] = _window.Dpi
            };
            return new ScannerFailureException(
                "warehouse_context_lost",
                "无法确认驱动盘仓库界面",
                "扫描期间无法继续确认驱动盘仓库界面，已在下一次输入前停止。",
                "请确保游戏可见、未被遮挡并停留在背包中的驱动盘页面后重试。",
                details);
        }
    }

    internal sealed class WarehouseInputGuardState
    {
        private readonly long _maximumAgeTicks;
        private readonly int _requiredStrongFailures;
        private long _lastHealthyTimestamp;

        public WarehouseInputGuardState(
            int maximumAgeMilliseconds,
            int requiredStrongFailures,
            WarehouseMonitorSignature fastBaseline)
        {
            _maximumAgeTicks = (long)Math.Ceiling(
                Math.Max(1, maximumAgeMilliseconds) / 1000d * Stopwatch.Frequency);
            _requiredStrongFailures = Math.Max(1, requiredStrongFailures);
            _lastHealthyTimestamp = Stopwatch.GetTimestamp();
            FastBaseline = fastBaseline;
        }

        public WarehouseMonitorSignature FastBaseline { get; private set; }
        public int StrongFailures { get; private set; }
        public bool ShouldBlock => StrongFailures >= _requiredStrongFailures;

        public bool IsFresh() => Stopwatch.GetTimestamp() - _lastHealthyTimestamp <= _maximumAgeTicks;

        public bool AcceptFast(bool captureHealthy, int score, int minimumScore)
        {
            if (!captureHealthy || score < minimumScore)
            {
                return false;
            }

            MarkHealthy();
            return true;
        }

        public bool AcceptStrong(bool passed, WarehouseMonitorSignature headerSignature)
        {
            if (!passed)
            {
                StrongFailures++;
                return false;
            }

            FastBaseline = headerSignature;
            MarkHealthy();
            return true;
        }

        private void MarkHealthy()
        {
            StrongFailures = 0;
            _lastHealthyTimestamp = Stopwatch.GetTimestamp();
        }

        public void BeginStrongConfirmation()
        {
            StrongFailures = 0;
        }

    }

    private readonly record struct InventoryCountConsensusResult(
        int? InventoryCount,
        int? InventoryCapacity,
        int ConsensusFrames,
        int Attempts);

    private readonly record struct WarehouseFastProbe(
        bool CaptureHealthy,
        int Score,
        CaptureHealthResult Health,
        WarehouseMonitorSignature Signature);

    private readonly record struct WarehouseStrongProbe(
        bool Passed,
        CaptureHealthResult Health,
        WarehouseHeaderProbeResult Header,
        WarehouseStructureProbeResult Structure,
        WarehouseMonitorSignature HeaderSignature);

    private readonly record struct ImageSignature(
        ulong Hash,
        int[] Samples,
        bool EmphasizeLocalizedChanges = false);

    private readonly record struct ViewportSignatures(ImageSignature Grid, RowVisualSignature[] Rows);

    private readonly record struct SafeBandMoveResult(int VisibleTopLogicalRow, bool Scrolled);

    private readonly record struct RowScrollVerification(int OneRowScore, int NoMoveScore, int TwoRowScore);

    private readonly record struct OverlapGapCoverage(bool Safe, int[] CoveredRows, int[] MissingRows);

    private readonly record struct ScrollRowsResult(
        bool Success,
        string Message,
        int RowsAdvanced = 0,
        bool Retryable = true,
        RowVisualSignature[]? AfterRows = null,
        RowAdvanceEvidence Evidence = default,
        IReadOnlyDictionary<string, object?>? DiagnosticDetails = null)
    {
        public static ScrollRowsResult Ok(
            string message,
            int rowsAdvanced = 0,
            RowVisualSignature[]? afterRows = null,
            RowAdvanceEvidence evidence = default,
            IReadOnlyDictionary<string, object?>? diagnosticDetails = null) =>
            new(true, message, rowsAdvanced, AfterRows: afterRows, Evidence: evidence, DiagnosticDetails: diagnosticDetails);

        public static ScrollRowsResult Fail(
            string message,
            bool retryable = true,
            RowAdvanceEvidence evidence = default,
            IReadOnlyDictionary<string, object?>? diagnosticDetails = null) =>
            new(false, message, Retryable: retryable, Evidence: evidence, DiagnosticDetails: diagnosticDetails);
    }

    private readonly record struct ScrollbarThumbProbe(
        bool Found,
        int StartY,
        int EndY,
        int CenterX,
        int CenterY);

    private static ScrollbarThumbProbe CaptureScrollbarThumbProbe(GameWindow window, ScanProfile profile)
    {
        var top = window.ToScreenPoint(profile.Point("scrollBarTop"));
        var bottom = window.ToScreenPoint(profile.Point("scrollBarBottom"));
        var expected = profile.Color("scrollBar");
        var tolerance = Math.Max(0, profile.ColorTolerance);
        var minY = Math.Min(top.Y, bottom.Y);
        var maxY = Math.Max(top.Y, bottom.Y);
        var bounds = new Rectangle(top.X - 2, minY, 5, Math.Max(1, maxY - minY + 1));
        using var image = window.CaptureFrame(bounds);
        var bestStart = -1;
        var bestEnd = -1;
        var bestCenterX = top.X;
        var currentStart = -1;
        var currentXCounts = new int[bounds.Width];

        void CompleteRun(int endY)
        {
            if (currentStart < 0)
            {
                return;
            }

            if (bestStart < 0 || endY - currentStart > bestEnd - bestStart)
            {
                bestStart = currentStart;
                bestEnd = endY;
                long weightedX = 0;
                var matchedPixels = 0;
                for (var x = 0; x < currentXCounts.Length; x++)
                {
                    weightedX += (long)x * currentXCounts[x];
                    matchedPixels += currentXCounts[x];
                }

                bestCenterX = bounds.Left + (matchedPixels > 0
                    ? (int)Math.Round(weightedX / (double)matchedPixels, MidpointRounding.AwayFromZero)
                    : bounds.Width / 2);
            }

            currentStart = -1;
            Array.Clear(currentXCounts);
        }

        for (var localY = 0; localY < bounds.Height; localY++)
        {
            var matched = false;
            for (var localX = 0; localX < bounds.Width; localX++)
            {
                if (image.GetPixel(localX, localY).IsCloseTo(expected, tolerance))
                {
                    matched = true;
                    currentXCounts[localX]++;
                }
            }

            if (matched)
            {
                currentStart = currentStart < 0 ? minY + localY : currentStart;
            }
            else
            {
                CompleteRun(minY + localY - 1);
            }
        }

        CompleteRun(maxY);
        return bestStart < 0
            ? default
            : new ScrollbarThumbProbe(true, bestStart, bestEnd, bestCenterX, (bestStart + bestEnd) / 2);
    }

    private static int? ScrollbarDelta(ScrollbarThumbProbe before, ScrollbarThumbProbe after) =>
        before.Found && after.Found ? after.CenterY - before.CenterY : null;

    private static int? DirectionalScrollbarDelta(
        ScrollbarThumbProbe before,
        ScrollbarThumbProbe after,
        bool upward)
    {
        var delta = ScrollbarDelta(before, after);
        return delta is int value ? (upward ? -value : value) : null;
    }

    private static double ScrollbarPixelsPerLogicalRow(
        GameWindow window,
        ScanProfile profile,
        ScrollbarThumbProbe thumb,
        int maxVisibleTop)
    {
        if (!thumb.Found || maxVisibleTop <= 1)
        {
            return 0;
        }

        var top = window.ToScreenPoint(profile.Point("scrollBarTop"));
        var bottom = window.ToScreenPoint(profile.Point("scrollBarBottom"));
        var thumbHeight = Math.Max(1, thumb.EndY - thumb.StartY + 1);
        var halfThumb = (thumbHeight - 1) / 2.0;
        var minimumCenter = Math.Min(top.Y, bottom.Y) + halfThumb;
        var maximumCenter = Math.Max(top.Y, bottom.Y) - halfThumb;
        return Math.Max(0, maximumCenter - minimumCenter) / (maxVisibleTop - 1);
    }

}
