using ZZZScannerNext.Scanning;
using ZZZScannerNext.Scanning.Gamepad;

namespace ZZZScannerNext.RegressionTests;

internal static class GamepadInputChecks
{
    public static void CheckSavedNavigation(string directory)
    {
        var calibration = new ScrollRowCalibration();
        foreach (var path in Directory.GetFiles(directory, "row-*.json").Order())
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = json.RootElement;
            var before = System.Text.Json.JsonSerializer.Deserialize<GridObservation>(root.GetProperty("Before"))!;
            var after = System.Text.Json.JsonSerializer.Deserialize<GridObservation>(root.GetProperty("After"))!;
            var delta = root.GetProperty("AfterThumb").GetProperty("CenterY").GetInt32()
                - root.GetProperty("BeforeThumb").GetProperty("CenterY").GetInt32();
            var combined = root.TryGetProperty("CombinedTransition", out var flag) && flag.GetBoolean();
            var visual = TraversalPolicy.Down(before, after, combined);
            // Replay the matching decision with stable thumb evidence assumed;
            // the live test separately checks consecutive thumb observations.
            var result = calibration.Reconcile(before, after, visual, delta, true, combined);
            calibration.Learn(visual, delta);
            Require(result.Kind == MoveKind.Advanced, $"saved transition rejected: {path}: {result}");
            Console.WriteLine($"PASS {Path.GetFileName(path)} delta={delta} calibrated={calibration.PixelsPerRow:F2} reason={result.Reason}");
        }
    }

    // Offline diagnostic: saved raw game frames only; never captures or controls a window.
    public static void CheckSavedFrames(IEnumerable<string> paths)
    {
        var profile = ScanProfileFile.Load().ResolveRequired(ScanOptions.DefaultProfileName);
        foreach (var path in paths)
        {
            using var frame = new Bitmap(path);
            var grid = GridVision.Analyze(frame, GridVision.DefaultArea);
            var candidates = new[] { new VisualRarityCandidate("S", profile.Color("rarityS")),
                new VisualRarityCandidate("A", profile.Color("rarityA")), new VisualRarityCandidate("B", profile.Color("rarityB")) };
            var checkedCells = 0;
            foreach (var row in Enumerable.Range(0, grid.Rows.Length).Where(r => grid.Rows[r].FullyVisible))
                foreach (var column in grid.Rows[row].Columns)
                {
                    var point = DiscSelectionInput.CellPoint(grid, row, column);
                    using var patch = frame.Clone(new Rectangle(point.X - 36, point.Y - 36, 73, 73), frame.PixelFormat);
                    var samples = Enumerable.Range(0, 37).SelectMany(y => Enumerable.Range(0, 37).Select(x => new Point(x * 2, y * 2)));
                    var result = VisualProbeEvaluator.EvaluateRarity(patch, candidates, samples, profile.VisualProbes.Rarity);
                    Require(result.Rarity is not null, $"rarity probe misses calibrated cell {row},{column}: {path}");
                    checkedCells++;
                }
            Console.WriteLine($"PASS saved_frame={path}, columns={grid.Columns}, rarity_cells={checkedCells}");
        }
    }

    public static async Task SelectionAsync()
    {
        var game = new FocusModel();
        var reads = 0;
        var origin = new Point(320, 80);
        var input = new DiscSelectionInput(game.Grid, origin, new GamepadPulse(game, () => { }, 0, 0),
            _ => { reads++; return Task.FromResult(game.Grid); });
        Point Cell(int row, int column)
        {
            var point = DiscSelectionInput.CellPoint(game.Grid, row, column);
            point.Offset(origin);
            return point;
        }
        async Task Select(int row, int column)
        {
            await input.SelectAsync(Cell(row, column), default);
            Require(game.Grid.SelectedRow == row && game.Grid.SelectedColumn == column, "selected exact cell");
        }

        // The real first-pair capture returns to 0, skips committed 1, then selects 2.
        await Select(0, 0);
        Require(game.Inputs.Count == 0, "reselecting the current item sends no direction");
        await Select(0, 1);
        await Select(0, 0);
        await Select(0, 2);
        Require(game.Inputs.SequenceEqual(new[] { Direction.Right, Direction.Left, Direction.Right, Direction.Right }),
            "original first-pair return and committed-cell skip retain exact positions");
        await Select(0, 6); // Several unselected rarities can be skipped by the row scanner.
        await Select(0, 8);
        var beforeWrap = game.Inputs.Count;
        await Select(0, 0);
        Require(game.Inputs.Count == beforeWrap + 1 && game.Inputs[^1] == Direction.Right, "row wrap is one input");
        Require(reads == 0, "normal same-row selection does not take grid or detail screenshots");
        await input.VerifyPositionAsync(default);
        Require(reads == 1, "row verification is explicit");

        await Select(1, 2); // The last row has only three cells.
        beforeWrap = game.Inputs.Count;
        await Select(1, 0);
        Require(game.Inputs.Count == beforeWrap + 1 && game.Inputs[^1] == Direction.Right, "partial row wraps at its own width");
        Require(!input.Contains(Cell(1, 0) + new Size(8 * 120, 0)), "missing cells cannot become witness targets");
        var beforeInvalid = game.Inputs.Count;
        await RejectAsync(() => input.SelectAsync(new Point(-200, -200), default));
        Require(game.Inputs.Count == beforeInvalid, "out-of-grid requests send no input");

        game.DropNext = true;
        await input.SelectAsync(Cell(1, 1), default);
        await RejectAsync(() => input.VerifyPositionAsync(default));
        await input.ResynchronizeAsync(default);
        await Select(1, 1);
        Require(game.Inputs.Count == game.Neutrals, "all inputs release the controller");
    }

    public static async Task PreparedStartAsync()
    {
        var game = new FocusModel();
        var pulse = new GamepadPulse(game, () => { }, 0, 0);
        await pulse.SendAsync(Direction.Right, default); // Startup leaves a neighbor baseline.
        var baselineColumn = game.Grid.SelectedColumn;
        var traces = new List<string>();
        var input = new DiscSelectionInput(game.Grid, Point.Empty, pulse,
            _ => Task.FromResult(game.Grid), traces.Add);
        var captured = new List<int>();
        foreach (var column in new[] { 0, 1, 2 })
        {
            await input.SelectAsync(DiscSelectionInput.CellPoint(game.Grid, 0, column), default);
            captured.Add(game.Grid.SelectedColumn);
        }
        Require(baselineColumn == 1 && captured.SequenceEqual(new[] { 0, 1, 2 }),
            "prepared neighbor baseline returns to first, then captures items in order");
        Require(game.Inputs.SequenceEqual(new[] { Direction.Right, Direction.Left, Direction.Right, Direction.Right }),
            "merged startup needs one round trip with no second first-pair round trip");
        Require(traces.Count == 3 && traces[0].Contains("directions=Left"),
            "selection trace exposes actual commanded direction");
        Require(game.Neutrals == game.Inputs.Count, "merged startup releases all reports");
    }

    public static async Task CombinedRowAsync()
    {
        foreach (var dropWrap in new[] { false, true })
        {
            var game = new FocusModel();
            var reads = 0;
            var input = new DiscSelectionInput(game.Grid, Point.Empty, new GamepadPulse(game, () => { }, 0, 0),
                _ => { reads++; return Task.FromResult(game.Grid); });
            await input.SelectAsync(DiscSelectionInput.CellPoint(game.Grid, 0, 8), default);
            game.DropNext = dropWrap;
            var before = input.Grid;
            var after = await input.AdvanceRowAsync(default);
            Require(reads == 1, "wrap and down share exactly one stable observation operation");
            var decision = TraversalPolicy.Down(before, after, afterHorizontalWrap: true);
            Require((decision.Kind == MoveKind.Advanced) == !dropWrap,
                "combined transition accepts first-column advance and rejects a dropped wrap");
            Require(game.Inputs.TakeLast(2).SequenceEqual(new[] { Direction.Right, Direction.Down }),
                "row-end wrap uses right then down");
        }
        var focus = Layout();
        var changedRows = focus.Rows.Select(r => r with { Signature = (byte[])r.Signature.Clone() }).ToArray();
        Array.Fill(changedRows[0].Signature, (byte)200, 0, 64);
        var focusAnimation = focus with { Rows = changedRows, SelectedColumn = 8 };
        Require(TraversalPolicy.SameViewport(focus, focusAnimation, 12),
            "previous focus artwork animation cannot masquerade as viewport movement");
        Array.Fill(changedRows[1].Signature, (byte)200);
        Require(!TraversalPolicy.SameViewport(focus, focusAnimation, 12),
            "changes to an unaffected row still reject viewport equality");
        var clipped = focus with { Rows = focus.Rows.Select((r, i) => r with { FullyVisible = i != 0 }).ToArray() };
        var clippedChange = clipped with { Rows = clipped.Rows.Select((r, i) => i == 0
            ? r with { Signature = Enumerable.Repeat((byte)200, r.Signature.Length).ToArray() } : r).ToArray() };
        Require(TraversalPolicy.SameViewport(clipped, clippedChange, 12), "clipped artwork is not stationary evidence");
        Require(!TraversalPolicy.SameViewport(focus, focus with { Rows = focus.Rows.Select(r => r with { Y = r.Y + 100 }).ToArray() }, 12),
            "geometry movement remains rejected even when card art is identical");
    }

    public static async Task VerifiedRowEvidenceAsync()
    {
        foreach (var action in new[] { "consume", "move", "resync", "rebase", "wrong_target", "expired", "rejected" })
        {
            var game = new FocusModel();
            var input = new DiscSelectionInput(game.Grid, Point.Empty, new GamepadPulse(game, () => { }, 0, 0),
                _ => Task.FromResult(game.Grid));
            Require(!input.ConsumeVerifiedRowStart(DiscSelectionInput.CellPoint(game.Grid, 0, 0)),
                "startup grid alone has no verified Down evidence");
            var before = input.Grid;
            var after = await input.AdvanceRowAsync(default);
            var decision = TraversalPolicy.Down(before, after, afterHorizontalWrap: true);
            input.RebaseAfterVerifiedRowAdvance(after, decision);
            var first = DiscSelectionInput.CellPoint(after, after.SelectedRow, 0);
            var inputs = game.Inputs.Count;
            await input.SelectAsync(first, default);
            Require(inputs == game.Inputs.Count, "already selected row start issues no extra input");
            switch (action)
            {
                case "move":
                    await input.SelectAsync(DiscSelectionInput.CellPoint(after, after.SelectedRow, 1), default);
                    await input.SelectAsync(first, default);
                    break;
                case "resync": await input.ResynchronizeAsync(default); break;
                case "rebase": input.Rebase(after); break;
                case "wrong_target":
                    Require(!input.ConsumeVerifiedRowStart(first + new Size(20, 0)), "evidence is bound to its target");
                    break;
                case "expired": await Task.Delay(1050); break;
                case "rejected":
                    await RejectAsync(() => { input.RebaseAfterVerifiedRowAdvance(after, new(MoveKind.Ambiguous, "dropped input")); return Task.CompletedTask; });
                    break;
            }
            Require(input.ConsumeVerifiedRowStart(first) == (action == "consume"),
                $"only fresh, unmodified verified position may be consumed: {action}");
            Require(!input.ConsumeVerifiedRowStart(first), "position evidence cannot be reused on retry");
        }
    }

    public static async Task ReleaseAsync()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var game = new FocusModel();
        var pulse = new GamepadPulse(game, () => { }, 0, 0);
        try { await pulse.SendAsync(Direction.Right, cancellation.Token); throw new Exception("expected cancellation"); }
        catch (OperationCanceledException) { }
        Require(game.Inputs.Count == 0, "pre-canceled movement never presses");

        var healthy = true;
        game.OnPress = () => healthy = false;
        pulse = new GamepadPulse(game, () => { if (!healthy) throw new InvalidOperationException("focus lost"); });
        await RejectAsync(() => pulse.SendAsync(Direction.Right, default));
        Require(game.Inputs.Count == 1 && game.Neutrals == 1, "loss of focus during hold always releases");

        game.OnPress = () => throw new InvalidOperationException("driver error");
        pulse = new GamepadPulse(game, () => { });
        await RejectAsync(() => pulse.SendAsync(Direction.Right, default));
        Require(game.Neutrals == 2, "driver submission failure also attempts release");
    }

    public static Task CoordinatesAsync()
    {
        foreach (var (width, height, columns) in new[] { (1280, 720, 7), (1920, 1080, 9), (2560, 1440, 10), (3840, 2160, 12) })
        {
            var grid = Layout(width, height, columns);
            var (offset, step) = ScanController.GamepadRowCoordinates(grid);
            foreach (var row in Enumerable.Range(0, grid.Rows.Length))
                foreach (var col in grid.Rows[row].Columns)
                {
                    var expected = DiscSelectionInput.CellPoint(grid, row, col);
                    var actual = new Point((int)Math.Round((offset.X + step.X * (col + 1)) * width),
                        (int)Math.Round((offset.Y + step.Y * (row + 1)) * height));
                    Require(Math.Abs(expected.X - actual.X) <= 1 && Math.Abs(expected.Y - actual.Y) <= 1,
                        "original 1-based row capture coordinates map to dynamic cells");
                }
        }
        return Task.CompletedTask;
    }

    private static GridObservation Layout(int width = 1920, int height = 1080, int columns = 9) => new(
        width, height, new Rectangle(0, 0, width, height),
        Enumerable.Range(0, columns).Select(c => width * (.08 + c * .65 / columns)).ToArray(),
        [new(height * .3, Enumerable.Range(0, columns).ToArray(), Enumerable.Repeat((byte)5, columns * 64).ToArray(), true),
         new(height * .5, [0, 1, 2], Enumerable.Repeat((byte)60, 3 * 64).ToArray(), true)],
        0, 0, Rectangle.Empty, width * .6 / columns, width * .6 / columns);

    private sealed class FocusModel : IGamepadOutput
    {
        public GridObservation Grid { get; private set; } = Layout();
        public List<Direction> Inputs { get; } = new();
        public int Neutrals { get; private set; }
        public bool DropNext { get; set; }
        public Action? OnPress { get; set; }
        public void Press(Direction direction, NavigationInput input)
        {
            Inputs.Add(direction);
            OnPress?.Invoke();
            if (DropNext) { DropNext = false; return; }
            var row = Grid.SelectedRow;
            var col = Grid.SelectedColumn;
            if (direction == Direction.Right) col = (col + 1) % Grid.RowCount;
            if (direction == Direction.Left) col = (col + Grid.RowCount - 1) % Grid.RowCount;
            if (direction is Direction.Down or Direction.Up)
            {
                row = Math.Clamp(row + (direction == Direction.Down ? 1 : -1), 0, Grid.Rows.Length - 1);
                col = Math.Min(col, Grid.Rows[row].Columns.Length - 1);
            }
            Grid = Grid with { SelectedRow = row, SelectedColumn = col };
        }
        public void Neutral() => Neutrals++;
    }

    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException) { return; }
        throw new Exception("expected rejected navigation");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
