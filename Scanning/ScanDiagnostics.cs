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
    public bool InventoryCountDetected { get; init; }
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
            ["inventoryCountDetected"] = diagnostics.InventoryCountDetected,
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
        bool inventoryCountDetected,
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
            ["inventoryCountDetected"] = inventoryCountDetected,
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

    public static IReadOnlyDictionary<string, object?>? FromException(Exception exception)
    {
        return exception is IScanDiagnosticException diagnostic ? diagnostic.DiagnosticDetails : null;
    }
}
