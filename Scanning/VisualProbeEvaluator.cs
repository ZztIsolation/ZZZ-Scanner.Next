using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

public enum VisualTransformClass
{
    Neutral,
    HighlightClipped,
    WarmShifted,
    SaturationShifted,
    ContrastShifted,
    Unknown
}

public sealed record ChromaticProbeResult(
    bool Passed,
    int Score,
    double Coverage,
    float MedianHue,
    float MedianSaturation,
    float MedianValue,
    int HueDelta,
    int SaturationDeltaPercent,
    int ValueDeltaPercent,
    Color MedianColor,
    VisualTransformClass TransformClass,
    double MeanLuminance,
    double LuminanceStandardDeviation);

public sealed record VisualRarityCandidate(string Rarity, Color Color);

public sealed record RarityProbeResult(
    string? Rarity,
    Color BestColor,
    string BestCandidate,
    int BestScore,
    int SecondScore,
    int Margin);

public readonly record struct RowPresenceProbeResult(
    bool Present,
    int ReferenceLuma,
    int CandidateLuma,
    int LumaDelta,
    int AllowedLumaDelta,
    int EdgeDensityPermille,
    int MinimumEdgeDensityPermille);

public sealed class SelectionRefreshGate
{
    private readonly int _requiredStableFrames;

    public SelectionRefreshGate(int requiredStableFrames = 2)
    {
        _requiredStableFrames = Math.Max(1, requiredStableFrames);
    }

    public bool ChangedFromTarget { get; private set; }
    public int StableFrames { get; private set; }
    public bool Accepted { get; private set; }

    public bool Observe(bool changedFromTarget, bool stableWithPreviousFrame)
    {
        if (!changedFromTarget)
        {
            ChangedFromTarget = false;
            StableFrames = 0;
            Accepted = false;
            return false;
        }

        ChangedFromTarget = true;
        StableFrames = stableWithPreviousFrame ? StableFrames + 1 : 1;
        Accepted = StableFrames >= _requiredStableFrames;
        return Accepted;
    }
}

public static class SelectionRefreshTiming
{
    public const int MaximumWaitMilliseconds = 600;

    public static int ResolveMaximumWaitMilliseconds(int loadTimeoutMilliseconds) =>
        Math.Min(Math.Max(1, loadTimeoutMilliseconds), MaximumWaitMilliseconds);
}

internal static class SelectionVisualProbe
{
    private const int EdgeBandPixels = 24;
    private const int SampleStride = 2;

    public static int[] CreateSamples(Bitmap image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width <= 0 || image.Height <= 0)
        {
            return [];
        }

        var bandX = Math.Min(EdgeBandPixels, Math.Max(1, image.Width / 3));
        var bandY = Math.Min(EdgeBandPixels, Math.Max(1, image.Height / 3));
        var samples = new List<int>();
        for (var y = 0; y < image.Height; y += SampleStride)
        {
            for (var x = 0; x < image.Width; x += SampleStride)
            {
                if (x >= bandX && x < image.Width - bandX
                    && y >= bandY && y < image.Height - bandY)
                {
                    continue;
                }

                var color = image.GetPixel(x, y);
                samples.Add((color.R * 299 + color.G * 587 + color.B * 114) / 1000);
                samples.Add(Math.Max(0, ((color.R + color.G) / 2) - color.B));
            }
        }

        return samples.ToArray();
    }

    public static int MeasureLocalizedMovement(
        IReadOnlyList<int> before,
        IReadOnlyList<int> after)
    {
        if (before.Count == 0 || before.Count != after.Count)
        {
            return int.MaxValue;
        }

        var differences = new int[before.Count];
        for (var index = 0; index < differences.Length; index++)
        {
            differences[index] = Math.Abs(before[index] - after[index]);
        }

        Array.Sort(differences);
        var retained = Math.Max(1, differences.Length / 4);
        var start = differences.Length - retained;
        var sum = 0L;
        for (var index = start; index < differences.Length; index++)
        {
            sum += differences[index];
        }

        return (int)Math.Min(int.MaxValue, sum / retained);
    }
}

internal static class PanelCaptureTimingPolicy
{
    public const int GdiSafeMinimumAcceptMilliseconds = 180;
    public const int SafeRequiredStableFrames = 2;

    public static int ResolvePollDelayMilliseconds(int requestedMilliseconds, string frameBackend) =>
        string.Equals(frameBackend, "dxgi-raw", StringComparison.OrdinalIgnoreCase)
            ? 0
            : Math.Max(1, requestedMilliseconds);

    public static int ResolveSafeMinimumAcceptMilliseconds(
        int profileMinimumMilliseconds,
        int requestedFloorMilliseconds,
        int loadTimeoutMilliseconds,
        string captureMode)
    {
        var captureMinimum = string.Equals(captureMode, "dxgi", StringComparison.OrdinalIgnoreCase)
            ? Math.Clamp(requestedFloorMilliseconds, 90, 120)
            : GdiSafeMinimumAcceptMilliseconds;
        return Math.Clamp(
            Math.Max(profileMinimumMilliseconds, captureMinimum),
            60,
            Math.Max(60, loadTimeoutMilliseconds));
    }
}

internal sealed class PanelTargetEvidenceGate
{
    public const int RequiredStableFrames = 2;
    public const double MinimumReliableChangeMilliseconds = 25.0;

    public bool Changed { get; private set; }
    public int StableFrames { get; private set; }
    public double? ChangeMilliseconds { get; private set; }
    public bool Stable => Changed && StableFrames >= RequiredStableFrames;

    public bool Observe(bool changedFromBaseline, bool stableWithPreviousFrame, double elapsedMilliseconds)
    {
        if (!changedFromBaseline || elapsedMilliseconds < MinimumReliableChangeMilliseconds)
        {
            Changed = false;
            StableFrames = 0;
            ChangeMilliseconds = null;
            return false;
        }

        Changed = true;
        ChangeMilliseconds ??= elapsedMilliseconds;
        StableFrames = stableWithPreviousFrame ? StableFrames + 1 : 1;
        return Stable;
    }

    public bool CanPromote(
        bool allowSelectionEvidence,
        bool postScrollFirstCell,
        bool selectionRoundTripReady,
        out string blockedReason)
    {
        if (!Stable)
        {
            blockedReason = Changed ? "selection_change_not_stable" : "no_selection_change";
            return false;
        }

        if (!selectionRoundTripReady)
        {
            blockedReason = postScrollFirstCell
                ? "post_scroll_first_cell"
                : "identical_neighbor_roundtrip_required";
            return false;
        }

        if (!allowSelectionEvidence)
        {
            blockedReason = "retry_or_recover_context";
            return false;
        }

        blockedReason = "";
        return true;
    }
}

public static class FirstPairBootstrapTiming
{
    public const int MaximumWaitMilliseconds = 1200;

    public static int ResolveMaximumWaitMilliseconds(int loadTimeoutMilliseconds) =>
        Math.Min(Math.Max(1, loadTimeoutMilliseconds), MaximumWaitMilliseconds);
}

internal enum ScrollTopResetTraceKind
{
    Probe,
    Wheel,
    Settle,
    Click,
    Confirmed,
    Failed
}

internal readonly record struct ScrollTopResetTrace(
    ScrollTopResetTraceKind Kind,
    string Phase,
    int Batch,
    int Tick,
    int Sample,
    int WheelTicks,
    int TopClicks,
    int StableMatches,
    Color ActualColor,
    Color ExpectedColor,
    int Tolerance,
    bool Matched,
    int DelayMilliseconds,
    long ElapsedMilliseconds,
    string MatchReason = "none");

internal readonly record struct ScrollTopResetResult(
    bool Confirmed,
    string Phase,
    int WheelTicks,
    int TopClicks,
    int ProbeSamples,
    Color LastActualColor,
    int StableMatches,
    long ElapsedMilliseconds);

internal static class ScrollTopResetCoordinator
{
    public const int WheelBatchSize = 4;
    public const int MaximumWheelTicks = 120;
    public const int ProbeSampleCount = 3;
    public const int RequiredStableMatches = 2;
    public const int ProbeSampleDelayMilliseconds = 40;
    public const int BatchSettleDelayMilliseconds = 160;
    public const int FallbackSettleDelayMilliseconds = 160;

    public static bool IsTopColor(Color actual, Color expected, int tolerance) =>
        actual.IsCloseTo(expected, Math.Max(0, tolerance));

    public static async Task<ScrollTopResetResult> RunAsync(
        int configuredMaximumWheelTicks,
        int wheelDelayMilliseconds,
        int clickDelayMilliseconds,
        Color expectedColor,
        int tolerance,
        Func<Color> captureTopPixel,
        Action wheelUp,
        Action clickTop,
        Action<ScrollTopResetTrace>? trace,
        CancellationToken token,
        Func<int, CancellationToken, Task>? delayAsync = null,
        Func<bool>? captureTopPosition = null)
    {
        ArgumentNullException.ThrowIfNull(captureTopPixel);
        ArgumentNullException.ThrowIfNull(wheelUp);
        ArgumentNullException.ThrowIfNull(clickTop);

        delayAsync ??= Task.Delay;
        var maximumWheelTicks = Math.Clamp(configuredMaximumWheelTicks, 0, MaximumWheelTicks);
        var wheelDelay = Math.Max(0, wheelDelayMilliseconds);
        var fallbackDelay = Math.Max(FallbackSettleDelayMilliseconds, clickDelayMilliseconds);
        var stopwatch = Stopwatch.StartNew();
        var wheelTicks = 0;
        var topClicks = 0;
        var probeSamples = 0;
        var lastActual = Color.Empty;
        var lastStableMatches = 0;

        async Task<bool> ProbeAsync(string phase, int batch)
        {
            var stableMatches = 0;
            for (var sample = 1; sample <= ProbeSampleCount; sample++)
            {
                token.ThrowIfCancellationRequested();
                lastActual = captureTopPixel();
                probeSamples++;
                var colorMatched = IsTopColor(lastActual, expectedColor, tolerance);
                var positionMatched = captureTopPosition?.Invoke() == true;
                var matched = colorMatched || positionMatched;
                var matchReason = positionMatched
                    ? "thumb_geometry"
                    : colorMatched
                        ? "top_color"
                        : "none";
                stableMatches = matched ? stableMatches + 1 : 0;
                lastStableMatches = stableMatches;
                trace?.Invoke(new ScrollTopResetTrace(
                    ScrollTopResetTraceKind.Probe,
                    phase,
                    batch,
                    wheelTicks,
                    sample,
                    wheelTicks,
                    topClicks,
                    stableMatches,
                    lastActual,
                    expectedColor,
                    tolerance,
                    matched,
                    0,
                    stopwatch.ElapsedMilliseconds,
                    matchReason));
                if (stableMatches >= RequiredStableMatches)
                {
                    return true;
                }

                if (sample < ProbeSampleCount)
                {
                    await delayAsync(ProbeSampleDelayMilliseconds, token);
                }
            }

            return false;
        }

        ScrollTopResetResult Result(bool confirmed, string phase) => new(
            confirmed,
            phase,
            wheelTicks,
            topClicks,
            probeSamples,
            lastActual,
            lastStableMatches,
            stopwatch.ElapsedMilliseconds);

        void TraceOutcome(ScrollTopResetTraceKind kind, string phase, int batch)
        {
            trace?.Invoke(new ScrollTopResetTrace(
                kind,
                phase,
                batch,
                wheelTicks,
                0,
                wheelTicks,
                topClicks,
                lastStableMatches,
                lastActual,
                expectedColor,
                tolerance,
                lastStableMatches >= RequiredStableMatches,
                0,
                stopwatch.ElapsedMilliseconds));
        }

        if (await ProbeAsync("initial", 0))
        {
            TraceOutcome(ScrollTopResetTraceKind.Confirmed, "initial", 0);
            return Result(true, "initial");
        }

        var batch = 0;
        while (wheelTicks < maximumWheelTicks)
        {
            batch++;
            var ticksInBatch = Math.Min(WheelBatchSize, maximumWheelTicks - wheelTicks);
            for (var tick = 0; tick < ticksInBatch; tick++)
            {
                token.ThrowIfCancellationRequested();
                wheelUp();
                wheelTicks++;
                trace?.Invoke(new ScrollTopResetTrace(
                    ScrollTopResetTraceKind.Wheel,
                    "wheel",
                    batch,
                    wheelTicks,
                    0,
                    wheelTicks,
                    topClicks,
                    0,
                    Color.Empty,
                    expectedColor,
                    tolerance,
                    false,
                    wheelDelay,
                    stopwatch.ElapsedMilliseconds));
                if (wheelDelay > 0)
                {
                    await delayAsync(wheelDelay, token);
                }
            }

            trace?.Invoke(new ScrollTopResetTrace(
                ScrollTopResetTraceKind.Settle,
                "wheel",
                batch,
                wheelTicks,
                0,
                wheelTicks,
                topClicks,
                0,
                Color.Empty,
                expectedColor,
                tolerance,
                false,
                BatchSettleDelayMilliseconds,
                stopwatch.ElapsedMilliseconds));
            await delayAsync(BatchSettleDelayMilliseconds, token);
            if (await ProbeAsync("wheel", batch))
            {
                TraceOutcome(ScrollTopResetTraceKind.Confirmed, "wheel", batch);
                return Result(true, "wheel");
            }
        }

        token.ThrowIfCancellationRequested();
        clickTop();
        topClicks++;
        trace?.Invoke(new ScrollTopResetTrace(
            ScrollTopResetTraceKind.Click,
            "fallback",
            batch,
            wheelTicks,
            0,
            wheelTicks,
            topClicks,
            0,
            Color.Empty,
            expectedColor,
            tolerance,
            false,
            fallbackDelay,
            stopwatch.ElapsedMilliseconds));
        trace?.Invoke(new ScrollTopResetTrace(
            ScrollTopResetTraceKind.Settle,
            "fallback",
            batch,
            wheelTicks,
            0,
            wheelTicks,
            topClicks,
            0,
            Color.Empty,
            expectedColor,
            tolerance,
            false,
            fallbackDelay,
            stopwatch.ElapsedMilliseconds));
        await delayAsync(fallbackDelay, token);
        if (await ProbeAsync("fallback", batch))
        {
            TraceOutcome(ScrollTopResetTraceKind.Confirmed, "fallback", batch);
            return Result(true, "fallback");
        }

        TraceOutcome(ScrollTopResetTraceKind.Failed, "fallback", batch);
        return Result(false, "fallback");
    }
}

internal static class ScrollbarTopResetPlanner
{
    public const int TopEdgeTolerancePixels = 2;

    public static int GetTopCenterY(
        int trackEndpointAY,
        int trackEndpointBY,
        int thumbStartY,
        int thumbEndY)
    {
        var trackStart = Math.Min(trackEndpointAY, trackEndpointBY);
        var trackEnd = Math.Max(trackEndpointAY, trackEndpointBY);
        var trackHeight = Math.Max(1, trackEnd - trackStart + 1);
        var thumbHeight = Math.Clamp(Math.Abs(thumbEndY - thumbStartY) + 1, 1, trackHeight);
        return trackStart + (thumbHeight - 1) / 2;
    }

    public static bool IsAtTop(
        int trackEndpointAY,
        int trackEndpointBY,
        int thumbStartY,
        int thumbEndY) =>
        Math.Abs(
            Math.Min(thumbStartY, thumbEndY)
            - Math.Min(trackEndpointAY, trackEndpointBY)) <= TopEdgeTolerancePixels;
}

internal enum RowAdvanceDecision
{
    NoMove,
    OneRow,
    TwoRows,
    Ambiguous
}

internal readonly record struct RowVisualSignature(ulong Hash, short[] NormalizedLuminance);

internal static class RowVisualSignatureExtractor
{
    public const int Columns = 24;
    public const int Rows = 8;
    public const int TrimPercent = 10;

    public static RowVisualSignature Create(Bitmap image, Rectangle rect) =>
        Create(image.Width, image.Height, image.GetPixel, rect);

    public static RowVisualSignature Create(CapturedFrame image, Rectangle rect) =>
        Create(image.Width, image.Height, image.GetPixel, rect);

    public static int Distance(RowVisualSignature left, RowVisualSignature right)
    {
        var leftSamples = left.NormalizedLuminance;
        var rightSamples = right.NormalizedLuminance;
        if (leftSamples.Length == 0 || leftSamples.Length != rightSamples.Length)
        {
            return left.Hash == right.Hash ? 0 : int.MaxValue;
        }

        var differences = new int[leftSamples.Length];
        for (var index = 0; index < differences.Length; index++)
        {
            differences[index] = Math.Abs(leftSamples[index] - rightSamples[index]);
        }

        Array.Sort(differences);
        var dropped = differences.Length * TrimPercent / 100;
        var retained = Math.Max(1, differences.Length - dropped);
        var sum = 0L;
        for (var index = 0; index < retained; index++)
        {
            sum += differences[index];
        }

        return (int)Math.Min(int.MaxValue, sum / retained);
    }

    private static RowVisualSignature Create(
        int width,
        int height,
        Func<int, int, Color> getPixel,
        Rectangle rect)
    {
        if (width <= 0 || height <= 0)
        {
            return new RowVisualSignature(0, []);
        }

        rect = Rectangle.Intersect(rect, new Rectangle(0, 0, width, height));
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return new RowVisualSignature(0, []);
        }

        var luminance = new int[Columns * Rows];
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        var hash = offset;
        var index = 0;
        for (var row = 0; row < Rows; row++)
        {
            var y = rect.Top + Math.Min(rect.Height - 1, ((row * 2 + 1) * rect.Height) / (Rows * 2));
            for (var column = 0; column < Columns; column++)
            {
                var x = rect.Left + Math.Min(rect.Width - 1, ((column * 2 + 1) * rect.Width) / (Columns * 2));
                var color = getPixel(x, y);
                luminance[index++] = (color.R * 299 + color.G * 587 + color.B * 114) / 1000;
                hash ^= color.R;
                hash *= prime;
                hash ^= color.G;
                hash *= prime;
                hash ^= color.B;
                hash *= prime;
            }
        }

        var ordered = (int[])luminance.Clone();
        Array.Sort(ordered);
        var median = (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2;
        var normalized = new short[luminance.Length];
        for (var sample = 0; sample < luminance.Length; sample++)
        {
            normalized[sample] = (short)(luminance[sample] - median);
        }

        return new RowVisualSignature(hash, normalized);
    }
}

internal readonly record struct RowAdvanceEvidence(
    RowAdvanceDecision Decision,
    int BestRows,
    int BestScore,
    int SecondScore,
    int Margin,
    bool Strong,
    int NoMoveScore,
    int OneRowScore,
    int TwoRowScore,
    int MovedDistance,
    string Reason)
{
    public bool UsesScrollbarPosition =>
        Reason.StartsWith("scrollbar_", StringComparison.Ordinal);

    public int? AcceptedRows => Decision switch
    {
        RowAdvanceDecision.NoMove => 0,
        RowAdvanceDecision.OneRow => 1,
        RowAdvanceDecision.TwoRows => 2,
        _ => null
    };
}

internal static class RowAdvanceEvaluator
{
    public const int MatchTolerance = 48;
    public const int ClearMargin = 12;
    public const int StrongMargin = 18;
    public const int MovementTolerance = 6;

    public static RowAdvanceEvidence ReconcileScrollbarPosition(
        RowAdvanceEvidence structuralEvidence,
        int directionalPixelDelta,
        double pixelsPerRow)
    {
        if (pixelsPerRow < 1)
        {
            return structuralEvidence;
        }

        var alignmentTolerance = Math.Max(1.5, pixelsPerRow * 0.35);
        if (directionalPixelDelta < -alignmentTolerance)
        {
            return structuralEvidence with
            {
                Decision = RowAdvanceDecision.Ambiguous,
                Strong = false,
                Reason = "scrollbar_moved_opposite_direction"
            };
        }

        var estimatedRows = Math.Max(0, (int)Math.Round(
            Math.Max(0, directionalPixelDelta) / pixelsPerRow,
            MidpointRounding.AwayFromZero));
        var residual = Math.Abs(directionalPixelDelta - estimatedRows * pixelsPerRow);
        if (residual > alignmentTolerance)
        {
            return structuralEvidence with
            {
                Decision = RowAdvanceDecision.Ambiguous,
                Strong = false,
                Reason = "scrollbar_position_between_rows"
            };
        }

        if (estimatedRows > 2)
        {
            return structuralEvidence with
            {
                Decision = RowAdvanceDecision.Ambiguous,
                BestRows = estimatedRows,
                Strong = false,
                Reason = "scrollbar_advanced_more_than_two_rows"
            };
        }

        var scrollbarDecision = estimatedRows switch
        {
            0 => RowAdvanceDecision.NoMove,
            1 => RowAdvanceDecision.OneRow,
            2 => RowAdvanceDecision.TwoRows,
            _ => RowAdvanceDecision.Ambiguous
        };
        if (estimatedRows == 0
            && structuralEvidence.MovedDistance > MovementTolerance)
        {
            return structuralEvidence with
            {
                Decision = RowAdvanceDecision.Ambiguous,
                Strong = false,
                Reason = "scrollbar_grid_movement_conflict"
            };
        }

        var hasPositiveStructureConflict = estimatedRows > 0
            && structuralEvidence.Decision is RowAdvanceDecision.OneRow or RowAdvanceDecision.TwoRows
            && structuralEvidence.Decision != scrollbarDecision;
        if (hasPositiveStructureConflict
            && structuralEvidence.Decision == RowAdvanceDecision.TwoRows
            && structuralEvidence.Strong)
        {
            return structuralEvidence;
        }

        if (hasPositiveStructureConflict && structuralEvidence.Strong)
        {
            return structuralEvidence with
            {
                Decision = RowAdvanceDecision.Ambiguous,
                Strong = false,
                Reason = "scrollbar_row_structure_conflict"
            };
        }

        var selectedScore = estimatedRows switch
        {
            0 => structuralEvidence.NoMoveScore,
            1 => structuralEvidence.OneRowScore,
            2 => structuralEvidence.TwoRowScore,
            _ => structuralEvidence.BestScore
        };
        var secondScore = estimatedRows switch
        {
            0 => Math.Min(structuralEvidence.OneRowScore, structuralEvidence.TwoRowScore),
            1 => Math.Min(structuralEvidence.NoMoveScore, structuralEvidence.TwoRowScore),
            2 => Math.Min(structuralEvidence.NoMoveScore, structuralEvidence.OneRowScore),
            _ => structuralEvidence.SecondScore
        };
        return structuralEvidence with
        {
            Decision = scrollbarDecision,
            BestRows = estimatedRows,
            BestScore = selectedScore,
            SecondScore = secondScore,
            Margin = secondScore - selectedScore,
            Strong = residual <= Math.Max(1, pixelsPerRow * 0.25),
            Reason = estimatedRows == 0
                ? "scrollbar_no_move_confirmed"
                : hasPositiveStructureConflict
                    ? $"scrollbar_{estimatedRows}_row_position_confirmed_weak_structure_override"
                    : $"scrollbar_{estimatedRows}_row_position_confirmed"
        };
    }

    public static RowAdvanceEvidence Evaluate(
        int noMoveScore,
        int oneRowScore,
        int twoRowScore,
        int movedDistance)
    {
        var ordered = new[]
        {
            (Rows: 0, Score: noMoveScore),
            (Rows: 1, Score: oneRowScore),
            (Rows: 2, Score: twoRowScore)
        }
        .OrderBy(candidate => candidate.Score)
        .ThenBy(candidate => candidate.Rows)
        .ToArray();
        var best = ordered[0];
        var secondScore = ordered[1].Score;
        var margin = secondScore == int.MaxValue || best.Score == int.MaxValue
            ? 0
            : Math.Max(0, secondScore - best.Score);
        var strong = best.Score <= MatchTolerance && margin >= StrongMargin;

        if (best.Score > MatchTolerance)
        {
            return Evidence(RowAdvanceDecision.Ambiguous, "no_structural_match");
        }

        if (margin < ClearMargin)
        {
            return Evidence(RowAdvanceDecision.Ambiguous, "insufficient_margin");
        }

        if (best.Rows == 0)
        {
            return Evidence(RowAdvanceDecision.NoMove, "no_move_structurally_confirmed");
        }

        if (movedDistance <= MovementTolerance)
        {
            return Evidence(RowAdvanceDecision.Ambiguous, "positive_shift_without_grid_movement");
        }

        return Evidence(
            best.Rows == 1 ? RowAdvanceDecision.OneRow : RowAdvanceDecision.TwoRows,
            best.Rows == 1 ? "one_row_structurally_confirmed" : "two_rows_structurally_confirmed");

        RowAdvanceEvidence Evidence(RowAdvanceDecision decision, string reason) => new(
            decision,
            best.Rows,
            best.Score,
            secondScore,
            margin,
            strong,
            noMoveScore,
            oneRowScore,
            twoRowScore,
            movedDistance,
            reason);
    }
}

internal enum RowScrollCoordinatorOutcome
{
    Accepted,
    AmbiguousStop,
    NoMovementStop
}

internal enum RowScrollPhase
{
    FastProbe,
    Settle
}

internal readonly record struct RowScrollObservation(
    RowAdvanceEvidence Evidence,
    int FrameDistance,
    int? ScrollbarPixelDelta = null)
{
    public bool Stable => FrameDistance <= RowScrollCoordinator.StableFrameTolerance;
    public bool ConfirmedZeroMovement => Stable
        && Evidence.Decision == RowAdvanceDecision.NoMove
        && Evidence.MovedDistance <= RowAdvanceEvaluator.MovementTolerance
        && ScrollbarPixelDelta == 0;
}

internal readonly record struct RowScrollCoordinatorTrace(
    int Tick,
    int Sample,
    RowScrollPhase Phase,
    RowScrollObservation Observation,
    string Action);

internal readonly record struct RowScrollCoordinatorResult(
    RowScrollCoordinatorOutcome Outcome,
    int Tick,
    int Sample,
    RowScrollPhase Phase,
    RowScrollObservation Observation,
    int SettleSamples,
    double SettleElapsedMilliseconds)
{
    public bool Accepted => Outcome == RowScrollCoordinatorOutcome.Accepted;
    public RowAdvanceEvidence Evidence => Observation.Evidence;
}

internal static class RowScrollReleasePolicy
{
    public static bool TryCreateSettleTimeoutProvisional(
        RowScrollCoordinatorResult result,
        out RowAdvanceEvidence provisionalEvidence)
    {
        provisionalEvidence = result.Evidence;
        if (result.Outcome != RowScrollCoordinatorOutcome.AmbiguousStop
            || !result.Evidence.Strong
            || !string.Equals(
                result.Evidence.Reason,
                "insufficient_stable_confirmation",
                StringComparison.Ordinal)
            || result.Evidence.BestRows is not (1 or 2))
        {
            return false;
        }

        provisionalEvidence = result.Evidence with
        {
            Decision = result.Evidence.BestRows == 1
                ? RowAdvanceDecision.OneRow
                : RowAdvanceDecision.TwoRows,
            Reason = "settle_timeout_strong_positive_release_pending"
        };
        return true;
    }
}

internal static class RowScrollCoordinator
{
    public const int MaximumSamplesPerTick = 3;
    public const int MaximumSettleMilliseconds = 500;
    public const int StableFrameTolerance = 4;
    public const int MinimumPartialMovementDistance = 12;

    public static async Task<RowScrollCoordinatorResult> RunAsync(
        int maximumTicks,
        ScrollAcceptMode acceptMode,
        int settlePollMilliseconds,
        Func<int, CancellationToken, Task> issueWheelAsync,
        Func<int, int, RowScrollPhase, CancellationToken, Task<RowScrollObservation>> captureObservationAsync,
        Action<RowScrollCoordinatorTrace>? trace,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(issueWheelAsync);
        ArgumentNullException.ThrowIfNull(captureObservationAsync);
        maximumTicks = Math.Clamp(maximumTicks, 1, 2);
        settlePollMilliseconds = Math.Max(1, settlePollMilliseconds);
        var maximumSettleSamples = Math.Max(1, (int)Math.Ceiling(MaximumSettleMilliseconds / (double)settlePollMilliseconds));
        RowScrollObservation lastObservation = default;
        var totalSettleSamples = 0;
        var settleWatch = new System.Diagnostics.Stopwatch();

        for (var tick = 1; tick <= maximumTicks; tick++)
        {
            token.ThrowIfCancellationRequested();
            await issueWheelAsync(tick, token);
            RowAdvanceDecision? consecutiveDecision = null;
            var consecutiveFrames = 0;
            var enterSettle = false;

            for (var sample = 1; sample <= MaximumSamplesPerTick; sample++)
            {
                token.ThrowIfCancellationRequested();
                lastObservation = await captureObservationAsync(tick, sample, RowScrollPhase.FastProbe, token);
                var evidence = lastObservation.Evidence;

                if (IsConfirmedOvershot(lastObservation))
                {
                    trace?.Invoke(new RowScrollCoordinatorTrace(
                        tick,
                        sample,
                        RowScrollPhase.FastProbe,
                        lastObservation,
                        "accept_overshot_for_block"));
                    return Result(RowScrollCoordinatorOutcome.Accepted, tick, sample, RowScrollPhase.FastProbe, lastObservation);
                }

                if (evidence.Decision == RowAdvanceDecision.NoMove)
                {
                    enterSettle = true;
                    trace?.Invoke(new RowScrollCoordinatorTrace(
                        tick,
                        sample,
                        RowScrollPhase.FastProbe,
                        lastObservation,
                        lastObservation.Stable
                            ? "enter_settle_no_move"
                            : "enter_settle_unstable_no_move"));
                    break;
                }

                if (evidence.Decision is RowAdvanceDecision.OneRow or RowAdvanceDecision.TwoRows)
                {
                    ObservePositive(lastObservation, ref consecutiveDecision, ref consecutiveFrames);
                    if (CanAccept(lastObservation, consecutiveFrames, acceptMode))
                    {
                        trace?.Invoke(new RowScrollCoordinatorTrace(tick, sample, RowScrollPhase.FastProbe, lastObservation, "accept"));
                        return Result(RowScrollCoordinatorOutcome.Accepted, tick, sample, RowScrollPhase.FastProbe, lastObservation);
                    }
                }
                else
                {
                    consecutiveDecision = null;
                    consecutiveFrames = 0;
                    enterSettle = true;
                }

                trace?.Invoke(new RowScrollCoordinatorTrace(
                    tick,
                    sample,
                    RowScrollPhase.FastProbe,
                    lastObservation,
                    enterSettle || sample == MaximumSamplesPerTick ? "enter_settle" : "resample"));
                if (enterSettle || sample == MaximumSamplesPerTick)
                {
                    enterSettle = true;
                    break;
                }
            }

            if (!enterSettle)
            {
                continue;
            }

            consecutiveDecision = null;
            consecutiveFrames = 0;
            var consecutiveStableNoMove = 0;
            settleWatch.Start();
            var settlePhaseWatch = System.Diagnostics.Stopwatch.StartNew();
            for (var settleSample = 1; settleSample <= maximumSettleSamples; settleSample++)
            {
                token.ThrowIfCancellationRequested();
                totalSettleSamples++;
                lastObservation = await captureObservationAsync(tick, settleSample, RowScrollPhase.Settle, token);
                var evidence = lastObservation.Evidence;

                if (evidence.Decision is RowAdvanceDecision.OneRow or RowAdvanceDecision.TwoRows)
                {
                    consecutiveStableNoMove = 0;
                    if (IsConfirmedOvershot(lastObservation))
                    {
                        trace?.Invoke(new RowScrollCoordinatorTrace(
                            tick,
                            settleSample,
                            RowScrollPhase.Settle,
                            lastObservation,
                            "accept_overshot_for_block"));
                        settleWatch.Stop();
                        return Result(RowScrollCoordinatorOutcome.Accepted, tick, settleSample, RowScrollPhase.Settle, lastObservation);
                    }

                    ObservePositive(lastObservation, ref consecutiveDecision, ref consecutiveFrames);
                    if (CanAccept(lastObservation, consecutiveFrames, acceptMode))
                    {
                        trace?.Invoke(new RowScrollCoordinatorTrace(tick, settleSample, RowScrollPhase.Settle, lastObservation, "accept_settled"));
                        settleWatch.Stop();
                        return Result(RowScrollCoordinatorOutcome.Accepted, tick, settleSample, RowScrollPhase.Settle, lastObservation);
                    }
                }
                else if (lastObservation.ConfirmedZeroMovement)
                {
                    consecutiveDecision = null;
                    consecutiveFrames = 0;
                    consecutiveStableNoMove++;
                }
                else
                {
                    consecutiveDecision = null;
                    consecutiveFrames = 0;
                    consecutiveStableNoMove = 0;
                }

                var settleTimedOut = settleSample == maximumSettleSamples
                    || settlePhaseWatch.ElapsedMilliseconds >= MaximumSettleMilliseconds;
                if (!settleTimedOut)
                {
                    trace?.Invoke(new RowScrollCoordinatorTrace(
                        tick,
                        settleSample,
                        RowScrollPhase.Settle,
                        lastObservation,
                        "settle_resample"));
                    continue;
                }

                if (consecutiveStableNoMove >= 3 && lastObservation.ConfirmedZeroMovement)
                {
                    var action = tick < maximumTicks
                        ? "retry_after_full_no_move_window"
                        : "stop_no_movement";
                    trace?.Invoke(new RowScrollCoordinatorTrace(
                        tick,
                        settleSample,
                        RowScrollPhase.Settle,
                        lastObservation,
                        action));
                    if (tick == maximumTicks)
                    {
                        settleWatch.Stop();
                        return Result(RowScrollCoordinatorOutcome.NoMovementStop, tick, settleSample, RowScrollPhase.Settle, lastObservation);
                    }

                    break;
                }

                trace?.Invoke(new RowScrollCoordinatorTrace(
                    tick,
                    settleSample,
                    RowScrollPhase.Settle,
                    lastObservation,
                    "stop_ambiguous"));
                {
                    var ambiguous = evidence with
                    {
                        Decision = RowAdvanceDecision.Ambiguous,
                        Reason = evidence.Decision == RowAdvanceDecision.Ambiguous
                            ? evidence.Reason
                            : "insufficient_stable_confirmation"
                    };
                    lastObservation = lastObservation with { Evidence = ambiguous };
                    settleWatch.Stop();
                    return Result(RowScrollCoordinatorOutcome.AmbiguousStop, tick, settleSample, RowScrollPhase.Settle, lastObservation);
                }
            }
            settleWatch.Stop();
        }

        return Result(RowScrollCoordinatorOutcome.NoMovementStop, maximumTicks, 1, RowScrollPhase.FastProbe, lastObservation);

        void ObservePositive(
            RowScrollObservation observation,
            ref RowAdvanceDecision? decision,
            ref int frames)
        {
            if (!observation.Stable)
            {
                decision = null;
                frames = 0;
                return;
            }

            if (decision == observation.Evidence.Decision)
            {
                frames++;
            }
            else
            {
                decision = observation.Evidence.Decision;
                frames = 1;
            }
        }

        static bool CanAccept(
            RowScrollObservation observation,
            int consecutiveFrames,
            ScrollAcceptMode mode)
        {
            if (!observation.Stable)
            {
                return false;
            }

            var evidence = observation.Evidence;
            var earlyStrongOneRow = mode == ScrollAcceptMode.EarlyOneRow
                && evidence.Decision == RowAdvanceDecision.OneRow
                && evidence.Strong
                && !evidence.UsesScrollbarPosition;
            return earlyStrongOneRow || consecutiveFrames >= 2;
        }

        static bool IsConfirmedOvershot(RowScrollObservation observation) =>
            observation.Evidence.Decision == RowAdvanceDecision.TwoRows
            && (observation.Stable
                || string.Equals(
                    observation.Evidence.Reason,
                    "scrollbar_overshot_above_single_row_limit",
                    StringComparison.Ordinal));

        RowScrollCoordinatorResult Result(
            RowScrollCoordinatorOutcome outcome,
            int tick,
            int sample,
            RowScrollPhase phase,
            RowScrollObservation observation) => new(
                outcome,
                tick,
                sample,
                phase,
                observation,
                totalSettleSamples,
                settleWatch.Elapsed.TotalMilliseconds);
    }
}

public readonly record struct FirstPairWitnessCoordinate(int VisualRow, int Column);

public static class FirstPairWitnessPlanner
{
    public static IReadOnlyList<FirstPairWitnessCoordinate> Build(
        int firstVisualRow,
        int firstColumn,
        int secondColumn,
        int maxColumns,
        int visibleRows,
        int visibleColumns)
    {
        maxColumns = Math.Max(1, maxColumns);
        visibleRows = Math.Max(1, visibleRows);
        visibleColumns = Math.Max(1, visibleColumns);
        var candidates = new List<FirstPairWitnessCoordinate>();

        for (var column = secondColumn + 1; column <= maxColumns; column++)
        {
            candidates.Add(new FirstPairWitnessCoordinate(firstVisualRow, column));
        }

        if (firstVisualRow == 1)
        {
            for (var visualRow = 2; visualRow <= visibleRows; visualRow++)
            {
                for (var column = 1; column <= visibleColumns; column++)
                {
                    candidates.Add(new FirstPairWitnessCoordinate(visualRow, column));
                }
            }
        }

        return candidates
            .Where(candidate => candidate.VisualRow != firstVisualRow
                || (candidate.Column != firstColumn && candidate.Column != secondColumn))
            .Distinct()
            .ToArray();
    }
}

public readonly record struct SelectionRefreshObservation(
    bool ChangedFromTarget,
    bool StableWithPreviousFrame);

public sealed record SelectionRefreshWaitResult(
    bool Ready,
    bool ChangedFromTarget,
    int StableFrames,
    int FrameCount,
    double ElapsedMilliseconds);

public readonly record struct PanelCaptureInitialGate(
    bool SawPanelChange,
    bool SelectionChanged,
    double? ChangeMilliseconds);

public static class PanelCaptureGate
{
    public static PanelCaptureInitialGate Initialize(bool hasPanelBaseline) =>
        new(
            SawPanelChange: false,
            SelectionChanged: false,
            ChangeMilliseconds: null);

    public static bool RequiresFirstCellNeighborRoundTrip(bool firstQueuedItem) => firstQueuedItem;

    public static bool IsStrongChangeCurrentFrame(
        int changeDistance,
        double elapsedMilliseconds,
        int strongChangeTolerance,
        double minimumReliableChangeMilliseconds) =>
        changeDistance > strongChangeTolerance
        && elapsedMilliseconds >= minimumReliableChangeMilliseconds;

    public static bool LatchStrongChange(
        bool alreadyChanged,
        int changeDistance,
        double elapsedMilliseconds,
        int strongChangeTolerance,
        double minimumReliableChangeMilliseconds) =>
        alreadyChanged || IsStrongChangeCurrentFrame(
            changeDistance,
            elapsedMilliseconds,
            strongChangeTolerance,
            minimumReliableChangeMilliseconds);
}

public static class SelectionRefreshWaiter
{
    public static async Task<SelectionRefreshWaitResult> WaitAsync(
        Func<SelectionRefreshObservation> observe,
        int maximumWaitMilliseconds,
        int pollMilliseconds,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(observe);
        maximumWaitMilliseconds = Math.Max(1, maximumWaitMilliseconds);
        pollMilliseconds = Math.Max(1, pollMilliseconds);
        var wait = Stopwatch.StartNew();
        var gate = new SelectionRefreshGate(requiredStableFrames: 2);
        var frameCount = 0;
        while (wait.ElapsedMilliseconds < maximumWaitMilliseconds)
        {
            token.ThrowIfCancellationRequested();
            var observation = observe();
            frameCount++;
            if (gate.Observe(observation.ChangedFromTarget, observation.StableWithPreviousFrame))
            {
                return new SelectionRefreshWaitResult(
                    true,
                    gate.ChangedFromTarget,
                    gate.StableFrames,
                    frameCount,
                    wait.Elapsed.TotalMilliseconds);
            }

            var remainingMilliseconds = maximumWaitMilliseconds - (int)wait.ElapsedMilliseconds;
            if (remainingMilliseconds <= 0)
            {
                break;
            }

            await Task.Delay(Math.Min(pollMilliseconds, remainingMilliseconds), token);
        }

        return new SelectionRefreshWaitResult(
            false,
            gate.ChangedFromTarget,
            gate.StableFrames,
            frameCount,
            wait.Elapsed.TotalMilliseconds);
    }
}

public static class VisualProbeEvaluator
{
    public static ChromaticProbeResult EvaluateChromaticAnchor(
        Bitmap image,
        Color expected,
        ChromaticProbePolicy? policy = null)
    {
        policy ??= new ChromaticProbePolicy();
        var expectedHsv = ToHsv(expected);
        var matched = new List<Color>();
        var luminance = new double[Math.Max(1, image.Width * image.Height)];
        var luminanceIndex = 0;

        for (var y = 0; y < image.Height; y++)
        {
            for (var x = 0; x < image.Width; x++)
            {
                var color = image.GetPixel(x, y);
                luminance[luminanceIndex++] = Luminance(color);
                var hsv = ToHsv(color);
                if (hsv.Saturation < policy.MinimumSaturation || hsv.Value < policy.MinimumValue)
                {
                    continue;
                }

                if (HueDelta(hsv.Hue, expectedHsv.Hue) <= policy.HueToleranceDegrees)
                {
                    matched.Add(color);
                }
            }
        }

        var coverage = image.Width <= 0 || image.Height <= 0
            ? 0
            : matched.Count / (double)(image.Width * image.Height);
        var medianColor = matched.Count == 0 ? Color.Empty : MedianColor(matched);
        var medianHsv = matched.Count == 0 ? default : ToHsv(medianColor);
        var hueDelta = matched.Count == 0 ? 180 : (int)Math.Round(HueDelta(medianHsv.Hue, expectedHsv.Hue));
        var saturationDelta = matched.Count == 0
            ? 100
            : (int)Math.Round(Math.Abs(medianHsv.Saturation - expectedHsv.Saturation) * 100);
        var valueDelta = matched.Count == 0
            ? 100
            : (int)Math.Round(Math.Abs(medianHsv.Value - expectedHsv.Value) * 100);
        var coverageScore = Math.Min(1, coverage / Math.Max(0.001, policy.MinimumCoverage));
        var hueScore = Math.Max(0, 1 - (hueDelta / (double)Math.Max(1, policy.HueToleranceDegrees)));
        var score = (int)Math.Round(Math.Clamp((coverageScore * 0.65) + (hueScore * 0.35), 0, 1) * 100);
        var passed = coverage >= policy.MinimumCoverage && hueDelta <= policy.HueToleranceDegrees;
        var mean = luminance.Take(luminanceIndex).DefaultIfEmpty(0).Average();
        var variance = luminance.Take(luminanceIndex).Select(value => Math.Pow(value - mean, 2)).DefaultIfEmpty(0).Average();
        var transform = passed
            ? ClassifyTransform(expected, expectedHsv, medianColor, medianHsv, hueDelta, saturationDelta, valueDelta)
            : VisualTransformClass.Unknown;

        return new ChromaticProbeResult(
            passed,
            score,
            coverage,
            medianHsv.Hue,
            medianHsv.Saturation,
            medianHsv.Value,
            hueDelta,
            saturationDelta,
            valueDelta,
            medianColor,
            transform,
            mean,
            Math.Sqrt(variance));
    }

    public static RarityProbeResult EvaluateRarity(
        Bitmap image,
        IReadOnlyList<VisualRarityCandidate> candidates,
        IEnumerable<Point> points,
        RarityProbePolicy? policy = null)
    {
        policy ??= new RarityProbePolicy();
        var votes = candidates.ToDictionary(
            candidate => candidate.Rarity,
            _ => new RarityVote());

        foreach (var point in points)
        {
            var x = Math.Clamp(point.X, 0, image.Width - 1);
            var y = Math.Clamp(point.Y, 0, image.Height - 1);
            var color = image.GetPixel(x, y);
            var scores = candidates
                .Select(candidate => new
                {
                    candidate.Rarity,
                    Score = ColorScore(color, candidate.Color)
                })
                .OrderBy(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Rarity, StringComparer.Ordinal)
                .ToArray();
            if (scores.Length == 0)
            {
                continue;
            }

            var margin = scores.Length > 1 ? scores[1].Score - scores[0].Score : int.MaxValue;
            if (scores[0].Score > policy.MaximumScore || margin < policy.MinimumCandidateMargin)
            {
                continue;
            }

            votes[scores[0].Rarity].Observe(scores[0].Score, margin, color);
        }

        var ordered = votes
            .OrderByDescending(pair => pair.Value.Count)
            .ThenBy(pair => pair.Value.BestScore)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .ToArray();
        if (ordered.Length == 0 || ordered[0].Value.Count == 0)
        {
            return new RarityProbeResult(null, Color.Empty, "", int.MaxValue, int.MaxValue, 0);
        }

        var best = ordered[0];
        var tiedVotes = ordered.Length > 1 && ordered[1].Value.Count == best.Value.Count;
        var rarity = tiedVotes ? null : best.Key;
        var secondScore = best.Value.BestMargin == int.MaxValue
            ? int.MaxValue
            : best.Value.BestScore + best.Value.BestMargin;
        return new RarityProbeResult(
            rarity,
            best.Value.BestColor,
            best.Key,
            best.Value.BestScore,
            secondScore,
            best.Value.BestMargin);
    }

    public static RowPresenceProbeResult EvaluateRelativeTextRowPresence(
        Bitmap image,
        Rectangle referenceRoi,
        Rectangle candidateRoi,
        Point sampleOffset,
        RowPresenceProbePolicy? policy = null)
    {
        policy ??= new RowPresenceProbePolicy();
        return EvaluateRelativeTextRowPresence(
            image.Width,
            image.Height,
            image.GetPixel,
            referenceRoi,
            candidateRoi,
            sampleOffset,
            policy);
    }

    internal static RowPresenceProbeResult EvaluateRelativeTextRowPresence(
        CapturedFrame image,
        Rectangle referenceRoi,
        Rectangle candidateRoi,
        Point sampleOffset,
        RowPresenceProbePolicy? policy = null)
    {
        policy ??= new RowPresenceProbePolicy();
        return EvaluateRelativeTextRowPresence(
            image.Width,
            image.Height,
            image.GetPixel,
            referenceRoi,
            candidateRoi,
            sampleOffset,
            policy);
    }

    public static bool IsRelativeTextRowPresent(
        Bitmap image,
        Rectangle referenceRoi,
        Rectangle candidateRoi,
        Point sampleOffset,
        RowPresenceProbePolicy? policy = null) =>
        EvaluateRelativeTextRowPresence(image, referenceRoi, candidateRoi, sampleOffset, policy).Present;

    internal static bool IsRelativeTextRowPresent(
        CapturedFrame image,
        Rectangle referenceRoi,
        Rectangle candidateRoi,
        Point sampleOffset,
        RowPresenceProbePolicy? policy = null) =>
        EvaluateRelativeTextRowPresence(image, referenceRoi, candidateRoi, sampleOffset, policy).Present;

    private static RowPresenceProbeResult EvaluateRelativeTextRowPresence(
        int width,
        int height,
        Func<int, int, Color> getPixel,
        Rectangle referenceRoi,
        Rectangle candidateRoi,
        Point sampleOffset,
        RowPresenceProbePolicy policy)
    {
        var reference = MedianPatchLuminance(width, height, getPixel, referenceRoi, sampleOffset, policy.PatchRadius);
        var candidate = MedianPatchLuminance(width, height, getPixel, candidateRoi, sampleOffset, policy.PatchRadius);
        var tolerance = Math.Max(policy.MinimumLuminanceTolerance, Math.Abs(reference) * policy.RelativeLuminanceTolerance);
        var lumaDelta = Math.Abs(reference - candidate);
        var edgeDensity = EdgeDensity(width, height, getPixel, candidateRoi, policy.EdgeThreshold);
        var present = lumaDelta <= tolerance && edgeDensity >= policy.MinimumEdgeDensity;
        return new RowPresenceProbeResult(
            present,
            ClampByteMetric(reference),
            ClampByteMetric(candidate),
            ClampByteMetric(lumaDelta),
            ClampByteMetric(tolerance),
            ClampPermilleMetric(edgeDensity),
            ClampPermilleMetric(policy.MinimumEdgeDensity));
    }

    public static Bitmap NormalizeLuminance(Bitmap source)
    {
        var values = new byte[Math.Max(1, source.Width * source.Height)];
        var index = 0;
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                values[index++] = (byte)Math.Clamp((int)Math.Round(Luminance(source.GetPixel(x, y))), 0, 255);
            }
        }

        Array.Sort(values, 0, index);
        var low = values[Math.Clamp((int)Math.Floor((index - 1) * 0.02), 0, index - 1)];
        var high = values[Math.Clamp((int)Math.Ceiling((index - 1) * 0.98), 0, index - 1)];
        if (high - low < 48)
        {
            return source.Clone(new Rectangle(0, 0, source.Width, source.Height), PixelFormat.Format32bppArgb);
        }

        var output = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var color = source.GetPixel(x, y);
                var current = Luminance(color);
                var target = Math.Clamp((current - low) * 255.0 / (high - low), 0, 255);
                var scale = current <= 0.5 ? 0 : target / current;
                output.SetPixel(x, y, Color.FromArgb(
                    color.A,
                    Math.Clamp((int)Math.Round(color.R * scale), 0, 255),
                    Math.Clamp((int)Math.Round(color.G * scale), 0, 255),
                    Math.Clamp((int)Math.Round(color.B * scale), 0, 255)));
            }
        }

        return output;
    }

    public static int MeasureLuminanceMovement(
        IReadOnlyList<int> before,
        IReadOnlyList<int> after)
    {
        if (before.Count == 0 || before.Count != after.Count)
        {
            return int.MaxValue;
        }

        var sum = 0L;
        for (var i = 0; i < before.Count; i++)
        {
            sum += Math.Abs(before[i] - after[i]);
        }

        return (int)Math.Min(int.MaxValue, sum / before.Count);
    }

    public static string TransformClassName(VisualTransformClass value) => value switch
    {
        VisualTransformClass.Neutral => "neutral",
        VisualTransformClass.HighlightClipped => "highlight_clipped",
        VisualTransformClass.WarmShifted => "warm_shifted",
        VisualTransformClass.SaturationShifted => "saturation_shifted",
        VisualTransformClass.ContrastShifted => "contrast_shifted",
        _ => "unknown"
    };

    private static VisualTransformClass ClassifyTransform(
        Color expected,
        HsvColor expectedHsv,
        Color observed,
        HsvColor observedHsv,
        int hueDelta,
        int saturationDelta,
        int valueDelta)
    {
        var maxChannelDelta = Math.Max(
            Math.Abs(observed.R - expected.R),
            Math.Max(Math.Abs(observed.G - expected.G), Math.Abs(observed.B - expected.B)));
        if (maxChannelDelta <= 26 && hueDelta <= 8)
        {
            return VisualTransformClass.Neutral;
        }

        if ((observed.R >= 250 || observed.G >= 250 || observed.B >= 250)
            && maxChannelDelta > 26
            && observedHsv.Saturation >= Math.Max(0.45f, expectedHsv.Saturation - 0.1f))
        {
            return VisualTransformClass.HighlightClipped;
        }

        if (expected.B > 0 && observed.B / (double)expected.B < 0.90
            && observed.R >= expected.R)
        {
            return VisualTransformClass.WarmShifted;
        }

        if (saturationDelta >= 8)
        {
            return VisualTransformClass.SaturationShifted;
        }

        if (valueDelta >= 12)
        {
            return VisualTransformClass.ContrastShifted;
        }

        return VisualTransformClass.Unknown;
    }

    private static int ColorScore(Color current, Color expected)
    {
        var channelDelta = Math.Max(
            Math.Abs(current.R - expected.R),
            Math.Max(Math.Abs(current.G - expected.G), Math.Abs(current.B - expected.B)));
        var currentHsv = ToHsv(current);
        if (currentHsv.Saturation < 0.35f || currentHsv.Value < 0.20f)
        {
            return channelDelta;
        }

        var expectedHsv = ToHsv(expected);
        var saturationPenalty = currentHsv.Saturation < 0.55f ? (0.55f - currentHsv.Saturation) * 80f : 0f;
        var valuePenalty = currentHsv.Value < 0.35f ? (0.35f - currentHsv.Value) * 80f : 0f;
        var hueScore = (int)Math.Round(HueDelta(currentHsv.Hue, expectedHsv.Hue) + saturationPenalty + valuePenalty);
        return Math.Min(channelDelta, hueScore);
    }

    private static double MedianPatchLuminance(
        int width,
        int height,
        Func<int, int, Color> getPixel,
        Rectangle roi,
        Point offset,
        int radius)
    {
        var centerX = Math.Clamp(roi.X + offset.X, 0, width - 1);
        var centerY = Math.Clamp(roi.Y + offset.Y, 0, height - 1);
        var values = new List<double>();
        for (var y = centerY - radius; y <= centerY + radius; y++)
        {
            for (var x = centerX - radius; x <= centerX + radius; x++)
            {
                values.Add(Luminance(getPixel(Math.Clamp(x, 0, width - 1), Math.Clamp(y, 0, height - 1))));
            }
        }

        values.Sort();
        return values[values.Count / 2];
    }

    private static double EdgeDensity(
        int width,
        int height,
        Func<int, int, Color> getPixel,
        Rectangle roi,
        int threshold)
    {
        var clipped = Rectangle.Intersect(new Rectangle(0, 0, width, height), roi);
        if (clipped.Width < 2 || clipped.Height < 2)
        {
            return 0;
        }

        var edges = 0;
        var comparisons = 0;
        for (var y = clipped.Top; y < clipped.Bottom - 1; y += 4)
        {
            for (var x = clipped.Left; x < clipped.Right - 1; x += 4)
            {
                var current = Luminance(getPixel(x, y));
                if (Math.Abs(current - Luminance(getPixel(x + 1, y))) >= threshold
                    || Math.Abs(current - Luminance(getPixel(x, y + 1))) >= threshold)
                {
                    edges++;
                }

                comparisons++;
            }
        }

        return comparisons == 0 ? 0 : edges / (double)comparisons;
    }

    private static Color MedianColor(IReadOnlyList<Color> values)
    {
        static int Median(IEnumerable<int> source)
        {
            var ordered = source.OrderBy(value => value).ToArray();
            return ordered[ordered.Length / 2];
        }

        return Color.FromArgb(
            255,
            Median(values.Select(value => (int)value.R)),
            Median(values.Select(value => (int)value.G)),
            Median(values.Select(value => (int)value.B)));
    }

    private static double Luminance(Color color) =>
        (0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B);

    private static int ClampByteMetric(double value) =>
        value <= 0 ? 0 : value >= 255 ? 255 : (int)(value + 0.5);

    private static int ClampPermilleMetric(double value) =>
        value <= 0 ? 0 : value >= 1 ? 1000 : (int)((value * 1000) + 0.5);

    private static float HueDelta(float left, float right)
    {
        var delta = Math.Abs(left - right);
        return Math.Min(delta, 360f - delta);
    }

    private static HsvColor ToHsv(Color color)
    {
        var r = color.R / 255f;
        var g = color.G / 255f;
        var b = color.B / 255f;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = 0f;
        if (delta > 0.0001f)
        {
            if (Math.Abs(max - r) < 0.0001f)
            {
                hue = 60f * (((g - b) / delta) % 6f);
            }
            else if (Math.Abs(max - g) < 0.0001f)
            {
                hue = 60f * (((b - r) / delta) + 2f);
            }
            else
            {
                hue = 60f * (((r - g) / delta) + 4f);
            }

            if (hue < 0f)
            {
                hue += 360f;
            }
        }

        return new HsvColor(hue, max <= 0.0001f ? 0 : delta / max, max);
    }

    private readonly record struct HsvColor(float Hue, float Saturation, float Value);

    private sealed class RarityVote
    {
        public int Count { get; private set; }
        public int BestScore { get; private set; } = int.MaxValue;
        public int BestMargin { get; private set; }
        public Color BestColor { get; private set; } = Color.Empty;

        public void Observe(int score, int margin, Color color)
        {
            Count++;
            if (score < BestScore || (score == BestScore && margin > BestMargin))
            {
                BestScore = score;
                BestMargin = margin;
                BestColor = color;
            }
        }
    }
}
