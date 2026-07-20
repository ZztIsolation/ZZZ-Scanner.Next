using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;

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

public sealed record RowPresenceProbeResult(
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

public readonly record struct SelectionRefreshObservation(
    bool ChangedFromTarget,
    bool StableWithPreviousFrame);

public sealed record SelectionRefreshWaitResult(
    bool Ready,
    bool ChangedFromTarget,
    int StableFrames,
    int FrameCount,
    double ElapsedMilliseconds);

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
        Math.Clamp((int)Math.Round(value), 0, 255);

    private static int ClampPermilleMetric(double value) =>
        Math.Clamp((int)Math.Round(value * 1000), 0, 1000);

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
