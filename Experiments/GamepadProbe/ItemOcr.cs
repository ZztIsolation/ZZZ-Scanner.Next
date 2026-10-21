using System.Diagnostics;
using System.Drawing.Imaging;
using System.Security.Cryptography;
using System.Text.Json;
using ZZZScannerNext.Cleaning;
using ZZZScannerNext.Core;
using ZZZScannerNext.Ocr;
using ZZZScannerNext.Scanning;

namespace ZZZ.GamepadProbe;

internal sealed class ItemOcr : IItemReader, IDisposable
{
    public const string ModelHash = "70CA955EBA4729651DA42E7CBCC69AAF41A601939EE5FFAA7A90EFDFCD13DB6D";
    public static readonly string[] Keys = ["name", "level", "mainStat", "mainStatValue", "subStat1", "subStatValue1",
        "subStat2", "subStatValue2", "subStat3", "subStatValue3", "subStat4", "subStatValue4"];
    private readonly PaddleOcrRecognizer recognizer;
    private readonly DriveDiscCleaner cleaner;
    private readonly Rectangle[] standardRois;
    public double InitializationMs { get; }
    public int Threads { get; }

    public ItemOcr(int threads = 4)
    {
        var timer = Stopwatch.StartNew();
        Threads = Math.Clamp(threads, 1, 8);
        var model = Path.Combine(AppContext.BaseDirectory, "Resources", "models", "PP-OCRv5_mobile_rec_infer.onnx");
        if (!File.Exists(model)) throw new FileNotFoundException("缺少随包 OCR 模型，请完整解压 OCR 测试包。", model);
        using (var stream = File.OpenRead(model))
            if (Convert.ToHexString(SHA256.HashData(stream)) != ModelHash) throw new InvalidDataException("OCR 模型哈希不符。");
        using var profile = JsonDocument.Parse(File.ReadAllText(AppPaths.DataFile("scan_profiles.json")));
        var rectangles = profile.RootElement.GetProperty("profiles")[0].GetProperty("rectangles");
        standardRois = Keys.Select(key =>
        {
            var v = rectangles.GetProperty(key).EnumerateArray().Select(x => x.GetInt32()).ToArray();
            return Rectangle.FromLTRB(v[0], v[1], v[2], v[3]);
        }).ToArray();
        cleaner = new DriveDiscCleaner(WikiData.Load());
        recognizer = new PaddleOcrRecognizer(model, Path.Combine(AppContext.BaseDirectory, "Resources", "models", "characterDict.txt"), Threads);
        InitializationMs = timer.Elapsed.TotalMilliseconds;
    }

    public Rectangle[] Regions(Size size)
    {
        if (Math.Abs(size.Width / (double)size.Height - 16d / 9) > .02)
            throw new InvalidOperationException("当前 OCR 详情区域仅校准 16:9；其他宽高比需先补充区域配置。");
        return standardRois.Select(r => Rectangle.FromLTRB((int)Math.Round(r.Left * size.Width / 1920d),
            (int)Math.Round(r.Top * size.Height / 1080d), (int)Math.Round(r.Right * size.Width / 1920d),
            (int)Math.Round(r.Bottom * size.Height / 1080d))).ToArray();
    }

    public byte[] Signature(Bitmap frame) => DetailFrameSignature.Signature(frame, Regions(frame.Size));

    public static double SignatureDifference(byte[] a, byte[] b) => DetailFrameSignature.SignatureDifference(a, b);

    public Task<ItemRead> ReadAsync(Bitmap frame, GridObservation grid, int index, string directory, bool savePanel, CancellationToken token) =>
        Task.Run(() => Read(frame, grid, index, directory, savePanel, token), token);

    private ItemRead Read(Bitmap frame, GridObservation grid, int index, string directory, bool savePanel, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        token.ThrowIfCancellationRequested();
        var regions = Regions(frame.Size);
        var bounds = regions.Aggregate(Rectangle.Union);
        using var panel = frame.Clone(bounds, PixelFormat.Format24bppRgb);
        var local = regions.Select(r => new Rectangle(r.X - bounds.X, r.Y - bounds.Y, r.Width, r.Height)).ToArray();
        var rarity = DetectRarity(frame, grid);
        var cropMs = timer.Elapsed.TotalMilliseconds;
        var recognition = recognizer.RecognizeBatchDetailed([new(panel, local)]);
        token.ThrowIfCancellationRequested();
        var raw = recognition.Results[0].ToArray();
        var cleanTimer = Stopwatch.StartNew();
        DriveDiscExport? item = null;
        string? error = null;
        try
        {
            if (raw.Take(4).Any(r => string.IsNullOrWhiteSpace(r.Text) || r.Score < .65f))
                throw new InvalidDataException("名称、等级或主属性存在空值／低置信度。");
            if (raw.Skip(4).Any(r => !string.IsNullOrWhiteSpace(r.Text) && r.Score < .65f))
                throw new InvalidDataException("副属性存在低置信度文字。");
            item = cleaner.Clean(index, rarity, raw);
            if (item.Level == item.MaxLevel && item.SubStats.Count != 4)
                throw new InvalidDataException("满级驱动盘未识别到四条副属性。");
        }
        catch (Exception ex) { item = null; error = ex.Message; }
        var cleanMs = cleanTimer.Elapsed.TotalMilliseconds;
        var totalMs = timer.Elapsed.TotalMilliseconds;
        var warnings = new List<string>();
        if (item is not null)
        {
            var recognizedName = string.Concat(StringMatcher.SimplifyChinese(raw[0].Text, true).Where(c => !char.IsDigit(c)));
            if (recognizedName != StringMatcher.SimplifyChinese(item.Name, true))
                warnings.Add($"名称词典映射需核对：OCR={recognizedName}，现有清洗器={item.Name}");
        }
        if (savePanel || error is not null)
        {
            Directory.CreateDirectory(Path.Combine(directory, "panels"));
            panel.Save(Path.Combine(directory, "panels", $"{index:D5}.png"), ImageFormat.Png);
        }
        return new ItemRead(index, rarity, raw, item, error, cropMs, recognition.Diagnostics, cleanMs, totalMs, warnings.ToArray());
    }

    private static string DetectRarity(Bitmap frame, GridObservation grid) => DetailFrameSignature.DetectRarity(frame, grid);

    public void Dispose() => recognizer.Dispose();
}
