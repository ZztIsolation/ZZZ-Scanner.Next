using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using OpenCvSharp;
using ZZZScannerNext.Scanning;
using CvRect = OpenCvSharp.Rect;

namespace ZZZScannerNext.Ocr;

public sealed class FastOcrShadowRecorder : IDisposable
{
    private readonly object _sync = new();
    private readonly IReadOnlyList<string> _fieldKeys;
    private readonly FastOcrTemplateIndex _index;
    private readonly StreamWriter _writer;

    private FastOcrShadowRecorder(string outputDirectory, string indexFile, IReadOnlyList<string> fieldKeys, FastOcrTemplateIndex index)
    {
        IndexFile = Path.GetFullPath(indexFile);
        _fieldKeys = fieldKeys.ToArray();
        _index = index;
        _writer = new StreamWriter(Path.Combine(outputDirectory, "ocr_fast_shadow.csv"), append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _writer.WriteLine("timestamp,item_index,roi_index,field_key,rarity,raw_text,clean_label,fast_label,score,top2_label,top2_score,margin,distance,candidate_count,assist_enabled,would_accept,matches_clean,elapsed_ms,match_source,reason");
        _writer.Flush();
    }

    public string IndexFile { get; }

    public int TemplateCount => _index.Templates.Count;

    public static FastOcrShadowRecorder? TryCreate(
        string outputDirectory,
        string? indexFile,
        IReadOnlyList<string> fieldKeys,
        Action<string> log)
    {
        indexFile = string.IsNullOrWhiteSpace(indexFile)
            ? FastOcrTemplateIndex.DefaultIndexFile
            : indexFile;

        if (!File.Exists(indexFile))
        {
            log($"Fast OCR shadow disabled because template index does not exist: {indexFile}");
            return null;
        }

        try
        {
            var index = FastOcrTemplateIndex.Load(indexFile);
            if (index.Templates.Count == 0)
            {
                log($"Fast OCR shadow disabled because template index is empty: {indexFile}");
                return null;
            }

            return new FastOcrShadowRecorder(outputDirectory, indexFile, fieldKeys, index);
        }
        catch (Exception ex)
        {
            log($"Fast OCR shadow disabled because template index cannot be loaded: {indexFile}. {ex.Message}");
            return null;
        }
    }

    public void Write(
        int itemIndex,
        string rarity,
        Bitmap image,
        IReadOnlyList<CvRect> rois,
        IReadOnlyList<OcrResult> ocr,
        DriveDiscExport export)
    {
        for (var roiIndex = 0; roiIndex < rois.Count; roiIndex++)
        {
            var fieldKey = FieldKey(roiIndex);
            if (!FastOcrTemplateIndex.IsSupportedField(fieldKey))
            {
                continue;
            }

            var cleanLabel = CleanLabel(roiIndex, export);
            var rawText = roiIndex < ocr.Count ? ocr[roiIndex].Text : "";
            var sw = Stopwatch.StartNew();
            var match = _index.Match(fieldKey, image, rois[roiIndex]);
            sw.Stop();
            var wouldAccept = _index.IsMatchAccepted(match, requireAssistEnabled: false);
            var matchesClean = wouldAccept && string.Equals(match.Label, cleanLabel, StringComparison.OrdinalIgnoreCase);

            WriteRow([
                DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                itemIndex.ToString(CultureInfo.InvariantCulture),
                roiIndex.ToString(CultureInfo.InvariantCulture),
                fieldKey,
                rarity,
                rawText,
                cleanLabel,
                match.Label,
                match.Score.ToString("F6", CultureInfo.InvariantCulture),
                match.Top2Label,
                match.Top2Score.ToString("F6", CultureInfo.InvariantCulture),
                match.Margin.ToString("F6", CultureInfo.InvariantCulture),
                match.Distance.ToString(CultureInfo.InvariantCulture),
                match.CandidateCount.ToString(CultureInfo.InvariantCulture),
                match.AssistEnabled.ToString(CultureInfo.InvariantCulture),
                wouldAccept.ToString(CultureInfo.InvariantCulture),
                matchesClean.ToString(CultureInfo.InvariantCulture),
                sw.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                match.SourceImage,
                match.Reason
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
            >= 4 when roiIndex % 2 == 0 => SubStatKey(roiIndex, export),
            _ => ""
        };
    }

    private static string FirstStatKey(IReadOnlyDictionary<string, object> stat)
    {
        return stat.Count == 0 ? "" : stat.First().Key;
    }

    private static string SubStatKey(int roiIndex, DriveDiscExport export)
    {
        var subIndex = (roiIndex - 4) / 2;
        if (subIndex < 0 || subIndex >= export.SubStats.Count || export.SubStats[subIndex].Count == 0)
        {
            return "";
        }

        return export.SubStats[subIndex].First().Key;
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
