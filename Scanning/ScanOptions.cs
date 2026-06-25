namespace ZZZScannerNext.Scanning;

public enum ScanTraversalMode
{
    FromProfile,
    SafeBandViewport,
    CalibratedPage,
    LegacyThirdRow
}

public sealed class ScanOptions
{
    public string ProcessName { get; set; } = "ZenlessZoneZero";
    public string ProfileName { get; set; } = "ZZZ背包驱动盘-16比9";
    public ScanTraversalMode TraversalMode { get; set; } = ScanTraversalMode.FromProfile;
    public int MaxItems { get; set; } = 0;
    public HashSet<string> Rarities { get; } = new(StringComparer.OrdinalIgnoreCase) { "S" };
    public bool BringToFront { get; set; } = true;
    public bool ShowDebugImages { get; set; }
    public bool StopAtNonLevel15 { get; set; } = true;
    public bool HighSpeedOcr { get; set; } = true;
    public int OcrBatchSize { get; set; } = 8;
    public int OcrWorkerCount { get; set; } = 0;
    public int OcrQueueCapacity { get; set; } = 16;
    public int OcrIntraOpThreads { get; set; } = 4;
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
    public int Failed { get; init; }
}
