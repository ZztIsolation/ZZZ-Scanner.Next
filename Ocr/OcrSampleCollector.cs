using OpenCvSharp;
using System.Text.Json;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Ocr;

public sealed class OcrSampleCollector
{
    private readonly object _sync = new();
    private readonly string _directory;
    private readonly int _limit;
    private int _count;

    public OcrSampleCollector(string outputDirectory, int limit)
    {
        _limit = Math.Max(0, limit);
        _directory = Path.Combine(outputDirectory, "ocr-samples");
        if (_limit > 0)
        {
            Directory.CreateDirectory(_directory);
        }
    }

    public bool Enabled => _limit > 0;

    public bool TrySave(int index, string rarity, Mat image, IReadOnlyList<Rect> rois, Action<string> log)
    {
        if (!Enabled)
        {
            return false;
        }

        int sampleNumber;
        lock (_sync)
        {
            if (_count >= _limit)
            {
                return false;
            }

            _count++;
            sampleNumber = _count;
        }

        try
        {
            var prefix = $"{sampleNumber:D5}_{index:D5}";
            var panelFile = Path.Combine(_directory, $"{prefix}.png");
            Cv2.ImWrite(panelFile, image);

            var metadata = new OcrSampleMetadata(index, rarity, rois.Select(r => new OcrSampleRect(r.X, r.Y, r.Width, r.Height)).ToArray());
            File.WriteAllText(Path.Combine(_directory, $"{prefix}.json"), JsonSerializer.Serialize(metadata, JsonDefaults.Write));
            return true;
        }
        catch (Exception ex)
        {
            log($"OCR sample save failed for #{index}: {ex.Message}");
            return false;
        }
    }

    private sealed record OcrSampleMetadata(int Index, string Rarity, IReadOnlyList<OcrSampleRect> Rois);

    private sealed record OcrSampleRect(int X, int Y, int Width, int Height);
}
