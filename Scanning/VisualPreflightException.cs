namespace ZZZScannerNext.Scanning;

public sealed class VisualPreflightException : InvalidOperationException, IScanDiagnosticException
{
    private VisualPreflightException(
        string reason,
        string message,
        IReadOnlyDictionary<string, object?> diagnosticDetails)
        : base(message)
    {
        Reason = reason;
        DiagnosticDetails = diagnosticDetails;
    }

    public string Reason { get; }
    public IReadOnlyDictionary<string, object?> DiagnosticDetails { get; }

    internal static VisualPreflightException Create(
        string reason,
        string message,
        ChromaticProbeResult? anchor,
        int gridScore,
        bool inventoryCountDetected,
        int stableFrames,
        int requiredStableFrames,
        GameWindow window,
        string visualProfileId)
    {
        anchor ??= new ChromaticProbeResult(false, 0, 0, 0, 0, 0, 180, 100, 100, System.Drawing.Color.Empty, VisualTransformClass.Unknown, 0, 0);
        return new VisualPreflightException(
            reason,
            message,
            ScanDiagnosticDetails.Preflight(
                reason,
                VisualProbeEvaluator.TransformClassName(anchor.TransformClass),
                anchor.Score,
                gridScore,
                inventoryCountDetected,
                anchor.HueDelta,
                anchor.SaturationDeltaPercent,
                anchor.ValueDeltaPercent,
                stableFrames,
                requiredStableFrames,
                window.ClientScreenRect.Width,
                window.ClientScreenRect.Height,
                window.Dpi,
                window.ActiveCaptureMode,
                visualProfileId));
    }
}

internal readonly record struct VisualPreflightResult(
    ChromaticProbeResult Anchor,
    int GridScore,
    int? InventoryCount);

public sealed class VisualPreflightGate
{
    private readonly int _requiredSignals;
    private readonly int _requiredStableFrames;

    public VisualPreflightGate(int requiredSignals = 2, int requiredStableFrames = 2)
    {
        _requiredSignals = Math.Clamp(requiredSignals, 1, 3);
        _requiredStableFrames = Math.Max(1, requiredStableFrames);
    }

    public int StableFrames { get; private set; }
    public bool Accepted { get; private set; }

    public bool Observe(bool anchorPassed, bool inventoryCountDetected, bool gridPassed)
    {
        if (Accepted)
        {
            return true;
        }

        var passedSignals = (anchorPassed ? 1 : 0)
            + (inventoryCountDetected ? 1 : 0)
            + (gridPassed ? 1 : 0);
        StableFrames = passedSignals >= _requiredSignals ? StableFrames + 1 : 0;
        Accepted = StableFrames >= _requiredStableFrames;
        return Accepted;
    }
}

internal sealed class ScanSessionDiagnosticException : InvalidOperationException, IScanDiagnosticException
{
    public ScanSessionDiagnosticException(
        Exception innerException,
        ScanSessionDiagnostics diagnostics)
        : base(innerException.Message, innerException)
    {
        var original = ScanDiagnosticDetails.FromException(innerException)
            ?? new Dictionary<string, object?>();
        DiagnosticDetails = ScanDiagnosticDetails.Merge(original, ScanDiagnosticDetails.Session(diagnostics));
    }

    public IReadOnlyDictionary<string, object?> DiagnosticDetails { get; }
}
