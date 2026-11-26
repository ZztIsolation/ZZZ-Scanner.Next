using System.Drawing;

namespace ZZZScannerNext.Scanning;

public sealed partial class ScanController
{
    private static async Task<bool> ConfirmTopWithSelectionAnchorAsync(GameWindow window, ScanProfile profile, ScanLog log, CancellationToken token)
    {
        var grid = window.ToScreenRectangle(profile.Rectangle("listGridRect"));
        var rects = BuildVisualRowSignatureRects(grid, profile.VisibleRows);
        var neutral = ListNeutralPoint(window, grid);
        var wheel = window.ToScreenPoint(profile.Point("listWheelArea"));
        var topPoint = window.ToScreenPoint(profile.Point("scrollBarTop"));
        var offset = profile.Point("driveDiscOffset");
        var step = profile.Point("driveDiscStep");
        var target = window.ToScreenPoint(new PointF(offset.X + profile.VisibleColumns * step.X, offset.Y + 2 * step.Y));
        var unchangedInputs = 0;
        var rearm = true;
        for (var input = 1; input <= 6; input++)
        {
            token.ThrowIfCancellationRequested();
            if (rearm)
            {
                window.LeftClick(target);
                await Task.Delay(Math.Max(120, profile.ClickDelayMs), token);
                window.MoveCursor(neutral);
                await Task.Delay(80, token);
            }
            var before = CaptureViewportSignatures(window, grid, rects, profile, profile.VisibleColumns);
            if (before.Anchor is not { Unique: true, Row: 2 })
            {
                log.WriteEvent("RESET_TOP_ANCHOR_UNAVAILABLE", $"input={input}, {before.Anchor?.Describe()}");
                return false;
            }
            window.MoveCursor(wheel);
            await Task.Delay(40, token);
            window.MouseWheel(120);
            await Task.Delay(300, token);
            window.MoveCursor(neutral);
            var previous = before;
            var stable = 0;
            int? lastRow = null;
            var confirmedThisInput = false;
            for (var sample = 1; sample <= 12; sample++)
            {
                await Task.Delay(Math.Max(15, profile.LoadPollMs), token);
                var after = CaptureViewportSignatures(window, grid, rects, profile, profile.VisibleColumns);
                var frameDistance = AnchorRowsDistance(previous.AnchorRows!, after.AnchorRows!);
                var contentDistance = AnchorRowsDistance(before.AnchorRows!, after.AnchorRows!);
                previous = after;
                var observed = after.Anchor!;
                stable = observed.Unique && frameDistance <= RowScrollCoordinator.StableFrameTolerance
                    ? lastRow == observed.Row ? stable + 1 : 1 : 0;
                lastRow = observed.Row;
                if (stable < 2) continue;
                if (observed.Row < 2)
                {
                    log.WriteEvent("RESET_TOP_ANCHOR_CONFLICT", $"input={input}, reason=opposite_direction, {observed.Describe()}");
                    return false;
                }
                var thumb = CaptureScrollbarThumbProbe(window, profile);
                var exactTop = thumb.Found && thumb.StartY == topPoint.Y;
                var topColor = ScrollTopResetCoordinator.IsTopColor(window.GetPixel(topPoint), profile.Color("scrollBar"), profile.ColorTolerance);
                var noMove = TopAnchorPolicy.IsNoMovementAtTop(observed.Row!.Value, contentDistance, stable, exactTop || topColor);
                unchangedInputs = noMove ? unchangedInputs + 1 : 0;
                log.WriteEvent("RESET_TOP_ANCHOR_EVIDENCE", $"input={input}/6, sample={sample}, rowBefore=2, rowAfter={observed.Row}, contentDistance={contentDistance}, stableFrames={stable}, exactThumbTop={exactTop}, topColor={topColor}, unchangedInputs={unchangedInputs}/2");
                if (unchangedInputs >= 2)
                {
                    log.WriteEvent("RESET_TOP_CONFIRMED", $"reason=anchor_no_move_and_track_top, verificationInputs={input}, unchangedInputs={unchangedInputs}");
                    return true;
                }
                rearm = observed.Row != 2;
                confirmedThisInput = true;
                break;
            }
            if (!confirmedThisInput)
            {
                log.WriteEvent("RESET_TOP_ANCHOR_UNSTABLE", $"input={input}, stableFrames={stable}");
                return false;
            }
        }
        return false;
    }

    private static int AnchorRowsDistance(IReadOnlyList<RowVisualSignature> before, IReadOnlyList<RowVisualSignature> after) =>
        before.Count == 0 || before.Count != after.Count ? int.MaxValue
        : (int)before.Select((row, i) => (double)RowVisualSignatureExtractor.Distance(row, after[i])).Average();
}

internal static class TopAnchorPolicy
{
    public static bool IsNoMovementAtTop(int observedRow, int contentDistance, int stableFrames, bool trackAtTop) =>
        observedRow == 2 && contentDistance <= RowAdvanceEvaluator.MovementTolerance && stableFrames >= 2 && trackAtTop;
}
