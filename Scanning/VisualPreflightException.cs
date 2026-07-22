namespace ZZZScannerNext.Scanning;

public sealed class VisualPreflightException : InvalidOperationException, IScannerFailureException
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
    public string Code => Reason switch
    {
        "capture_unavailable" => "capture_failed",
        "inventory_screen_unreadable" => "inventory_screen_unreadable",
        "unsupported_display_layout" => "unsupported_display_layout",
        _ => "inventory_screen_not_detected"
    };
    public string Title => Code switch
    {
        "capture_failed" => "无法读取游戏画面",
        "inventory_screen_unreadable" => "驱动盘仓库画面无法确认",
        "unsupported_display_layout" => "当前显示布局不受支持",
        _ => "未识别到驱动盘仓库"
    };
    public string Remedy => Code switch
    {
        "capture_failed" => "请保持游戏可见且未最小化，并关闭遮挡窗口后重试。",
        "inventory_screen_unreadable" => "已检测到部分仓库证据，但标题和布局无法同时确认；请关闭遮挡并保持仓库界面完整可见。",
        "unsupported_display_layout" => "请切换到受支持的 16:9 分辨率后重试。",
        _ => "请确认游戏为简体中文并打开背包中的驱动盘页面后重试。"
    };
    public bool Retryable => true;
    public IReadOnlyDictionary<string, object?> DiagnosticDetails { get; }

    internal static VisualPreflightException Create(
        string reason,
        string message,
        ChromaticProbeResult? anchor,
        bool headerDetected,
        int headerScore,
        int gridStructureScore,
        int layoutScore,
        bool inventoryCountDetected,
        int countConsensusFrames,
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
                gridStructureScore,
                headerDetected,
                headerScore,
                gridStructureScore,
                layoutScore,
                inventoryCountDetected,
                countConsensusFrames,
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
    bool HeaderDetected,
    int HeaderScore,
    int GridStructureScore,
    int LayoutScore,
    int? InventoryCount,
    int? InventoryCapacity,
    int CountConsensusFrames,
    WarehouseMonitorPlan MonitorPlan);

public sealed class VisualPreflightGate
{
    private readonly int _requiredStableFrames;

    public VisualPreflightGate(int requiredStableFrames = 2)
    {
        _requiredStableFrames = Math.Max(1, requiredStableFrames);
    }

    public int StableFrames { get; private set; }
    public bool Accepted { get; private set; }

    public bool Observe(bool captureHealthy, bool headerDetected, bool gridPassed, bool layoutPassed)
    {
        if (Accepted)
        {
            return true;
        }

        StableFrames = captureHealthy && headerDetected && (gridPassed || layoutPassed)
            ? StableFrames + 1
            : 0;
        Accepted = StableFrames >= _requiredStableFrames;
        return Accepted;
    }
}

internal sealed class ScanSessionDiagnosticException : InvalidOperationException, IScannerFailureException
{
    public ScanSessionDiagnosticException(
        Exception innerException,
        ScanSessionDiagnostics diagnostics)
        : base(innerException.Message, innerException)
    {
        var scannerFailure = innerException as IScannerFailureException;
        Code = scannerFailure?.Code ?? "scan_failed";
        Title = scannerFailure?.Title ?? "扫描失败";
        Remedy = scannerFailure?.Remedy ?? "请重试；如果问题持续，请打开日志。";
        Retryable = scannerFailure?.Retryable ?? true;
        var original = ScanDiagnosticDetails.FromException(innerException)
            ?? new Dictionary<string, object?>();
        DiagnosticDetails = ScanDiagnosticDetails.Merge(original, ScanDiagnosticDetails.Session(diagnostics));
    }

    public IReadOnlyDictionary<string, object?> DiagnosticDetails { get; }
    public string Code { get; }
    public string Title { get; }
    public string Remedy { get; }
    public bool Retryable { get; }
}
