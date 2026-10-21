using System.Collections.Concurrent;
using System.Threading.Channels;
using ZZZScannerNext.Scanning;
using static ZZZScannerNext.Scanning.ScanController;

namespace ZZZScannerNext.RegressionTests;

internal static class GamepadPipelineChecks
{
    private static string DirectoryFor(string name)
    {
        var path = Path.Combine(Path.GetTempPath(), "zzz-production-gamepad-" + name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    public static Task OrderingAsync() => ResultsAsync(stopAtNon15: false);
    public static Task LevelStopAsync() => ResultsAsync(stopAtNon15: true);

    private static async Task ResultsAsync(bool stopAtNon15)
    {
        var path = DirectoryFor("results");
        using var queue = new BlockingCollection<OcrWorkResult>(2);
        using var linked = new CancellationTokenSource();
        using var log = new ScanLog(Path.Combine(path, "scan.log"));
        var results = new ConcurrentBag<DriveDiscExport>();
        var counters = new Counters();
        var orderedProgress = new List<int>();
        var options = new ScanOptions { StopAtNonLevel15 = stopAtNon15 };
        var consumer = Task.Run(() => ConsumeOcrResults(queue, results, counters, path,
            new InlineProgress(p => { if (p.Item is not null) orderedProgress.Add(p.Item.Index); }),
            log, new DuplicateGuard(2), options, linked));
        var kind = TargetVerificationKind.IdenticalNeighborRoundTrip;
        OcrWorkResult Good(int index, int level = 15) => OcrWorkResult.Success(index,
            new DriveDiscExport { Index = index, Name = "A", Level = level }, kind);
        // Completion order is deliberately different from capture order.
        queue.Add(Good(3));
        queue.Add(stopAtNon15 ? Good(2, 0) : OcrWorkResult.Failure(2, kind, "injected ordinary OCR error", "OCR failed"));
        queue.Add(Good(1));
        queue.Add(Good(4));
        queue.Add(Good(5));
        queue.CompleteAdding();
        await consumer.WaitAsync(TimeSpan.FromSeconds(5));
        if (stopAtNon15)
        {
            Require(linked.IsCancellationRequested && counters.StopAfterIndex == 2, "non-15 boundary cancels navigation");
            Require(orderedProgress.SequenceEqual(new[] { 1 }), "later out-of-order success is not exported past level stop");
        }
        else
        {
            Require(!linked.IsCancellationRequested && counters.Failed == 1 && counters.Completed == 4, "ordinary failure continues");
            Require(orderedProgress.SequenceEqual(new[] { 1, 3, 4, 5 }), "progress and results retain sequence across failed index");
            Require(results.Count == 4 && File.Exists(Path.Combine(path, "0002.error.txt")), "duplicates retained and error recorded");
        }
        Require(ResolveTraversalMode(new ScanOptions(), ScanProfileFile.Load().ResolveRequired(ScanOptions.DefaultProfileName)) == ScanTraversalMode.Gamepad,
            "default production entry uses controller navigation");
        var guard = new DuplicateGuard(1);
        foreach (var index in Enumerable.Range(1, 8))
            Require(guard.Observe(new DriveDiscExport { Name = "A" }, kind, out _), "original round-trip evidence permits adjacent identical items");
        var unverified = new DuplicateGuard(1);
        Require(unverified.Observe(new DriveDiscExport { Name = "A" }, TargetVerificationKind.ChangedText, out _), "first item accepted");
        Require(!unverified.Observe(new DriveDiscExport { Name = "A" }, TargetVerificationKind.ChangedText, out _), "controller does not bypass the original duplicate guard");
    }

    public static async Task NavigationAsync()
    {
        var inventory = new ZZZ.GamepadProbe.GridChecks.SimulatedInventory();
        using var sink = new QueueSink(4, stopAfter: 2);
        var result = await Navigator(inventory, sink, "overlap").RunAsync(default);
        Require(result.Outcome == "RequestedLimitReached" && inventory.Presses == 1 && sink.Queued == 2,
            "second position is reached while both OCR frames are still queued");
        using var first = await sink.Frames.Reader.ReadAsync();
        Require(first.GetPixel(0, 0).A == 255, "queued bitmap survives disposal of navigation frame");

        inventory = new ZZZ.GamepadProbe.GridChecks.SimulatedInventory();
        using var blocked = new QueueSink(1);
        using var cancellation = new CancellationTokenSource();
        var active = Navigator(inventory, blocked, "cancel").RunAsync(cancellation.Token);
        await blocked.SecondVisit.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(inventory.Presses == 1 && blocked.Queued == 1, "bounded queue holds navigation at second position");
        cancellation.Cancel();
        result = await active.WaitAsync(TimeSpan.FromSeconds(5));
        Require(result.Outcome == "Canceled" && inventory.Presses == 1 && blocked.Queued == 1,
            "cancellation during backpressure sends no further direction and commits no pending frame");

        GridTraversal Navigator(ZZZ.GamepadProbe.GridChecks.SimulatedInventory sim, QueueSink target, string name) =>
            new(sim, sim, () => { }, _ => { }, DirectoryFor(name),
                new TraversalOptions(GridVision.DefaultArea, NavigationInput.DPad, 20, 100, 9, PollMs: 20), sink: target);
    }

    private sealed class QueueSink(int capacity, int stopAfter = 0) : ITraversalSink, IDisposable
    {
        public Channel<Bitmap> Frames { get; } = Channel.CreateBounded<Bitmap>(capacity);
        public TaskCompletionSource SecondVisit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Queued { get; private set; }
        public TraversalDetails ObserveDetails(Bitmap frame) => new(new byte[12], 12);
        public async ValueTask<bool> VisitAsync(Bitmap frame, GridObservation grid, int position, CancellationToken token)
        {
            var owned = (Bitmap)frame.Clone();
            var committed = false;
            try
            {
                if (position == 2) SecondVisit.TrySetResult();
                await Frames.Writer.WriteAsync(owned, token);
                committed = true;
                Queued++;
                return stopAfter == 0 || Queued < stopAfter;
            }
            finally { if (!committed) owned.Dispose(); }
        }
        public void Dispose() { while (Frames.Reader.TryRead(out var image)) image.Dispose(); }
    }

    private sealed class InlineProgress(Action<ScanProgress> report) : IProgress<ScanProgress>
    {
        public void Report(ScanProgress value) => report(value);
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
