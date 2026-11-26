using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ZZZScannerNext.Scanning;

public sealed class SelectionAnchorPolicy
{
    public int EdgeBandInner { get; set; } = 54;
    public int EdgeBandOuter { get; set; } = 70;
    public int SpanTop { get; set; } = -20;
    public int SpanBottom { get; set; } = 44;
    public int SampleStep { get; set; } = 4;
    public double HueMinDegrees { get; set; } = 45;
    public double HueMaxDegrees { get; set; } = 100;
    public double MinimumSaturation { get; set; } = .55;
    public double MinimumValue { get; set; } = .45;
    public int MinimumScore { get; set; } = 85;
    public int MaximumOtherScore { get; set; } = 30;
    public int MaximumAsymmetry { get; set; } = 8;
    public int RequiredStableFrames { get; set; } = 2;
    public int ReleaseMaxSamples { get; set; } = 12;
    public int CaptureInflatePixels { get; set; } = 8;
    public bool Validate() => EdgeBandInner > 0 && EdgeBandInner < EdgeBandOuter && EdgeBandOuter <= 100
        && SpanTop < SpanBottom && SpanTop >= -100 && SpanBottom <= 100 && SampleStep is >= 1 and <= 20
        && MinimumScore is > 0 and <= 100 && MaximumOtherScore >= 0 && MaximumOtherScore < MinimumScore
        && MinimumSaturation is >= 0 and <= 1 && MinimumValue is >= 0 and <= 1
        && HueMinDegrees >= 0 && HueMinDegrees < HueMaxDegrees && HueMaxDegrees <= 360
        && MaximumAsymmetry is >= 0 and <= 30 && RequiredStableFrames is >= 2 and <= 5
        && ReleaseMaxSamples >= RequiredStableFrames && ReleaseMaxSamples <= 30 && CaptureInflatePixels is >= 0 and <= 32;
}

internal enum SelectionAnchorVerdict { None, Weak, Multiple, Unique }

internal readonly record struct SelectionAnchorGeometry(
    PointF FirstCenter, PointF Step, double ScaleX, double ScaleY, int Rows, int Columns)
{
    public static SelectionAnchorGeometry FromProfile(ScanProfile profile, Rectangle client, Rectangle capture)
    {
        var offset = profile.Points["driveDiscOffset"];
        var step = profile.Points["driveDiscStep"];
        var centerOffset = profile.Points.GetValueOrDefault("selectionTileCenterOffset", [-49, -53]);
        var sx = client.Width / (double)profile.StandardScreen[0];
        var sy = client.Height / (double)profile.StandardScreen[1];
        return new(new PointF((float)(client.X - capture.X + (offset[0] + step[0] + centerOffset[0]) * sx),
            (float)(client.Y - capture.Y + (offset[1] + step[1] + centerOffset[1]) * sy)),
            new PointF((float)(step[0] * sx), (float)(step[1] * sy)), sx, sy, profile.VisibleRows, profile.VisibleColumns);
    }
    public Point Center(int row, int column) => new(
        (int)Math.Round(FirstCenter.X + (column - 1) * Step.X),
        (int)Math.Round(FirstCenter.Y + (row - 1) * Step.Y));
}

internal sealed record SelectionAnchorCellScore(int Row, int Column, int Score,
    double LeftCoverage, double RightCoverage, int LeftX, int RightX, int Asymmetry);

internal sealed record SelectionAnchorObservation(SelectionAnchorVerdict Verdict, int? Row, int? Column,
    int BestScore, int SecondScore, IReadOnlyList<SelectionAnchorCellScore> Cells, double DetectMilliseconds)
{
    public bool Unique => Verdict == SelectionAnchorVerdict.Unique;
    public string Describe() => $"anchorVerdict={Verdict}, anchorRow={Row?.ToString() ?? "unknown"}, anchorColumn={Column?.ToString() ?? "unknown"}, anchorBest={BestScore}, anchorSecond={SecondScore}, detectMs={DetectMilliseconds:F3}";
}

internal static class SelectionAnchorDetector
{
    public static SelectionAnchorObservation FindUnique(CapturedFrame frame, SelectionAnchorGeometry geometry,
        SelectionAnchorPolicy policy, int? column = null)
    {
        if (frame is BitmapCapturedFrame bitmap) return FindUnique(bitmap.BorrowBitmap, geometry, policy, column);
        return Detect(frame.Width, frame.Height, frame.GetPixel, geometry, policy, column);
    }

    public static SelectionAnchorObservation FindUnique(Bitmap image, SelectionAnchorGeometry geometry,
        SelectionAnchorPolicy policy, int? column = null)
    {
        var bits = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            Color Pixel(int x, int y) => Color.FromArgb(Marshal.ReadInt32(bits.Scan0, y * bits.Stride + x * 4));
            return Detect(image.Width, image.Height, Pixel, geometry, policy, column);
        }
        finally { image.UnlockBits(bits); }
    }

    private static SelectionAnchorObservation Detect(int width, int height, Func<int, int, Color> pixel,
        SelectionAnchorGeometry geometry, SelectionAnchorPolicy policy, int? column)
    {
        var watch = Stopwatch.StartNew();
        if (!policy.Validate()) throw new ArgumentException("Invalid selection anchor policy.");
        var cells = new List<SelectionAnchorCellScore>();
        for (var row = 1; row <= geometry.Rows; row++)
        for (var col = column ?? 1; col <= (column ?? geometry.Columns); col++)
        {
            var center = geometry.Center(row, col);
            var top = center.Y + (int)Math.Round(policy.SpanTop * geometry.ScaleY);
            var bottom = center.Y + (int)Math.Round(policy.SpanBottom * geometry.ScaleY);
            var inner = (int)Math.Round(policy.EdgeBandInner * geometry.ScaleX);
            var outer = (int)Math.Round(policy.EdgeBandOuter * geometry.ScaleX);
            var step = Math.Max(1, (int)Math.Round(policy.SampleStep * geometry.ScaleY));
            (double Coverage, int X) Edge(int left, int right)
            {
                var best = 0.0;
                var bestX = left;
                for (var x = left; x <= right; x++)
                {
                    var hits = 0;
                    var count = 0;
                    for (var y = top; y <= bottom; y += step)
                    {
                        count++;
                        if (x < 0 || x >= width || y < 0 || y >= height) continue;
                        var color = pixel(x, y);
                        var max = Math.Max(color.R, Math.Max(color.G, color.B));
                        var min = Math.Min(color.R, Math.Min(color.G, color.B));
                        if (max / 255.0 < policy.MinimumValue || max == 0
                            || (max - min) / (double)max < policy.MinimumSaturation) continue;
                        var hue = color.GetHue();
                        if (hue >= policy.HueMinDegrees && hue <= policy.HueMaxDegrees) hits++;
                    }
                    var coverage = hits / (double)Math.Max(1, count);
                    if (coverage > best) { best = coverage; bestX = x; }
                }
                return (best, bestX);
            }
            var l = Edge(center.X - outer, center.X - inner);
            var r = Edge(center.X + inner, center.X + outer);
            var asymmetry = Math.Abs(2 * center.X - l.X - r.X);
            var score = asymmetry <= Math.Max(1, policy.MaximumAsymmetry * geometry.ScaleX)
                ? (int)Math.Round(100 * Math.Min(l.Coverage, r.Coverage)) : 0;
            cells.Add(new(row, col, score, l.Coverage, r.Coverage, l.X, r.X, asymmetry));
        }
        var ranked = cells.OrderByDescending(c => c.Score).ToArray();
        var bestScore = ranked.FirstOrDefault()?.Score ?? 0;
        var second = ranked.Skip(1).FirstOrDefault()?.Score ?? 0;
        var verdict = second >= policy.MinimumScore ? SelectionAnchorVerdict.Multiple
            : bestScore >= policy.MinimumScore && second <= policy.MaximumOtherScore ? SelectionAnchorVerdict.Unique
            : bestScore >= policy.MaximumOtherScore ? SelectionAnchorVerdict.Weak : SelectionAnchorVerdict.None;
        return new(verdict, verdict == SelectionAnchorVerdict.Unique ? ranked[0].Row : null,
            verdict == SelectionAnchorVerdict.Unique ? ranked[0].Column : null, bestScore, second, cells, watch.Elapsed.TotalMilliseconds);
    }
}
