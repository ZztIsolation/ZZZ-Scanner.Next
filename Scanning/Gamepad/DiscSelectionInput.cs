using System.Diagnostics;

namespace ZZZScannerNext.Scanning.Gamepad;

// The production capture pipeline owns panel waits, retries and OCR. This input
// adapter owns only focus movement; it never captures a detail panel or enqueues OCR.
internal interface IDiscSelectionInput
{
    bool Contains(Point screenPoint);
    Task SelectAsync(Point screenPoint, CancellationToken token);
    Task ResynchronizeAsync(CancellationToken token);
}

internal sealed class GamepadPulse(IGamepadOutput output, Action guard,
    int holdMilliseconds = 60, int releaseMilliseconds = 40)
{
    private long lastRelease;
    public int Count { get; private set; }

    public async Task SendAsync(Direction direction, CancellationToken token)
    {
        // Panel capture normally supplies the release time. Only back-to-back
        // inputs (skipped cells / first-pair return) need the remainder here.
        var remaining = lastRelease == 0 ? 0 : releaseMilliseconds - Stopwatch.GetElapsedTime(lastRelease).TotalMilliseconds;
        if (remaining > 0) await WaitAsync((int)Math.Ceiling(remaining), token);
        token.ThrowIfCancellationRequested();
        guard();
        try
        {
            output.Press(direction, NavigationInput.DPad);
            Count++;
            await WaitAsync(holdMilliseconds, token);
        }
        finally
        {
            output.Neutral();
            lastRelease = Stopwatch.GetTimestamp();
        }
    }

    private async Task WaitAsync(int milliseconds, CancellationToken token)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds)
        {
            token.ThrowIfCancellationRequested();
            guard();
            await Task.Delay(Math.Min(10, Math.Max(1, milliseconds - (int)watch.ElapsedMilliseconds)), token);
        }
        token.ThrowIfCancellationRequested();
        guard();
    }
}

internal sealed class DiscSelectionInput : IDiscSelectionInput
{
    private readonly GamepadPulse pulse;
    private readonly Func<CancellationToken, Task<GridObservation>> observe;
    private readonly Point origin;
    private readonly Action<string>? trace;
    private readonly Action<GridObservation, GridObservation>? mismatch;
    private int row, column;
    private Point? verifiedRowStart;
    private long rowStartVerifiedAt;
    public GridObservation Grid { get; private set; }

    public DiscSelectionInput(GridObservation grid, Point screenOrigin, GamepadPulse pulse,
        Func<CancellationToken, Task<GridObservation>> observe, Action<string>? trace = null,
        Action<GridObservation, GridObservation>? mismatch = null)
    {
        this.pulse = pulse;
        this.observe = observe;
        this.trace = trace;
        this.mismatch = mismatch;
        origin = screenOrigin;
        Grid = grid;
        row = grid.SelectedRow;
        column = grid.SelectedColumn;
    }

    // Sample the rarity stripe near the lower right, as the mouse profile does.
    public static Point CellPoint(GridObservation grid, int row, int column) => new(
        (int)Math.Round(grid.ColumnX[column] + grid.CellWidth * .34),
        (int)Math.Round(grid.Rows[row].Y + grid.CellHeight * .43));

    public bool Contains(Point screenPoint)
    {
        var local = new Point(screenPoint.X - origin.X, screenPoint.Y - origin.Y);
        return Enumerable.Range(0, Grid.Rows.Length).Any(r => Grid.Rows[r].Columns.Any(c =>
        {
            var target = CellPoint(Grid, r, c);
            return Math.Abs(target.X - local.X) <= Grid.CellWidth * .2
                && Math.Abs(target.Y - local.Y) <= Grid.CellHeight * .2;
        }));
    }

    public async Task SelectAsync(Point screenPoint, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var local = new Point(screenPoint.X - origin.X, screenPoint.Y - origin.Y);
        var targetRow = Enumerable.Range(0, Grid.Rows.Length).MinBy(r => Math.Abs(CellPoint(Grid, r, 0).Y - local.Y));
        var targetColumn = Enumerable.Range(0, Grid.Rows[targetRow].Columns.Length)
            .MinBy(c => Math.Abs(CellPoint(Grid, targetRow, c).X - local.X));
        var target = CellPoint(Grid, targetRow, targetColumn);
        if (Math.Abs(target.X - local.X) > Grid.CellWidth * .2 || Math.Abs(target.Y - local.Y) > Grid.CellHeight * .2)
            throw new InvalidOperationException("选盘目标不在已标定的网格中，停止输入。");

        if (row != targetRow || column != targetColumn)
            verifiedRowStart = null;

        // Cross-row moves occur only in the original neighbor/witness recovery.
        // Verify their viewport so stale screen coordinates cannot select a new row.
        while (row != targetRow)
        {
            var before = Grid;
            var expectedRow = row + Math.Sign(targetRow - row);
            var expectedColumn = Math.Min(column, Grid.Rows[expectedRow].Columns.Length - 1);
            await pulse.SendAsync(targetRow > row ? Direction.Down : Direction.Up, token);
            var after = await observe(token);
            if (!TraversalPolicy.SameViewport(before, after, 12)
                || after.SelectedRow != expectedRow || after.SelectedColumn != expectedColumn)
                throw new InvalidOperationException("邻盘刷新发生非预期滚动或位移，停止使用旧坐标。");
            Rebase(after);
        }
        var directions = PlanHorizontal(column, targetColumn, Grid.Rows[row].Columns.Length);
        if (directions.Length > 0)
            trace?.Invoke($"row={row + 1}, fromColumn={column + 1}, toColumn={targetColumn + 1}, directions={string.Join("/", directions)}");
        foreach (var direction in directions)
        {
            await pulse.SendAsync(direction, token);
            column = (column + (direction == Direction.Right ? 1 : -1) + Grid.Rows[row].Columns.Length)
                % Grid.Rows[row].Columns.Length;
        }
    }

    internal static Direction[] PlanHorizontal(int from, int to, int count)
    {
        if (count <= 0 || from < 0 || to < 0 || from >= count || to >= count)
            throw new ArgumentOutOfRangeException(nameof(count));
        var right = (to - from + count) % count;
        var left = (from - to + count) % count;
        return Enumerable.Repeat(right <= left ? Direction.Right : Direction.Left, Math.Min(right, left)).ToArray();
    }

    public async Task<GridObservation> AdvanceRowAsync(CancellationToken token)
    {
        var first = CellPoint(Grid, row, 0);
        first.Offset(origin);
        await SelectAsync(first, token);
        trace?.Invoke($"row={row + 1}, fromColumn=1, directions=Down, phase=combined_transition");
        await pulse.SendAsync(Direction.Down, token);
        // One observation operation (stable frames internally) covers both inputs.
        // The caller must validate the result before rebasing or capturing a row.
        return await observe(token);
    }

    public async Task ResynchronizeAsync(CancellationToken token)
    {
        verifiedRowStart = null;
        var actual = await observe(token);
        if (!TraversalPolicy.SameViewport(Grid, actual, 12))
        {
            mismatch?.Invoke(Grid, actual);
            throw new InvalidOperationException("网格内容或滚动位置发生非预期变化，无法恢复选盘位置。");
        }
        Rebase(actual);
    }

    public async Task VerifyPositionAsync(CancellationToken token)
    {
        var expectedRow = row;
        var expectedColumn = column;
        await ResynchronizeAsync(token);
        if (row != expectedRow || column != expectedColumn)
            throw new InvalidOperationException($"手柄位置不符：预期 {expectedRow + 1},{expectedColumn + 1}，实际 {row + 1},{column + 1}。");
    }

    public void Rebase(GridObservation actual)
    {
        verifiedRowStart = null;
        Grid = actual;
        row = actual.SelectedRow;
        column = actual.SelectedColumn;
    }

    public void RebaseAfterVerifiedRowAdvance(GridObservation actual, MoveDecision decision)
    {
        verifiedRowStart = null;
        if (decision.Kind != MoveKind.Advanced || actual.SelectedColumn != 0)
            throw new InvalidOperationException("只有已确认的一行推进可以提供行首位置证据。");
        Rebase(actual);
        var point = CellPoint(actual, row, 0);
        point.Offset(origin);
        verifiedRowStart = point;
        rowStartVerifiedAt = Stopwatch.GetTimestamp();
    }

    public bool ConsumeVerifiedRowStart(Point target)
    {
        var expected = verifiedRowStart;
        verifiedRowStart = null; // One target, one initial capture attempt only.
        return expected is { } point && column == 0 && Grid.SelectedRow == row
            && Math.Abs(point.X - target.X) <= 1 && Math.Abs(point.Y - target.Y) <= 1
            && Stopwatch.GetElapsedTime(rowStartVerifiedAt).TotalMilliseconds <= 1000;
    }
}
