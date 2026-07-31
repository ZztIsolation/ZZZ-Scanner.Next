using System.Drawing;
using System.Security.Cryptography;
using System.Text;

namespace ZZZScannerNext.Scanning;

internal enum NativeEdgeClickDecision
{
    AdvanceAssumedOne,
    HashFallback,
    Bottom,
    Stop
}

internal readonly record struct NativeEdgeClickSettleResult(
    bool Settled,
    bool Changed,
    int MovementDistance,
    int StableFrames,
    int Samples,
    double ElapsedMilliseconds,
    string Reason,
    Point TargetPoint,
    string BeforeListHash,
    string AfterListHash);

internal static class NativeEdgeClickPolicy
{
    public static NativeEdgeClickDecision ResolveSettle(NativeEdgeClickSettleResult result)
    {
        if (!result.Settled)
        {
            return NativeEdgeClickDecision.Stop;
        }

        return result.Changed
            ? NativeEdgeClickDecision.AdvanceAssumedOne
            : NativeEdgeClickDecision.HashFallback;
    }

    public static NativeEdgeClickDecision ResolveFallback(string previousRowHash, string currentRowHash)
    {
        if (string.IsNullOrWhiteSpace(previousRowHash)
            || string.IsNullOrWhiteSpace(currentRowHash))
        {
            return NativeEdgeClickDecision.Stop;
        }

        return string.Equals(previousRowHash, currentRowHash, StringComparison.Ordinal)
            ? NativeEdgeClickDecision.Bottom
            : NativeEdgeClickDecision.AdvanceAssumedOne;
    }
}

internal sealed class RowDetailFingerprintBuilder
{
    private readonly SortedDictionary<int, string> tokens = new();

    public int Count => tokens.Count;

    public void AddGridCell(int column, string rarity, ulong gridHash)
    {
        tokens[column] = $"grid:{column}:{rarity}:{gridHash:X16}";
    }

    public void AddDetailPanel(int column, string rarity, Bitmap image, IReadOnlyList<Rectangle> rois)
    {
        tokens[column] = $"panel:{column}:{rarity}:{DetailPanelFingerprint.Compute(image, rois)}";
    }

    public string Complete()
    {
        if (tokens.Count == 0)
        {
            return "";
        }

        var payload = string.Join('|', tokens.Select(pair => pair.Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}

internal sealed class BufferedRowCapture : IDisposable
{
    private readonly List<BufferedCellCapture> cells = [];

    public IReadOnlyList<BufferedCellCapture> Cells => cells;

    public void AddSkipped(int column, string rarity)
    {
        cells.Add(new BufferedCellCapture(column, rarity, null, [], TargetVerificationKind.ChangedText));
    }

    public void AddCapture(
        int column,
        string rarity,
        Bitmap image,
        Rectangle[] rois,
        TargetVerificationKind targetVerificationKind)
    {
        cells.Add(new BufferedCellCapture(column, rarity, image, rois, targetVerificationKind));
    }

    public void Dispose()
    {
        foreach (var cell in cells)
        {
            cell.Dispose();
        }
    }
}

internal sealed class BufferedCellCapture : IDisposable
{
    private Bitmap? image;

    public BufferedCellCapture(
        int column,
        string rarity,
        Bitmap? image,
        Rectangle[] rois,
        TargetVerificationKind targetVerificationKind)
    {
        Column = column;
        Rarity = rarity;
        this.image = image;
        Rois = rois;
        TargetVerificationKind = targetVerificationKind;
    }

    public int Column { get; }
    public string Rarity { get; }
    public bool HasCapture => image is not null;
    public bool ImageTransferred { get; private set; }
    public bool Disposed { get; private set; }
    public Rectangle[] Rois { get; }
    public TargetVerificationKind TargetVerificationKind { get; }

    public Bitmap TakeImage()
    {
        var result = image ?? throw new InvalidOperationException("Buffered cell image has already been transferred.");
        image = null;
        ImageTransferred = true;
        return result;
    }

    public void Dispose()
    {
        image?.Dispose();
        image = null;
        Disposed = true;
    }
}

internal static class DetailPanelFingerprint
{
    private const int HorizontalSamples = 16;
    private const int VerticalSamples = 8;

    public static string Compute(Bitmap image, IReadOnlyList<Rectangle> rois)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(rois);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[4];
        Span<byte> samples = stackalloc byte[HorizontalSamples * VerticalSamples];
        for (var roiIndex = 0; roiIndex < rois.Count; roiIndex++)
        {
            var roi = Rectangle.Intersect(rois[roiIndex], new Rectangle(0, 0, image.Width, image.Height));
            header[0] = (byte)Math.Min(byte.MaxValue, roiIndex);
            header[1] = (byte)Math.Min(byte.MaxValue, roi.Width);
            header[2] = (byte)Math.Min(byte.MaxValue, roi.Height);
            header[3] = (byte)(roi.IsEmpty ? 0 : 1);
            hash.AppendData(header);
            if (roi.IsEmpty)
            {
                continue;
            }

            samples.Clear();
            var sampleIndex = 0;
            for (var row = 0; row < VerticalSamples; row++)
            {
                var y = roi.Top + Math.Min(
                    roi.Height - 1,
                    (int)Math.Round((row + 0.5) * roi.Height / VerticalSamples, MidpointRounding.AwayFromZero));
                for (var column = 0; column < HorizontalSamples; column++)
                {
                    var x = roi.Left + Math.Min(
                        roi.Width - 1,
                        (int)Math.Round((column + 0.5) * roi.Width / HorizontalSamples, MidpointRounding.AwayFromZero));
                    var color = image.GetPixel(x, y);
                    var luminance = (54 * color.R + 183 * color.G + 19 * color.B) >> 8;
                    samples[sampleIndex++] = (byte)(luminance & 0xF0);
                }
            }

            hash.AppendData(samples);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
