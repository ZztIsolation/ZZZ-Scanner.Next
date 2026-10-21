using System.Text;

namespace ZZZ.GamepadProbe;

internal static class GridChecks
{
    public static async Task<string> RunTraversalAsync()
    {
        var report = new StringBuilder();
        var root = Path.Combine(Path.GetTempPath(), "zzz-grid-check-" + Guid.NewGuid().ToString("N"));
        // Keep failed evidence for diagnosis; successful evidence is small and remains
        // in the OS temporary directory, never in the distributable package.
        foreach (var (expected, consumeFirst, wrapDown) in new[] { (9, false, false), (0, false, false), (12, false, false), (9, true, false),
            (9, false, true), (0, false, true), (12, false, true) })
        {
            var simulator = new SimulatedInventory(consumeFirst, wrapDown);
            var traversal = new GridTraversal(simulator, simulator, () => { }, _ => { },
                Path.Combine(root, $"{expected}-{consumeFirst}-{wrapDown}"), new TraversalOptions(GridVision.DefaultArea, NavigationInput.DPad, 20, 100, expected));
            var result = await traversal.RunAsync(default);
            var correctOutcome = expected == 9 ? "CompletedWithExpectedCount" : expected == 0 ? (wrapDown ? "WrappedToStartCandidate" : "BottomCandidate") : "StoppedAmbiguous";
            if (result.Outcome != correctOutcome || result.VisitedItems != 9 || result.CompletedRows != 2)
                throw new Exception($"End-to-end traversal failed: {result}; evidence={root}");
            report.AppendLine($"PASS simulated traversal total=9 expected={expected} consumeFirst={consumeFirst} wrapDown={wrapDown}: {correctOutcome}, each position counted once");
        }
        return report.ToString();
    }

    internal sealed class SimulatedInventory(bool consumeFirst = false, bool wrapDown = false) : IClientCapture, IGamepadOutput
    {
        private int row, col;
        public int Presses { get; private set; }
        public void Press(Direction direction, NavigationInput input)
        {
            Presses++;
            if (consumeFirst) { consumeFirst = false; return; }
            if (direction == Direction.Right) col = (col + 1) % (row == 0 ? 7 : 2);
            else if (direction == Direction.Down) { row = wrapDown ? (row + 1) % 2 : 1; col = Math.Min(col, 1); }
        }
        public void Neutral() { }
        public Bitmap Capture()
        {
            var image = Draw(1280, 720, 7, 7, row, col);
            using var graphics = Graphics.FromImage(image);
            using var background = new SolidBrush(Color.FromArgb(22, 22, 22));
            graphics.FillRectangle(background, 0, (float)(720 * .54), 1280, 720);
            graphics.FillRectangle(background, (float)(1280 * (.055 + .65 * 2 / 7) - 5), (float)(720 * .37), 1280, (float)(720 * .16));
            return image;
        }
    }

    public static string Run()
    {
        var report = new StringBuilder();
        foreach (var (width, height, columns) in new[] { (1280, 720, 7), (1920, 1080, 9), (2560, 1440, 10), (3840, 2160, 12) })
        {
            using var image = Draw(width, height, columns, columns, 0, 0);
            var grid = GridVision.Analyze(image, GridVision.DefaultArea);
            Require(grid.Columns == columns && grid.Rows.Length == 4 && grid.SelectedColumn == 0 && grid.SelectedRow == 0,
                $"synthetic {width}x{height}: discovers {columns} columns and first selection");
        }
        using var firstImage = Draw(1920, 1080, 9, 3, 0, 0);
        var first = GridVision.Analyze(firstImage, GridVision.DefaultArea);
        using var portraitImage = Draw(1920, 1080, 9, 3, 0, 0);
        using (var graphics = Graphics.FromImage(portraitImage))
        using (var border = new Pen(Color.Yellow, 3))
            graphics.DrawRectangle(border, 680, 265, 38, 38);
        var portrait = GridVision.Analyze(portraitImage, GridVision.DefaultArea);
        Require(portrait.SelectedRow == 0 && portrait.SelectedColumn == 0 && portrait.Columns == 9,
            "small saturated portrait frame cannot compete with a card-sized focus border");
        using var splitImage = Draw(1920, 1080, 9, 3, 0, 0);
        using (var graphics = Graphics.FromImage(splitImage))
        {
            using var cover = new SolidBrush(Color.FromArgb(22, 22, 22));
            graphics.FillRectangle(cover, 100, 246, 13, 20);
            graphics.FillRectangle(cover, 220, 246, 13, 20);
        }
        var split = GridVision.Analyze(splitImage, GridVision.DefaultArea);
        Require(split.Columns == 9 && split.Rows.Length == 4 && split.SelectedColumn == 0,
            "portrait-split selection border does not become an extra grid row");
        using var thickImage = Draw(1920, 1080, 9, 3, 0, 0, .07f);
        var thick = GridVision.Analyze(thickImage, GridVision.DefaultArea);
        Require(TraversalPolicy.SameViewport(first, thick, 3), "pulsing focus border does not change row content sampling");
        Require(first.Rows[^1].Columns.Length == 3, "partial final row detected without forcing full width");
        using var nextImage = Draw(1920, 1080, 9, 3, 0, 1);
        var next = GridVision.Analyze(nextImage, GridVision.DefaultArea);
        Require(TraversalPolicy.Right(first, next).Kind == MoveKind.Advanced, "right movement verified geometrically");
        Require(TraversalPolicy.Right(first, first).Kind == MoveKind.Unchanged, "lost right input is not counted");
        using var skippedImage = Draw(1920, 1080, 9, 3, 0, 2);
        Require(TraversalPolicy.Right(first, GridVision.Analyze(skippedImage, GridVision.DefaultArea)).Kind == MoveKind.Ambiguous,
            "two-column jump rejected");
        using var lastImage = Draw(1920, 1080, 9, 3, 0, 8);
        Require(TraversalPolicy.Right(GridVision.Analyze(lastImage, GridVision.DefaultArea), first).Kind == MoveKind.Wrapped,
            "last column wraps to first without reading first twice");
        using var rowDownImage = Draw(1920, 1080, 9, 3, 1, 0);
        Require(TraversalPolicy.Down(first, GridVision.Analyze(rowDownImage, GridVision.DefaultArea)).Kind == MoveKind.Advanced,
            "down movement without viewport scrolling");
        using var partialStartImage = Draw(1920, 1080, 9, 3, 3, 0);
        using var partialLastImage = Draw(1920, 1080, 9, 3, 3, 2);
        var partialStart = GridVision.Analyze(partialStartImage, GridVision.DefaultArea);
        Require(TraversalPolicy.Right(GridVision.Analyze(partialLastImage, GridVision.DefaultArea), partialStart).Kind == MoveKind.Wrapped,
            "partial row wraps after its actual final cell");
        Require(TraversalPolicy.Down(partialStart, partialStart).Kind == MoveKind.Unchanged,
            "unchanged down does not prove bottom by itself");
        var before = Fake([10, 70, 130, 220], 3);
        var after = Fake([70, 130, 220, 180], 3);
        Require(TraversalPolicy.Down(before, after).Kind == MoveKind.Advanced, "one-row scrolling proven by overlapping row contents");
        var jump = Fake([130, 220, 180, 30], 3);
        Require(TraversalPolicy.Down(before, jump).Kind == MoveKind.Ambiguous, "two-row scrolling rejected");
        var duplicate = Fake([70, 70, 70, 70], 3);
        Require(TraversalPolicy.Down(duplicate, duplicate).Kind != MoveKind.Advanced, "identical rows never guessed as a scroll");
        var calibration = new ScrollRowCalibration();
        var uncertain = TraversalPolicy.Down(duplicate, duplicate);
        Require(calibration.Reconcile(duplicate, duplicate, uncertain, 12, true).Kind != MoveKind.Advanced,
            "uncalibrated scrollbar never resolves repeated rows");
        foreach (var delta in new[] { 8, 17, 12, 11, 12 }) calibration.Learn(new(MoveKind.Advanced, "verified", -1), delta);
        Require(calibration.Reconcile(duplicate, duplicate, uncertain, 12, true).Kind == MoveKind.Advanced,
            "stable calibrated scrollbar plus two matching rows resolves identical art");
        foreach (int? delta in new int?[] { null, 0, -12, 24, 6 })
            Require(calibration.Reconcile(duplicate, duplicate, uncertain, delta, true).Kind != MoveKind.Advanced,
                "missing, lost, reversed, double and fractional scrollbar movement rejected");
        Require(calibration.Reconcile(duplicate, duplicate, uncertain, 12, false).Kind != MoveKind.Advanced,
            "animating scrollbar cannot confirm a row");
        Require(calibration.Reconcile(before, jump, uncertain, 12, true).Kind != MoveKind.Advanced,
            "scrollbar does not override incompatible row content");
        using var black = new Bitmap(1920, 1080);
        try { GridVision.Analyze(black, GridVision.DefaultArea); throw new Exception("Black screen accepted"); }
        catch (InvalidOperationException) { report.AppendLine("PASS black screen rejected"); }
        return report.ToString();

        void Require(bool valid, string name)
        {
            if (!valid) throw new Exception("FAIL " + name);
            report.AppendLine("PASS " + name);
        }
    }

    private static GridObservation Fake(byte[] content, int selectedRow) => new(1920, 1080,
        new Rectangle(40, 180, 1200, 700), [100, 220, 340],
        content.Select((value, row) => new GridRow(250 + 160 * row, [0, 1, 2], Enumerable.Repeat(value, 192).ToArray(), true)).ToArray(),
        selectedRow, 0, new Rectangle(50, 200 + 160 * selectedRow, 100, 100), 100, 100);

    private static Bitmap Draw(int width, int height, int columns, int lastCount, int selectedRow, int selectedCol, float strokeScale = .04f)
    {
        var image = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(image);
        graphics.Clear(Color.FromArgb(22, 22, 22));
        var pitchX = width * .65 / columns;
        var size = Math.Min(pitchX * .88, height * .125);
        var pitchY = height * .165;
        for (var row = 0; row < 4; row++)
            for (var col = 0; col < (row == 3 ? lastCount : columns); col++)
            {
                var x = (float)(width * .055 + pitchX * col);
                var y = (float)(height * .22 + pitchY * row);
                using var card = new SolidBrush(Color.FromArgb(40 + row * 30, 40 + row * 30, 40 + row * 30));
                graphics.FillRectangle(card, x, y, (float)size, (float)size);
                graphics.FillRectangle(Brushes.Gold, x + (float)size * .06f, y + (float)size * .88f, (float)size * .88f, (float)size * .10f);
                using var stroke = new Pen(Color.YellowGreen, Math.Max(3, (float)size * strokeScale));
                if (row == selectedRow && col == selectedCol) graphics.DrawRectangle(stroke, x, y, (float)size, (float)size);
            }
        return image;
    }
}
