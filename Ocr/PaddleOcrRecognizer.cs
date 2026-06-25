using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System.Buffers;
using System.Diagnostics;
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

    public IReadOnlyList<OcrResult> Recognize(Mat source, IReadOnlyList<Rect> rois)
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

        foreach (var bucket in BuildBuckets(flat))
        {
            var prepStart = Stopwatch.GetTimestamp();
            var inputValues = CreateInput(bucket.Rois, out var bucketMaxWidth);
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

    private IReadOnlyCollection<NamedOnnxValue> CreateInput(IReadOnlyList<FlatRoi> flatRois, out int maxWidth)
    {
        maxWidth = flatRois.Max(x => ResizedWidth(x.Roi.Width, x.Roi.Height));
        var tensor = new DenseTensor<float>(new[] { flatRois.Count, ChannelCount, MaxHeight, maxWidth });

        var dim = tensor.Dimensions;
        var batchStride = dim[1] * dim[2] * dim[3];
        var channelStride = dim[2] * dim[3];
        var heightStride = dim[3];
        var span = tensor.Buffer.Span;
        var bgrSources = new Dictionary<Mat, Mat>();

        try
        {
            for (var b = 0; b < flatRois.Count; b++)
            {
                var (source, roi) = flatRois[b];
                var newWidth = ResizedWidth(roi.Width, roi.Height);
                var bgrSource = GetBgrSource(source, bgrSources);
                using var roiMat = new Mat(bgrSource, roi);
                using var resized = new Mat();
                using var normalized = new Mat();

                Cv2.Resize(roiMat, resized, new OpenCvSharp.Size(newWidth, MaxHeight));
                resized.ConvertTo(normalized, MatType.CV_32FC3, Alpha);
                CopyInterleavedBgrToTensor(normalized, span, b * batchStride, channelStride, heightStride);
            }
        }
        finally
        {
            foreach (var mat in bgrSources.Values)
            {
                mat.Dispose();
            }
        }

        return new[] { NamedOnnxValue.CreateFromTensor(InputName, tensor) };
    }

    private static Mat GetBgrSource(Mat source, Dictionary<Mat, Mat> cache)
    {
        if (source.Channels() == 3)
        {
            return source;
        }

        if (cache.TryGetValue(source, out var cached))
        {
            return cached;
        }

        var dest = new Mat();
        if (source.Channels() == 4)
        {
            Cv2.CvtColor(source, dest, ColorConversionCodes.BGRA2BGR);
        }
        else if (source.Channels() == 1)
        {
            Cv2.CvtColor(source, dest, ColorConversionCodes.GRAY2BGR);
        }
        else
        {
            source.CopyTo(dest);
        }

        cache[source] = dest;
        return dest;
    }

    private static void CopyInterleavedBgrToTensor(Mat image, Span<float> target, int batchOffset, int channelStride, int heightStride)
    {
        var width = image.Cols;
        var height = image.Rows;
        var length = width * height * ChannelCount;

        if (image.IsContinuous())
        {
            var buffer = ArrayPool<float>.Shared.Rent(length);
            try
            {
                Marshal.Copy(image.Data, buffer, 0, length);
                CopyInterleavedBgr(buffer.AsSpan(0, length), target, batchOffset, channelStride, heightStride, width, height);
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }

            return;
        }

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = image.At<Vec3f>(y, x);
                var offset = batchOffset + y * heightStride + x;
                target[offset] = pixel.Item0;
                target[offset + channelStride] = pixel.Item1;
                target[offset + channelStride * 2] = pixel.Item2;
            }
        }
    }

    private static void CopyInterleavedBgr(
        ReadOnlySpan<float> source,
        Span<float> target,
        int batchOffset,
        int channelStride,
        int heightStride,
        int width,
        int height)
    {
        for (var y = 0; y < height; y++)
        {
            var rowSource = y * width * ChannelCount;
            var rowTarget = batchOffset + y * heightStride;
            for (var x = 0; x < width; x++)
            {
                var src = rowSource + x * ChannelCount;
                var dst = rowTarget + x;
                target[dst] = source[src];
                target[dst + channelStride] = source[src + 1];
                target[dst + channelStride * 2] = source[src + 2];
            }
        }
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

    public readonly record struct OcrBatchInput(Mat Source, IReadOnlyList<Rect> Rois);

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

    private readonly record struct FlatRoi(Mat Source, Rect Roi);

    private readonly record struct IndexedFlatRoi(int Index, FlatRoi Roi);

    private sealed record FlatRoiBucket(IReadOnlyList<FlatRoi> Rois, IReadOnlyList<int> OriginalIndices);

    private static double ElapsedMilliseconds(long ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }
}
