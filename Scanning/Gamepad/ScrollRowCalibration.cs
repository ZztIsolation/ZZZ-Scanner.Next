namespace ZZZScannerNext.Scanning.Gamepad;

// Learn scrollbar travel only from independently verified one-row transitions.
// It supplies an independent position witness when sorted rows share the same art.
internal sealed class ScrollRowCalibration
{
    private readonly Queue<double> samples = new();
    public double PixelsPerRow => samples.Count < 3 ? 0 : samples.Order().ElementAt(samples.Count / 2);

    public void Learn(MoveDecision decision, int? pixelDelta)
    {
        if (decision.Kind != MoveKind.Advanced || decision.GridShift != -1 || pixelDelta is not >= 4) return;
        samples.Enqueue(pixelDelta.Value);
        // The first scrolls can reposition a partially clipped viewport. Use the
        // latest three verified transitions, not the initial partial travel.
        while (samples.Count > 3) samples.Dequeue();
    }

    public MoveDecision Reconcile(GridObservation before, GridObservation after, MoveDecision visual,
        int? pixelDelta, bool thumbStable, bool afterHorizontalWrap = false)
    {
        if (visual.Kind == MoveKind.Advanced || !thumbStable || pixelDelta is null || PixelsPerRow < 4
            || !GridVision.SameLayout(before, after) || (!afterHorizontalWrap && before.SelectedColumn != 0) || after.SelectedColumn != 0
            || before.SelectedRow != after.SelectedRow) return visual;
        var tolerance = Math.Max(1.5, PixelsPerRow * .20);
        if (samples.Any(s => Math.Abs(s - PixelsPerRow) > tolerance)
            || Math.Abs(pixelDelta.Value - PixelsPerRow) > tolerance) return visual;
        var errors = new List<double>();
        for (var i = 1; i < before.Rows.Length; i++)
        {
            var j = i - 1;
            if (j >= after.Rows.Length || !before.Rows[i].FullyVisible || !after.Rows[j].FullyVisible
                || !before.Rows[i].Columns.SequenceEqual(after.Rows[j].Columns)) continue;
            errors.Add(TraversalPolicy.RowDifference(before, i, after, j));
        }
        if (errors.Count < 2 || errors.Any(e => e > 12)) return visual;
        return new(MoveKind.Advanced,
            $"scrollbar_one_row: delta={pixelDelta}, calibrated={PixelsPerRow:F2}, pairs={errors.Count}, visual={visual.Reason}",
            -1, errors.Average());
    }
}
