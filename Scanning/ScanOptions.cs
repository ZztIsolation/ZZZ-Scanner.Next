namespace ZZZScannerNext.Scanning;

public enum ScanTraversalMode
{
    FromProfile,
    OverlapSignaturePage,
    SafeBandViewport,
    CalibratedPage,
    LegacyThirdRow
}

public enum CaptureMode
{
    Gdi,
    Dxgi
}

public enum PanelStabilityMode
{
    Panel,
    TextCore,
    Auto
}

public enum ScrollAcceptMode
{
    Safe,
    EarlyOneRow
}

public enum PanelAcceptMode
{
    Safe,
    AdaptiveEarlyFullRoi
}

public enum PostScrollPanelAcceptMode
{
    Safe,
    AdaptiveAfterScroll
}

public enum PanelFloorMode
{
    Static,
    SceneAdaptive
}

public enum OverlapConflictMode
{
    Strict,
    Recheck,
    Recover
}

public enum VisualProfileClientKind
{
    Auto,
    Local,
    Cloud,
    Unknown
}

public enum ProfileRoutingMode
{
    Strict,
    Family,
    Compatible,
    Auto
}

public static class ScanModeDefaults
{
    public static ScrollAcceptMode ScrollAccept(bool fastMode) =>
        fastMode ? ScrollAcceptMode.EarlyOneRow : ScrollAcceptMode.Safe;

    public static PanelAcceptMode PanelAccept(bool fastMode) =>
        fastMode ? PanelAcceptMode.AdaptiveEarlyFullRoi : PanelAcceptMode.Safe;

    public static OverlapConflictMode OverlapConflict(bool fastMode) =>
        fastMode ? OverlapConflictMode.Recover : OverlapConflictMode.Recheck;
}

public sealed class ScanOptions
{
    public const string DefaultProfileName = "ZZZ背包驱动盘-16比9";
    public const string FastProfileName = "ZZZ背包驱动盘-16比9-fast";

    public string ProcessName { get; set; } = "ZenlessZoneZero";
    public string ProfileName { get; set; } = DefaultProfileName;
    public ScanTraversalMode TraversalMode { get; set; } = ScanTraversalMode.FromProfile;
    public int MaxItems { get; set; } = 0;
    public HashSet<string> Rarities { get; } = new(StringComparer.OrdinalIgnoreCase) { "S" };
    public bool BringToFront { get; set; } = true;
    public bool ShowDebugImages { get; set; }
    public bool StopAtNonLevel15 { get; set; } = true;
    public bool HighSpeedOcr { get; set; } = true;
    public bool OcrShadowDataset { get; set; }
    public bool FastOcrShadow { get; set; }
    public bool FastOcrAssist { get; set; }
    public bool FastMode { get; set; }
    public bool? AdaptiveTiming { get; set; }
    public CaptureMode CaptureMode { get; set; } = CaptureMode.Gdi;
    public PanelStabilityMode PanelStabilityMode { get; set; } = PanelStabilityMode.Panel;
    public ScrollAcceptMode ScrollAcceptMode { get; set; } = ScrollAcceptMode.Safe;
    public PanelAcceptMode PanelAcceptMode { get; set; } = PanelAcceptMode.Safe;
    public PostScrollPanelAcceptMode PostScrollPanelAcceptMode { get; set; } = PostScrollPanelAcceptMode.Safe;
    public PanelFloorMode PanelFloorMode { get; set; } = PanelFloorMode.Static;
    public int PanelMinAcceptFloorMs { get; set; } = 120;
    public int SameRowPanelMinAcceptFloorMs { get; set; } = 105;
    public int PostScrollPanelMinAcceptFloorMs { get; set; } = 110;
    public int ScrollTickDelayOverrideMs { get; set; }
    public OverlapConflictMode OverlapConflictMode { get; set; } = OverlapConflictMode.Recheck;
    public string FastOcrTemplateIndexFile { get; set; } = "";
    public string VisualProfileId { get; set; } = "auto";
    public string VisualQualityLabel { get; set; } = "current";
    public VisualProfileClientKind VisualProfileClient { get; set; } = VisualProfileClientKind.Auto;
    public bool CollectVisualProfile { get; set; }
    public ProfileRoutingMode ProfileRouting { get; set; } = ProfileRoutingMode.Strict;
    public int OcrBatchSize { get; set; } = 1;
    public int OcrWorkerCount { get; set; } = 0;
    public int OcrQueueCapacity { get; set; } = 48;
    public int OcrIntraOpThreads { get; set; } = 3;
}

public sealed class ScanProgress
{
    public string Message { get; init; } = "";
    public DriveDiscExport? Item { get; init; }
    public Bitmap? DebugImage { get; init; }
    public int Visited { get; init; }
    public int Queued { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }
}

public sealed class ScanSessionResult
{
    public string OutputDirectory { get; init; } = "";
    public string ExportFile { get; init; } = "";
    public List<DriveDiscExport> Items { get; init; } = new();
    public int Visited { get; init; }
    public int Queued { get; init; }
    public int Completed { get; init; }
    public int Failed { get; init; }
}

public sealed class ScanRunCommand
{
    public string ProcessName { get; set; } = "ZenlessZoneZero";
    public string ProfileName { get; set; } = ScanOptions.DefaultProfileName;
    public ScanTraversalMode TraversalMode { get; set; } = ScanTraversalMode.FromProfile;
    public int MaxItems { get; set; } = 120;
    public bool BringToFront { get; set; } = true;
    public bool StopAtNonLevel15 { get; set; } = true;
    public bool HighSpeedOcr { get; set; } = true;
    public bool OcrShadowDataset { get; set; }
    public bool FastOcrShadow { get; set; }
    public bool FastOcrAssist { get; set; }
    public bool FastMode { get; set; }
    public bool? AdaptiveTiming { get; set; }
    public CaptureMode CaptureMode { get; set; } = CaptureMode.Gdi;
    public PanelStabilityMode PanelStabilityMode { get; set; } = PanelStabilityMode.Panel;
    public ScrollAcceptMode ScrollAcceptMode { get; set; } = ScrollAcceptMode.Safe;
    public PanelAcceptMode PanelAcceptMode { get; set; } = PanelAcceptMode.Safe;
    public PostScrollPanelAcceptMode PostScrollPanelAcceptMode { get; set; } = PostScrollPanelAcceptMode.Safe;
    public PanelFloorMode PanelFloorMode { get; set; } = PanelFloorMode.Static;
    public int PanelMinAcceptFloorMs { get; set; } = 120;
    public int SameRowPanelMinAcceptFloorMs { get; set; } = 105;
    public int PostScrollPanelMinAcceptFloorMs { get; set; } = 110;
    public int ScrollTickDelayOverrideMs { get; set; }
    public OverlapConflictMode OverlapConflictMode { get; set; } = OverlapConflictMode.Recheck;
    public string FastOcrTemplateIndexFile { get; set; } = "";
    public string VisualProfileId { get; set; } = "auto";
    public string VisualQualityLabel { get; set; } = "current";
    public VisualProfileClientKind VisualProfileClient { get; set; } = VisualProfileClientKind.Auto;
    public bool CollectVisualProfile { get; set; }
    public ProfileRoutingMode ProfileRouting { get; set; } = ProfileRoutingMode.Strict;
    public int OcrBatchSize { get; set; } = 1;
    public int OcrWorkerCount { get; set; } = 0;
    public int OcrQueueCapacity { get; set; } = 48;
    public int OcrIntraOpThreads { get; set; } = 3;
    public string[] Rarities { get; set; } = ["S"];
}

public sealed class ScanRunResult
{
    public bool Success { get; set; }
    public string Status { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string ExportFile { get; set; } = "";
    public int Items { get; set; }
    public int Visited { get; set; }
    public int Queued { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public string Error { get; set; } = "";
}
