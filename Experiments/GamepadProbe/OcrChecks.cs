using System.Text;
using ZZZScannerNext.Ocr;
using ZZZScannerNext.Scanning;

namespace ZZZ.GamepadProbe;

internal static class OcrChecks
{
    public static async Task<string> RunAsync()
    {
        var report = new StringBuilder();
        var a = new byte[120]; var b = new byte[120]; b[0] = 120;
        Check(ItemOcr.SignatureDifference(a, b) == 12, "a changing OCR field is not diluted by other fields");
        Check(OcrBenchmark.Differences(["same", "same"], ["same", "same"]).Length == 0,
            "equal adjacent OCR items retain separate ordered positions");
        Check(OcrBenchmark.Differences(["a", "b"], ["b", "a"]).SequenceEqual(new[] { 1, 2 }),
            "benchmark detects reordered OCR results");
        Check(OcrBenchmark.Differences(["a", "b"], ["a"]).SequenceEqual(new[] { 2 }), "benchmark detects missing OCR positions");
        var root = Path.Combine(Path.GetTempPath(), "zzz-ocr-check-" + Guid.NewGuid().ToString("N"));
        using var canceled = new CancellationTokenSource();
        var inventory = new GridChecks.SimulatedInventory();
        var gatedReader = new FakeReader(wait: true);
        var active = Traversal(inventory, gatedReader, "cancel").RunAsync(canceled.Token);
        await gatedReader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(inventory.Presses == 0, "navigation waits while OCR is in progress");
        canceled.Cancel();
        var canceledResult = await active;
        Check(canceledResult.Outcome == "Canceled" && canceledResult.OcrItems == 0 && inventory.Presses == 0,
            "cancel during OCR commits no result and sends no next direction");
        inventory = new GridChecks.SimulatedInventory();
        var failed = await Traversal(inventory, new FakeReader(fail: true), "failure").RunAsync(default);
        Check(failed.Outcome == "StoppedAmbiguous" && failed.OcrItems == 0 && inventory.Presses == 0,
            "OCR validation failure stops before the next direction");
        inventory = new GridChecks.SimulatedInventory();
        var successful = await Traversal(inventory, new FakeReader(warn: true), "success").RunAsync(default);
        Check(successful.Outcome == "ProbeLimitReached" && successful.VisitedItems == 7 && successful.OcrItems == 7 && successful.OcrReviewItems == 7,
            "one OCR commit and review count per position; row wrap is not read twice");
        return report.ToString();

        GridTraversal Traversal(GridChecks.SimulatedInventory simulator, IItemReader reader, string name) =>
            new(simulator, simulator, () => { }, _ => { }, Path.Combine(root, name),
                new TraversalOptions(GridVision.DefaultArea, NavigationInput.DPad, 20, 100, 0, RowLimit: 1, PollMs: 20), reader);
        void Check(bool valid, string name)
        {
            if (!valid) throw new Exception("FAIL " + name);
            report.AppendLine("PASS " + name);
        }
    }

    private sealed class FakeReader(bool wait = false, bool fail = false, bool warn = false) : IItemReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Signature(Bitmap frame) => new byte[12];
        public async Task<ItemRead> ReadAsync(Bitmap frame, GridObservation grid, int index, string directory, bool savePanel, CancellationToken token)
        {
            Entered.TrySetResult();
            if (wait) await Task.Delay(Timeout.Infinite, token);
            return new ItemRead(index, "S", [], fail ? null : new DriveDiscExport { Index = index, Name = "same" },
                fail ? "injected OCR failure" : null, 0, new PaddleOcrRecognizer.OcrBatchDiagnostics(12, 1, 1, 0, 0, 0, 0), 0, 0,
                warn ? ["review name mapping"] : []);
        }
    }
}
