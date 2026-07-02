using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

public static class ScanBenchmark
{
    private const double RecommendationBaselineCompletedPerSecond = 3.593;
    private const double RecommendationMinimumP10CompletedPerSecond = 3.65;
    private const double RecommendationMinimumAverageGainPercent = 5.0;

    private static readonly Regex EventRegex = new(
        @"^\[(?<timestamp>[^\]]+)\].*EVENT #\d+ (?<kind>[A-Z_]+): (?<detail>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex CellClickRegex = new(
        @"col=(?<col>\d+)/(?<max>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex SafeBandPositionRegex = new(
        @"logicalRow=(?<logical>\d+).*?visualRow=(?<visual>\d+).*?visibleTopLogicalRow=(?<top>\d+).*?state=(?<state>[A-Za-z]+)",
        RegexOptions.Compiled);

    private static readonly Regex CellTimingRegex = new(
        @"CELL_TIMING: index=(?<index>\d+).*?(?:afterScroll=(?<afterScroll>True|False), postScrollFirstCell=(?<postScrollFirst>True|False), )?panelWaitMs=(?<panel>[\d.]+), enqueueWaitMs=(?<enqueue>[\d.]+), fallback=(?<fallback>True|False), visibleRois=(?<visible>\d+)/(?<total>\d+), totalMs=(?<cellTotal>[\d.]+)(?:, panelFrames=(?<frames>\d+), changeMs=(?<change>[\d.]+|NA)(?:, selectionChangeMs=(?<selection>[\d.]+|NA))?, fullRoiMs=(?<fullRoi>[\d.]+|NA), stableMs=(?<stable>[\d.]+|NA)(?:, panelTextStableMs=(?<textStable>[\d.]+|NA), panelStableSource=(?<stableSource>[^,]+), panelStabilityReason=(?<stabilityReason>[^,]+), rarityProbeMs=(?<rarityProbe>[\d.]+), selectionProbeMs=(?<selectionProbe>[\d.]+))?(?:, captureMs=(?<capture>[\d.]+), signatureMs=(?<signature>[\d.]+), visibleRoiMs=(?<visibleRoi>[\d.]+), frameLoopMs=(?<frameLoop>[\d.]+)(?:, frameToBitmapMs=(?<frameToBitmap>[\d.]+), bitmapCreatedCount=(?<bitmapCreated>\d+))?(?:, quickAccept=(?<quickAccept>True|False), quickRejectReason=(?<quickReject>[^,]+))?(?:, adaptiveThrottleMs=(?<throttle>[\d.]+), ocrBacklogBeforeEnqueue=(?<backlog>\d+), adaptivePanelMinMs=(?<panelMin>\d+), adaptivePanelSamples=(?<panelSamples>\d+), adaptivePanelReason=(?<panelReason>[^,]+)(?:, panelAcceptMode=(?<panelAcceptMode>[^,]+)(?:, postScrollAcceptMode=(?<postScrollAcceptMode>[^,]+), panelMinFloorMs=(?<panelMinFloor>\d+))?, roiCompleteFrames=(?<roiCompleteFrames>\d+), selectedStableFrames=(?<selectedStableFrames>\d+), acceptGateReason=(?<acceptGateReason>[^,]+)(?:, panelFloorMode=(?<panelFloorMode>[^,]+), sameRowPanelFloorMs=(?<sameRowPanelFloor>\d+), postScrollPanelFloorMs=(?<postScrollPanelFloor>\d+), panelFloorReason=(?<panelFloorReason>[^,]+), floorWaitLimitedMs=(?<floorWaitLimited>[\d.]+), panelAcceptElapsedVsFloorMs=(?<acceptElapsedVsFloor>[-\d.]+), scrollTickDelayMs=(?<scrollTickDelay>\d+))?)?)?)?)?",
        RegexOptions.Compiled);

    private static readonly Regex ScrollTimingRegex = new(
        @"ROW_SCROLL_TIMING: .*?scroll_tick_wait_ms=(?<tick>[\d.]+), list_stable_ms=(?<stable>[\d.]+), row_signature_ms=(?<row>[\d.]+), post_scroll_viewport_ms=(?<viewport>[\d.]+)",
        RegexOptions.Compiled);

    private static readonly Regex StartRegex = new(
        @"Start scan\..*?OcrWorkers=(?<workers>\d+), OcrBatchSize=(?<batch>\d+), OcrQueueCapacity=(?<queue>\d+), OcrIntraOpThreads=(?<intra>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex MaxItemsRegex = new(
        @"Start scan\..*?MaxItems=(?<max>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex TraversalRegex = new(
        @"Traversal:\s*(?<mode>[a-zA-Z0-9_-]+)",
        RegexOptions.Compiled);

    private static readonly Regex OverlapTraversalRegex = new(
        @"Traversal:\s*overlap-signature-page\..*?totalRows=(?<total>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OverlapCompletedRegex = new(
        @"End:\s*overlap-signature-page completed\..*?visited=(?<visited>\d+).*?queued=(?<queued>\d+).*?completed=(?<completed>\d+).*?failed=(?<failed>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CounterRegex = new(
        @"visited=(?<visited>\d+), queued=(?<queued>\d+), completed=(?<completed>\d+), failed=(?<failed>\d+)",
        RegexOptions.Compiled);

    private static readonly Regex CellTimingIndexRegex = new(
        @"CELL_TIMING: index=(?<index>\d+)",
        RegexOptions.Compiled);

    public static int Run(string scanDirectory, string? baselineDirectory)
    {
        if (!Directory.Exists(scanDirectory))
        {
            Console.Error.WriteLine($"Scan directory not found: {scanDirectory}");
            return 2;
        }

        if (baselineDirectory is not null && !Directory.Exists(baselineDirectory))
        {
            Console.Error.WriteLine($"Baseline scan directory not found: {baselineDirectory}");
            return 2;
        }

        var current = Analyze(scanDirectory);
        if (!current.Valid)
        {
            Console.Error.WriteLine(current.ErrorMessage);
            return 2;
        }

        ScanReport? baseline = null;
        if (baselineDirectory is not null)
        {
            baseline = Analyze(baselineDirectory);
            if (!baseline.Valid)
            {
                Console.Error.WriteLine(baseline.ErrorMessage);
                return 2;
            }
        }

        WriteReport("current", current);
        if (baseline is not null)
        {
            WriteReport("baseline", baseline);
            WriteDeltaReport(current, baseline);
        }

        WriteDiagnosis(current);
        return 0;
    }

    public static int RunStabilitySuite(string scanParent)
    {
        if (!Directory.Exists(scanParent))
        {
            Console.Error.WriteLine($"Scan parent not found: {scanParent}");
            return 2;
        }

        var scanDirectories = EnumerateScanDirectories(scanParent).ToArray();
        if (scanDirectories.Length == 0)
        {
            Console.Error.WriteLine($"No scan.log files found under: {scanParent}");
            return 2;
        }

        var reports = new List<ScanReport>();
        foreach (var scanDirectory in scanDirectories)
        {
            var report = Analyze(scanDirectory);
            if (!report.Valid)
            {
                Write("suite", $"scan_{NormalizeMetricName(Path.GetFileName(scanDirectory))}_valid", "false");
                continue;
            }

            reports.Add(report);
            var scanName = NormalizeMetricName(Path.GetFileName(scanDirectory));
            Write("suite", $"scan_{scanName}_completed_per_sec", report.CompletedPerSecond);
            Write("suite", $"scan_{scanName}_correctness", IsCorrectnessPass(report) ? "pass" : "fail");
            Write("suite", $"scan_{scanName}_failed", report.LastFailed);
            Write("suite", $"scan_{scanName}_duplicates", report.ExportDuplicateItemCount);
            Write("suite", $"scan_{scanName}_incomplete_roi", report.IncompleteRoiCount);
            Write("suite", $"scan_{scanName}_overshot", report.RowScrollOvershotCount);
        }

        var correctnessFailCount = reports.Count(report => !IsCorrectnessPass(report));
        var completedPerSecondValues = reports.Select(report => report.CompletedPerSecond).ToArray();
        var completedPerSecondStats = Stats.From(completedPerSecondValues.Where(value => value is not null).Select(value => value!.Value));
        var completedPerSecondP10 = Percentile(completedPerSecondValues, 0.10);
        double? speedVsBaselinePercent = completedPerSecondStats.Average is not null
            ? (completedPerSecondStats.Average.Value - RecommendationBaselineCompletedPerSecond) * 100.0 / RecommendationBaselineCompletedPerSecond
            : null;
        var rejectReason = BuildSuiteRejectReason(reports.Count, correctnessFailCount, completedPerSecondP10, speedVsBaselinePercent);
        var recommendedCandidate = string.Equals(rejectReason, "none", StringComparison.OrdinalIgnoreCase);

        Write("suite", "scan_parent", scanParent);
        Write("suite", "scan_count", reports.Count);
        Write("suite", "correctness_pass_count", reports.Count(IsCorrectnessPass));
        Write("suite", "correctness_fail_count", correctnessFailCount);
        WriteSuiteStats("completed_per_sec", completedPerSecondValues);
        WriteSuiteStats("panel_wait_ms_avg", reports.Select(report => report.PanelWait.Average));
        WriteSuiteStats("post_scroll_first_panel_wait_ms_avg", reports.Select(report => report.PostScrollFirstPanelWait.Average));
        WriteSuiteStats("scroll_ms_avg", reports.Select(report => report.ScrollDuration.Average));
        Write("suite", "failed_sum", reports.Sum(report => report.LastFailed ?? 0));
        Write("suite", "export_duplicate_items_sum", reports.Sum(report => report.ExportDuplicateItemCount));
        Write("suite", "incomplete_roi_sum", reports.Sum(report => report.IncompleteRoiCount));
        Write("suite", "row_scroll_overshot_sum", reports.Sum(report => report.RowScrollOvershotCount));
        Write("suite", "overlap_conflict_sum", reports.Sum(report => report.OverlapConflictCount));
        Write("suite", "overlap_conflict_recheck_sum", reports.Sum(report => report.OverlapConflictRecheckCount));
        Write("suite", "overlap_conflict_recovered_sum", reports.Sum(report => report.OverlapConflictRecoveredCount));
        Write("suite", "overlap_ambiguous_accept_sum", reports.Sum(report => report.OverlapAmbiguousAcceptCount));
        Write("suite", "overlap_confirmed_two_row_accept_sum", reports.Sum(report => report.OverlapConfirmedTwoRowAcceptCount));
        Write("suite", "overlap_hard_stop_sum", reports.Sum(report => report.OverlapHardStopCount));
        Write("suite", "missing_logical_rows_sum", reports.Sum(report => report.OverlapMissingLogicalRowsCount));
        Write("suite", "full_scan_complete_count", reports.Count(report => report.FullScanComplete));
        Write("suite", "quick_accept_sum", reports.Sum(report => report.QuickAcceptCount));
        Write("suite", "fallback_sum", reports.Sum(report => report.EffectiveFallbackCount));
        Write("suite", "recommendation_baseline_completed_per_sec", RecommendationBaselineCompletedPerSecond);
        Write("suite", "speed_vs_baseline_percent", speedVsBaselinePercent);
        Write("suite", "recommended_candidate", recommendedCandidate.ToString().ToLowerInvariant());
        Write("suite", "reject_reason", rejectReason);
        return reports.Count == 0 ? 2 : 0;
    }

    private static IEnumerable<string> EnumerateScanDirectories(string scanParent)
    {
        if (File.Exists(Path.Combine(scanParent, "scan.log")))
        {
            yield return scanParent;
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(scanParent)
                     .Where(directory => File.Exists(Path.Combine(directory, "scan.log")))
                     .OrderBy(directory => directory, StringComparer.OrdinalIgnoreCase))
        {
            yield return directory;
        }
    }

    private static ScanReport Analyze(string scanDirectory)
    {
        var logFile = Path.Combine(scanDirectory, "scan.log");
        if (!File.Exists(logFile))
        {
            return ScanReport.Invalid(scanDirectory, $"scan.log not found: {logFile}");
        }

        var lines = File.ReadAllLines(logFile);
        var events = ParseEvents(lines);
        var cellTimings = ParseCellTimings(lines);
        var scrollTimings = ParseScrollTimings(lines);
        var ocrRows = ReadCsv(Path.Combine(scanDirectory, "ocr_diagnostics.csv"));
        var fastAssistRows = ReadCsv(Path.Combine(scanDirectory, "ocr_fast_assist.csv"));
        var resourceRows = ReadCsv(Path.Combine(scanDirectory, "resource.csv"));
        var ocrPerItemTotal = ReadOcrMillisecondsPerItem(ocrRows);
        var fastMatchMsPerItem = ReadColumnPerItem(ocrRows, "fast_match_ms");
        var fastAcceptedPerItem = ReadColumnPerItem(ocrRows, "fast_accepted_count");
        var fastRejectedPerItem = ReadColumnPerItem(ocrRows, "fast_rejected_count");
        var ppOcrRoiPerItem = ReadColumnPerItem(ocrRows, "ppocr_roi_count");
        var intervals = BuildClickIntervals(events);
        var clickPositions = ParseClickPositions(events);
        var scrollDurations = BuildScrollDurations(events);
        var lastCounters = ParseLastCounters(lines);
        var resourceCounters = ParseResourceCounters(resourceRows);
        var scanOnceCounters = ParseScanOnceCounters(Path.Combine(scanDirectory, "scan-once-result.json"));
        var visualProfile = RuntimeVisualProfile.LoadOrLegacy(scanDirectory);
        var startSettings = ParseStartSettings(lines);
        var traversal = ParseTraversal(lines);
        var overlapSummary = ParseOverlapTraversalSummary(lines, events);
        var exportStats = ReadExportStats(Path.Combine(scanDirectory, "export.json"));
        var captureEndTime = ParseCaptureEndTime(lines);
        var overlapMode = string.Equals(traversal, "overlap-signature-page", StringComparison.OrdinalIgnoreCase);
        var sameRowClick = Stats.From(intervals.Where(x => !x.AfterScroll).Select(x => x.Milliseconds));
        var afterScrollClick = Stats.From(intervals.Where(x => x.AfterScroll).Select(x => x.Milliseconds));

        var report = new ScanReport(scanDirectory)
        {
            Valid = true,
            StartTime = ParseStartTime(lines),
            EndTime = ParseEndTime(lines),
            StopReason = ParseStopReason(lines),
            CaptureEndTime = captureEndTime,
            OcrWorkers = startSettings?.Workers,
            OcrBatchSizeSetting = startSettings?.BatchSize,
            OcrQueueCapacity = startSettings?.QueueCapacity,
            OcrIntraOpThreads = startSettings?.IntraOpThreads,
            MaxItemsSetting = ParseMaxItems(lines),
            ProfileId = visualProfile.ProfileId,
            TrainingProfileId = visualProfile.TrainingProfileId,
            ProfileFamilyId = visualProfile.ProfileFamilyId,
            ProfileGeometryStatus = visualProfile.ProfileGeometryStatus,
            RequestedProfileId = visualProfile.RequestedProfileId,
            DetectedProfileId = visualProfile.DetectedProfileId,
            ProfileDetectedGeometry = visualProfile.GeometryKey,
            ProfileRoute = ParseProfileRoute(lines),
            FastAcceptByProfileFamily = ParseFastAcceptByProfileFamily(fastAssistRows),
            FastExactProfileAcceptCount = CountFastExactProfileAccepts(fastAssistRows),
            HealthFallbackCount = lines.Count(line => line.Contains("PROFILE_HEALTH_DEGRADED", StringComparison.Ordinal)),
            CanonicalCropSucceededCount = CountBool(fastAssistRows, "canonical_crop_succeeded", expected: true),
            CanonicalCropFallbackCount = CountBool(fastAssistRows, "canonical_crop_fallback", expected: true),
            CanonicalCropDecisionCount = CountRowsWithColumn(fastAssistRows, "canonical_crop_fallback"),
            Traversal = traversal,
            ExportItemCount = exportStats.ItemCount,
            ExportDuplicateGroupCount = exportStats.DuplicateGroupCount,
            ExportDuplicateItemCount = exportStats.DuplicateItemCount,
            ErrorFileCount = Directory.EnumerateFiles(scanDirectory, "*.error.txt").Count(),
            Non15FileCount = Directory.EnumerateFiles(scanDirectory, "*.non15.txt").Count(),
            CellTimingCount = cellTimings.Count,
            CellTimingFallbackCount = cellTimings.Count(x => x.Fallback),
            CellTimingFallbackLogCount = lines.Count(line => line.Contains("Panel probes stayed unchanged", StringComparison.Ordinal)),
            SelectionOnlyAcceptCount = lines.Count(line => line.Contains("accept=selection_changed_stable_full_roi", StringComparison.Ordinal)),
            PostScrollSelectionOnlyBlockedCount = events.Count(x => x.Kind == "PANEL_SELECTION_ONLY_BLOCKED"
                && x.Detail.Contains("post_scroll_panel_change_required=True", StringComparison.OrdinalIgnoreCase)),
            WeakPanelChangeBlockedCount = events.Count(x => x.Kind == "PANEL_WEAK_CHANGE_BLOCKED"),
            PanelStablePanelCount = cellTimings.Count(x => string.Equals(x.StableSource, "panel", StringComparison.OrdinalIgnoreCase)),
            PanelStableTextCoreCount = cellTimings.Count(x => string.Equals(x.StableSource, "text-core", StringComparison.OrdinalIgnoreCase)),
            VisualRow2ClickCount = clickPositions.Count(x => x.VisualRow == 2),
            UnsafeVisualRow2ClickCount = clickPositions.Count(x => x.VisualRow == 2 && !IsAllowedVisualRow2Click(x, overlapMode)),
            OverlapViewportCount = events.Count(x => x.Kind == "OVERLAP_VIEWPORT"),
            OverlapRowScannedCount = events.Count(x => x.Kind == "OVERLAP_ROW_SCANNED"),
            OverlapScrollAcceptedCount = events.Count(x => x.Kind == "OVERLAP_SCROLL_ACCEPTED"),
            OverlapConflictCount = events.Count(x => x.Kind == "OVERLAP_SCROLL_SIGNATURE_MISMATCH"),
            OverlapConflictRecheckCount = events.Count(x => x.Kind == "OVERLAP_SCROLL_SIGNATURE_RECHECK_FRAME"),
            OverlapConflictRecoveredCount = events.Count(x => x.Kind == "OVERLAP_SCROLL_SIGNATURE_RECHECK_RECOVERED"),
            OverlapAmbiguousAcceptCount = events.Count(x => x.Kind == "OVERLAP_SCROLL_SIGNATURE_AMBIGUOUS_ACCEPTED"),
            OverlapConfirmedTwoRowAcceptCount = events.Count(x => x.Kind == "OVERLAP_SCROLL_TWO_ROW_COVERAGE_ACCEPTED"),
            OverlapHardStopCount = events.Count(x => x.Kind == "OVERLAP_SCROLL_CONFLICT_HARD_STOP"),
            TotalLogicalRows = overlapSummary.TotalRows,
            OverlapScannedLogicalRowsCount = overlapSummary.ScannedRows,
            OverlapMissingLogicalRowsCount = overlapSummary.MissingRows,
            FullScanComplete = overlapSummary.FullScanComplete,
            RowScrollOvershotCount = events.Count(x => x.Kind == "ROW_SCROLL_OVERSHOT"),
            RowScrollRecoveryAcceptedCount = events.Count(x => x.Kind == "ROW_SCROLL_RECOVERY_ACCEPTED"),
            RowScrollRecoveryFailCount = events.Count(x => x.Kind == "ROW_SCROLL_RECOVERY_FAIL"),
            RowScrollStrictStopCount = events.Count(x => x.Kind == "ROW_SCROLL_STRICT_STOP"),
            EdgeClickBlockedCount = events.Count(x => x.Kind == "EDGE_CLICK_BLOCKED"),
            MinVisibleRois = cellTimings.Count > 0 ? cellTimings.Min(x => x.VisibleRois) : null,
            IncompleteRoiCount = cellTimings.Count(x => x.VisibleRois < x.TotalRois),
            LastVisited = scanOnceCounters?.Visited ?? lastCounters?.Visited ?? resourceCounters?.Visited,
            LastQueued = scanOnceCounters?.Queued ?? lastCounters?.Queued ?? resourceCounters?.Queued,
            LastCompleted = scanOnceCounters?.Completed ?? lastCounters?.Completed ?? resourceCounters?.Completed,
            LastFailed = scanOnceCounters?.Failed ?? lastCounters?.Failed ?? resourceCounters?.Failed,
            ClickSameRow = sameRowClick,
            ClickAfterScroll = afterScrollClick,
            AfterScrollExtra = Stats.From(sameRowClick.Average is null
                ? Enumerable.Empty<double>()
                : intervals.Where(x => x.AfterScroll).Select(x => Math.Max(0, x.Milliseconds - sameRowClick.Average.Value))),
            ClickAll = Stats.From(intervals.Select(x => x.Milliseconds)),
            ScrollDuration = Stats.From(scrollDurations),
            PanelWait = Stats.From(cellTimings.Select(x => x.PanelWaitMs)),
            SameRowPanelWait = Stats.From(cellTimings.Where(x => !x.AfterScroll).Select(x => x.PanelWaitMs)),
            PostScrollFirstPanelWait = Stats.From(cellTimings.Where(x => x.PostScrollFirstCell).Select(x => x.PanelWaitMs)),
            PostScrollFirstCellTotal = Stats.From(cellTimings.Where(x => x.PostScrollFirstCell).Select(x => x.TotalMs)),
            PanelFrames = Stats.From(cellTimings.Where(x => x.PanelFrames is not null).Select(x => x.PanelFrames!.Value)),
            PanelFramesAfterWarmup = Stats.From(cellTimings.Where(x => x.PanelFrames is not null && x.AdaptivePanelMinMs is not null).Skip(AdaptiveTimingState.DefaultWarmupItems).Select(x => x.PanelFrames!.Value)),
            PanelChange = Stats.From(cellTimings.Where(x => x.ChangeMs is not null).Select(x => x.ChangeMs!.Value)),
            SelectionChange = Stats.From(cellTimings.Where(x => x.SelectionChangeMs is not null).Select(x => x.SelectionChangeMs!.Value)),
            PanelFullRoi = Stats.From(cellTimings.Where(x => x.FullRoiMs is not null).Select(x => x.FullRoiMs!.Value)),
            RoiCompleteFrames = Stats.From(cellTimings.Where(x => x.RoiCompleteFrames is not null).Select(x => x.RoiCompleteFrames!.Value)),
            SelectedStableFrames = Stats.From(cellTimings.Where(x => x.SelectedStableFrames is not null).Select(x => x.SelectedStableFrames!.Value)),
            PanelStable = Stats.From(cellTimings.Where(x => x.StableMs is not null).Select(x => x.StableMs!.Value)),
            PanelTextStable = Stats.From(cellTimings.Where(x => x.TextStableMs is not null).Select(x => x.TextStableMs!.Value)),
            RarityProbe = Stats.From(cellTimings.Where(x => x.RarityProbeMs is not null).Select(x => x.RarityProbeMs!.Value)),
            SelectionProbe = Stats.From(cellTimings.Where(x => x.SelectionProbeMs is not null).Select(x => x.SelectionProbeMs!.Value)),
            PanelCapture = Stats.From(cellTimings.Where(x => x.CaptureMs is not null).Select(x => x.CaptureMs!.Value)),
            PanelSignature = Stats.From(cellTimings.Where(x => x.SignatureMs is not null).Select(x => x.SignatureMs!.Value)),
            VisibleRoi = Stats.From(cellTimings.Where(x => x.VisibleRoiMs is not null).Select(x => x.VisibleRoiMs!.Value)),
            FrameLoop = Stats.From(cellTimings.Where(x => x.FrameLoopMs is not null).Select(x => x.FrameLoopMs!.Value)),
            FrameToBitmap = Stats.From(cellTimings.Where(x => x.FrameToBitmapMs is not null).Select(x => x.FrameToBitmapMs!.Value)),
            BitmapCreatedCount = Stats.From(cellTimings.Where(x => x.BitmapCreatedCount is not null).Select(x => x.BitmapCreatedCount!.Value)),
            AdaptiveThrottle = Stats.From(cellTimings.Where(x => x.AdaptiveThrottleMs is not null).Select(x => x.AdaptiveThrottleMs!.Value)),
            OcrBacklogBeforeEnqueue = Stats.From(cellTimings.Where(x => x.OcrBacklogBeforeEnqueue is not null).Select(x => x.OcrBacklogBeforeEnqueue!.Value)),
            AdaptivePanelMin = Stats.From(cellTimings.Where(x => x.AdaptivePanelMinMs is not null).Select(x => x.AdaptivePanelMinMs!.Value)),
            PanelMinAcceptFloor = Stats.From(cellTimings.Where(x => x.PanelMinFloorMs is not null).Select(x => x.PanelMinFloorMs!.Value)),
            SameRowPanelFloor = Stats.From(cellTimings.Where(x => x.SameRowPanelFloorMs is not null).Select(x => x.SameRowPanelFloorMs!.Value)),
            PostScrollPanelFloor = Stats.From(cellTimings.Where(x => x.PostScrollPanelFloorMs is not null).Select(x => x.PostScrollPanelFloorMs!.Value)),
            FloorWaitLimited = Stats.From(cellTimings.Where(x => x.FloorWaitLimitedMs is not null).Select(x => x.FloorWaitLimitedMs!.Value)),
            PanelAcceptElapsedVsFloor = Stats.From(cellTimings.Where(x => x.PanelAcceptElapsedVsFloorMs is not null).Select(x => x.PanelAcceptElapsedVsFloorMs!.Value)),
            ScrollTickDelay = Stats.From(cellTimings.Where(x => x.ScrollTickDelayMs is not null).Select(x => x.ScrollTickDelayMs!.Value)),
            ScrollTickWait = Stats.From(scrollTimings.Select(x => x.ScrollTickWaitMs)),
            ScrollListStable = Stats.From(scrollTimings.Select(x => x.ListStableMs)),
            RowSignature = Stats.From(scrollTimings.Select(x => x.RowSignatureMs)),
            PostScrollViewport = Stats.From(scrollTimings.Select(x => x.PostScrollViewportMs)),
            CellTotal = Stats.From(cellTimings.Select(x => x.TotalMs)),
            EnqueueWait = Stats.From(cellTimings.Select(x => x.EnqueueWaitMs)),
            FallbackPanelWait = Stats.From(cellTimings.Where(x => x.Fallback).Select(x => x.PanelWaitMs)),
            NormalPanelWait = Stats.From(cellTimings.Where(x => !x.Fallback).Select(x => x.PanelWaitMs)),
            OcrBatchSize = Stats.From(ReadColumn(ocrRows, "batch_size")),
            OcrBitmapToMat = Stats.From(ReadColumn(ocrRows, "bitmap_to_mat_ms")),
            OcrPreprocess = Stats.From(ReadColumn(ocrRows, "preprocess_ms")),
            OcrInference = Stats.From(ReadColumn(ocrRows, "inference_ms")),
            OcrDecode = Stats.From(ReadColumn(ocrRows, "decode_ms")),
            OcrTotal = Stats.From(ReadColumn(ocrRows, "total_ms")),
            OcrTotalPerItem = Stats.From(ocrPerItemTotal),
            OcrClean = Stats.From(ReadColumn(ocrRows, "clean_ms")),
            OcrBacklog = Stats.From(ReadColumn(ocrRows, "queued_completed_backlog")),
            FastMatchMsPerItem = Stats.From(fastMatchMsPerItem),
            FastAcceptedPerItem = Stats.From(fastAcceptedPerItem),
            FastRejectedPerItem = Stats.From(fastRejectedPerItem),
            PpOcrRoiPerItem = Stats.From(ppOcrRoiPerItem),
            FastOcrFeatureMs = Stats.From(ReadColumn(fastAssistRows, "feature_ms")),
            ScannerCpu = Stats.From(ReadColumn(resourceRows, "scanner_cpu_percent")),
            ResourceBacklog = Stats.From(ReadColumn(resourceRows, "ocr_backlog"))
        };
        report.FullScanExpected = report.MaxItemsSetting == 0
            && string.Equals(report.Traversal, "overlap-signature-page", StringComparison.OrdinalIgnoreCase);
        report.QuickAcceptCount = cellTimings.Count(x => x.QuickAccept == true);
        report.QuickRejectCount = cellTimings.Count(x => x.QuickAccept == false);
        report.AcceptGateReasons = cellTimings
            .Where(x => !string.IsNullOrWhiteSpace(x.AcceptGateReason))
            .GroupBy(x => x.AcceptGateReason!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        report.PostScrollAdaptiveAcceptCount = cellTimings.Count(x => string.Equals(x.PostScrollAcceptMode, "AdaptiveAfterScroll", StringComparison.OrdinalIgnoreCase));
        report.PostScrollSafeAcceptCount = cellTimings.Count(x => string.Equals(x.PostScrollAcceptMode, "Safe", StringComparison.OrdinalIgnoreCase));
        report.BeforeMinAcceptGateCount = cellTimings.Count(x => string.Equals(x.AcceptGateReason, "before_min_accept", StringComparison.OrdinalIgnoreCase));
        report.FloorWaitLimitedCount = cellTimings.Count(x => x.FloorWaitLimitedMs is > 0.5);
        report.PanelFloorModes = cellTimings
            .Where(x => !string.IsNullOrWhiteSpace(x.PanelFloorMode))
            .GroupBy(x => x.PanelFloorMode!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        report.DurationSeconds = report.StartTime is not null && report.EndTime is not null
            ? Math.Max(0, (report.EndTime.Value - report.StartTime.Value).TotalSeconds)
            : null;
        report.CaptureDurationSeconds = report.StartTime is not null && report.CaptureEndTime is not null
            ? Math.Max(0, (report.CaptureEndTime.Value - report.StartTime.Value).TotalSeconds)
            : null;
        report.CellTimingPerSecond = Rate(report.CellTimingCount, report.DurationSeconds);
        report.CaptureCellTimingPerSecond = Rate(report.CellTimingCount, report.CaptureDurationSeconds);
        report.CaptureQueuedPerSecond = Rate(report.LastQueued, report.CaptureDurationSeconds);
        report.CompletedPerSecond = Rate(report.LastCompleted, report.DurationSeconds);
        report.QueuedPerSecond = Rate(report.LastQueued, report.DurationSeconds);
        report.CaptureLimited = IsCaptureLimited(report);

        return report;
    }

    private static List<ScanEvent> ParseEvents(IEnumerable<string> lines)
    {
        var events = new List<ScanEvent>();
        foreach (var line in lines)
        {
            var match = EventRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (!DateTime.TryParse(match.Groups["timestamp"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp))
            {
                continue;
            }

            events.Add(new ScanEvent(timestamp, match.Groups["kind"].Value, match.Groups["detail"].Value));
        }

        return events;
    }

    private static List<CellTiming> ParseCellTimings(IEnumerable<string> lines)
    {
        var timings = new List<CellTiming>();
        foreach (var line in lines)
        {
            var match = CellTimingRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            timings.Add(new CellTiming(
                ParseInt(match.Groups["index"].Value),
                ParseDouble(match.Groups["panel"].Value),
                ParseDouble(match.Groups["enqueue"].Value),
                bool.Parse(match.Groups["fallback"].Value),
                ParseInt(match.Groups["visible"].Value),
                ParseInt(match.Groups["total"].Value),
                ParseDouble(match.Groups["cellTotal"].Value),
                ParseOptionalBool(match.Groups["afterScroll"].Value) == true,
                ParseOptionalBool(match.Groups["postScrollFirst"].Value) == true,
                ParseOptionalDouble(match.Groups["frames"].Value),
                ParseOptionalDouble(match.Groups["change"].Value),
                ParseOptionalDouble(match.Groups["selection"].Value),
                ParseOptionalDouble(match.Groups["fullRoi"].Value),
                ParseOptionalDouble(match.Groups["stable"].Value),
                ParseOptionalDouble(match.Groups["textStable"].Value),
                match.Groups["stableSource"].Success ? match.Groups["stableSource"].Value : null,
                match.Groups["stabilityReason"].Success ? match.Groups["stabilityReason"].Value : null,
                ParseOptionalDouble(match.Groups["rarityProbe"].Value),
                ParseOptionalDouble(match.Groups["selectionProbe"].Value),
                ParseOptionalDouble(match.Groups["capture"].Value),
                ParseOptionalDouble(match.Groups["signature"].Value),
                ParseOptionalDouble(match.Groups["visibleRoi"].Value),
                ParseOptionalDouble(match.Groups["frameLoop"].Value),
                ParseOptionalDouble(match.Groups["frameToBitmap"].Value),
                ParseOptionalDouble(match.Groups["bitmapCreated"].Value),
                ParseOptionalBool(match.Groups["quickAccept"].Value),
                match.Groups["quickReject"].Success ? match.Groups["quickReject"].Value : null,
                ParseOptionalDouble(match.Groups["throttle"].Value),
                ParseOptionalDouble(match.Groups["backlog"].Value),
                ParseOptionalDouble(match.Groups["panelMin"].Value),
                match.Groups["panelAcceptMode"].Success ? match.Groups["panelAcceptMode"].Value : null,
                match.Groups["postScrollAcceptMode"].Success ? match.Groups["postScrollAcceptMode"].Value : null,
                ParseOptionalDouble(match.Groups["panelMinFloor"].Value),
                ParseOptionalDouble(match.Groups["roiCompleteFrames"].Value),
                ParseOptionalDouble(match.Groups["selectedStableFrames"].Value),
                match.Groups["acceptGateReason"].Success ? match.Groups["acceptGateReason"].Value : null,
                match.Groups["panelFloorMode"].Success ? match.Groups["panelFloorMode"].Value : null,
                ParseOptionalDouble(match.Groups["sameRowPanelFloor"].Value),
                ParseOptionalDouble(match.Groups["postScrollPanelFloor"].Value),
                match.Groups["panelFloorReason"].Success ? match.Groups["panelFloorReason"].Value : null,
                ParseOptionalDouble(match.Groups["floorWaitLimited"].Value),
                ParseOptionalDouble(match.Groups["acceptElapsedVsFloor"].Value),
                ParseOptionalDouble(match.Groups["scrollTickDelay"].Value)));
        }

        return timings;
    }

    private static List<ScrollTiming> ParseScrollTimings(IEnumerable<string> lines)
    {
        var timings = new List<ScrollTiming>();
        foreach (var line in lines)
        {
            var match = ScrollTimingRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            timings.Add(new ScrollTiming(
                ParseDouble(match.Groups["tick"].Value),
                ParseDouble(match.Groups["stable"].Value),
                ParseDouble(match.Groups["row"].Value),
                ParseDouble(match.Groups["viewport"].Value)));
        }

        return timings;
    }

    private static List<ClickInterval> BuildClickIntervals(IReadOnlyList<ScanEvent> events)
    {
        var intervals = new List<ClickInterval>();
        for (var i = 0; i < events.Count; i++)
        {
            var item = events[i];
            if (item.Kind != "CELL_CLICK" || !CellClickRegex.IsMatch(item.Detail))
            {
                continue;
            }

            var afterScroll = false;
            for (var j = i + 1; j < events.Count; j++)
            {
                if (events[j].Kind == "ROW_SCROLL_START")
                {
                    afterScroll = true;
                }

                if (events[j].Kind == "CELL_MOVE")
                {
                    intervals.Add(new ClickInterval((events[j].Timestamp - item.Timestamp).TotalMilliseconds, afterScroll));
                    break;
                }
            }
        }

        return intervals;
    }

    private static List<ClickPosition> ParseClickPositions(IReadOnlyList<ScanEvent> events)
    {
        var positions = new List<ClickPosition>();
        foreach (var item in events)
        {
            if (item.Kind != "CELL_CLICK")
            {
                continue;
            }

            var match = SafeBandPositionRegex.Match(item.Detail);
            if (!match.Success)
            {
                continue;
            }

            positions.Add(new ClickPosition(
                ParseInt(match.Groups["logical"].Value),
                ParseInt(match.Groups["visual"].Value),
                ParseInt(match.Groups["top"].Value),
                match.Groups["state"].Value));
        }

        return positions;
    }

    private static bool IsAllowedVisualRow2Click(ClickPosition position, bool overlapMode)
    {
        if (overlapMode && position.VisualRow == 2 && position.LogicalRow == position.VisibleTopLogicalRow + 1)
        {
            return true;
        }

        return position.VisualRow == 2
            && position.LogicalRow == 2
            && position.VisibleTopLogicalRow == 1
            && position.State is "Top" or "TopAndBottom";
    }

    private static List<double> BuildScrollDurations(IReadOnlyList<ScanEvent> events)
    {
        var durations = new List<double>();
        for (var i = 0; i < events.Count; i++)
        {
            if (events[i].Kind != "ROW_SCROLL_START")
            {
                continue;
            }

            for (var j = i + 1; j < events.Count; j++)
            {
                if (events[j].Kind is "ROW_SCROLL_DONE" or "ROW_SCROLL_VERIFY")
                {
                    durations.Add((events[j].Timestamp - events[i].Timestamp).TotalMilliseconds);
                    break;
                }
            }
        }

        return durations;
    }

    private static IReadOnlyList<Dictionary<string, string>> ReadCsv(string file)
    {
        if (!File.Exists(file))
        {
            return Array.Empty<Dictionary<string, string>>();
        }

        var lines = File.ReadAllLines(file);
        if (lines.Length < 2)
        {
            return Array.Empty<Dictionary<string, string>>();
        }

        var headers = lines[0].Split(',');
        var rows = new List<Dictionary<string, string>>();
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var values = lines[i].Split(',');
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < Math.Min(headers.Length, values.Length); c++)
            {
                row[headers[c]] = values[c];
            }

            rows.Add(row);
        }

        return rows;
    }

    private static IEnumerable<double> ReadColumn(IReadOnlyList<Dictionary<string, string>> rows, string column)
    {
        foreach (var row in rows)
        {
            if (row.TryGetValue(column, out var value)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                yield return parsed;
            }
        }
    }

    private static IEnumerable<double> ReadOcrMillisecondsPerItem(IReadOnlyList<Dictionary<string, string>> rows)
    {
        foreach (var row in rows)
        {
            if (!row.TryGetValue("total_ms", out var totalValue)
                || !double.TryParse(totalValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var totalMs)
                || !row.TryGetValue("batch_size", out var batchValue)
                || !double.TryParse(batchValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var batchSize)
                || batchSize <= 0)
            {
                continue;
            }

            yield return totalMs / batchSize;
        }
    }

    private static IEnumerable<double> ReadColumnPerItem(IReadOnlyList<Dictionary<string, string>> rows, string column)
    {
        foreach (var row in rows)
        {
            if (!row.TryGetValue(column, out var value)
                || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                || !row.TryGetValue("batch_size", out var batchValue)
                || !double.TryParse(batchValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var batchSize)
                || batchSize <= 0)
            {
                continue;
            }

            yield return parsed / batchSize;
        }
    }

    private static DateTime? ParseStartTime(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Contains("Start scan.", StringComparison.Ordinal))
            {
                return ParseLineTime(line);
            }
        }

        return null;
    }

    private static DateTime? ParseEndTime(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var time = ParseLineTime(lines[i]);
            if (time is not null)
            {
                return time;
            }
        }

        return null;
    }

    private static DateTime? ParseLineTime(string line)
    {
        var end = line.IndexOf(']');
        if (end <= 1 || line[0] != '[')
        {
            return null;
        }

        return DateTime.TryParse(line.Substring(1, end - 1), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var timestamp)
            ? timestamp
            : null;
    }

    private static string ParseStopReason(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Contains("Scan canceled.", StringComparison.Ordinal)
                || line.Contains("Stop at #", StringComparison.Ordinal)
                || line.Contains("Scan failed:", StringComparison.Ordinal)
                || line.Contains("End:", StringComparison.Ordinal))
            {
                var close = line.IndexOf("] ", StringComparison.Ordinal);
                return close >= 0 ? line[(close + 2)..] : line;
            }
        }

        return "unknown";
    }

    private static StartSettings? ParseStartSettings(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = StartRegex.Match(line);
            if (match.Success)
            {
                return new StartSettings(
                    ParseInt(match.Groups["workers"].Value),
                    ParseInt(match.Groups["batch"].Value),
                    ParseInt(match.Groups["queue"].Value),
                    ParseInt(match.Groups["intra"].Value));
            }
        }

        return null;
    }

    private static int? ParseMaxItems(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = MaxItemsRegex.Match(line);
            if (match.Success)
            {
                return ParseInt(match.Groups["max"].Value);
            }
        }

        return null;
    }

    private static string ParseTraversal(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var match = TraversalRegex.Match(line);
            if (match.Success)
            {
                return match.Groups["mode"].Value;
            }
        }

        return "unknown";
    }

    private static OverlapTraversalSummary ParseOverlapTraversalSummary(IReadOnlyList<string> lines, IReadOnlyList<ScanEvent> events)
    {
        int? totalRows = null;
        var completed = false;
        foreach (var line in lines)
        {
            if (totalRows is null)
            {
                var traversalMatch = OverlapTraversalRegex.Match(line);
                if (traversalMatch.Success)
                {
                    totalRows = ParseInt(traversalMatch.Groups["total"].Value);
                }
            }

            if (OverlapCompletedRegex.IsMatch(line))
            {
                completed = true;
            }
        }

        var scannedRows = events.Count(x => x.Kind == "OVERLAP_ROW_SCANNED");
        var missingRows = totalRows is null ? 0 : Math.Max(0, totalRows.Value - scannedRows);
        return new OverlapTraversalSummary(totalRows, scannedRows, missingRows, completed && missingRows == 0);
    }

    private static DateTime? ParseCaptureEndTime(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Contains("End:", StringComparison.Ordinal)
                || CellTimingIndexRegex.IsMatch(line))
            {
                return ParseLineTime(line);
            }
        }

        return null;
    }

    private static ExportStats ReadExportStats(string exportFile)
    {
        if (!File.Exists(exportFile))
        {
            return new ExportStats(null, 0, 0);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(exportFile));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return new ExportStats(null, 0, 0);
            }

            var itemCount = 0;
            var fingerprints = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateArray())
            {
                itemCount++;
                var fingerprint = ExportFingerprint(item);
                fingerprints[fingerprint] = fingerprints.TryGetValue(fingerprint, out var count) ? count + 1 : 1;
            }

            var duplicateGroups = fingerprints.Values.Count(count => count > 1);
            var duplicateItems = fingerprints.Values.Where(count => count > 1).Sum();
            return new ExportStats(itemCount, duplicateGroups, duplicateItems);
        }
        catch
        {
            return new ExportStats(null, 0, 0);
        }
    }

    private static string ExportFingerprint(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject()
                .Where(property => !string.Equals(property.Name, "序号", StringComparison.Ordinal))
                .OrderBy(property => property.Name, StringComparer.Ordinal)
                .Select(property => $"{property.Name}:{ExportFingerprint(property.Value)}")) + "}",
            JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(ExportFingerprint)) + "]",
            JsonValueKind.String => "s:" + (element.GetString() ?? ""),
            JsonValueKind.Number => "n:" + element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        };
    }

    private static CounterSnapshot? ParseLastCounters(IReadOnlyList<string> lines)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            var match = CounterRegex.Match(lines[i]);
            if (match.Success)
            {
                return new CounterSnapshot(
                    ParseInt(match.Groups["visited"].Value),
                    ParseInt(match.Groups["queued"].Value),
                    ParseInt(match.Groups["completed"].Value),
                    ParseInt(match.Groups["failed"].Value));
            }
        }

        return null;
    }

    private static CounterSnapshot? ParseScanOnceCounters(string resultFile)
    {
        if (!File.Exists(resultFile))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(resultFile));
            var root = document.RootElement;
            return new CounterSnapshot(
                ReadJsonInt(root, "Visited"),
                ReadJsonInt(root, "Queued"),
                ReadJsonInt(root, "Completed"),
                ReadJsonInt(root, "Failed"));
        }
        catch
        {
            return null;
        }
    }

    private static int ReadJsonInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.TryGetInt32(out var value)
            ? value
            : 0;
    }

    private static CounterSnapshot? ParseResourceCounters(IReadOnlyList<Dictionary<string, string>> rows)
    {
        if (rows.Count == 0)
        {
            return null;
        }

        var last = rows[^1];
        return new CounterSnapshot(
            ReadInt(last, "visited"),
            ReadInt(last, "queued"),
            ReadInt(last, "completed"),
            ReadInt(last, "failed"));
    }

    private static string ParseProfileRoute(IEnumerable<string> lines)
    {
        var routes = lines
            .Where(line => line.Contains("FAST_OCR_PROFILE_ROUTE", StringComparison.Ordinal))
            .Select(line => Regex.Match(line, @"route=(?<route>[^,\s]+)"))
            .Where(match => match.Success)
            .Select(match => match.Groups["route"].Value)
            .GroupBy(route => route, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{NormalizeMetricName(group.Key)}:{group.Count()}");
        var summary = string.Join("+", routes);
        return string.IsNullOrWhiteSpace(summary) ? "none" : summary;
    }

    private static string ParseFastAcceptByProfileFamily(IReadOnlyList<Dictionary<string, string>> rows)
    {
        var groups = rows
            .Where(row => row.TryGetValue("source", out var source)
                && source.Equals("fast", StringComparison.OrdinalIgnoreCase))
            .Select(row => row.TryGetValue("source_family_id", out var family) && !string.IsNullOrWhiteSpace(family)
                ? family
                : "unknown")
            .GroupBy(family => family, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => $"{NormalizeMetricName(group.Key)}:{group.Count()}");
        var summary = string.Join("+", groups);
        return string.IsNullOrWhiteSpace(summary) ? "none" : summary;
    }

    private static int CountRowsWithColumn(IReadOnlyList<Dictionary<string, string>> rows, string column)
    {
        return rows.Count(row => row.ContainsKey(column));
    }

    private static int CountFastExactProfileAccepts(IReadOnlyList<Dictionary<string, string>> rows)
    {
        return rows.Count(row => row.TryGetValue("source", out var source)
            && source.Equals("fast", StringComparison.OrdinalIgnoreCase)
            && row.TryGetValue("reason", out var reason)
            && reason.StartsWith("profile_exact:", StringComparison.OrdinalIgnoreCase));
    }

    private static int CountBool(IReadOnlyList<Dictionary<string, string>> rows, string column, bool expected)
    {
        return rows.Count(row => row.TryGetValue(column, out var value)
            && bool.TryParse(value, out var parsed)
            && parsed == expected);
    }

    private static void WriteReport(string prefix, ScanReport report)
    {
        Write(prefix, "scan_dir", report.ScanDirectory);
        Write(prefix, "scan_name", Path.GetFileName(report.ScanDirectory));
        Write(prefix, "duration_sec", report.DurationSeconds);
        Write(prefix, "capture_duration_sec", report.CaptureDurationSeconds);
        Write(prefix, "cell_timing_per_sec", report.CellTimingPerSecond);
        Write(prefix, "capture_cell_timing_per_sec", report.CaptureCellTimingPerSecond);
        Write(prefix, "queued_per_sec", report.QueuedPerSecond);
        Write(prefix, "capture_queued_per_sec", report.CaptureQueuedPerSecond);
        Write(prefix, "completed_per_sec", report.CompletedPerSecond);
        Write(prefix, "capture_limited", report.CaptureLimited.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());
        Write(prefix, "stop_reason", report.StopReason);
        Write(prefix, "traversal", report.Traversal);
        Write(prefix, "ocr_workers", report.OcrWorkers);
        Write(prefix, "ocr_batch_setting", report.OcrBatchSizeSetting);
        Write(prefix, "ocr_queue_capacity", report.OcrQueueCapacity);
        Write(prefix, "ocr_intra_op_threads", report.OcrIntraOpThreads);
        Write(prefix, "max_items_setting", report.MaxItemsSetting);
        Write(prefix, "profile_id", report.ProfileId);
        Write(prefix, "training_profile_id", report.TrainingProfileId);
        Write(prefix, "profile_family_id", report.ProfileFamilyId);
        Write(prefix, "profile_geometry_status", report.ProfileGeometryStatus);
        Write(prefix, "profile_requested_id", report.RequestedProfileId);
        Write(prefix, "profile_detected_id", report.DetectedProfileId);
        Write(prefix, "profile_detected_geometry", report.ProfileDetectedGeometry);
        Write(prefix, "profile_route", report.ProfileRoute);
        Write(prefix, "fast_accept_by_profile_family", report.FastAcceptByProfileFamily);
        Write(prefix, "fast_exact_profile_accept_count", report.FastExactProfileAcceptCount);
        Write(prefix, "health_fallback_count", report.HealthFallbackCount);
        Write(prefix, "canonical_crop_succeeded_count", report.CanonicalCropSucceededCount);
        Write(prefix, "canonical_crop_fallback_count", report.CanonicalCropFallbackCount);
        Write(prefix, "canonical_crop_fallback_rate", PercentValue(report.CanonicalCropFallbackCount, report.CanonicalCropDecisionCount));
        Write(prefix, "export_items", report.ExportItemCount);
        Write(prefix, "export_matches_completed", ExportMatchesCompleted(report));
        Write(prefix, "export_duplicate_groups", report.ExportDuplicateGroupCount);
        Write(prefix, "export_duplicate_items", report.ExportDuplicateItemCount);
        Write(prefix, "error_files", report.ErrorFileCount);
        Write(prefix, "non15_files", report.Non15FileCount);
        Write(prefix, "last_visited", report.LastVisited);
        Write(prefix, "last_queued", report.LastQueued);
        Write(prefix, "last_completed", report.LastCompleted);
        Write(prefix, "last_failed", report.LastFailed);
        Write(prefix, "cell_timing_count", report.CellTimingCount);
        Write(prefix, "fallback_count", report.EffectiveFallbackCount);
        Write(prefix, "fallback_rate_percent", Percent(report.EffectiveFallbackCount, Math.Max(report.CellTimingCount, report.ClickAll.Count)));
        Write(prefix, "selection_only_accept_count", report.SelectionOnlyAcceptCount);
        Write(prefix, "post_scroll_selection_only_blocked_count", report.PostScrollSelectionOnlyBlockedCount);
        Write(prefix, "weak_panel_change_blocked_count", report.WeakPanelChangeBlockedCount);
        Write(prefix, "quick_accept_count", report.QuickAcceptCount);
        Write(prefix, "quick_reject_count", report.QuickRejectCount);
        Write(prefix, "quick_accept_rate_percent", Percent(report.QuickAcceptCount, Math.Max(1, report.QuickAcceptCount + report.QuickRejectCount)));
        Write(prefix, "panel_stable_source_panel_count", report.PanelStablePanelCount);
        Write(prefix, "panel_stable_source_text_core_count", report.PanelStableTextCoreCount);
        Write(prefix, "panel_stable_text_core_rate_percent", Percent(report.PanelStableTextCoreCount, Math.Max(1, report.PanelStablePanelCount + report.PanelStableTextCoreCount)));
        Write(prefix, "post_scroll_adaptive_accept_count", report.PostScrollAdaptiveAcceptCount);
        Write(prefix, "post_scroll_safe_accept_count", report.PostScrollSafeAcceptCount);
        Write(prefix, "before_min_accept_count", report.BeforeMinAcceptGateCount);
        Write(prefix, "floor_wait_limited_count", report.FloorWaitLimitedCount);
        Write(prefix, "panel_floor_mode", string.Join("+", report.PanelFloorModes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{NormalizeMetricName(pair.Key)}:{pair.Value}")));
        Write(prefix, "visual_row2_clicks", report.VisualRow2ClickCount);
        Write(prefix, "unsafe_visual_row2_clicks", report.UnsafeVisualRow2ClickCount);
        Write(prefix, "overlap_viewport_count", report.OverlapViewportCount);
        Write(prefix, "overlap_row_scanned_count", report.OverlapRowScannedCount);
        Write(prefix, "overlap_scroll_accepted_count", report.OverlapScrollAcceptedCount);
        Write(prefix, "overlap_conflict_count", report.OverlapConflictCount);
        Write(prefix, "overlap_conflict_recheck_count", report.OverlapConflictRecheckCount);
        Write(prefix, "overlap_conflict_recovered_count", report.OverlapConflictRecoveredCount);
        Write(prefix, "overlap_ambiguous_accept_count", report.OverlapAmbiguousAcceptCount);
        Write(prefix, "overlap_confirmed_two_row_accept_count", report.OverlapConfirmedTwoRowAcceptCount);
        Write(prefix, "overlap_hard_stop_count", report.OverlapHardStopCount);
        Write(prefix, "full_scan_expected", report.FullScanExpected.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());
        Write(prefix, "full_scan_complete", report.FullScanComplete.ToString(CultureInfo.InvariantCulture).ToLowerInvariant());
        Write(prefix, "missing_logical_rows_count", report.OverlapMissingLogicalRowsCount);
        Write(prefix, "scanned_logical_rows_count", report.OverlapScannedLogicalRowsCount);
        Write(prefix, "total_rows", report.TotalLogicalRows);
        Write(prefix, "row_scroll_overshot_count", report.RowScrollOvershotCount);
        Write(prefix, "row_scroll_recovery_accepted_count", report.RowScrollRecoveryAcceptedCount);
        Write(prefix, "row_scroll_recovery_fail_count", report.RowScrollRecoveryFailCount);
        Write(prefix, "row_scroll_strict_stop_count", report.RowScrollStrictStopCount);
        Write(prefix, "edge_click_blocked_count", report.EdgeClickBlockedCount);
        Write(prefix, "min_visible_rois", report.MinVisibleRois);
        Write(prefix, "incomplete_roi_count", report.IncompleteRoiCount);

        WriteStats(prefix, "same_row_click_ms", report.ClickSameRow);
        WriteStats(prefix, "after_scroll_click_ms", report.ClickAfterScroll);
        WriteStats(prefix, "after_scroll_extra_ms", report.AfterScrollExtra);
        WriteStats(prefix, "all_click_ms", report.ClickAll);
        WriteStats(prefix, "scroll_ms", report.ScrollDuration);
        WriteStats(prefix, "panel_wait_ms", report.PanelWait);
        WriteStats(prefix, "same_row_panel_wait_ms", report.SameRowPanelWait);
        WriteStats(prefix, "post_scroll_first_panel_wait_ms", report.PostScrollFirstPanelWait);
        WriteStats(prefix, "post_scroll_first_cell_total_ms", report.PostScrollFirstCellTotal);
        WriteStats(prefix, "panel_frames", report.PanelFrames);
        WriteStats(prefix, "panel_frames_after_warmup", report.PanelFramesAfterWarmup);
        WriteStats(prefix, "panel_change_ms", report.PanelChange);
        WriteStats(prefix, "selection_change_ms", report.SelectionChange);
        WriteStats(prefix, "panel_roi_ms", report.PanelFullRoi);
        WriteStats(prefix, "roi_complete_frames", report.RoiCompleteFrames);
        WriteStats(prefix, "selected_stable_frames", report.SelectedStableFrames);
        WriteStats(prefix, "panel_stable_ms", report.PanelStable);
        WriteStats(prefix, "panel_text_stable_ms", report.PanelTextStable);
        WriteStats(prefix, "rarity_probe_ms", report.RarityProbe);
        WriteStats(prefix, "selection_probe_ms", report.SelectionProbe);
        WriteStats(prefix, "capture_ms", report.PanelCapture);
        WriteStats(prefix, "frame_capture_ms", report.PanelCapture);
        WriteStats(prefix, "panel_signature_ms", report.PanelSignature);
        WriteStats(prefix, "visible_roi_ms", report.VisibleRoi);
        WriteStats(prefix, "frame_loop_ms", report.FrameLoop);
        WriteStats(prefix, "frame_to_bitmap_ms", report.FrameToBitmap);
        WriteStats(prefix, "bitmap_created_count", report.BitmapCreatedCount);
        WriteStats(prefix, "adaptive_throttle_ms", report.AdaptiveThrottle);
        WriteStats(prefix, "ocr_backlog_before_enqueue", report.OcrBacklogBeforeEnqueue);
        WriteStats(prefix, "adaptive_panel_min_ms", report.AdaptivePanelMin);
        WriteStats(prefix, "panel_min_floor_ms", report.PanelMinAcceptFloor);
        WriteStats(prefix, "same_row_panel_floor_ms", report.SameRowPanelFloor);
        WriteStats(prefix, "post_scroll_panel_floor_ms", report.PostScrollPanelFloor);
        WriteStats(prefix, "floor_wait_limited_ms", report.FloorWaitLimited);
        WriteStats(prefix, "panel_accept_elapsed_vs_floor_ms", report.PanelAcceptElapsedVsFloor);
        WriteStats(prefix, "scroll_tick_delay_ms", report.ScrollTickDelay);
        WriteStats(prefix, "scroll_tick_wait_ms", report.ScrollTickWait);
        WriteStats(prefix, "scroll_list_stable_ms", report.ScrollListStable);
        WriteStats(prefix, "row_signature_ms", report.RowSignature);
        WriteStats(prefix, "post_scroll_viewport_ms", report.PostScrollViewport);
        WriteStats(prefix, "cell_total_ms", report.CellTotal);
        WriteStats(prefix, "enqueue_wait_ms", report.EnqueueWait);
        WriteStats(prefix, "fallback_panel_wait_ms", report.FallbackPanelWait);
        WriteStats(prefix, "normal_panel_wait_ms", report.NormalPanelWait);
        WriteStats(prefix, "ocr_batch_size", report.OcrBatchSize);
        WriteStats(prefix, "ocr_bitmap_to_mat_ms", report.OcrBitmapToMat);
        WriteStats(prefix, "ocr_preprocess_ms", report.OcrPreprocess);
        WriteStats(prefix, "ocr_inference_ms", report.OcrInference);
        WriteStats(prefix, "ocr_decode_ms", report.OcrDecode);
        WriteStats(prefix, "ocr_total_ms", report.OcrTotal);
        WriteStats(prefix, "ocr_total_ms_per_item", report.OcrTotalPerItem);
        WriteStats(prefix, "ocr_clean_ms", report.OcrClean);
        WriteStats(prefix, "ocr_backlog", report.OcrBacklog);
        WriteStats(prefix, "fast_match_ms_per_item", report.FastMatchMsPerItem);
        WriteStats(prefix, "fast_accepted_per_item", report.FastAcceptedPerItem);
        WriteStats(prefix, "fast_rejected_per_item", report.FastRejectedPerItem);
        WriteStats(prefix, "ppocr_roi_per_item", report.PpOcrRoiPerItem);
        WriteStats(prefix, "v6_feature_ms", report.FastOcrFeatureMs);
        WriteStats(prefix, "scanner_cpu_percent", report.ScannerCpu);
        WriteStats(prefix, "resource_ocr_backlog", report.ResourceBacklog);
        foreach (var (reason, count) in report.AcceptGateReasons.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            Write(prefix, $"accept_gate_reason_{NormalizeMetricName(reason)}_count", count);
        }
    }

    private static void WriteDeltaReport(ScanReport current, ScanReport baseline)
    {
        WriteDelta("same_row_click_delta_percent", current.ClickSameRow.Average, baseline.ClickSameRow.Average);
        WriteDelta("after_scroll_click_delta_percent", current.ClickAfterScroll.Average, baseline.ClickAfterScroll.Average);
        WriteDelta("after_scroll_extra_delta_percent", current.AfterScrollExtra.Average, baseline.AfterScrollExtra.Average);
        WriteDelta("all_click_delta_percent", current.ClickAll.Average, baseline.ClickAll.Average);
        WriteDelta("cell_timing_per_sec_delta_percent", current.CellTimingPerSecond, baseline.CellTimingPerSecond);
        WriteDelta("capture_cell_timing_per_sec_delta_percent", current.CaptureCellTimingPerSecond, baseline.CaptureCellTimingPerSecond);
        WriteDelta("queued_per_sec_delta_percent", current.QueuedPerSecond, baseline.QueuedPerSecond);
        WriteDelta("capture_queued_per_sec_delta_percent", current.CaptureQueuedPerSecond, baseline.CaptureQueuedPerSecond);
        WriteDelta("panel_wait_delta_percent", current.PanelWait.Average, baseline.PanelWait.Average);
        WriteDelta("same_row_panel_wait_delta_percent", current.SameRowPanelWait.Average, baseline.SameRowPanelWait.Average);
        WriteDelta("post_scroll_first_panel_wait_delta_percent", current.PostScrollFirstPanelWait.Average, baseline.PostScrollFirstPanelWait.Average);
        WriteDelta("post_scroll_first_cell_total_delta_percent", current.PostScrollFirstCellTotal.Average, baseline.PostScrollFirstCellTotal.Average);
        WriteDelta("panel_frames_delta_percent", current.PanelFrames.Average, baseline.PanelFrames.Average);
        WriteDelta("panel_frames_after_warmup_delta_percent", current.PanelFramesAfterWarmup.Average, baseline.PanelFramesAfterWarmup.Average);
        WriteDelta("panel_change_delta_percent", current.PanelChange.Average, baseline.PanelChange.Average);
        WriteDelta("selection_change_delta_percent", current.SelectionChange.Average, baseline.SelectionChange.Average);
        WriteDelta("panel_roi_delta_percent", current.PanelFullRoi.Average, baseline.PanelFullRoi.Average);
        WriteDelta("roi_complete_frames_delta_percent", current.RoiCompleteFrames.Average, baseline.RoiCompleteFrames.Average);
        WriteDelta("selected_stable_frames_delta_percent", current.SelectedStableFrames.Average, baseline.SelectedStableFrames.Average);
        WriteDelta("panel_stable_delta_percent", current.PanelStable.Average, baseline.PanelStable.Average);
        WriteDelta("panel_text_stable_delta_percent", current.PanelTextStable.Average, baseline.PanelTextStable.Average);
        WriteDelta("rarity_probe_delta_percent", current.RarityProbe.Average, baseline.RarityProbe.Average);
        WriteDelta("selection_probe_delta_percent", current.SelectionProbe.Average, baseline.SelectionProbe.Average);
        WriteDelta("capture_ms_delta_percent", current.PanelCapture.Average, baseline.PanelCapture.Average);
        WriteDelta("frame_to_bitmap_ms_delta_percent", current.FrameToBitmap.Average, baseline.FrameToBitmap.Average);
        WriteDelta("panel_signature_delta_percent", current.PanelSignature.Average, baseline.PanelSignature.Average);
        WriteDelta("visible_roi_delta_percent", current.VisibleRoi.Average, baseline.VisibleRoi.Average);
        WriteDelta("scroll_list_stable_delta_percent", current.ScrollListStable.Average, baseline.ScrollListStable.Average);
        WriteDelta("row_signature_delta_percent", current.RowSignature.Average, baseline.RowSignature.Average);
        WriteDelta("ocr_total_delta_percent", current.OcrTotal.Average, baseline.OcrTotal.Average);
        WriteDelta("ocr_total_per_item_delta_percent", current.OcrTotalPerItem.Average, baseline.OcrTotalPerItem.Average);
        WriteDelta("ppocr_roi_per_item_delta_percent", current.PpOcrRoiPerItem.Average, baseline.PpOcrRoiPerItem.Average);
        WriteDelta("fast_accepted_per_item_delta_percent", current.FastAcceptedPerItem.Average, baseline.FastAcceptedPerItem.Average);
        WriteDelta("fallback_rate_delta_percent",
            PercentValue(current.EffectiveFallbackCount, Math.Max(current.CellTimingCount, current.ClickAll.Count)),
            PercentValue(baseline.EffectiveFallbackCount, Math.Max(baseline.CellTimingCount, baseline.ClickAll.Count)));
    }

    private static void WriteDiagnosis(ScanReport report)
    {
        var panelHigh = report.PanelWait.HasData && report.PanelWait.Average >= 180;
        var scrollHigh = report.ClickAfterScroll.HasData
            && report.ClickSameRow.HasData
            && report.ClickAfterScroll.Average >= report.ClickSameRow.Average * 2.0;
        var backlogHigh = report.ResourceBacklog.HasData
            && report.OcrWorkers is > 0
            && report.ResourceBacklog.Maximum >= report.OcrWorkers.Value * 2;
        var ocrHigh = report.OcrTotal.HasData && report.OcrBacklog.HasData && report.OcrBacklog.Maximum >= 4;

        Write("diagnosis", "panel_wait", panelHigh ? "high: tune panel settle/poll/fallback next" : "ok");
        Write("diagnosis", "scroll_wait", scrollHigh ? "high: tune list stable wait carefully" : "ok");
        Write("diagnosis", "ocr", backlogHigh || ocrHigh ? "high: review ocr_diagnostics.csv and OCR settings" : "ok");
        Write("diagnosis", "roi_integrity", report.IncompleteRoiCount > 0 ? "risk: incomplete ROI captures present" : "ok");
        Write("diagnosis", "safe_band", report.UnsafeVisualRow2ClickCount > 0 ? "risk: middle visual row 2 clicks present" : "ok");
        Write("acceptance", "no_incomplete_roi", report.IncompleteRoiCount == 0 ? "pass" : "fail");
        Write("acceptance", "no_error_files", report.ErrorFileCount == 0 ? "pass" : "fail");
        Write("acceptance", "export_consistency", ExportMatchesCompleted(report) is false ? "risk" : "pass");
        Write("acceptance", "no_export_duplicates", report.ExportDuplicateItemCount == 0 ? "pass" : "fail");
        Write("acceptance", "backlog_not_saturated", IsBacklogSaturated(report) ? "risk" : "pass");
        Write("acceptance", "no_unsafe_visual_row2", report.UnsafeVisualRow2ClickCount == 0 ? "pass" : "fail");
        Write("acceptance", "overlap_rows_complete", string.Equals(report.Traversal, "overlap-signature-page", StringComparison.OrdinalIgnoreCase) && report.OverlapRowScannedCount > 0 && report.LastQueued == report.ExportItemCount ? "pass" : "skip");
        Write("acceptance", "overlap_no_hard_stop", report.OverlapHardStopCount == 0 ? "pass" : "fail");
        Write("acceptance", "overlap_no_missing_rows", !report.FullScanExpected ? "skip" : report.OverlapMissingLogicalRowsCount == 0 ? "pass" : "fail");
        Write("acceptance", "full_scan_complete", !report.FullScanExpected ? "skip" : report.FullScanComplete ? "pass" : "fail");
        Write("acceptance", "strict_one_way_scroll", report.RowScrollOvershotCount == 0 && report.RowScrollRecoveryAcceptedCount == 0 && report.RowScrollStrictStopCount == 0 ? "pass" : "risk");
        Write("acceptance", "scroll_overshot_recovered", report.RowScrollRecoveryFailCount == 0 && report.RowScrollOvershotCount <= report.RowScrollRecoveryAcceptedCount ? "pass" : "risk");
    }

    private static void WriteStats(string prefix, string name, Stats stats)
    {
        Write(prefix, $"{name}_count", stats.Count);
        Write(prefix, $"{name}_avg", stats.Average);
        Write(prefix, $"{name}_p50", stats.P50);
        Write(prefix, $"{name}_p90", stats.P90);
        Write(prefix, $"{name}_min", stats.Minimum);
        Write(prefix, $"{name}_max", stats.Maximum);
    }

    private static string NormalizeMetricName(string value)
    {
        var chars = value
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        var normalized = Regex.Replace(new string(chars), "_+", "_").Trim('_');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static void Write(string prefix, string name, object? value)
    {
        var formatted = value switch
        {
            null => "N/A",
            double d => double.IsNaN(d) ? "N/A" : d.ToString("F3", CultureInfo.InvariantCulture),
            float f => float.IsNaN(f) ? "N/A" : f.ToString("F3", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "N/A"
        };

        Console.WriteLine($"{prefix}.{name}={formatted}");
    }

    private static void WriteDelta(string name, double? current, double? baseline)
    {
        if (current is null || baseline is null || Math.Abs(baseline.Value) < 0.000001)
        {
            Console.WriteLine($"delta.{name}=N/A");
            return;
        }

        var delta = (current.Value - baseline.Value) * 100.0 / baseline.Value;
        Console.WriteLine($"delta.{name}={delta.ToString("F3", CultureInfo.InvariantCulture)}");
    }

    private static double? Percent(int numerator, int denominator)
    {
        return denominator > 0 ? numerator * 100.0 / denominator : null;
    }

    private static double? PercentValue(int numerator, int denominator)
    {
        return denominator > 0 ? numerator * 100.0 / denominator : null;
    }

    private static int ParseInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static double ParseDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static double? ParseOptionalDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    private static bool? ParseOptionalBool(string value)
    {
        return bool.TryParse(value, out var parsed) ? parsed : null;
    }

    private static int ReadInt(IReadOnlyDictionary<string, string> row, string column)
    {
        return row.TryGetValue(column, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : 0;
    }

    private static double? Rate(int? count, double? seconds)
    {
        return count is not null && seconds is > 0.000001 ? count.Value / seconds.Value : null;
    }

    private static bool? ExportMatchesCompleted(ScanReport report)
    {
        if (report.ExportItemCount is null || report.LastCompleted is null)
        {
            return null;
        }

        if (report.ExportItemCount.Value == report.LastCompleted.Value)
        {
            return true;
        }

        if (report.LastQueued is not null)
        {
            var expectedExportCount = Math.Max(0, report.LastQueued.Value - report.ErrorFileCount - report.Non15FileCount);
            return report.ExportItemCount.Value == expectedExportCount;
        }

        return false;
    }

    private static void WriteSuiteStats(string metric, IEnumerable<double?> values)
    {
        var stats = Stats.From(values.Where(value => value is not null).Select(value => value!.Value));
        Write("suite", $"{metric}_count", stats.Count);
        Write("suite", $"{metric}_min", stats.Minimum);
        Write("suite", $"{metric}_p10", Percentile(values, 0.10));
        Write("suite", $"{metric}_avg", stats.Average);
        Write("suite", $"{metric}_p90", stats.P90);
        Write("suite", $"{metric}_max", stats.Maximum);
    }

    private static string BuildSuiteRejectReason(
        int scanCount,
        int correctnessFailCount,
        double? completedPerSecondP10,
        double? speedVsBaselinePercent)
    {
        var reasons = new List<string>();
        if (scanCount == 0)
        {
            reasons.Add("no_valid_scans");
        }

        if (correctnessFailCount > 0)
        {
            reasons.Add("correctness_failed");
        }

        if (completedPerSecondP10 is null || completedPerSecondP10.Value < RecommendationMinimumP10CompletedPerSecond)
        {
            reasons.Add("p10_below_3_65");
        }

        if (speedVsBaselinePercent is null || speedVsBaselinePercent.Value < RecommendationMinimumAverageGainPercent)
        {
            reasons.Add("avg_gain_below_5_percent");
        }

        return reasons.Count == 0 ? "none" : string.Join(",", reasons);
    }

    private static double? Percentile(IEnumerable<double?> values, double percentile)
    {
        var sorted = values
            .Where(value => value is not null && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value))
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToArray();
        if (sorted.Length == 0)
        {
            return null;
        }

        var index = Math.Clamp((int)(sorted.Length * percentile), 0, sorted.Length - 1);
        return sorted[index];
    }

    private static bool IsCorrectnessPass(ScanReport report)
    {
        if (!report.Valid)
        {
            return false;
        }

        var exportMatchesCompleted = ExportMatchesCompleted(report);
        return report.LastFailed == 0
            && report.ErrorFileCount == 0
            && report.ExportDuplicateItemCount == 0
            && report.IncompleteRoiCount == 0
            && report.QuickAcceptCount == 0
            && report.RowScrollOvershotCount == 0
            && report.RowScrollRecoveryAcceptedCount == 0
            && report.RowScrollRecoveryFailCount == 0
            && report.RowScrollStrictStopCount == 0
            && report.UnsafeVisualRow2ClickCount == 0
            && exportMatchesCompleted != false;
    }

    private static bool IsBacklogSaturated(ScanReport report)
    {
        if (!report.OcrBacklog.HasData || report.OcrQueueCapacity is null || report.OcrBacklog.Maximum is null)
        {
            return false;
        }

        return report.OcrBacklog.Maximum.Value >= Math.Max(1, report.OcrQueueCapacity.Value - 1);
    }

    private static bool IsCaptureLimited(ScanReport report)
    {
        if (report.CaptureQueuedPerSecond is null || report.CompletedPerSecond is null)
        {
            return false;
        }

        var backlogMax = report.OcrBacklog.Maximum ?? report.ResourceBacklog.Maximum ?? 0;
        return backlogMax <= 4
            && report.CompletedPerSecond.Value >= report.CaptureQueuedPerSecond.Value * 0.90;
    }

    private sealed class ScanReport
    {
        public ScanReport(string scanDirectory)
        {
            ScanDirectory = scanDirectory;
        }

        public string ScanDirectory { get; }
        public bool Valid { get; init; }
        public string ErrorMessage { get; init; } = "";
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public DateTime? CaptureEndTime { get; set; }
        public double? DurationSeconds { get; set; }
        public double? CaptureDurationSeconds { get; set; }
        public double? CellTimingPerSecond { get; set; }
        public double? CaptureCellTimingPerSecond { get; set; }
        public double? QueuedPerSecond { get; set; }
        public double? CaptureQueuedPerSecond { get; set; }
        public double? CompletedPerSecond { get; set; }
        public bool CaptureLimited { get; set; }
        public string StopReason { get; set; } = "";
        public string Traversal { get; set; } = "unknown";
        public int? OcrWorkers { get; set; }
        public int? OcrBatchSizeSetting { get; set; }
        public int? OcrQueueCapacity { get; set; }
        public int? OcrIntraOpThreads { get; set; }
        public int? MaxItemsSetting { get; set; }
        public string ProfileId { get; set; } = "";
        public string TrainingProfileId { get; set; } = "";
        public string ProfileFamilyId { get; set; } = "";
        public string ProfileGeometryStatus { get; set; } = "";
        public string RequestedProfileId { get; set; } = "";
        public string DetectedProfileId { get; set; } = "";
        public string ProfileDetectedGeometry { get; set; } = "";
        public string ProfileRoute { get; set; } = "";
        public string FastAcceptByProfileFamily { get; set; } = "";
        public int FastExactProfileAcceptCount { get; set; }
        public int HealthFallbackCount { get; set; }
        public int CanonicalCropSucceededCount { get; set; }
        public int CanonicalCropFallbackCount { get; set; }
        public int CanonicalCropDecisionCount { get; set; }
        public int? ExportItemCount { get; set; }
        public int ExportDuplicateGroupCount { get; set; }
        public int ExportDuplicateItemCount { get; set; }
        public int ErrorFileCount { get; set; }
        public int Non15FileCount { get; set; }
        public int CellTimingCount { get; set; }
        public int CellTimingFallbackCount { get; set; }
        public int CellTimingFallbackLogCount { get; set; }
        public int EffectiveFallbackCount => CellTimingCount > 0 ? CellTimingFallbackCount : CellTimingFallbackLogCount;
        public int SelectionOnlyAcceptCount { get; set; }
        public int PostScrollSelectionOnlyBlockedCount { get; set; }
        public int WeakPanelChangeBlockedCount { get; set; }
        public int QuickAcceptCount { get; set; }
        public int QuickRejectCount { get; set; }
        public int PanelStablePanelCount { get; set; }
        public int PanelStableTextCoreCount { get; set; }
        public int PostScrollAdaptiveAcceptCount { get; set; }
        public int PostScrollSafeAcceptCount { get; set; }
        public int BeforeMinAcceptGateCount { get; set; }
        public int FloorWaitLimitedCount { get; set; }
        public int VisualRow2ClickCount { get; set; }
        public int UnsafeVisualRow2ClickCount { get; set; }
        public int OverlapViewportCount { get; set; }
        public int OverlapRowScannedCount { get; set; }
        public int OverlapScrollAcceptedCount { get; set; }
        public int OverlapConflictCount { get; set; }
        public int OverlapConflictRecheckCount { get; set; }
        public int OverlapConflictRecoveredCount { get; set; }
        public int OverlapAmbiguousAcceptCount { get; set; }
        public int OverlapConfirmedTwoRowAcceptCount { get; set; }
        public int OverlapHardStopCount { get; set; }
        public int? TotalLogicalRows { get; set; }
        public int OverlapScannedLogicalRowsCount { get; set; }
        public int OverlapMissingLogicalRowsCount { get; set; }
        public bool FullScanExpected { get; set; }
        public bool FullScanComplete { get; set; }
        public int RowScrollOvershotCount { get; set; }
        public int RowScrollRecoveryAcceptedCount { get; set; }
        public int RowScrollRecoveryFailCount { get; set; }
        public int RowScrollStrictStopCount { get; set; }
        public int EdgeClickBlockedCount { get; set; }
        public int? MinVisibleRois { get; set; }
        public int IncompleteRoiCount { get; set; }
        public int? LastVisited { get; set; }
        public int? LastQueued { get; set; }
        public int? LastCompleted { get; set; }
        public int? LastFailed { get; set; }
        public Stats ClickSameRow { get; set; }
        public Stats ClickAfterScroll { get; set; }
        public Stats AfterScrollExtra { get; set; }
        public Stats ClickAll { get; set; }
        public Stats ScrollDuration { get; set; }
        public Stats PanelWait { get; set; }
        public Stats SameRowPanelWait { get; set; }
        public Stats PostScrollFirstPanelWait { get; set; }
        public Stats PostScrollFirstCellTotal { get; set; }
        public Stats PanelFrames { get; set; }
        public Stats PanelFramesAfterWarmup { get; set; }
        public Stats PanelChange { get; set; }
        public Stats SelectionChange { get; set; }
        public Stats PanelFullRoi { get; set; }
        public Stats RoiCompleteFrames { get; set; }
        public Stats SelectedStableFrames { get; set; }
        public Stats PanelStable { get; set; }
        public Stats PanelTextStable { get; set; }
        public Stats RarityProbe { get; set; }
        public Stats SelectionProbe { get; set; }
        public Stats PanelCapture { get; set; }
        public Stats PanelSignature { get; set; }
        public Stats VisibleRoi { get; set; }
        public Stats FrameLoop { get; set; }
        public Stats FrameToBitmap { get; set; }
        public Stats BitmapCreatedCount { get; set; }
        public Stats AdaptiveThrottle { get; set; }
        public Stats OcrBacklogBeforeEnqueue { get; set; }
        public Stats AdaptivePanelMin { get; set; }
        public Stats PanelMinAcceptFloor { get; set; }
        public Stats SameRowPanelFloor { get; set; }
        public Stats PostScrollPanelFloor { get; set; }
        public Stats FloorWaitLimited { get; set; }
        public Stats PanelAcceptElapsedVsFloor { get; set; }
        public Stats ScrollTickDelay { get; set; }
        public Stats ScrollTickWait { get; set; }
        public Stats ScrollListStable { get; set; }
        public Stats RowSignature { get; set; }
        public Stats PostScrollViewport { get; set; }
        public Stats CellTotal { get; set; }
        public Stats EnqueueWait { get; set; }
        public Stats FallbackPanelWait { get; set; }
        public Stats NormalPanelWait { get; set; }
        public Stats OcrBatchSize { get; set; }
        public Stats OcrBitmapToMat { get; set; }
        public Stats OcrPreprocess { get; set; }
        public Stats OcrInference { get; set; }
        public Stats OcrDecode { get; set; }
        public Stats OcrTotal { get; set; }
        public Stats OcrTotalPerItem { get; set; }
        public Stats OcrClean { get; set; }
        public Stats OcrBacklog { get; set; }
        public Stats FastMatchMsPerItem { get; set; }
        public Stats FastAcceptedPerItem { get; set; }
        public Stats FastRejectedPerItem { get; set; }
        public Stats PpOcrRoiPerItem { get; set; }
        public Stats FastOcrFeatureMs { get; set; }
        public Stats ScannerCpu { get; set; }
        public Stats ResourceBacklog { get; set; }
        public Dictionary<string, int> AcceptGateReasons { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> PanelFloorModes { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static ScanReport Invalid(string scanDirectory, string message)
        {
            return new ScanReport(scanDirectory)
            {
                Valid = false,
                ErrorMessage = message
            };
        }
    }

    private readonly record struct ScanEvent(DateTime Timestamp, string Kind, string Detail);

    private readonly record struct ClickInterval(double Milliseconds, bool AfterScroll);

    private readonly record struct ClickPosition(int LogicalRow, int VisualRow, int VisibleTopLogicalRow, string State);

    private readonly record struct CellTiming(int Index, double PanelWaitMs, double EnqueueWaitMs, bool Fallback, int VisibleRois, int TotalRois, double TotalMs, bool AfterScroll, bool PostScrollFirstCell, double? PanelFrames, double? ChangeMs, double? SelectionChangeMs, double? FullRoiMs, double? StableMs, double? TextStableMs, string? StableSource, string? StabilityReason, double? RarityProbeMs, double? SelectionProbeMs, double? CaptureMs, double? SignatureMs, double? VisibleRoiMs, double? FrameLoopMs, double? FrameToBitmapMs, double? BitmapCreatedCount, bool? QuickAccept, string? QuickRejectReason, double? AdaptiveThrottleMs, double? OcrBacklogBeforeEnqueue, double? AdaptivePanelMinMs, string? PanelAcceptMode, string? PostScrollAcceptMode, double? PanelMinFloorMs, double? RoiCompleteFrames, double? SelectedStableFrames, string? AcceptGateReason, string? PanelFloorMode, double? SameRowPanelFloorMs, double? PostScrollPanelFloorMs, string? PanelFloorReason, double? FloorWaitLimitedMs, double? PanelAcceptElapsedVsFloorMs, double? ScrollTickDelayMs);

    private readonly record struct ScrollTiming(double ScrollTickWaitMs, double ListStableMs, double RowSignatureMs, double PostScrollViewportMs);

    private readonly record struct CounterSnapshot(int Visited, int Queued, int Completed, int Failed);

    private readonly record struct StartSettings(int Workers, int BatchSize, int QueueCapacity, int IntraOpThreads);

    private readonly record struct ExportStats(int? ItemCount, int DuplicateGroupCount, int DuplicateItemCount);

    private readonly record struct OverlapTraversalSummary(int? TotalRows, int ScannedRows, int MissingRows, bool FullScanComplete);

    private readonly record struct Stats(int Count, double? Average, double? P50, double? P90, double? Minimum, double? Maximum)
    {
        public bool HasData => Count > 0;

        public static Stats From(IEnumerable<double> values)
        {
            var sorted = values
                .Where(value => !double.IsNaN(value) && !double.IsInfinity(value))
                .OrderBy(value => value)
                .ToArray();
            if (sorted.Length == 0)
            {
                return new Stats(0, null, null, null, null, null);
            }

            return new Stats(
                sorted.Length,
                sorted.Average(),
                Percentile(sorted, 0.50),
                Percentile(sorted, 0.90),
                sorted[0],
                sorted[^1]);
        }

        private static double Percentile(IReadOnlyList<double> sorted, double percentile)
        {
            var index = Math.Clamp((int)(sorted.Count * percentile), 0, sorted.Count - 1);
            return sorted[index];
        }
    }
}
