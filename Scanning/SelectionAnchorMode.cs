namespace ZZZScannerNext.Scanning;

public enum SelectionAnchorMode { Off, Shadow, Verify, Strict }

internal sealed record SelectionAnchorSettings(SelectionAnchorMode Mode, string Source, SelectionAnchorPolicy Policy, bool ForceActive)
{
    public bool Enabled => Mode != SelectionAnchorMode.Off;
    public bool Verify => Mode is SelectionAnchorMode.Verify or SelectionAnchorMode.Strict;
}

internal static class SelectionAnchorModeResolver
{
    public static SelectionAnchorMode Parse(string value) => Enum.TryParse<SelectionAnchorMode>(value, true, out var mode)
        && Enum.IsDefined(mode) ? mode : throw new ArgumentException($"Unknown selection anchor mode: {value}");

    public static SelectionAnchorSettings Resolve(ScanOptions options, ScanProfile profile, string? environment)
    {
        var source = "default";
        var mode = SelectionAnchorMode.Off;
        if (!string.IsNullOrWhiteSpace(environment)) { mode = Parse(environment); source = "env"; }
        else if (options.SelectionAnchorMode.HasValue) { mode = options.SelectionAnchorMode.Value; source = "cli"; }
        else if (!string.IsNullOrWhiteSpace(profile.SelectionAnchorMode)) { mode = Parse(profile.SelectionAnchorMode); source = "profile"; }
        if (!Enum.IsDefined(mode)) throw new ArgumentException($"Unknown selection anchor mode: {mode}");
        var policy = profile.VisualProbes?.SelectionAnchor;
        var valid = policy is not null && policy.Validate();
        return new(valid ? mode : SelectionAnchorMode.Off,
            valid ? source : "invalid_policy", policy ?? new(), options.AnchorForceActive);
    }
}

internal static class SelectionAnchorAdvancePolicy
{
    public static bool IsObservedBottom(int ticks, int stableAnchorFrames, int? rowsAdvanced, int bottomFrames, int requiredFrames) =>
        ticks >= 2 && stableAnchorFrames >= requiredFrames && rowsAdvanced == 0 && bottomFrames >= 2;
    public static RowAdvanceEvidence Reconcile(RowAdvanceEvidence structure, SelectionAnchorObservation anchor, int baselineRow)
    {
        RowAdvanceEvidence Result(RowAdvanceDecision decision, int rows, string reason, bool strong) => structure with
        { Decision = decision, BestRows = rows, Strong = strong, Reason = reason };
        if (!anchor.Unique)
        {
            if (baselineRow == 2 && anchor.Verdict == SelectionAnchorVerdict.None
                && structure.Strong && structure.Decision == RowAdvanceDecision.TwoRows)
                return Result(RowAdvanceDecision.TwoRows, 2, "anchor_left_viewport_two_rows", true);
            return structure with { Reason = structure.Reason + "_anchor_unavailable" };
        }
        var rows = baselineRow - anchor.Row!.Value;
        if (rows is < 0 or > 2) return Result(RowAdvanceDecision.Ambiguous, rows, "anchor_impossible_shift", false);
        if (rows == 0) return structure.MovedDistance <= RowAdvanceEvaluator.MovementTolerance
            ? Result(RowAdvanceDecision.NoMove, 0, "anchor_no_move_confirmed", true)
            : Result(RowAdvanceDecision.Ambiguous, 0, "anchor_grid_movement_conflict", false);
        var expected = rows == 1 ? RowAdvanceDecision.OneRow : RowAdvanceDecision.TwoRows;
        if (structure.Strong && structure.Decision != RowAdvanceDecision.Ambiguous && structure.Decision != expected)
            return Result(RowAdvanceDecision.Ambiguous, rows, "anchor_structure_conflict", false);
        return Result(expected, rows, rows == 1 ? "anchor_1_row_confirmed" : "anchor_2_rows_confirmed", true);
    }
}

internal sealed class SelectionAnchorReleaseTracker
{
    private int? previousRow;
    public int StableFrames { get; private set; }
    public void Observe(SelectionAnchorObservation anchor, RowAdvanceEvidence evidence, bool gridStable)
    {
        if (gridStable && anchor.Unique && evidence.Decision != RowAdvanceDecision.Ambiguous)
            StableFrames = previousRow == anchor.Row ? StableFrames + 1 : 1;
        else StableFrames = 0;
        previousRow = anchor.Row;
    }
}
