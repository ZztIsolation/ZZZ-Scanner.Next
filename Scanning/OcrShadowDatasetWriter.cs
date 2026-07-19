using System.Drawing.Imaging;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ZZZScannerNext.Ocr;

namespace ZZZScannerNext.Scanning;

public sealed class OcrShadowDatasetWriter : IDisposable
{
    private readonly object _sync = new();
    private readonly string _roiDirectory;
    private readonly IReadOnlyList<string> _fieldKeys;
    private readonly StreamWriter _writer;

    public OcrShadowDatasetWriter(string outputDirectory, IReadOnlyList<string> fieldKeys)
    {
        _fieldKeys = fieldKeys.ToArray();
        _roiDirectory = Path.Combine(outputDirectory, "ocr_shadow_rois");
        Directory.CreateDirectory(_roiDirectory);

        var csvFile = Path.Combine(outputDirectory, "ocr_shadow.csv");
        _writer = new StreamWriter(csvFile, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _writer.WriteLine("timestamp,item_index,roi_index,field_key,image_file,rarity,raw_text,score,clean_name,clean_slot,clean_level,clean_max_level,clean_label");
        _writer.Flush();
    }

    public void Write(
        int itemIndex,
        string rarity,
        Bitmap image,
        IReadOnlyList<Rectangle> rois,
        IReadOnlyList<OcrResult> ocr,
        DriveDiscExport export)
    {
        for (var roiIndex = 0; roiIndex < rois.Count; roiIndex++)
        {
            var fieldKey = FieldKey(roiIndex);
            var fileName = $"{itemIndex:D4}_{roiIndex:D2}_{SafeFilePart(fieldKey)}.png";
            var relativeImageFile = Path.Combine("ocr_shadow_rois", fileName);
            var imageFile = Path.Combine(_roiDirectory, fileName);
            var saved = TrySaveCrop(image, rois[roiIndex], imageFile);
            var result = roiIndex < ocr.Count ? ocr[roiIndex] : new OcrResult(0, string.Empty);

            WriteRow([
                DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                itemIndex.ToString(CultureInfo.InvariantCulture),
                roiIndex.ToString(CultureInfo.InvariantCulture),
                fieldKey,
                saved ? relativeImageFile : "",
                rarity,
                result.Text,
                result.Score.ToString("F6", CultureInfo.InvariantCulture),
                export.Name,
                export.Slot.ToString(CultureInfo.InvariantCulture),
                export.Level.ToString(CultureInfo.InvariantCulture),
                export.MaxLevel.ToString(CultureInfo.InvariantCulture),
                CleanLabel(roiIndex, export)
            ]);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer.Dispose();
        }
    }

    private void WriteRow(IReadOnlyList<string> values)
    {
        lock (_sync)
        {
            _writer.WriteLine(string.Join(",", values.Select(EscapeCsv)));
            _writer.Flush();
        }
    }

    private string FieldKey(int roiIndex)
    {
        return roiIndex < _fieldKeys.Count ? _fieldKeys[roiIndex] : $"roi{roiIndex:D2}";
    }

    private static string CleanLabel(int roiIndex, DriveDiscExport export)
    {
        return roiIndex switch
        {
            0 => export.Slot > 0 ? $"{export.Name}{export.Slot}" : export.Name,
            1 => $"{export.Level:D2}/{export.MaxLevel:D2}",
            2 => FirstStatKey(export.MainStat),
            3 => FirstStatValue(export.MainStat),
            >= 4 => SubStatLabel(roiIndex, export),
            _ => ""
        };
    }

    private static string SubStatLabel(int roiIndex, DriveDiscExport export)
    {
        var subIndex = (roiIndex - 4) / 2;
        if (subIndex < 0 || subIndex >= export.SubStats.Count)
        {
            return "";
        }

        var stat = export.SubStats[subIndex];
        if (stat.Count == 0)
        {
            return "";
        }

        var pair = stat.First();
        return (roiIndex - 4) % 2 == 0 ? pair.Key : FormatValue(pair.Value);
    }

    private static string FirstStatKey(IReadOnlyDictionary<string, object> stat)
    {
        return stat.Count == 0 ? "" : stat.First().Key;
    }

    private static string FirstStatValue(IReadOnlyDictionary<string, object> stat)
    {
        return stat.Count == 0 ? "" : FormatValue(stat.First().Value);
    }

    private static string FormatValue(object value)
    {
        return value switch
        {
            JsonElement element => element.ToString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
    }

    private static bool TrySaveCrop(Bitmap source, Rectangle roi, string file)
    {
        var bounds = new Rectangle(0, 0, source.Width, source.Height);
        var rect = Rectangle.Intersect(bounds, roi);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        using var cropped = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(cropped))
        {
            graphics.DrawImage(source, new Rectangle(0, 0, rect.Width, rect.Height), rect, GraphicsUnit.Pixel);
        }

        cropped.Save(file, ImageFormat.Png);
        return true;
    }

    private static string SafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(invalid.Contains(c) ? '_' : c);
        }

        return builder.Length == 0 ? "roi" : builder.ToString();
    }

    private static string EscapeCsv(string value)
    {
        if (value.IndexOfAny(['"', ',', '\r', '\n']) < 0)
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
