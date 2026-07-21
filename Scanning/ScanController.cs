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

public sealed class ScanController
{
    private const int PanelChangeTolerance = 8;
    private const int PanelStrongChangeTolerance = PanelChangeTolerance * 2;
    private const int PanelStableTolerance = 4;
    private const double MinReliablePanelChangeMs = 25.0;
    private const int ListMovementTolerance = 6;
    private const int ListStableTolerance = 4;
    private const int ScrollTopThumbMinimumLuminance = 24;
    private const int ScrollTopThumbMinimumContrast = 16;
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
        Exception? pendingException = null;
        Exception? ocrException = null;
        ScanSessionDiagnostics? sessionDiagnostics = null;

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
            runtimeState.VisualProfileId = visualProfile.ProfileId;
            visualProfile.Save(outputDir);
            scanLog.Write($"VISUAL_PROFILE_SELECTED id={visualProfile.ProfileId}, trainingProfile={visualProfile.TrainingProfileId}, profileFamily={visualProfile.ProfileFamilyId}, geometryStatus={visualProfile.ProfileGeometryStatus}, requested_label={visualProfile.RequestedProfileId}, detected_profile={visualProfile.DetectedProfileId}, detected_geometry={visualProfile.GeometryKey}, clientKind={visualProfile.ClientKind}, quality={visualProfile.QualityLabel}, size={visualProfile.ClientWidth}x{visualProfile.ClientHeight}, dpi={visualProfile.Dpi}, captureRequested={visualProfile.CaptureModeRequested}, captureActive={visualProfile.CaptureModeActive}, frameBackend={visualProfile.CaptureFrameBackend}, profileRouting={options.ProfileRouting}");
            if (!visualProfile.ProfileId.Equals(visualProfile.DetectedProfileId, StringComparison.OrdinalIgnoreCase))
            {
                scanLog.Write($"VISUAL_PROFILE_LABEL_GEOMETRY_MISMATCH requested_label={visualProfile.ProfileId}, detected_profile={visualProfile.DetectedProfileId}, detected_geometry={visualProfile.GeometryKey}");
            }

            using var inventoryRecognizer = new PaddleOcrRecognizer(AppPaths.ModelFile, AppPaths.CharacterDictFile, ocrIntraOpThreads);
            Task[] ocrWorkers = [];
            var resultConsumer = Task.CompletedTask;
            var captureCompleted = false;

            try
            {
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
                    fastOcrAssist = FastOcrAssistEngine.TryCreate(options.FastOcrTemplateIndexFile, profile.OrderedRoiKeys(), visualProfile.ProfileId, options.ProfileRouting, scanLog.Write);
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
                window.ConfigureInputGuard(contextGuard.EnsureHealthy);
                window.LeftClick(window.ToScreenPoint(profile.Point("driveDiscTab")));
                await Task.Delay(profile.ClickDelayMs, linked.Token);
                await ResetListToTopAsync(window, profile, progress, counters, scanLog, linked.Token);
                Report(progress, counters, "已定位到驱动盘列表顶部。");

                ocrWorkers = StartOcrWorkers(queue, ocrResults, outputDir, options, scanLog, ocrWorkerCount, ocrIntraOpThreads, counters, ocrDiagnostics, ocrShadowDataset, fastOcrShadow, fastOcrAssist, fastOcrAssistRecorder, shiftedVisualEnvironment);
                resultConsumer = Task.Run(() => ConsumeOcrResults(ocrResults, results, counters, outputDir, progress, scanLog, duplicateGuard, options, linked));
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

        if (pendingException is not null)
        {
            if (sessionDiagnostics is not null)
            {
                pendingException = new ScanSessionDiagnosticException(pendingException, sessionDiagnostics);
            }

            ExceptionDispatchInfo.Capture(pendingException).Throw();
        }

        if (ocrException is not null)
        {
            throw new ScannerFailureException(
                "ocr_worker_failed",
                "OCR 识别进程失败",
                "后台 OCR 工作线程发生异常。",
                "请重新扫描；如果持续发生，请打开日志并提供诊断信息。",
                innerException: ocrException);
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
        window.MoveCursor(window.ToScreenPoint(profile.Point("listWheelArea")));
        var resetDelay = Math.Clamp(profile.ResetToTopWheelDelayMs, 20, 80);
        var initialProbe = CaptureScrollTopProbe(window, profile);
        scanLog.WriteEvent(
            "RESET_TOP_PROBE",
            $"phase=initial, detected={initialProbe.Detected}, topLuma={initialProbe.TopLuminance}, trackLuma={initialProbe.TrackLuminance}");
        var reachedTop = initialProbe.Detected;
        for (var i = 0; !reachedTop && i < profile.ResetToTopWheelTicks; i++)
        {
            token.ThrowIfCancellationRequested();
            scanLog.WriteEvent("RESET_WHEEL", $"tick={i + 1}/{profile.ResetToTopWheelTicks}, delta=120, cursor={window.ToScreenPoint(profile.Point("listWheelArea"))}");
            window.MouseWheel(120);
            if (resetDelay > 0)
            {
                await Task.Delay(resetDelay, token);
            }

            if ((i + 1) % 4 != 0 && i + 1 < profile.ResetToTopWheelTicks)
            {
                continue;
            }

            var probe = CaptureScrollTopProbe(window, profile);
            scanLog.WriteEvent(
                "RESET_TOP_PROBE",
                $"phase=wheel, tick={i + 1}, detected={probe.Detected}, topLuma={probe.TopLuminance}, trackLuma={probe.TrackLuminance}");
            reachedTop = probe.Detected;
        }

        if (reachedTop)
        {
            scanLog.Write("Reset confirmed the scrollbar thumb at the top.");
        }
        else
        {
            scanLog.Write($"Reset top probe did not confirm after {profile.ResetToTopWheelTicks} ticks; using explicit top click fallback.");
            scanLog.WriteEvent("RESET_TOP_CLICK", $"point={scrollTop}");
            window.LeftClick(scrollTop, durationMs: 30);
            await Task.Delay(Math.Max(80, profile.ClickDelayMs), token);
            var fallbackProbe = CaptureScrollTopProbe(window, profile);
            scanLog.WriteEvent(
                "RESET_TOP_PROBE",
                $"phase=fallback, detected={fallbackProbe.Detected}, topLuma={fallbackProbe.TopLuminance}, trackLuma={fallbackProbe.TrackLuminance}");
        }
    }

    private static ScrollTopProbeResult CaptureScrollTopProbe(GameWindow window, ScanProfile profile)
    {
        var point = window.ToScreenPoint(profile.Point("scrollBarTop"));
        var scaleX = window.ClientScreenRect.Width / (double)profile.StandardScreen[0];
        var scaleY = window.ClientScreenRect.Height / (double)profile.StandardScreen[1];
        var width = Math.Max(5, (int)Math.Round(9 * scaleX));
        var topHeight = Math.Max(10, (int)Math.Round(18 * scaleY));
        var trackOffset = Math.Max(topHeight + 8, (int)Math.Round(38 * scaleY));
        var trackHeight = topHeight;
        var requested = new Rectangle(
            point.X - width / 2,
            point.Y,
            width,
            trackOffset + trackHeight);
        var captureRect = Rectangle.Intersect(window.ClientScreenRect, requested);
        using var frame = window.CaptureFrame(captureRect);
        var topRect = new Rectangle(0, 0, frame.Width, Math.Min(topHeight, frame.Height));
        var trackTop = Math.Min(trackOffset, Math.Max(0, frame.Height - 1));
        var trackRect = new Rectangle(0, trackTop, frame.Width, Math.Min(trackHeight, frame.Height - trackTop));
        return EvaluateScrollTopProbe(frame, topRect, trackRect);
    }

    internal static ScrollTopProbeResult EvaluateScrollTopProbe(
        CapturedFrame image,
        Rectangle topRect,
        Rectangle trackRect)
    {
        var topLuminance = MeanLuminance(image, topRect);
        var trackLuminance = MeanLuminance(image, trackRect);
        return new ScrollTopProbeResult(
            topLuminance >= ScrollTopThumbMinimumLuminance
                && topLuminance - trackLuminance >= ScrollTopThumbMinimumContrast,
            topLuminance,
            trackLuminance);
    }

    private static int MeanLuminance(CapturedFrame image, Rectangle rect)
    {
        rect = ClampRectangle(rect, image.Size);
        var sum = 0L;
        var count = 0;
        for (var y = rect.Top; y < rect.Bottom; y++)
        {
            for (var x = rect.Left; x < rect.Right; x++)
            {
                var color = image.GetPixel(x, y);
                sum += (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
                count++;
            }
        }

        return count == 0 ? 0 : (int)(sum / count);
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
                throw NavigationFailure($"重叠签名扫描到达底部但仍有逻辑行未扫：{string.Join(",", missing)}。为避免漏扫，本次停止。");
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
                throw NavigationFailure($"重叠签名扫描滚动失败：{scroll.Message}");
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
                    throw NavigationFailure($"重叠签名扫描检测到滚动估计与下一屏签名不一致：previousTop={beforeTop}, scrollRows={scroll.RowsAdvanced}, signatureRows={signatureAdvanced.Value}。为避免重复或漏扫，本次停止。");
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
                    throw NavigationFailure($"重叠签名扫描检测到未被下一屏签名确认的越行滚动：previousTop={beforeTop}, scrollRows={scroll.RowsAdvanced}, signatureRows={signatureAdvanced?.ToString() ?? "NA"}。为避免重复读取，本次停止。");
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
        int startColumn = 1)
    {
        var currentY = offset.Y + step.Y * row;
        ImageSignature[]? previousPanelSignatures = CaptureCurrentPanelSignatures(
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

            counters.Visited++;
            if (!options.Rarities.Contains(rarity))
            {
                Report(progress, counters, $"跳过 {rarity} 级驱动盘。");
                continue;
            }

            var firstQueuedItem = counters.Queued == 0;
            if (firstQueuedItem)
            {
                scanLog.WriteEvent(
                    "FIRST_CELL_BASELINE_CAPTURED",
                    $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, probes={previousPanelSignatures?.Length ?? 0}");
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

            if (PanelCaptureGate.RequiresFirstCellNeighborRoundTrip(firstQueuedItem))
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
                firstQueuedItem);
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
                throw NavigationFailure($"安全带逐行滚动失败：{result.Message}");
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

                    var rowsRecovered = Math.Max(1, recovery.RowsAdvanced);
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

    private static string FormatOptionalInt(int? value)
    {
        return value.HasValue ? value.Value.ToString() : "NA";
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

        return VisualProbeEvaluator.MeasureLuminanceMovement(left.Samples, right.Samples);
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
        bool sceneAdaptivePanelFloorEligible,
        bool firstQueuedItem)
    {
        const int maxAttempts = 3;
        var activePreviousPanelSignatures = previousPanelSignatures;
        var activeSelectionProbeRect = selectionProbeRect;
        var activeBeforeSelectionSignature = beforeSelectionSignature;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var allowSelectionOnlyFallback = !firstQueuedItem && !postScrollFirstCell && attempt == 1;
                return await CaptureStablePanelAsync(window, profile, panelRect, rois, statOffset, statRowBackground, panelChangeProbeRect, activePreviousPanelSignatures, activeSelectionProbeRect, activeBeforeSelectionSignature, runtimeState, scanLog, token, postScrollFirstCell, sceneAdaptivePanelFloorEligible && attempt == 1, allowSelectionOnlyFallback);
            }
            catch (StalePanelException) when (attempt < maxAttempts)
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
                activePreviousPanelSignatures = refresh.PanelSignatures;
                activeSelectionProbeRect = refresh.SelectionProbeRect;
                activeBeforeSelectionSignature = refresh.SelectionSignature;
            }
            catch (TimeoutException ex) when (attempt < maxAttempts)
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
                    maxAttempts,
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
        var targetPanelSignatures = CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois);
        ImageSignature[] refreshedPanelSignatures = targetPanelSignatures;
        var refreshReady = false;
        if (selectionRefreshPoint is { } refreshPoint && refreshPoint != clickPoint)
        {
            scanLog.WriteEvent("PANEL_SELECTION_REFRESH", $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, visibleTopLogicalRow={visibleTopText}, state={viewportStateText}, refreshPoint={refreshPoint}, targetPoint={clickPoint}");
            window.MoveCursor(refreshPoint);
            window.LeftClickCurrent();

            var maximumWaitMs = SelectionRefreshTiming.ResolveMaximumWaitMilliseconds(profile.LoadTimeoutMs);
            var pollMs = Math.Max(5, profile.LoadPollMs);
            ImageSignature[]? previousObservedSignatures = null;
            ImageSignature[]? latestObservedSignatures = null;
            var result = await SelectionRefreshWaiter.WaitAsync(
                () =>
                {
                    var currentSignatures = CaptureCurrentPanelSignatures(window, panelRect, panelChangeProbeRect, rois);
                    var changedFromTarget = ProbeChangeDistance(targetPanelSignatures, currentSignatures) > PanelStrongChangeTolerance;
                    var stableWithPrevious = previousObservedSignatures is not null
                        && AreProbesStable(previousObservedSignatures, currentSignatures);
                    previousObservedSignatures = currentSignatures;
                    latestObservedSignatures = currentSignatures;
                    return new SelectionRefreshObservation(changedFromTarget, stableWithPrevious);
                },
                maximumWaitMs,
                pollMs,
                token);
            if (result.Ready && latestObservedSignatures is not null)
            {
                refreshReady = true;
                refreshedPanelSignatures = latestObservedSignatures;
                scanLog.WriteEvent(
                    "PANEL_SELECTION_REFRESH_READY",
                    $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, elapsedMs={result.ElapsedMilliseconds:F1}, stableFrames={result.StableFrames}/2, frameCount={result.FrameCount}");
            }
            else
            {
                scanLog.WriteEvent(
                    "PANEL_SELECTION_REFRESH_TIMEOUT",
                    $"pass={pass}, logicalRow={logicalRow?.ToString() ?? "unknown"}, visualRow={row}, col={col}/{maxColumns}, elapsedMs={result.ElapsedMilliseconds:F1}, timeoutMs={maximumWaitMs}, changedFromTarget={result.ChangedFromTarget}, stableFrames={result.StableFrames}/2, frameCount={result.FrameCount}, baseline=target_snapshot");
            }
        }

        token.ThrowIfCancellationRequested();
        var refreshedSelectionProbeRect = SelectionProbeRect(window, clickPoint);
        var refreshedSelectionSignature = CaptureScreenSignature(refreshedSelectionProbeRect);
        window.MoveCursor(clickPoint);
        window.LeftClickCurrent();
        return new SelectionRefreshCapture(
            refreshedPanelSignatures,
            refreshedSelectionProbeRect,
            refreshedSelectionSignature,
            refreshReady);
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
        bool sceneAdaptivePanelFloorEligible,
        bool allowSelectionOnlyFallback)
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
        var initialGate = PanelCaptureGate.Initialize(previousPanelSignatures is not null);
        var sawPanelChange = initialGate.SawPanelChange;
        var panelChangedFromBaseline = false;
        var selectionChanged = initialGate.SelectionChanged;
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

        bool PromoteSelectionChange(double elapsed, out string blockedReason)
        {
            blockedReason = "";
            if (!ObserveSelectionChange(elapsed))
            {
                return false;
            }

            if (postScrollFirstCell)
            {
                blockedReason = "post_scroll_first_cell";
                return false;
            }

            if (!allowSelectionOnlyFallback)
            {
                blockedReason = "retry_or_recover_context";
                return false;
            }

            if (selectionChangeMilliseconds is null || selectionChangeMilliseconds.Value <= 0.5)
            {
                blockedReason = "selection_change_not_positive";
                return false;
            }

            blockedReason = weakPanelChange
                ? "weak_panel_change_requires_retry"
                : "selection_only_requires_retry";
            return false;
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
            var changeProbeSignatures = CreateSignatures(image, fullPanelProbeRects);
            if (previousPanelSignatures is not null)
            {
                var fullPanelChangeDistance = ProbeChangeDistance(previousPanelSignatures, changeProbeSignatures);
                panelChangedFromBaseline = PanelCaptureGate.IsStrongChangeCurrentFrame(
                    fullPanelChangeDistance,
                    elapsedMilliseconds,
                    PanelStrongChangeTolerance,
                    MinReliablePanelChangeMs);
                if (panelChangedFromBaseline)
                {
                    sawPanelChange = true;
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
                var panelSelectedStableFrames = stabilityDecision.Source == PanelStabilitySource.TextCore
                    ? textCoreStableProbeFrames
                    : stableProbeFrames;
                var effectiveRequiredStableFrames = panelTiming.EffectivePanelAcceptMode == PanelAcceptMode.AdaptiveEarlyFullRoi
                    ? 1
                    : requiredStableFrames;
                var selectedStableFrames = panelTiming.EffectivePanelAcceptMode == PanelAcceptMode.AdaptiveEarlyFullRoi
                    ? roiCompleteFrames
                    : panelSelectedStableFrames;
                var stableEnough = selectedStableFrames >= effectiveRequiredStableFrames;
                var roiEnough = panelReadable;
                acceptGateReason = !panelChangedFromBaseline
                    ? "waiting_for_panel_change"
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
                    && panelTiming.WarmupComplete
                    && previousPanelSignatures is not null
                    && fullPanelReadable
                    && panelChangedFromBaseline)
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
                        : panelChangedFromBaseline
                            ? visibleCount == rois.Count ? "waiting_for_full_roi" : "variable_roi_requires_stability"
                            : "waiting_for_panel_change";
                }

                if (roiEnough
                    && panelChangedFromBaseline
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

                if (panelReadable && !panelChangedFromBaseline && stableProbeFrames >= requiredStableFrames && DateTime.UtcNow - start >= unchangedFallbackDelay)
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
        bool normalizedRetryEnabled)
    {
        return Enumerable.Range(1, workerCount)
            .Select(workerId => Task.Run(() => ConsumeOcrWorker(queue, ocrResults, outputDir, options, scanLog, workerId, ocrIntraOpThreads, counters, diagnostics, shadowDataset, fastOcrShadow, fastOcrAssist, fastOcrAssistRecorder, normalizedRetryEnabled)))
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
        bool normalizedRetryEnabled)
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
                        ocrResults.Add(OcrWorkResult.Success(capture.Index, export, BuildOcrDetail(capture, ocr)));
                    }
                    catch (Exception ex)
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
                                ocrResults.Add(OcrWorkResult.Success(capture.Index, retryExport, BuildOcrDetail(capture, retryOcr)));
                                continue;
                            }
                            catch (Exception retryException)
                            {
                                scanLog.WriteEvent("OCR_LUMINANCE_RETRY_REJECTED", $"index={capture.Index}, originalError={SanitizeLogValue(ex.Message)}, retryError={SanitizeLogValue(retryException.Message)}");
                            }
                        }

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
                    bitmapInputSw.Elapsed.TotalMilliseconds,
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
                    Interlocked.CompareExchange(ref counters.StopReason, "non_level_15_stop", null);
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
                    Interlocked.CompareExchange(ref counters.StopReason, "duplicate_guard", null);
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

    private static ScannerFailureException NavigationFailure(string message) => new(
        "scan_navigation_failed",
        "驱动盘列表滚动失败",
        message,
        "请保持游戏前台且不要操作鼠标滚轮，然后重新扫描。");

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

    private sealed record SelectionRefreshCapture(
        ImageSignature[] PanelSignatures,
        Rectangle SelectionProbeRect,
        ImageSignature SelectionSignature,
        bool RefreshReady);

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

    internal readonly record struct ScrollTopProbeResult(
        bool Detected,
        int TopLuminance,
        int TrackLuminance);

}
