using OpenCvSharp;
using System.Diagnostics;
using System.Text.Json;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Ocr;

public static class OcrBenchmark
{
    public static int Run(string samplesDirectory, int workers, int batchSize, int intraOpThreads)
    {
        if (!Directory.Exists(samplesDirectory))
        {
            Console.Error.WriteLine($"Samples directory not found: {samplesDirectory}");
            return 2;
        }

        var samples = Directory.EnumerateFiles(samplesDirectory, "*.json")
            .Select(TryLoadSample)
            .Where(sample => sample is not null)
            .Cast<Sample>()
            .OrderBy(sample => sample.Index)
            .ToArray();

        if (samples.Length == 0)
        {
            Console.Error.WriteLine($"No OCR sample metadata found: {samplesDirectory}");
            return 2;
        }

        workers = Math.Clamp(workers <= 0 ? 1 : workers, 1, 4);
        batchSize = Math.Clamp(batchSize <= 0 ? 8 : batchSize, 1, 32);
        intraOpThreads = Math.Clamp(intraOpThreads <= 0 ? 4 : intraOpThreads, 1, 8);

        var stopwatch = Stopwatch.StartNew();
        var next = 0;
        var sync = new object();
        var processed = 0;
        var roiCount = 0;
        var preprocessMs = 0.0;
        var inferenceMs = 0.0;
        var decodeMs = 0.0;
        var runCount = 0;
        var maxWidth = 0;

        var tasks = Enumerable.Range(1, workers).Select(_ => Task.Run(() =>
        {
            using var recognizer = new PaddleOcrRecognizer(AppPaths.ModelFile, AppPaths.CharacterDictFile, intraOpThreads);
            while (true)
            {
                Sample[] batch;
                lock (sync)
                {
                    if (next >= samples.Length)
                    {
                        return;
                    }

                    batch = samples.Skip(next).Take(batchSize).ToArray();
                    next += batch.Length;
                }

                using var mats = new MatDisposer(batch.Length);
                var inputs = new PaddleOcrRecognizer.OcrBatchInput[batch.Length];
                for (var i = 0; i < batch.Length; i++)
                {
                    var mat = Cv2.ImRead(batch[i].PanelFile, ImreadModes.Unchanged);
                    mats.Add(mat);
                    inputs[i] = new PaddleOcrRecognizer.OcrBatchInput(mat, batch[i].Rois);
                }

                var result = recognizer.RecognizeBatchDetailed(inputs);
                lock (sync)
                {
                    processed += batch.Length;
                    roiCount += result.Diagnostics.RoiCount;
                    preprocessMs += result.Diagnostics.PreprocessMs;
                    inferenceMs += result.Diagnostics.InferenceMs;
                    decodeMs += result.Diagnostics.DecodeMs;
                    runCount += result.Diagnostics.RunCount;
                    maxWidth = Math.Max(maxWidth, result.Diagnostics.MaxWidth);
                }
            }
        })).ToArray();

        Task.WaitAll(tasks);
        stopwatch.Stop();

        var itemsPerSecond = processed / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
        Console.WriteLine($"samples={processed}");
        Console.WriteLine($"workers={workers}");
        Console.WriteLine($"batch_size={batchSize}");
        Console.WriteLine($"intra_op_threads={intraOpThreads}");
        Console.WriteLine($"roi_count={roiCount}");
        Console.WriteLine($"run_count={runCount}");
        Console.WriteLine($"max_width={maxWidth}");
        Console.WriteLine($"elapsed_ms={stopwatch.Elapsed.TotalMilliseconds:F1}");
        Console.WriteLine($"items_per_second={itemsPerSecond:F3}");
        Console.WriteLine($"items_per_minute={itemsPerSecond * 60.0:F1}");
        Console.WriteLine($"preprocess_ms={preprocessMs:F1}");
        Console.WriteLine($"inference_ms={inferenceMs:F1}");
        Console.WriteLine($"decode_ms={decodeMs:F1}");
        return 0;
    }

    private static Sample? TryLoadSample(string metadataFile)
    {
        try
        {
            var metadata = JsonSerializer.Deserialize<SampleMetadata>(File.ReadAllText(metadataFile), JsonDefaults.Read);
            if (metadata is null)
            {
                return null;
            }

            var panelFile = Path.ChangeExtension(metadataFile, ".png");
            if (!File.Exists(panelFile))
            {
                return null;
            }

            return new Sample(
                metadata.Index,
                panelFile,
                metadata.Rois.Select(r => new Rect(r.X, r.Y, r.Width, r.Height)).ToArray());
        }
        catch
        {
            return null;
        }
    }

    private sealed record Sample(int Index, string PanelFile, IReadOnlyList<Rect> Rois);

    private sealed record SampleMetadata(int Index, string Rarity, IReadOnlyList<SampleRect> Rois);

    private sealed record SampleRect(int X, int Y, int Width, int Height);

    private sealed class MatDisposer : IDisposable
    {
        private readonly List<Mat> _mats;

        public MatDisposer(int capacity)
        {
            _mats = new List<Mat>(capacity);
        }

        public void Add(Mat mat)
        {
            _mats.Add(mat);
        }

        public void Dispose()
        {
            foreach (var mat in _mats)
            {
                mat.Dispose();
            }
        }
    }
}
