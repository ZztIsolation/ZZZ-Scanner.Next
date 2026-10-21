namespace ZZZScannerNext.Scanning.Gamepad;

internal enum MoveKind { Advanced, Wrapped, Unchanged, Ambiguous }
internal sealed record MoveDecision(MoveKind Kind, string Reason, int GridShift = 0, double Difference = 0);

internal static class TraversalPolicy
{
    public static bool ReturnedToStart(GridObservation start, GridObservation before, GridObservation after) =>
        before.SelectedRow == before.Rows.Length - 1 && before.SelectedColumn == 0
        && after.SelectedRow == 0 && after.SelectedColumn == 0 && SameViewport(start, after, 8);

    public static MoveDecision Right(GridObservation before, GridObservation after)
    {
        if (!GridVision.SameLayout(before, after) || !SameViewport(before, after, 12))
            return new(MoveKind.Ambiguous, "右移时网格布局或列表内容发生非预期变化");
        if (before.SelectedRow != after.SelectedRow || before.RowCount != after.RowCount)
            return new(MoveKind.Ambiguous, "右移改变了行位置或行内格子数量");
        if (after.SelectedColumn == before.SelectedColumn)
            return new(MoveKind.Unchanged, "未观察到选中框移动");
        if (after.SelectedColumn == before.SelectedColumn + 1)
            return new(MoveKind.Advanced, "选中框右移一列");
        if (before.SelectedColumn == before.RowCount - 1 && after.SelectedColumn == 0)
            return new(MoveKind.Wrapped, "末列回到首列，行结束");
        return new(MoveKind.Ambiguous, "发生跳列或非预期方向移动");
    }

    public static MoveDecision Down(GridObservation before, GridObservation after, bool afterHorizontalWrap = false)
    {
        if (!GridVision.SameLayout(before, after) || (!afterHorizontalWrap && before.SelectedColumn != 0) || after.SelectedColumn != 0)
            return new(MoveKind.Ambiguous, "下移前后必须位于同一网格的首列");
        if (SameViewport(before, after, 8))
        {
            if (after.SelectedRow == before.SelectedRow + 1)
                return new(MoveKind.Advanced, "选中框下移一行，列表未滚动");
            if (after.SelectedRow == before.SelectedRow)
                return new(MoveKind.Unchanged, "选中框与列表均未观察到变化；不单独证明到底");
        }
        var candidates = new List<(int Shift, double Error, int Pairs)>();
        for (var shift = -2; shift <= 2; shift++)
        {
            var errors = new List<double>();
            for (var i = 0; i < before.Rows.Length; i++)
            {
                var j = i + shift;
                if (j < 0 || j >= after.Rows.Length || !before.Rows[i].FullyVisible || !after.Rows[j].FullyVisible) continue;
                if (before.Rows[i].Columns.Length != after.Rows[j].Columns.Length) continue;
                errors.Add(RowDifference(before, i, after, j));
            }
            if (errors.Count >= 2) candidates.Add((shift, errors.Average(), errors.Count));
        }
        var ordered = candidates.OrderBy(c => c.Error).ToArray();
        if (ordered.Length == 0 || ordered[0].Error > 12)
            return new(MoveKind.Ambiguous, "无法通过至少两行内容证明滚动位移");
        var best = ordered[0];
        if (ordered.Length > 1 && ordered[1].Error - best.Error < 4)
            return new(MoveKind.Ambiguous, "多种行位移都能匹配；相同行内容或动画导致歧义："
                + string.Join(";", ordered.Select(c => $"shift={c.Shift},error={c.Error:F3},pairs={c.Pairs}")), best.Shift, best.Error);
        var logicalAdvance = after.SelectedRow - before.SelectedRow - best.Shift;
        if (logicalAdvance != 1)
            return new(MoveKind.Ambiguous, $"列表匹配但逻辑位移为 {logicalAdvance}，不是一行", best.Shift, best.Error);
        return new(MoveKind.Advanced, "选中位置与重叠行内容共同证明下移一行", best.Shift, best.Error);
    }

    public static bool SameViewport(GridObservation a, GridObservation b, double tolerance)
    {
        if (!GridVision.SameLayout(a, b) || a.Rows.Length != b.Rows.Length) return false;
        var compared = 0;
        for (var i = 0; i < a.Rows.Length; i++)
        {
            if (Math.Abs(a.Rows[i].Y - b.Rows[i].Y) > a.CellHeight * .10
                || !a.Rows[i].Columns.SequenceEqual(b.Rows[i].Columns)) return false;
            // Clipped artwork and the active card's focus animation are not
            // stationary viewport evidence. Compare the other visible cards.
            if (!a.Rows[i].FullyVisible || !b.Rows[i].FullyVisible) continue;
            if (RowDifference(a, i, b, i) > tolerance) return false;
            compared++;
        }
        return compared > 0;
    }

    internal static double RowDifference(GridObservation a, int rowA, GridObservation b, int rowB)
    {
        var left = a.Rows[rowA];
        var right = b.Rows[rowB];
        if (!left.Columns.SequenceEqual(right.Columns) || left.Columns.Length == 0
            || left.Signature.Length != right.Signature.Length
            || left.Signature.Length % left.Columns.Length != 0) return 255;
        var stride = left.Signature.Length / left.Columns.Length;
        if (stride == 0) return 255;
        long difference = 0;
        var count = 0;
        for (var cell = 0; cell < left.Columns.Length; cell++)
        {
            var column = left.Columns[cell];
            if ((rowA == a.SelectedRow && column == a.SelectedColumn)
                || (rowB == b.SelectedRow && column == b.SelectedColumn)) continue;
            for (var sample = cell * stride; sample < (cell + 1) * stride; sample++)
            {
                difference += Math.Abs(left.Signature[sample] - right.Signature[sample]);
                count++;
            }
        }
        // A single-column row has no independent card after masking; retain the
        // original strict comparison instead of claiming evidence from no pixels.
        return count == 0 ? GridVision.Difference(left.Signature, right.Signature) : (double)difference / count;
    }
}
