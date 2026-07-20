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
}

internal static class ScanDiagnosticDetails
{
    public static IReadOnlyDictionary<string, object?> PanelCapture(
        int? logicalRow,
        int visualRow,
        int column,
        int maxColumns,
        int visibleRois,
        int totalRois,
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
