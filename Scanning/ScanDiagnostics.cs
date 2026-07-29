namespace ZZZScannerNext.Scanning;

public interface IScanDiagnosticException
{
    IReadOnlyDictionary<string, object?> DiagnosticDetails { get; }
}

public sealed class ScanSessionDiagnostics
{
    public int ClientWidth { get; init; }
    public int ClientHeight { get; init; }
    public int Dpi { get; init; }
    public string CaptureMode { get; init; } = "";
    public string VisualProfileId { get; init; } = "";
    public string PreflightState { get; init; } = "";
    public string VisualTransformClass { get; init; } = "";
    public int AnchorScore { get; init; }
    public int GridScore { get; init; }
    public bool WarehouseHeaderDetected { get; init; }
    public int HeaderScore { get; init; }
    public int GridStructureScore { get; init; }
    public int LayoutScore { get; init; }
    public bool InventoryCountDetected { get; init; }
    public int CountConsensusFrames { get; init; }
    public int HueDelta { get; init; }
    public int SaturationDeltaPct { get; init; }
    public int ValueDeltaPct { get; init; }
}

internal static class ScanDiagnosticDetails
{
    public static IReadOnlyDictionary<string, object?> Session(ScanSessionDiagnostics diagnostics)
    {
        return new Dictionary<string, object?>
        {
            ["preflightState"] = diagnostics.PreflightState,
            ["visualTransformClass"] = diagnostics.VisualTransformClass,
            ["anchorScore"] = diagnostics.AnchorScore,
            ["gridScore"] = diagnostics.GridScore,
            ["warehouseHeaderDetected"] = diagnostics.WarehouseHeaderDetected,
            ["headerScore"] = diagnostics.HeaderScore,
            ["gridStructureScore"] = diagnostics.GridStructureScore,
            ["layoutScore"] = diagnostics.LayoutScore,
            ["inventoryCountDetected"] = diagnostics.InventoryCountDetected,
            ["countConsensusFrames"] = diagnostics.CountConsensusFrames,
            ["hueDelta"] = diagnostics.HueDelta,
            ["saturationDeltaPct"] = diagnostics.SaturationDeltaPct,
            ["valueDeltaPct"] = diagnostics.ValueDeltaPct,
            ["clientWidth"] = diagnostics.ClientWidth,
            ["clientHeight"] = diagnostics.ClientHeight,
            ["dpi"] = diagnostics.Dpi,
            ["captureMode"] = diagnostics.CaptureMode,
            ["visualProfileId"] = diagnostics.VisualProfileId
        };
    }

    public static IReadOnlyDictionary<string, object?> Merge(
        IReadOnlyDictionary<string, object?> primary,
        IReadOnlyDictionary<string, object?> secondary)
    {
        var result = new Dictionary<string, object?>(secondary, StringComparer.Ordinal);
        foreach (var (key, value) in primary)
        {
            result[key] = value;
        }

        return result;
    }

    public static IReadOnlyDictionary<string, object?> Preflight(
        string preflightState,
        string visualTransformClass,
        int anchorScore,
        int gridScore,
        bool warehouseHeaderDetected,
        int headerScore,
        int gridStructureScore,
        int layoutScore,
        bool inventoryCountDetected,
        int countConsensusFrames,
        int hueDelta,
        int saturationDeltaPct,
        int valueDeltaPct,
        int stableFrames,
        int requiredStableFrames,
        int clientWidth,
        int clientHeight,
        int dpi,
        string captureMode,
        string visualProfileId)
    {
        return new Dictionary<string, object?>
        {
            ["preflightState"] = preflightState,
            ["visualTransformClass"] = visualTransformClass,
            ["anchorScore"] = anchorScore,
            ["gridScore"] = gridScore,
            ["warehouseHeaderDetected"] = warehouseHeaderDetected,
            ["headerScore"] = headerScore,
            ["gridStructureScore"] = gridStructureScore,
            ["layoutScore"] = layoutScore,
            ["inventoryCountDetected"] = inventoryCountDetected,
            ["countConsensusFrames"] = countConsensusFrames,
            ["hueDelta"] = hueDelta,
            ["saturationDeltaPct"] = saturationDeltaPct,
            ["valueDeltaPct"] = valueDeltaPct,
            ["stableFrames"] = stableFrames,
            ["requiredStableFrames"] = requiredStableFrames,
            ["clientWidth"] = clientWidth,
            ["clientHeight"] = clientHeight,
            ["dpi"] = dpi,
            ["captureMode"] = captureMode,
            ["visualProfileId"] = visualProfileId
        };
    }

    public static IReadOnlyDictionary<string, object?> PanelCapture(
        int? logicalRow,
        int visualRow,
        int column,
        int maxColumns,
        int visibleRois,
        int totalRois,
        string? firstMissingRoi,
        int? referenceLuma,
        int? candidateLuma,
        int? lumaDelta,
        int? allowedLumaDelta,
        int? edgeDensityPermille,
        int? minimumEdgeDensityPermille,
        string acceptGateReason,
        bool sawPanelChange,
        bool selectionChanged,
        int stableFrames,
        int requiredStableFrames,
        int attempts,
        int frameCount,
        int clientWidth,
        int clientHeight,
        int dpi,
        string captureMode,
        string visualProfileId)
    {
        return new Dictionary<string, object?>
        {
            ["logicalRow"] = logicalRow,
            ["visualRow"] = visualRow,
            ["column"] = column,
            ["maxColumns"] = maxColumns,
            ["visibleRois"] = visibleRois,
            ["totalRois"] = totalRois,
            ["firstMissingRoi"] = firstMissingRoi,
            ["referenceLuma"] = referenceLuma,
            ["candidateLuma"] = candidateLuma,
            ["lumaDelta"] = lumaDelta,
            ["allowedLumaDelta"] = allowedLumaDelta,
            ["edgeDensityPermille"] = edgeDensityPermille,
            ["minimumEdgeDensityPermille"] = minimumEdgeDensityPermille,
            ["acceptGateReason"] = acceptGateReason,
            ["sawPanelChange"] = sawPanelChange,
            ["selectionChanged"] = selectionChanged,
            ["stableFrames"] = stableFrames,
            ["requiredStableFrames"] = requiredStableFrames,
            ["attempts"] = attempts,
            ["frameCount"] = frameCount,
            ["clientWidth"] = clientWidth,
            ["clientHeight"] = clientHeight,
            ["dpi"] = dpi,
            ["captureMode"] = captureMode,
            ["visualProfileId"] = visualProfileId
        };
    }

    public static IReadOnlyDictionary<string, object?> Terminal(ScanTerminalSnapshot snapshot)
    {
        return new Dictionary<string, object?>
        {
            ["partial"] = snapshot.Partial,
            ["items"] = snapshot.Items,
            ["visited"] = snapshot.Visited,
            ["queued"] = snapshot.Queued,
            ["completed"] = snapshot.Completed,
            ["failed"] = snapshot.Failed,
            ["terminationCode"] = snapshot.TerminationCode
        };
    }

    public static IReadOnlyDictionary<string, object?> RowScroll(
        string direction,
        int tick,
        int maximumTicks,
        int sample,
        RowAdvanceDecision decision,
        int noMoveScore,
        int oneRowScore,
        int twoRowScore,
        int bestScore,
        int secondScore,
        int margin,
        int movedDistance,
        int frameDistance,
        string observationPhase,
        int settleSamples,
        double settleElapsedMilliseconds,
        string reason)
    {
        return new Dictionary<string, object?>
        {
            ["phase"] = "row_scroll",
            ["direction"] = direction,
            ["tick"] = tick,
            ["maximumTicks"] = maximumTicks,
            ["sample"] = sample,
            ["decision"] = decision.ToString(),
            ["noMoveScore"] = noMoveScore,
            ["oneRowScore"] = oneRowScore,
            ["twoRowScore"] = twoRowScore,
            ["bestScore"] = bestScore,
            ["secondScore"] = secondScore,
            ["margin"] = margin,
            ["movedDistance"] = movedDistance,
            ["frameDistance"] = frameDistance,
            ["observationPhase"] = observationPhase,
            ["settleSamples"] = settleSamples,
            ["settleElapsedMs"] = Math.Round(settleElapsedMilliseconds, 1),
            ["reason"] = reason
        };
    }

    public static IReadOnlyDictionary<string, object?>? FromException(Exception exception)
    {
        return exception is IScanDiagnosticException diagnostic ? diagnostic.DiagnosticDetails : null;
    }
}
