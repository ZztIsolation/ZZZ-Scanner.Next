using System.Globalization;
using Microsoft.VisualBasic.FileIO;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Ocr;

public sealed class OcrShadowDatasetRow
{
    public string CsvFile { get; init; } = "";
    public string ScanDirectory { get; init; } = "";
    public string Timestamp { get; init; } = "";
    public int ItemIndex { get; init; }
    public int RoiIndex { get; init; }
    public string FieldKey { get; init; } = "";
    public string ImageFile { get; init; } = "";
    public string Rarity { get; init; } = "";
    public string RawText { get; init; } = "";
    public double Score { get; init; }
    public string CleanName { get; init; } = "";
    public int CleanSlot { get; init; }
    public int CleanLevel { get; init; }
    public int CleanMaxLevel { get; init; }
    public string CleanLabel { get; init; } = "";
    public string VisualProfileId { get; init; } = "legacy";
    public string VisualClientKind { get; init; } = "legacy";
    public string VisualQualityLabel { get; init; } = "unknown";

    public string ResolvedImageFile =>
        Path.IsPathRooted(ImageFile) ? ImageFile : Path.Combine(ScanDirectory, ImageFile);
}

public static class OcrShadowDataset
{
    public static IReadOnlyList<string> FindCsvFiles(string path)
    {
        if (File.Exists(path))
        {
            return Path.GetFileName(path).Equals("ocr_shadow.csv", StringComparison.OrdinalIgnoreCase)
                ? [Path.GetFullPath(path)]
                : [];
        }

        if (!Directory.Exists(path))
        {
            return [];
        }

        var direct = Path.Combine(path, "ocr_shadow.csv");
        if (File.Exists(direct))
        {
            return [Path.GetFullPath(direct)];
        }

        return Directory.EnumerateFiles(path, "ocr_shadow.csv", System.IO.SearchOption.AllDirectories)
            .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
            .Select(Path.GetFullPath)
            .ToArray();
    }

    public static IReadOnlyList<OcrShadowDatasetRow> ReadRows(string path)
    {
        return FindCsvFiles(path).SelectMany(ReadRowsFromCsv).ToArray();
    }

    public static IReadOnlyList<OcrShadowDatasetRow> ReadRowsFromCsv(string csvFile)
    {
        using var parser = new TextFieldParser(csvFile);
        parser.TextFieldType = FieldType.Delimited;
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;

        if (parser.EndOfData)
        {
            return [];
        }

        var header = parser.ReadFields() ?? [];
        var columns = header
            .Select((name, index) => (name, index))
            .ToDictionary(item => item.name, item => item.index, StringComparer.OrdinalIgnoreCase);
        var rows = new List<OcrShadowDatasetRow>();
        var scanDirectory = Path.GetDirectoryName(Path.GetFullPath(csvFile)) ?? ".";
        var visualProfile = RuntimeVisualProfile.LoadOrLegacy(scanDirectory);

        while (!parser.EndOfData)
        {
            var fields = parser.ReadFields();
            if (fields is null || fields.Length == 0)
            {
                continue;
            }

            rows.Add(new OcrShadowDatasetRow
            {
                CsvFile = Path.GetFullPath(csvFile),
                ScanDirectory = scanDirectory,
                Timestamp = Get(fields, columns, "timestamp"),
                ItemIndex = ParseInt(Get(fields, columns, "item_index")),
                RoiIndex = ParseInt(Get(fields, columns, "roi_index")),
                FieldKey = Get(fields, columns, "field_key"),
                ImageFile = Get(fields, columns, "image_file"),
                Rarity = Get(fields, columns, "rarity"),
                RawText = Get(fields, columns, "raw_text"),
                Score = ParseDouble(Get(fields, columns, "score")),
                CleanName = Get(fields, columns, "clean_name"),
                CleanSlot = ParseInt(Get(fields, columns, "clean_slot")),
                CleanLevel = ParseInt(Get(fields, columns, "clean_level")),
                CleanMaxLevel = ParseInt(Get(fields, columns, "clean_max_level")),
                CleanLabel = Get(fields, columns, "clean_label"),
                VisualProfileId = string.IsNullOrWhiteSpace(visualProfile.ProfileId) ? "legacy" : visualProfile.ProfileId,
                VisualClientKind = visualProfile.ClientKind,
                VisualQualityLabel = visualProfile.QualityLabel
            });
        }

        return rows;
    }

    private static string Get(string[] fields, IReadOnlyDictionary<string, int> columns, string name)
    {
        return columns.TryGetValue(name, out var index) && index >= 0 && index < fields.Length
            ? fields[index]
            : "";
    }

    private static int ParseInt(string value)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static double ParseDouble(string value)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }
}
