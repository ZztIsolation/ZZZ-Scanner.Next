using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ZZZScannerNext.Ocr;

public sealed class PaddleOcrRecognizer : IDisposable
{
    private const string InputName = "x";
    private const int ChannelCount = 3;
    private const int MaxHeight = 48;
    private const double Alpha = 1.0 / 255.0;

    private readonly string[] _characterDict;
    private readonly InferenceSession _session;

    public PaddleOcrRecognizer(string modelFile, string characterDictFile, int intraOpThreads = 4)
    {
        _characterDict = File.ReadAllLines(characterDictFile);
        using var sessionOptions = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            IntraOpNumThreads = Math.Max(1, intraOpThreads),
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            EnableMemoryPattern = false,
            EnableCpuMemArena = false
        };
        _session = new InferenceSession(modelFile, sessionOptions);
    }

    public IReadOnlyList<OcrResult> Recognize(Bitmap source, IReadOnlyList<Rectangle> rois)
    {
        return RecognizeBatch([new OcrBatchInput(source, rois)]).First();
    }

    public IReadOnlyList<IReadOnlyList<OcrResult>> RecognizeBatch(IReadOnlyList<OcrBatchInput> inputs)
    {
        return RecognizeBatchDetailed(inputs).Results;
    }

    public OcrBatchRecognition RecognizeBatchDetailed(IReadOnlyList<OcrBatchInput> inputs)
    {
        var counts = inputs.Select(x => x.Rois.Count).ToArray();
        if (counts.Sum() == 0)
        {
            return new OcrBatchRecognition(
                inputs.Select(_ => (IReadOnlyList<OcrResult>)Array.Empty<OcrResult>()).ToArray(),
                new OcrBatchDiagnostics(0, 0, 0, 0, 0, 0, 0));
        }

        var stopwatch = Stopwatch.StartNew();
        var flat = inputs
            .SelectMany(input => input.Rois.Select(roi => new FlatRoi(input.Source, roi)))
            .ToArray();

        var prepTicks = 0L;
        var runTicks = 0L;
        var decodeTicks = 0L;
        var maxWidth = 0;
        var runs = 0;
        var decodedByFlatIndex = new OcrResult[flat.Length];
        var pixelSources = new Dictionary<Bitmap, PaddleOcrPreprocessor.BitmapPixels>(ReferenceEqualityComparer.Instance);
        foreach (var source in flat.Select(item => item.Source))
        {
            if (!pixelSources.ContainsKey(source))
            {
                pixelSources.Add(source, PaddleOcrPreprocessor.Read(source));
            }
        }

        foreach (var bucket in BuildBuckets(flat))
        {
            var prepStart = Stopwatch.GetTimestamp();
            var inputValues = CreateInput(bucket.Rois, pixelSources, out var bucketMaxWidth);
            prepTicks += Stopwatch.GetTimestamp() - prepStart;
            maxWidth = Math.Max(maxWidth, bucketMaxWidth);

            var runStart = Stopwatch.GetTimestamp();
            using var outputs = _session.Run(inputValues);
            runTicks += Stopwatch.GetTimestamp() - runStart;
            runs++;

            var decodeStart = Stopwatch.GetTimestamp();
            var decoded = Decode(outputs);
            decodeTicks += Stopwatch.GetTimestamp() - decodeStart;

            for (var i = 0; i < decoded.Count; i++)
            {
                decodedByFlatIndex[bucket.OriginalIndices[i]] = decoded[i];
            }
        }

        var result = new List<IReadOnlyList<OcrResult>>(inputs.Count);
        var offset = 0;
        foreach (var count in counts)
        {
            var perInput = new OcrResult[count];
            Array.Copy(decodedByFlatIndex, offset, perInput, 0, count);
            result.Add(perInput);
            offset += count;
        }

        stopwatch.Stop();
        return new OcrBatchRecognition(
            result,
            new OcrBatchDiagnostics(
                flat.Length,
                maxWidth,
                runs,
                ElapsedMilliseconds(prepTicks),
                ElapsedMilliseconds(runTicks),
                ElapsedMilliseconds(decodeTicks),
                stopwatch.Elapsed.TotalMilliseconds));
    }

    public void Dispose()
    {
        _session.Dispose();
    }

    private static int ResizedWidth(int width, int height)
    {
        return Math.Max(1, (int)(width * (float)MaxHeight / height + 0.5f));
    }

    private static IReadOnlyList<FlatRoiBucket> BuildBuckets(IReadOnlyList<FlatRoi> flatRois)
    {
        const int smallWidth = 96;
        const int mediumWidth = 192;

        var small = new List<IndexedFlatRoi>();
        var medium = new List<IndexedFlatRoi>();
        var large = new List<IndexedFlatRoi>();

        for (var i = 0; i < flatRois.Count; i++)
        {
            var resizedWidth = ResizedWidth(flatRois[i].Roi.Width, flatRois[i].Roi.Height);
            var target = resizedWidth <= smallWidth
                ? small
                : resizedWidth <= mediumWidth
                    ? medium
                    : large;
            target.Add(new IndexedFlatRoi(i, flatRois[i]));
        }

        return new[] { small, medium, large }
            .Where(bucket => bucket.Count > 0)
            .Select(bucket => new FlatRoiBucket(
                bucket.Select(x => x.Roi).ToArray(),
                bucket.Select(x => x.Index).ToArray()))
            .ToArray();
    }

    private IReadOnlyCollection<NamedOnnxValue> CreateInput(
        IReadOnlyList<FlatRoi> flatRois,
        IReadOnlyDictionary<Bitmap, PaddleOcrPreprocessor.BitmapPixels> pixelSources,
        out int maxWidth)
    {
        maxWidth = flatRois.Max(x => ResizedWidth(x.Roi.Width, x.Roi.Height));
        var tensor = new DenseTensor<float>(new[] { flatRois.Count, ChannelCount, MaxHeight, maxWidth });

        var dim = tensor.Dimensions;
        var batchStride = dim[1] * dim[2] * dim[3];
        var channelStride = dim[2] * dim[3];
        var heightStride = dim[3];
        var span = tensor.Buffer.Span;

        for (var b = 0; b < flatRois.Count; b++)
        {
            var (source, roi) = flatRois[b];
            var newWidth = ResizedWidth(roi.Width, roi.Height);
            PaddleOcrPreprocessor.CopyResizedBgrToTensor(
                pixelSources[source],
                roi,
                span,
                b * batchStride,
                channelStride,
                heightStride,
                newWidth,
                MaxHeight,
                Alpha);
        }

        return new[] { NamedOnnxValue.CreateFromTensor(InputName, tensor) };
    }

    private IReadOnlyList<OcrResult> Decode(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs)
    {
        var tensor = (DenseTensor<float>)outputs.First().Value;
        var dim = tensor.Dimensions;
        var batchStride = dim[1] * dim[2];
        var typeStride = dim[2];
        var span = tensor.Buffer.Span;
        var list = new List<OcrResult>(dim[0]);

        for (var b = 0; b < dim[0]; b++)
        {
            var batchOffset = b * batchStride;
            var chars = new List<string>();
            var score = 0f;
            var count = 0;
            var previous = -2;

            for (var t = 0; t < dim[1]; t++)
            {
                var typeOffset = batchOffset + t * typeStride;
                var maxIndex = -1;
                var maxScore = float.MinValue;

                for (var i = 0; i < dim[2]; i++)
                {
                    var value = span[typeOffset + i];
                    if (!float.IsNaN(value) && value > maxScore)
                    {
                        maxScore = value;
                        maxIndex = i;
                    }
                }

                maxIndex--;
                if (maxIndex == _characterDict.Length)
                {
                    break;
                }

                if (maxIndex >= 0 && maxIndex < _characterDict.Length)
                {
                    if (maxIndex != previous)
                    {
                        chars.Add(_characterDict[maxIndex]);
                        score += maxScore;
                        count++;
                    }

                    previous = maxIndex;
                }
                else
                {
                    previous = -1;
                }
            }

            list.Add(count > 0
                ? new OcrResult(score / count, string.Concat(chars))
                : new OcrResult(0, string.Empty));
        }

        return list;
    }

    public readonly record struct OcrBatchInput(Bitmap Source, IReadOnlyList<Rectangle> Rois);

    public sealed record OcrBatchRecognition(
        IReadOnlyList<IReadOnlyList<OcrResult>> Results,
        OcrBatchDiagnostics Diagnostics);

    public sealed record OcrBatchDiagnostics(
        int RoiCount,
        int MaxWidth,
        int RunCount,
        double PreprocessMs,
        double InferenceMs,
        double DecodeMs,
        double TotalMs);

    private readonly record struct FlatRoi(Bitmap Source, Rectangle Roi);

    private readonly record struct IndexedFlatRoi(int Index, FlatRoi Roi);

    private sealed record FlatRoiBucket(IReadOnlyList<FlatRoi> Rois, IReadOnlyList<int> OriginalIndices);

    private static double ElapsedMilliseconds(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }
}

internal static class PaddleOcrPreprocessor
{
    private const int ChannelCount = 3;
    private const int ResizeCoefficientBits = 11;
    private const int ResizeCoefficientScale = 1 << ResizeCoefficientBits;
    private const int ResizeHorizontalShift = 4;
    private const int ResizeVerticalShift = 16;
    private const int ResizeFinalShift = 2;
    private const int ResizeFinalRoundingDelta = 1 << (ResizeFinalShift - 1);

    internal sealed record BitmapPixels(int Width, int Height, int BytesPerPixel, byte[] Data)
    {
        public byte Channel(int x, int y, int channel)
        {
            return Data[((y * Width) + x) * BytesPerPixel + channel];
        }
    }

    public static BitmapPixels Read(Bitmap source)
    {
        Bitmap? converted = null;
        var bitmap = source;
        var pixelFormat = source.PixelFormat;
        var bytesPerPixel = Image.GetPixelFormatSize(pixelFormat) / 8;
        if (bytesPerPixel is not (3 or 4)
            || (pixelFormat & PixelFormat.Indexed) != 0)
        {
            converted = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(converted))
            {
                graphics.DrawImageUnscaled(source, 0, 0);
            }

            bitmap = converted;
            pixelFormat = PixelFormat.Format32bppArgb;
            bytesPerPixel = 4;
        }

        var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var bitmapData = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, pixelFormat);
        try
        {
            var rowBytes = bitmap.Width * bytesPerPixel;
            var pixels = new byte[rowBytes * bitmap.Height];
            for (var y = 0; y < bitmap.Height; y++)
            {
                var row = bitmapData.Stride >= 0
                    ? IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride)
                    : IntPtr.Add(bitmapData.Scan0, (bitmap.Height - 1 - y) * -bitmapData.Stride);
                Marshal.Copy(row, pixels, y * rowBytes, rowBytes);
            }

            return new BitmapPixels(bitmap.Width, bitmap.Height, bytesPerPixel, pixels);
        }
        finally
        {
            bitmap.UnlockBits(bitmapData);
            converted?.Dispose();
        }
    }

    public static void CopyResizedBgrToTensor(
        BitmapPixels source,
        Rectangle roi,
        Span<float> target,
        int batchOffset,
        int channelStride,
        int heightStride,
        int destinationWidth,
        int destinationHeight,
        double alpha)
    {
        ValidateRoi(source, roi);
        if (destinationWidth <= 0 || destinationHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(destinationWidth), "OCR destination dimensions must be positive.");
        }

        var xScale = roi.Width / (double)destinationWidth;
        var yScale = roi.Height / (double)destinationHeight;
        var xSamples = BuildSamples(roi.X, roi.Width, destinationWidth, xScale);
        var ySamples = BuildSamples(roi.Y, roi.Height, destinationHeight, yScale);

        for (var y = 0; y < destinationHeight; y++)
        {
            var sy = ySamples[y];
            var rowTarget = batchOffset + y * heightStride;
            for (var x = 0; x < destinationWidth; x++)
            {
                var sx = xSamples[x];
                var targetIndex = rowTarget + x;
                for (var channel = 0; channel < ChannelCount; channel++)
                {
                    var top = (source.Channel(sx.Low, sy.Low, channel) * sx.LowWeight)
                        + (source.Channel(sx.High, sy.Low, channel) * sx.HighWeight);
                    var bottom = (source.Channel(sx.Low, sy.High, channel) * sx.LowWeight)
                        + (source.Channel(sx.High, sy.High, channel) * sx.HighWeight);
                    // Match OpenCV's optimized 8-bit INTER_LINEAR fixed-point stages.
                    var weighted = (((top >> ResizeHorizontalShift) * sy.LowWeight) >> ResizeVerticalShift)
                        + (((bottom >> ResizeHorizontalShift) * sy.HighWeight) >> ResizeVerticalShift);
                    var rounded = Math.Clamp(
                        (weighted + ResizeFinalRoundingDelta) >> ResizeFinalShift,
                        0,
                        255);
                    target[targetIndex + channel * channelStride] = (float)(rounded * alpha);
                }
            }
        }
    }

    internal static float[] CreateTensorForTesting(Bitmap source, Rectangle roi, int destinationWidth, int destinationHeight)
    {
        var pixels = Read(source);
        var channelStride = destinationWidth * destinationHeight;
        var tensor = new float[channelStride * ChannelCount];
        CopyResizedBgrToTensor(
            pixels,
            roi,
            tensor,
            0,
            channelStride,
            destinationWidth,
            destinationWidth,
            destinationHeight,
            1.0 / 255.0);
        return tensor;
    }

    private static Sample[] BuildSamples(int origin, int sourceLength, int destinationLength, double scale)
    {
        var samples = new Sample[destinationLength];
        var maximum = sourceLength - 1;
        for (var index = 0; index < destinationLength; index++)
        {
            var coordinate = ((index + 0.5) * scale) - 0.5;
            var lowRelative = (int)Math.Floor(coordinate);
            var highWeight = coordinate - lowRelative;
            if (lowRelative < 0)
            {
                lowRelative = 0;
                highWeight = 0;
            }
            else if (lowRelative >= maximum)
            {
                lowRelative = maximum;
                highWeight = 0;
            }

            var highRelative = Math.Min(maximum, lowRelative + 1);
            var highCoefficient = Math.Clamp(
                (int)Math.Round(highWeight * ResizeCoefficientScale, MidpointRounding.ToEven),
                0,
                ResizeCoefficientScale);
            samples[index] = new Sample(
                origin + lowRelative,
                origin + highRelative,
                ResizeCoefficientScale - highCoefficient,
                highCoefficient);
        }

        return samples;
    }

    private static void ValidateRoi(BitmapPixels source, Rectangle roi)
    {
        if (roi.Width <= 0
            || roi.Height <= 0
            || roi.Left < 0
            || roi.Top < 0
            || roi.Right > source.Width
            || roi.Bottom > source.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(roi), $"OCR ROI {roi} is outside bitmap bounds {source.Width}x{source.Height}.");
        }
    }

    private readonly record struct Sample(int Low, int High, int LowWeight, int HighWeight);
}
