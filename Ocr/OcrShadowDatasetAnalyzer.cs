using System.Globalization;

namespace ZZZScannerNext.Ocr;

public static class OcrShadowDatasetAnalyzer
{
    private const double LowScoreThreshold = 0.90;

    public static int Run(string path, string? fastIndexOutput)
    {
        var csvFiles = OcrShadowDataset.FindCsvFiles(path);
        if (csvFiles.Count == 0)
        {
            Console.Error.WriteLine($"No ocr_shadow.csv found under: {path}");
            return 2;
        }

        var rows = csvFiles.SelectMany(OcrShadowDataset.ReadRowsFromCsv).ToArray();
        WriteSummary(csvFiles, rows);

        if (!string.IsNullOrWhiteSpace(fastIndexOutput))
        {
            var index = FastOcrTemplateIndex.Build(rows, Console.Error.WriteLine);
            index.Save(fastIndexOutput);
            Console.WriteLine($"fast_index.file={Path.GetFullPath(fastIndexOutput)}");
            Console.WriteLine($"fast_index.version={index.Version}");
            Console.WriteLine($"fast_index.feature={index.Feature}");
            Console.WriteLine($"fast_index.templates={index.Templates.Count}");
            Console.WriteLine($"fast_index.fields={index.Templates.Select(t => t.FieldKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()}");
            Console.WriteLine($"fast_index.labels={index.Templates.Select(t => $"{t.FieldKey}\0{t.Label}").Distinct(StringComparer.OrdinalIgnoreCase).Count()}");
        }

        return 0;
    }

    private static void WriteSummary(IReadOnlyList<string> csvFiles, IReadOnlyList<OcrShadowDatasetRow> rows)
    {
        Console.WriteLine($"shadow.csv_files={csvFiles.Count}");
        Console.WriteLine($"shadow.rows={rows.Count}");
        Console.WriteLine($"shadow.items={rows.Select(row => $"{row.CsvFile}\0{row.ItemIndex}").Distinct().Count()}");
        Console.WriteLine($"shadow.fields={rows.Select(row => row.FieldKey).Distinct(StringComparer.OrdinalIgnoreCase).Count()}");
        Console.WriteLine($"shadow.supported_fast_rows={rows.Count(row => FastOcrTemplateIndex.IsSupportedField(row.FieldKey))}");
        Console.WriteLine($"shadow.empty_labels={rows.Count(row => string.IsNullOrWhiteSpace(row.CleanLabel))}");
        Console.WriteLine($"shadow.low_score_rows={rows.Count(row => row.Score > 0 && row.Score < LowScoreThreshold)}");
        Console.WriteLine($"shadow.missing_images={rows.Count(row => string.IsNullOrWhiteSpace(row.ImageFile) || !File.Exists(row.ResolvedImageFile))}");

        foreach (var group in rows.GroupBy(row => row.FieldKey, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var values = group.ToArray();
            var prefix = $"field.{SanitizeKey(group.Key)}";
            Console.WriteLine($"{prefix}.rows={values.Length}");
            Console.WriteLine($"{prefix}.labels={values.Select(row => row.CleanLabel).Where(label => !string.IsNullOrWhiteSpace(label)).Distinct(StringComparer.OrdinalIgnoreCase).Count()}");
            Console.WriteLine($"{prefix}.empty_labels={values.Count(row => string.IsNullOrWhiteSpace(row.CleanLabel))}");
            Console.WriteLine($"{prefix}.low_score_rows={values.Count(row => row.Score > 0 && row.Score < LowScoreThreshold)}");
            Console.WriteLine($"{prefix}.avg_score={AverageScore(values)}");
            Console.WriteLine($"{prefix}.missing_images={values.Count(row => string.IsNullOrWhiteSpace(row.ImageFile) || !File.Exists(row.ResolvedImageFile))}");
            Console.WriteLine($"{prefix}.fast_supported={FastOcrTemplateIndex.IsSupportedField(group.Key).ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
        }
    }

    private static string AverageScore(IReadOnlyList<OcrShadowDatasetRow> rows)
    {
        var scored = rows.Where(row => row.Score > 0).Select(row => row.Score).ToArray();
        return scored.Length == 0 ? "N/A" : scored.Average().ToString("F6", CultureInfo.InvariantCulture);
    }

    private static string SanitizeKey(string key)
    {
        return string.Concat(key.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
    }
}
