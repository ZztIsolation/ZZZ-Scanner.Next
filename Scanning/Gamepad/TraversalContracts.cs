using ZZZScannerNext.Ocr;

namespace ZZZScannerNext.Scanning.Gamepad;

internal interface IClientCapture { Bitmap Capture(); }

// The production sink owns only capture validation and enqueueing. OCR runs in
// ScanController's existing workers; the navigator never waits for recognition.
internal interface ITraversalSink
{
    void ValidateStart(GridObservation grid) { }
    TraversalDetails ObserveDetails(Bitmap frame);
    ValueTask<bool> VisitAsync(Bitmap frame, GridObservation grid, int position, CancellationToken token);
}

internal sealed record TraversalDetails(byte[] Signature, int LayoutKey);

// Serial recognition remains available to the independent benchmark tool.
internal sealed record ItemRead(int Index, string Rarity, OcrResult[] Raw, DriveDiscExport? Item,
    string? Error, double CropMs, PaddleOcrRecognizer.OcrBatchDiagnostics Ocr, double CleanMs, double TotalMs, string[] Warnings);

internal interface IItemReader
{
    byte[] Signature(Bitmap frame);
    Task<ItemRead> ReadAsync(Bitmap frame, GridObservation grid, int index, string directory, bool savePanel, CancellationToken token);
}
