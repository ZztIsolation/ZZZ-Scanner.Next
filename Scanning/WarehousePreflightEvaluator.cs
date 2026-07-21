using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;

namespace ZZZScannerNext.Scanning;

public readonly record struct CaptureHealthResult(
    bool Passed,
    int Score,
    int MeanLuminance,
    int LuminanceStandardDeviation,
    int DarkPixelPercent,
    int BrightPixelPercent,
    int EdgeDensityPermille);

public readonly record struct WarehouseHeaderProbeResult(
    bool HeaderDetected,
    int HeaderScore,
    int TitleEditDistance,
    float Confidence,
    int? InventoryCount,
    int? InventoryCapacity,
    bool UsedNormalizedImage,
    string NormalizedText)
{
    public bool InventoryCountDetected => InventoryCount.HasValue && InventoryCapacity.HasValue;
}

public readonly record struct WarehouseStructureProbeResult(
    int GridStructureScore,
    int LayoutScore,
    int RecognizedGridCells,
    int GridEdgeDensityPermille,
    int LayoutEdgeDensityPermille,
    int VerticalLineScore,
    int HorizontalLineScore);

public sealed record WarehouseMonitorSignature(int[] Samples);

internal sealed record WarehouseMonitorPlan(
    Rectangle FastScreenBounds,
    WarehouseMonitorSignature FastBaseline,
    Rectangle ConfirmationScreenBounds,
    Rectangle ConfirmationHeaderRect,
    Rectangle ConfirmationListGridRect,
    Rectangle ConfirmationDetailPanelRect,
    Point ConfirmationDriveDiscOffset,
    Size DriveDiscStep);

public static class WarehousePreflightEvaluator
{
    private const string WarehouseTitle = "驱动仓库";
    private static readonly Regex InventoryCountPattern = new(
        @"(?<current>\d{1,4})\s*/\s*(?<capacity>\d{2,4})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static CaptureHealthResult EvaluateCaptureHealth(Bitmap image)
    {
        if (image.Width <= 0 || image.Height <= 0)
        {
            return new CaptureHealthResult(false, 0, 0, 0, 100, 0, 0);
        }

        var stepX = Math.Max(1, image.Width / 192);
        var stepY = Math.Max(1, image.Height / 108);
        var values = new List<int>();
        var dark = 0;
        var bright = 0;
        var edges = 0;
        var edgePairs = 0;

        for (var y = 0; y < image.Height; y += stepY)
        {
            for (var x = 0; x < image.Width; x += stepX)
            {
                var current = Luminance(image.GetPixel(x, y));
                values.Add(current);
                if (current <= 4)
                {
                    dark++;
                }
                else if (current >= 251)
                {
                    bright++;
                }

                if (x + stepX < image.Width)
                {
                    edgePairs++;
                    if (Math.Abs(current - Luminance(image.GetPixel(x + stepX, y))) >= 18)
                    {
                        edges++;
                    }
                }

                if (y + stepY < image.Height)
                {
                    edgePairs++;
                    if (Math.Abs(current - Luminance(image.GetPixel(x, y + stepY))) >= 18)
                    {
                        edges++;
                    }
                }
            }
        }

        var mean = values.Count == 0 ? 0 : values.Average();
        var variance = values.Count == 0
            ? 0
            : values.Select(value => Math.Pow(value - mean, 2)).Average();
        var standardDeviation = Math.Sqrt(variance);
        var darkPercent = Percent(dark, values.Count);
        var brightPercent = Percent(bright, values.Count);
        var edgeDensityPermille = Permille(edges, edgePairs);
        var flat = standardDeviation < 2 && edgeDensityPermille < 1;
        var passed = darkPercent < 98 && brightPercent < 98 && !flat;
        var dynamicScore = Math.Clamp((int)Math.Round((standardDeviation / 24d) * 60), 0, 60);
        var edgeScore = Math.Clamp(edgeDensityPermille, 0, 40);
        var score = passed ? Math.Clamp(dynamicScore + edgeScore, 1, 100) : 0;

        return new CaptureHealthResult(
            passed,
            score,
            (int)Math.Round(mean),
            (int)Math.Round(standardDeviation),
            darkPercent,
            brightPercent,
            edgeDensityPermille);
    }

    public static WarehouseHeaderProbeResult EvaluateHeader(
        string? text,
        float confidence,
        WarehousePreflightPolicy? policy = null,
        bool usedNormalizedImage = false)
    {
        policy ??= new WarehousePreflightPolicy();
        var normalized = NormalizeHeaderText(text);
        var editDistance = BestTitleEditDistance(normalized, WarehouseTitle);
        var exact = normalized.Contains(WarehouseTitle, StringComparison.Ordinal);
        var maximumEditDistance = Math.Clamp(policy.MaximumTitleEditDistance, 0, WarehouseTitle.Length - 1);
        var headerDetected = exact
            ? confidence >= policy.ExactTitleMinimumConfidence
            : editDistance <= maximumEditDistance && confidence >= policy.FuzzyTitleMinimumConfidence;
        var confidenceScore = Math.Clamp((int)Math.Round(confidence * 100), 0, 100);
        var headerScore = headerDetected
            ? Math.Clamp(confidenceScore - (editDistance * 15), 0, 100)
            : Math.Clamp(confidenceScore - 40 - (editDistance * 10), 0, 69);

        int? inventoryCount = null;
        int? inventoryCapacity = null;
        var match = InventoryCountPattern.Match(normalized);
        if (match.Success
            && int.TryParse(match.Groups["current"].Value, out var current)
            && int.TryParse(match.Groups["capacity"].Value, out var capacity)
            && current >= 0
            && current <= capacity
            && capacity >= policy.MinimumInventoryCapacity
            && capacity <= policy.MaximumInventoryCapacity)
        {
            inventoryCount = current;
            inventoryCapacity = capacity;
        }

        return new WarehouseHeaderProbeResult(
            headerDetected,
            headerScore,
            editDistance,
            confidence,
            inventoryCount,
            inventoryCapacity,
            usedNormalizedImage,
            normalized);
    }

    public static WarehouseHeaderProbeResult ChooseHeaderResult(
        WarehouseHeaderProbeResult original,
        WarehouseHeaderProbeResult normalized)
    {
        if (normalized.HeaderDetected && !original.HeaderDetected)
        {
            return normalized;
        }

        if (normalized.HeaderDetected == original.HeaderDetected
            && normalized.InventoryCountDetected
            && !original.InventoryCountDetected)
        {
            return normalized;
        }

        return normalized.HeaderScore > original.HeaderScore ? normalized : original;
    }

    public static WarehouseStructureProbeResult EvaluateStructure(
        Bitmap image,
        Rectangle listGridRect,
        Rectangle detailPanelRect,
        Point driveDiscOffset,
        Size driveDiscStep,
        int maximumCells = 6)
    {
        listGridRect = ClampRectangle(listGridRect, image.Size);
        detailPanelRect = ClampRectangle(detailPanelRect, image.Size);
        if (listGridRect.IsEmpty || detailPanelRect.IsEmpty)
        {
            return new WarehouseStructureProbeResult(0, 0, 0, 0, 0, 0, 0);
        }

        var recognizedCells = 0;
        var cellEdgeDensities = new List<int>();
        var stepWidth = Math.Max(8, driveDiscStep.Width);
        var stepHeight = Math.Max(8, driveDiscStep.Height);
        var halfWidth = Math.Max(8, (int)Math.Round(stepWidth * 0.40));
        var halfHeight = Math.Max(8, (int)Math.Round(stepHeight * 0.40));
        for (var column = 0; column < Math.Max(1, maximumCells); column++)
        {
            var center = new Point(
                driveDiscOffset.X + (stepWidth * (column + 1)),
                driveDiscOffset.Y + stepHeight);
            var cellRect = Rectangle.Intersect(
                ClampRectangle(
                    Rectangle.FromLTRB(center.X - halfWidth, center.Y - halfHeight, center.X + halfWidth, center.Y + halfHeight),
                    image.Size),
                listGridRect);
            if (cellRect.IsEmpty)
            {
                continue;
            }

            var metrics = MeasureRegion(image, cellRect);
            cellEdgeDensities.Add(metrics.EdgeDensityPermille);
            if (metrics.StandardDeviation >= 8 && metrics.EdgeDensityPermille >= 12)
            {
                recognizedCells++;
            }
        }

        var gridStructureScore = Math.Clamp(recognizedCells * 50, 0, 100);
        var gridEdgeDensity = cellEdgeDensities.Count == 0
            ? 0
            : (int)Math.Round(cellEdgeDensities.Average());

        var layoutMetrics = MeasureRegion(image, detailPanelRect);
        var verticalLineScore = StrongestLineScore(image, detailPanelRect, vertical: true);
        var horizontalLineScore = StrongestLineScore(image, detailPanelRect, vertical: false);
        var layoutEdgeScore = Math.Clamp((int)Math.Round(layoutMetrics.EdgeDensityPermille / 60d * 100), 0, 100);
        var layoutScore = Math.Clamp((int)Math.Round(
            (verticalLineScore * 0.40)
            + (horizontalLineScore * 0.35)
            + (layoutEdgeScore * 0.25)), 0, 100);

        return new WarehouseStructureProbeResult(
            gridStructureScore,
            layoutScore,
            recognizedCells,
            gridEdgeDensity,
            layoutMetrics.EdgeDensityPermille,
            verticalLineScore,
            horizontalLineScore);
    }

    public static WarehouseMonitorSignature CreateMonitorSignature(Bitmap image, IReadOnlyList<Rectangle> regions)
    {
        var samples = new List<int>(regions.Count * 48);
        foreach (var region in regions)
        {
            var rect = ClampRectangle(region, image.Size);
            if (rect.IsEmpty)
            {
                continue;
            }

            var local = new int[48];
            var index = 0;
            for (var row = 0; row < 4; row++)
            {
                var y = rect.Top + Math.Min(rect.Height - 1, (int)Math.Round((row + 0.5) * rect.Height / 4));
                for (var column = 0; column < 12; column++)
                {
                    var x = rect.Left + Math.Min(rect.Width - 1, (int)Math.Round((column + 0.5) * rect.Width / 12));
                    local[index++] = Luminance(image.GetPixel(x, y));
                }
            }

            NormalizeSamples(local);
            samples.AddRange(local);
        }

        return new WarehouseMonitorSignature(samples.ToArray());
    }

    public static int CompareMonitorSignature(WarehouseMonitorSignature baseline, WarehouseMonitorSignature current)
    {
        if (baseline.Samples.Length == 0 || baseline.Samples.Length != current.Samples.Length)
        {
            return 0;
        }

        long difference = 0;
        for (var i = 0; i < baseline.Samples.Length; i++)
        {
            difference += Math.Abs(baseline.Samples[i] - current.Samples[i]);
        }

        var averageDifference = difference / (double)baseline.Samples.Length;
        return Math.Clamp((int)Math.Round(100 - (averageDifference / 80d * 100)), 0, 100);
    }

    internal static bool IsStrongConfirmationAccepted(
        CaptureHealthResult health,
        WarehouseHeaderProbeResult header,
        WarehouseStructureProbeResult structure,
        WarehousePreflightPolicy policy)
    {
        return health.Passed
            && header.HeaderDetected
            && (structure.GridStructureScore >= policy.GridMinimumScore
                || structure.LayoutScore >= policy.LayoutMinimumScore);
    }

    public static string NormalizeHeaderText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text.Normalize(NormalizationForm.FormKC))
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            builder.Append(character switch
            {
                '【' or '〔' or '（' or '(' => '[',
                '】' or '〕' or '）' or ')' => ']',
                '／' => '/',
                _ => character
            });
        }

        return builder.ToString();
    }

    private static int BestTitleEditDistance(string text, string expected)
    {
        if (string.IsNullOrEmpty(text))
        {
            return expected.Length;
        }

        var best = expected.Length;
        var minimumLength = Math.Max(1, expected.Length - 1);
        var maximumLength = Math.Min(text.Length, expected.Length + 1);
        for (var length = minimumLength; length <= maximumLength; length++)
        {
            for (var start = 0; start + length <= text.Length; start++)
            {
                best = Math.Min(best, LevenshteinDistance(text.AsSpan(start, length), expected.AsSpan()));
            }
        }

        return best;
    }

    private static int LevenshteinDistance(ReadOnlySpan<char> left, ReadOnlySpan<char> right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
        {
            previous[column] = column;
        }

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var substitution = previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1);
                current[column] = Math.Min(Math.Min(previous[column] + 1, current[column - 1] + 1), substitution);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static RegionMetrics MeasureRegion(Bitmap image, Rectangle rect)
    {
        var stepX = Math.Max(1, rect.Width / 48);
        var stepY = Math.Max(1, rect.Height / 32);
        var values = new List<int>();
        var edges = 0;
        var pairs = 0;
        for (var y = rect.Top; y < rect.Bottom; y += stepY)
        {
            for (var x = rect.Left; x < rect.Right; x += stepX)
            {
                var current = Luminance(image.GetPixel(x, y));
                values.Add(current);
                if (x + stepX < rect.Right)
                {
                    pairs++;
                    if (Math.Abs(current - Luminance(image.GetPixel(x + stepX, y))) >= 18)
                    {
                        edges++;
                    }
                }

                if (y + stepY < rect.Bottom)
                {
                    pairs++;
                    if (Math.Abs(current - Luminance(image.GetPixel(x, y + stepY))) >= 18)
                    {
                        edges++;
                    }
                }
            }
        }

        var mean = values.Count == 0 ? 0 : values.Average();
        var variance = values.Count == 0 ? 0 : values.Select(value => Math.Pow(value - mean, 2)).Average();
        return new RegionMetrics((int)Math.Round(Math.Sqrt(variance)), Permille(edges, pairs));
    }

    private static int StrongestLineScore(Bitmap image, Rectangle rect, bool vertical)
    {
        var lineCandidates = Math.Clamp(vertical ? rect.Width - 1 : rect.Height - 1, 2, 256);
        const int samplesPerLine = 40;
        var strongest = 0d;
        for (var line = 0; line < lineCandidates; line++)
        {
            var matches = 0;
            for (var sample = 0; sample < samplesPerLine; sample++)
            {
                var x = vertical
                    ? rect.Left + Math.Min(rect.Width - 2, (int)Math.Round(line * (rect.Width - 1d) / (lineCandidates - 1)))
                    : rect.Left + Math.Min(rect.Width - 2, (int)Math.Round((sample + 0.5) * (rect.Width - 1) / samplesPerLine));
                var y = vertical
                    ? rect.Top + Math.Min(rect.Height - 2, (int)Math.Round((sample + 0.5) * (rect.Height - 1) / samplesPerLine))
                    : rect.Top + Math.Min(rect.Height - 2, (int)Math.Round(line * (rect.Height - 1d) / (lineCandidates - 1)));
                var adjacent = vertical ? image.GetPixel(x + 1, y) : image.GetPixel(x, y + 1);
                if (Math.Abs(Luminance(image.GetPixel(x, y)) - Luminance(adjacent)) >= 18)
                {
                    matches++;
                }
            }

            strongest = Math.Max(strongest, matches / (double)samplesPerLine);
        }

        return Math.Clamp((int)Math.Round(strongest / 0.55 * 100), 0, 100);
    }

    private static void NormalizeSamples(int[] values)
    {
        if (values.Length == 0)
        {
            return;
        }

        var ordered = values.OrderBy(value => value).ToArray();
        var low = ordered[(int)Math.Floor((ordered.Length - 1) * 0.02)];
        var high = ordered[(int)Math.Ceiling((ordered.Length - 1) * 0.98)];
        if (high - low < 12)
        {
            Array.Fill(values, 128);
            return;
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = Math.Clamp((int)Math.Round((values[i] - low) * 255d / (high - low)), 0, 255);
        }
    }

    private static Rectangle ClampRectangle(Rectangle rect, Size size)
    {
        var bounds = new Rectangle(Point.Empty, size);
        var result = Rectangle.Intersect(rect, bounds);
        return result.Width > 0 && result.Height > 0 ? result : Rectangle.Empty;
    }

    private static int Luminance(Color color) =>
        (color.R * 299 + color.G * 587 + color.B * 114) / 1000;

    private static int Percent(int count, int total) =>
        total <= 0 ? 0 : Math.Clamp((int)Math.Round(count * 100d / total), 0, 100);

    private static int Permille(int count, int total) =>
        total <= 0 ? 0 : Math.Clamp((int)Math.Round(count * 1000d / total), 0, 1000);

    private readonly record struct RegionMetrics(int StandardDeviation, int EdgeDensityPermille);
}
