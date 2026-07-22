using System.Diagnostics;
using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Ocr;

internal static class OcrRuntimeSmoke
{
    private const float ScoreTolerance = 0.005f;

    private static readonly Rectangle[] Regions =
    {
        new(0, 0, 225, 40),
        new(0, 40, 135, 30),
        new(0, 70, 302, 41),
        new(0, 111, 100, 41),
        new(0, 152, 302, 41),
        new(0, 193, 100, 41)
    };

    private static readonly ExpectedResult[] ExpectedResults =
    {
        new(0.90290445f, "呼啸沙龙[1]"),
        new(0.9952606f, "等级15/15"),
        new(0.9990817f, "生命值"),
        new(0.9960429f, "2200"),
        new(0.9936991f, "攻击力+2"),
        new(0.980103f, "9%")
    };

    public static int Run(string fixturePath, TextWriter output)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            if (string.IsNullOrWhiteSpace(fixturePath))
            {
                throw new ArgumentException("An OCR fixture path is required.", nameof(fixturePath));
            }

            var fullFixturePath = Path.GetFullPath(fixturePath);
            var modelPath = Path.Combine(AppContext.BaseDirectory, "Resources", "models", "PP-OCRv5_mobile_rec_infer.onnx");
            var dictionaryPath = Path.Combine(AppContext.BaseDirectory, "Resources", "models", "characterDict.txt");
            if (!File.Exists(fullFixturePath))
            {
                throw new FileNotFoundException("The OCR runtime smoke fixture was not found.", fullFixturePath);
            }
            if (!File.Exists(modelPath))
            {
                throw new FileNotFoundException("The packaged PP-OCRv5 model was not found.", modelPath);
            }
            if (!File.Exists(dictionaryPath))
            {
                throw new FileNotFoundException("The packaged OCR character dictionary was not found.", dictionaryPath);
            }

            var imageBytes = fullFixturePath.EndsWith(".b64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(File.ReadAllText(fullFixturePath).Trim())
                : File.ReadAllBytes(fullFixturePath);
            using var stream = new MemoryStream(imageBytes, writable: false);
            using var bitmap = new Bitmap(stream);
            using var recognizer = new PaddleOcrRecognizer(modelPath, dictionaryPath, intraOpThreads: 1);
            var actual = recognizer.Recognize(bitmap, Regions);

            if (actual.Count != ExpectedResults.Length)
            {
                throw new InvalidDataException($"Expected {ExpectedResults.Length} OCR results, got {actual.Count}.");
            }

            for (var index = 0; index < ExpectedResults.Length; index++)
            {
                var expected = ExpectedResults[index];
                var result = actual[index];
                if (!string.Equals(expected.Text, result.Text, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"OCR text mismatch at ROI {index}: expected '{expected.Text}', got '{result.Text}'.");
                }
                if (Math.Abs(expected.Score - result.Score) > ScoreTolerance)
                {
                    throw new InvalidDataException(
                        $"OCR score mismatch at ROI {index}: expected {expected.Score:R}, got {result.Score:R}.");
                }
            }

            stopwatch.Stop();
            WriteJson(output, new
            {
                ok = true,
                command = "ocr-runtime-smoke",
                scannerVersion = AppInfo.FileVersion,
                fixture = Path.GetFileName(fullFixturePath),
                fixtureSha256 = Convert.ToHexString(SHA256.HashData(imageBytes)).ToLowerInvariant(),
                modelSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(modelPath))).ToLowerInvariant(),
                elapsedMs = stopwatch.ElapsedMilliseconds,
                results = actual.Select(result => new { text = result.Text, score = result.Score }).ToArray()
            });
            return 0;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            WriteJson(output, new
            {
                ok = false,
                command = "ocr-runtime-smoke",
                scannerVersion = AppInfo.FileVersion,
                elapsedMs = stopwatch.ElapsedMilliseconds,
                errorType = exception.GetType().Name,
                error = exception.Message
            });
            return 1;
        }
    }

    private static void WriteJson(TextWriter output, object payload)
    {
        output.WriteLine(JsonSerializer.Serialize(payload));
        output.Flush();
    }

    private sealed record ExpectedResult(float Score, string Text);
}
