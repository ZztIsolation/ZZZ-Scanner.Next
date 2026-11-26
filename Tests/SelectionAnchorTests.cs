using System.Drawing;
using System.Globalization;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.RegressionTests;

internal static partial class Program
{
    private static Task TestSelectionAnchorSyntheticAsync()
    {
        var geometry = new SelectionAnchorGeometry(new PointF(166, 267), new PointF(135, 175), 1, 1, 4, 9);
        var policy = new SelectionAnchorPolicy();
        using var image = new Bitmap(1920, 1080);
        using var graphics = Graphics.FromImage(image);
        void Frame(int row, int col, Color color, int dy = 0)
        {
            var c = geometry.Center(row, col);
            using var pen = new Pen(color, 8);
            graphics.DrawRectangle(pen, c.X - 64, c.Y - 50 + dy, 128, 118);
        }
        foreach (var color in new[] { Color.FromArgb(237, 215, 0), Color.FromArgb(150, 189, 0), Color.Yellow })
        {
            graphics.Clear(Color.FromArgb(20, 20, 20));
            Frame(3, 9, color);
            var found = SelectionAnchorDetector.FindUnique(image, geometry, policy);
            AssertEqual(SelectionAnchorVerdict.Unique, found.Verdict);
            AssertEqual(3, found.Row!.Value);
            AssertEqual(9, found.Column!.Value);
            AssertEqual(SelectionAnchorVerdict.None, SelectionAnchorDetector.FindUnique(image, geometry, policy, 8).Verdict);
            // Occluded top corners do not remove the middle portions of the two vertical edges.
            graphics.FillRectangle(Brushes.Gray, geometry.Center(3, 9).X - 76, geometry.Center(3, 9).Y - 75, 160, 45);
            AssertEqual(SelectionAnchorVerdict.Unique, SelectionAnchorDetector.FindUnique(image, geometry, policy).Verdict);
            Frame(2, 1, color);
            AssertEqual(SelectionAnchorVerdict.Multiple, SelectionAnchorDetector.FindUnique(image, geometry, policy).Verdict);
        }
        graphics.Clear(Color.Black);
        for (var row = 1; row <= 4; row++) for (var col = 1; col <= 9; col++)
        {
            var c = geometry.Center(row, col);
            graphics.FillRectangle(Brushes.Yellow, c.X - 60, c.Y + 51, 120, 10);
        }
        AssertEqual(SelectionAnchorVerdict.None, SelectionAnchorDetector.FindUnique(image, geometry, policy).Verdict);
        foreach (var shift in new[] { 44, 88, 131 })
        {
            graphics.Clear(Color.Black);
            Frame(2, 5, Color.Yellow, shift);
            AssertTrue(!SelectionAnchorDetector.FindUnique(image, geometry, policy).Unique, $"mid-scroll {shift} must not select a row");
        }
        return Task.CompletedTask;
    }

    private static string AnchorFixture(string file) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SelectionAnchor", file + ".png");

    private static Task TestSelectionAnchorFixturesAsync()
    {
        var profile = ScanProfileFile.Load().ResolveRequired(ScanOptions.DefaultProfileName);
        var policy = new SelectionAnchorPolicy();
        foreach (var (file, row, col) in new[] { ("selected-r3-c9", 3, 9), ("hover-other-cell", 3, 9),
            ("selected-and-hovered", 3, 1), ("wheel-after", 2, 9), ("click-r4-after", 4, 1) })
        {
            using var image = new Bitmap(AnchorFixture(file));
            var geometry = SelectionAnchorGeometry.FromProfile(profile, new Rectangle(0, 0, 1920, 1080), new Rectangle(80, 192, image.Width, image.Height));
            var result = SelectionAnchorDetector.FindUnique(image, geometry, policy);
            AssertTrue(result.Unique && result.Row == row && result.Column == col, file);
            foreach (var size in new[] { new Size(1600, 900), new Size(1280, 720) })
            {
                var sx = size.Width / 1920.0;
                var sy = size.Height / 1080.0;
                using var scaled = new Bitmap(image, new Size((int)Math.Round(image.Width * sx), (int)Math.Round(image.Height * sy)));
                var scaledGeometry = SelectionAnchorGeometry.FromProfile(profile, new Rectangle(Point.Empty, size),
                    new Rectangle((int)Math.Round(80 * sx), (int)Math.Round(192 * sy), scaled.Width, scaled.Height));
                var detected = SelectionAnchorDetector.FindUnique(scaled, scaledGeometry, policy);
                AssertTrue(detected.Unique && detected.Row == row && detected.Column == col, $"{file} {size}");
            }
        }
        using var original = new Bitmap(AnchorFixture("selected-r3-c9"));
        var originalGeometry = SelectionAnchorGeometry.FromProfile(profile, new Rectangle(0, 0, 1920, 1080), new Rectangle(80, 192, original.Width, original.Height));
        var transforms = new[]
        {
            new FixtureTransform("hdr", 1, 1, 1, 1, 1, 0, true),
            new FixtureTransform("blue85", 1, .85, 1, 1, 1, 0, false), new FixtureTransform("blue75", 1, .75, 1, 1, 1, 0, false),
            new FixtureTransform("red", 1.1, 1, 1, 1, 1, 0, false),
            new FixtureTransform("sat80", 1, 1, .8, 1, 1, 0, false), new FixtureTransform("sat125", 1, 1, 1.25, 1, 1, 0, false),
            new FixtureTransform("gamma85", 1, 1, 1, .85, 1, 0, false), new FixtureTransform("gamma115", 1, 1, 1, 1.15, 1, 0, false),
            new FixtureTransform("contrast90", 1, 1, 1, 1, .9, 0, false), new FixtureTransform("contrast110", 1, 1, 1, 1, 1.1, 0, false),
            new FixtureTransform("dark", 1, 1, 1, 1, 1, -8, false), new FixtureTransform("bright", 1, 1, 1, 1, 1, 8, false),
            new FixtureTransform("warm", 1.08, .8, .9, .92, 1, 4, false), new FixtureTransform("vivid", 1, 1, 1.2, .88, 1.08, 4, true)
        };
        foreach (var transform in transforms)
        {
            using var image = ApplyFixtureTransform(original, transform);
            var detected = SelectionAnchorDetector.FindUnique(image, originalGeometry, policy);
            AssertTrue(detected.Unique && detected.Row == 3 && detected.Column == 9, transform.ToString());
        }
        return Task.CompletedTask;
    }

    private static Task TestSelectionAnchorPitchAsync()
    {
        var profile = ScanProfileFile.Load().ResolveRequired(ScanOptions.DefaultProfileName);
        foreach (var size in new[] { new Size(1920, 1080), new Size(1600, 900) })
        {
        var prefix = size.Width == 1920 ? "run-002" : "900p-run-005";
        using var before = new BitmapCapturedFrame(new Bitmap(AnchorFixture(prefix + "-before")), "fixture");
        using var after = new BitmapCapturedFrame(new Bitmap(AnchorFixture(prefix + "-after")), "fixture");
        var geometry = SelectionAnchorGeometry.FromProfile(profile, new Rectangle(Point.Empty, size),
            new Rectangle((int)Math.Round(96 * size.Width / 1920.0), (int)Math.Round(208 * size.Height / 1080.0), before.Width, before.Height));
        var rowsBefore = ScanController.CaptureAnchorRowSignatures(before, geometry, 0, before.Width);
        var rowsAfter = ScanController.CaptureAnchorRowSignatures(after, geometry, 0, after.Width);
        var oneRow = Enumerable.Range(0, 3).Average(i => RowVisualSignatureExtractor.Distance(rowsBefore[i + 1], rowsAfter[i]));
        var noMove = Enumerable.Range(0, 4).Average(i => RowVisualSignatureExtractor.Distance(rowsBefore[i], rowsAfter[i]));
        var twoRows = Enumerable.Range(0, 2).Average(i => RowVisualSignatureExtractor.Distance(rowsBefore[i + 2], rowsAfter[i]));
        var evidence = RowAdvanceEvaluator.Evaluate((int)noMove, (int)oneRow, (int)twoRows, 20);
        AssertTrue(!(evidence.Strong && evidence.Decision is RowAdvanceDecision.NoMove or RowAdvanceDecision.TwoRows),
            $"a real one-row move must not become a strong conflicting decision: {evidence}");
        }
        return Task.CompletedTask;
    }

    private static Task TestSelectionAnchorPolicyAsync()
    {
        var ambiguous = RowAdvanceEvaluator.Evaluate(20, 21, 22, 30);
        SelectionAnchorObservation At(int row) => new(SelectionAnchorVerdict.Unique, row, 9, 100, 0, [], 0);
        AssertEqual(RowAdvanceDecision.OneRow, SelectionAnchorAdvancePolicy.Reconcile(ambiguous, At(2), 3).Decision);
        AssertEqual(RowAdvanceDecision.TwoRows, SelectionAnchorAdvancePolicy.Reconcile(ambiguous, At(1), 3).Decision);
        AssertEqual(RowAdvanceDecision.Ambiguous, SelectionAnchorAdvancePolicy.Reconcile(ambiguous, At(4), 3).Decision);
        AssertEqual(RowAdvanceDecision.Ambiguous, SelectionAnchorAdvancePolicy.Reconcile(RowAdvanceEvaluator.Evaluate(50, 60, 0, 40), At(2), 3).Decision);
        var zero = SelectionAnchorAdvancePolicy.Reconcile(RowAdvanceEvaluator.Evaluate(0, 60, 80, 0), At(3), 3);
        AssertTrue(new RowScrollObservation(zero, 0, null, 0).ConfirmedZeroMovement);
        AssertTrue(!new RowScrollObservation(zero, 0, null, null).ConfirmedZeroMovement);
        var profile = new ScanProfile { SelectionAnchorMode = "verify" };
        var options = new ScanOptions { SelectionAnchorMode = SelectionAnchorMode.Strict };
        AssertEqual(SelectionAnchorMode.Off, SelectionAnchorModeResolver.Resolve(options, profile, "off").Mode);
        AssertEqual("env", SelectionAnchorModeResolver.Resolve(options, profile, "off").Source);
        AssertEqual(SelectionAnchorMode.Strict, SelectionAnchorModeResolver.Resolve(options, profile, null).Mode);
        options.SelectionAnchorMode = null;
        AssertEqual(SelectionAnchorMode.Verify, SelectionAnchorModeResolver.Resolve(options, profile, null).Mode);
        profile.VisualProbes.SelectionAnchor.MinimumScore = 10;
        AssertEqual(SelectionAnchorMode.Off, SelectionAnchorModeResolver.Resolve(options, profile, null).Mode);
        return Task.CompletedTask;
    }

    private static Task TestSelectionAnchorBenchmarkAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "zzz-anchor-benchmark", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "export.json"), "[]");
            int Run(string mode, params string[] extra)
            {
                var lines = new List<string>
                {
                    "[2026-11-25T00:00:00Z] Start scan. RowAdvanceMode=WheelVerified, MaxItems=200, OcrWorkers=1, OcrBatchSize=1, OcrQueueCapacity=4, OcrIntraOpThreads=1",
                    $"[2026-11-25T00:00:00Z] SelectionAnchorMode={mode}",
                    "[2026-11-25T00:00:00Z] Traversal: overlap-signature-page. totalRows=110",
                    "[2026-11-25T00:00:01Z] EVENT #1 ROW_SCROLL_DONE: rowsAdvanced=1, decision=OneRow",
                    "[2026-11-25T00:00:01Z] EVENT #2 OVERLAP_SCROLL_ACCEPTED: rowsAdvanced=1, decision=OneRow",
                    "[2026-11-25T00:00:02Z] EVENT #3 SCAN_TERMINAL: visited=0, queued=0, completed=0, failed=0, partial=False, terminationCode=, exportFile=export.json"
                };
                lines.AddRange(extra.Select((e, i) => $"[2026-11-25T00:00:01Z] EVENT #{i + 4} {e}"));
                File.WriteAllLines(Path.Combine(directory, "scan.log"), lines);
                var output = Console.Out;
                using var sink = new StringWriter(CultureInfo.InvariantCulture);
                try { Console.SetOut(sink); return ScanBenchmark.Run(directory, null); }
                finally { Console.SetOut(output); }
            }
            const string accepted = "WHEEL_ANCHOR_DECISION: rowsAdvanced=1, source=anchor";
            AssertEqual(0, Run("Off"));
            AssertEqual(1, Run("Verify")); // Missing evidence must change the process exit code.
            AssertEqual(0, Run("Verify", accepted));
            AssertEqual(1, Run("Verify", accepted, accepted));
            AssertEqual(1, Run("Verify", accepted, "WHEEL_ANCHOR_CONFLICT: reason=fixture"));
            AssertEqual(0, Run("Verify", accepted, "WHEEL_ANCHOR_FALLBACK: reason=fixture"));
            AssertEqual(1, Run("Strict", accepted, "WHEEL_ANCHOR_FALLBACK: reason=fixture"));
            AssertEqual(1, Run("Shadow", "WHEEL_ANCHOR_SHADOW: agreement=false"));
            AssertEqual(0, Run("Shadow", "WHEEL_ANCHOR_SHADOW: agreement=na, wouldRescue=True"));
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    private static Task TestSelectionAnchorReleaseAsync()
    {
        var tracker = new SelectionAnchorReleaseTracker();
        var selected = new SelectionAnchorObservation(SelectionAnchorVerdict.Unique, 2, 9, 100, 0, [], 0);
        var evidence = RowAdvanceEvaluator.Evaluate(70, 1, 80, 30);
        tracker.Observe(selected, evidence, false);
        AssertEqual(0, tracker.StableFrames);
        tracker.Observe(selected, evidence, true);
        AssertEqual(1, tracker.StableFrames);
        tracker.Observe(selected with { Row = 1 }, evidence, true);
        AssertEqual(1, tracker.StableFrames);
        tracker.Observe(selected with { Row = 1 }, evidence, true);
        AssertEqual(2, tracker.StableFrames);
        tracker.Observe(selected, evidence with { Decision = RowAdvanceDecision.Ambiguous }, true);
        AssertEqual(0, tracker.StableFrames);
        tracker.Observe(selected with { Verdict = SelectionAnchorVerdict.Multiple }, evidence, true);
        AssertEqual(0, tracker.StableFrames);
        AssertTrue(!SelectionAnchorAdvancePolicy.IsObservedBottom(2, 2, 0, 0, 2), "swallowed input alone must not prove bottom");
        AssertTrue(!SelectionAnchorAdvancePolicy.IsObservedBottom(1, 2, 0, 2, 2), "one tick is insufficient");
        AssertTrue(!SelectionAnchorAdvancePolicy.IsObservedBottom(2, 1, 0, 2, 2), "unstable anchor is insufficient");
        AssertTrue(!SelectionAnchorAdvancePolicy.IsObservedBottom(2, 2, 1, 2, 2), "positive movement is not bottom");
        AssertTrue(SelectionAnchorAdvancePolicy.IsObservedBottom(2, 2, 0, 2, 2));
        var atSameRow = selected with { Row = 3 };
        var stableContent = RowAdvanceEvaluator.Evaluate(6, 3, 5, 6);
        var noMovement = SelectionAnchorAdvancePolicy.Reconcile(stableContent, atSameRow, 3);
        AssertEqual(RowAdvanceDecision.NoMove, noMovement.Decision);
        AssertTrue(new RowScrollObservation(noMovement, 0, null, 0).ConfirmedZeroMovement);
        return Task.CompletedTask;
    }

    private static Task TestSelectionAnchorDuplicatePositionsAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "zzz-anchor-positions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string item = "{\"名称\":\"啄木鸟电音\",\"槽位\":1,\"品质\":\"S\",\"等级\":15,\"最大等级\":15,\"主属性\":{\"生命值\":2200},\"副属性\":[]}";
            File.WriteAllText(Path.Combine(directory, "export.json"), $"[{item},{item.Replace("啄木鸟电音", "自由蓝调")},{item}]");
            int Run(string mode = "Verify", string targetKind = "ChangedText", int toTop = 2, bool fallback = false, bool duplicateCell = false)
            {
                var events = new List<string>
                {
                    "CELL_TIMING: index=1, logicalRow=1, visualRow=1, col=1/9",
                    "ITEM_TARGET_VERIFICATION: index=1, kind=ChangedText",
                    "CELL_TIMING: index=2, logicalRow=1, visualRow=1, col=2/9",
                    "ITEM_TARGET_VERIFICATION: index=2, kind=ChangedText",
                    "WHEEL_ANCHOR_DECISION: fromTop=1, rowsAdvanced=1, source=anchor",
                    "ROW_SCROLL_DONE: fromTop=1, rowsAdvanced=1, decision=OneRow",
                    $"OVERLAP_SCROLL_ACCEPTED: fromTop=1, toTop={toTop}, rowsAdvanced=1, decision=OneRow",
                    duplicateCell ? "CELL_TIMING: index=3, logicalRow=1, visualRow=1, col=1/9" : "CELL_TIMING: index=3, logicalRow=3, visualRow=2, col=1/9",
                    $"ITEM_TARGET_VERIFICATION: index=3, kind={targetKind}",
                    "SCAN_TERMINAL: visited=3, queued=3, completed=3, failed=0, partial=False, terminationCode=, exportFile=export.json"
                };
                if (fallback) events.Add("WHEEL_ANCHOR_FALLBACK: reason=fixture");
                var lines = new[]
                {
                    "[2026-11-25T00:00:00Z] Start scan. RowAdvanceMode=WheelVerified, MaxItems=200, OcrWorkers=1, OcrBatchSize=1, OcrQueueCapacity=4, OcrIntraOpThreads=1",
                    $"[2026-11-25T00:00:00Z] SelectionAnchorMode={mode}",
                    "[2026-11-25T00:00:00Z] Traversal: overlap-signature-page. totalRows=110"
                }.Concat(events.Select((e, i) => $"[2026-11-25T00:00:01Z] EVENT #{i + 1} {e}"));
                File.WriteAllLines(Path.Combine(directory, "scan.log"), lines);
                var original = Console.Out;
                using var sink = new StringWriter(CultureInfo.InvariantCulture);
                try { Console.SetOut(sink); return ScanBenchmark.Run(directory, null); }
                finally { Console.SetOut(original); }
            }
            AssertEqual(0, Run());
            AssertEqual(1, Run(mode: "Off"));
            AssertEqual(1, Run(targetKind: "Unverified"));
            AssertEqual(1, Run(toTop: 3));
            AssertEqual(1, Run(fallback: true));
            AssertEqual(1, Run(duplicateCell: true));
            File.WriteAllText(Path.Combine(directory, "export.json"), $"[{item},{item},{item}]");
            var firstPairLines = new List<string>
            {
                "[2026-11-25T00:00:00Z] Start scan. RowAdvanceMode=WheelVerified, MaxItems=3, OcrWorkers=1, OcrBatchSize=1, OcrQueueCapacity=4, OcrIntraOpThreads=1",
                "[2026-11-25T00:00:00Z] SelectionAnchorMode=Verify",
                "[2026-11-25T00:00:00Z] Traversal: overlap-signature-page. totalRows=4"
            };
            int RunFirstPair(string repeatKind)
            {
                var lines = new List<string>(firstPairLines);
                for (var i = 1; i <= 3; i++)
                {
                    lines.Add($"[2026-11-25T00:00:01Z] EVENT #{i * 2} ITEM_TARGET_VERIFICATION: index={i}, kind={(i == 1 ? "ChangedText" : repeatKind)}");
                    lines.Add($"[2026-11-25T00:00:01Z] EVENT #{i * 2 + 1} ITEM_POSITION_VERIFIED: index={i}, logicalRow=1, column={i}");
                }
                lines.Add("[2026-11-25T00:00:02Z] EVENT #9 SCAN_TERMINAL: visited=3, queued=3, completed=3, failed=0, partial=False, terminationCode=, exportFile=export.json");
                File.WriteAllLines(Path.Combine(directory, "scan.log"), lines);
                var originalOutput = Console.Out;
                using var firstPairOutput = new StringWriter(CultureInfo.InvariantCulture);
                try { Console.SetOut(firstPairOutput); return ScanBenchmark.Run(directory, null); }
                finally { Console.SetOut(originalOutput); }
            }
            AssertEqual(0, RunFirstPair("IdenticalNeighborRoundTrip"));
            AssertEqual(1, RunFirstPair("ChangedText"));
        }
        finally { Directory.Delete(directory, true); }
        return Task.CompletedTask;
    }

    private static Task TestSelectionAnchorIdenticalItemsAsync()
    {
        var traversal = new VerifiedInventoryTraversal(9);
        var guard = new ScanController.DuplicateGuard(9);
        var identical = new DriveDiscExport { Name = "同属性测试盘", Slot = 3, Level = 0 };
        // Multiple consecutive identical rows must retain every distinct verified inventory cell,
        // provided each adjacent repeat carries neighbor round-trip evidence.
        for (var row = 1; row <= 3; row++) for (var col = 1; col <= 9; col++)
            AssertTrue(guard.Observe(identical, row == 1 && col == 1 ? TargetVerificationKind.ChangedText : TargetVerificationKind.IdenticalNeighborRoundTrip,
                out _, traversal.Capture(row, row, col)));
        var beforeScroll = traversal.Capture(3, 3, 9);
        traversal.Advance(1, 1, true);
        AssertEqual(new VerifiedInventoryPosition(3, 9), beforeScroll!.Value); // OCR snapshot does not move.
        for (var col = 1; col <= 9; col++)
            AssertTrue(guard.Observe(identical, TargetVerificationKind.IdenticalNeighborRoundTrip, out _, traversal.Capture(4, 3, col)));
        AssertTrue(!guard.Observe(identical, TargetVerificationKind.ChangedText, out _, traversal.Capture(4, 3, 9)), "the same cell must be rejected");
        identical.Name = "OCR variation";
        AssertTrue(!guard.Observe(identical, TargetVerificationKind.ChangedText, out _, traversal.Capture(4, 3, 9)), "different OCR text cannot hide the same cell");
        traversal.Advance(2, 1, false);
        AssertTrue(traversal.Capture(5, 3, 1) is null, "fallback invalidates later absolute positions");
        traversal.Advance(3, 1, true);
        AssertTrue(traversal.Capture(6, 3, 1) is null, "a later successful step does not restore lost provenance");
        var adjacentGuard = new ScanController.DuplicateGuard(9);
        var adjacentTraversal = new VerifiedInventoryTraversal(9);
        var other = new DriveDiscExport { Name = "另一件盘", Slot = 3, Level = 0 };
        var repeated = new DriveDiscExport { Name = "同属性测试盘", Slot = 3, Level = 0 };
        AssertTrue(adjacentGuard.Observe(repeated, TargetVerificationKind.ChangedText, out _, adjacentTraversal.Capture(1, 1, 1)));
        AssertTrue(!adjacentGuard.Observe(repeated, TargetVerificationKind.ChangedText, out _, adjacentTraversal.Capture(1, 1, 2)),
            "a verified position does not prove the detail panel refreshed for an adjacent identical item");
        var spacedGuard = new ScanController.DuplicateGuard(9);
        AssertTrue(spacedGuard.Observe(repeated, TargetVerificationKind.ChangedText, out _, adjacentTraversal.Capture(1, 1, 1)));
        AssertTrue(spacedGuard.Observe(other, TargetVerificationKind.ChangedText, out _, adjacentTraversal.Capture(1, 1, 2)));
        AssertTrue(spacedGuard.Observe(repeated, TargetVerificationKind.ChangedText, out _, adjacentTraversal.Capture(1, 1, 3)),
            "non-adjacent identical items at distinct verified cells are kept");
        var legacy = new ScanController.DuplicateGuard(9);
        AssertTrue(legacy.Observe(identical, TargetVerificationKind.ChangedText, out _));
        AssertTrue(!legacy.Observe(identical, TargetVerificationKind.ChangedText, out _), "unverified repeated content remains guarded");
        var invalidJump = new VerifiedInventoryTraversal(9);
        invalidJump.Advance(1, 2, true);
        AssertTrue(invalidJump.Capture(5, 3, 1) is null);
        return Task.CompletedTask;
    }

    private static Task TestSelectionAnchorTopAsync()
    {
        AssertTrue(TopAnchorPolicy.IsNoMovementAtTop(2, 0, 2, true));
        AssertTrue(!TopAnchorPolicy.IsNoMovementAtTop(3, 0, 2, true), "a downshifted anchor proves upward movement, not top");
        AssertTrue(!TopAnchorPolicy.IsNoMovementAtTop(2, 0, 2, false), "swallowed wheel input away from top cannot prove top");
        AssertTrue(!TopAnchorPolicy.IsNoMovementAtTop(2, 0, 1, true), "one stable sample is insufficient");
        AssertTrue(!TopAnchorPolicy.IsNoMovementAtTop(2, 7, 2, true), "content movement must still block top confirmation");
        AssertTrue(!TopAnchorPolicy.IsNoMovementAtTop(1, 0, 2, true), "opposite direction cannot prove top");
        return Task.CompletedTask;
    }
}
