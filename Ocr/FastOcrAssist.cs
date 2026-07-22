using System.Diagnostics;
using System.Globalization;
using System.Text;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Ocr;

public sealed class FastOcrAssistEngine
{
    private const int HealthWarmupItems = 12;
    private const double MinimumAcceptedPerItem = 4.0;

    private readonly IReadOnlyList<string> _fieldKeys;
    private readonly FastOcrTemplateIndex _index;
    private readonly Action<string> _log;
    private readonly ProfileRoutingMode _profileRoutingMode;
    private readonly IReadOnlyDictionary<string, FastOcrTemplateCoverage> _labelCoverageByField;
    private readonly object _healthSync = new();
    private int _healthItems;
    private int _healthAccepted;
    private bool _disabledByHealth;

    private FastOcrAssistEngine(
        string indexFile,
        IReadOnlyList<string> fieldKeys,
        FastOcrTemplateIndex index,
        string visualProfileId,
        ProfileRoutingMode profileRoutingMode,
        IReadOnlyDictionary<string, IReadOnlyList<string>> requiredLabelsByField,
        Action<string> log)
    {
        IndexFile = Path.GetFullPath(indexFile);
        _fieldKeys = fieldKeys.ToArray();
        _index = index;
        VisualProfileId = FastOcrTemplateIndex.NormalizeProfileId(visualProfileId);
        _profileRoutingMode = profileRoutingMode;
        _log = log;
        _labelCoverageByField = requiredLabelsByField
            .Where(pair => FastOcrTemplateIndex.IsSupportedField(pair.Key) && pair.Value.Count > 0)
            .ToDictionary(
                pair => pair.Key,
                pair => _index.DescribeLabelCoverage(pair.Key, VisualProfileId, _profileRoutingMode, pair.Value),
                StringComparer.OrdinalIgnoreCase);
    }

    public string IndexFile { get; }
    public string VisualProfileId { get; }

    public int TemplateCount => _index.Templates.Count;

    public ProfileRoutingMode ProfileRoutingMode => _profileRoutingMode;

    public FastOcrAssistRecorder CreateRecorder(string outputDirectory)
    {
        return new FastOcrAssistRecorder(outputDirectory);
    }

    public static FastOcrAssistEngine? TryCreate(
        string? indexFile,
        IReadOnlyList<string> fieldKeys,
        string visualProfileId,
        ProfileRoutingMode profileRoutingMode,
        IReadOnlyDictionary<string, IReadOnlyList<string>> requiredLabelsByField,
        Action<string> log)
    {
        indexFile = string.IsNullOrWhiteSpace(indexFile)
            ? FastOcrTemplateIndex.DefaultIndexFile
            : indexFile;

        if (!File.Exists(indexFile))
        {
            log($"Fast OCR assist disabled because template index does not exist: {indexFile}");
            return null;
        }

        try
        {
            var index = FastOcrTemplateIndex.Load(indexFile);
            if (index.Templates.Count == 0)
            {
                log($"Fast OCR assist disabled because template index is empty: {indexFile}");
                return null;
            }

            var engine = new FastOcrAssistEngine(indexFile, fieldKeys, index, visualProfileId, profileRoutingMode, requiredLabelsByField, log);
            engine.LogProfileRoutes();
            return engine;
        }
        catch (Exception ex)
        {
            log($"Fast OCR assist disabled because template index cannot be loaded: {indexFile}. {ex.Message}");
            return null;
        }
    }

    public FastOcrAssistPlan Plan(int itemIndex, string rarity, Bitmap image, IReadOnlyList<Rectangle> rois)
    {
        var results = new OcrResult[rois.Count];
        var hasResult = new bool[rois.Count];
        var ppOcrIndices = new List<int>();
        var ppOcrRois = new List<Rectangle>();
        var decisions = new List<FastOcrAssistDecision>();
        var fastMatchMs = 0.0;
        var fastAccepted = 0;
        var fastRejected = 0;
        var disabledByHealth = IsDisabledByHealth();

        for (var roiIndex = 0; roiIndex < rois.Count; roiIndex++)
        {
            var fieldKey = FieldKey(roiIndex);
            if (fieldKey.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                AddPpOcr();
                decisions.Add(FastOcrAssistDecision.PpOcr(itemIndex, roiIndex, fieldKey, rarity, "slot_safety_name_ppocr", source: "fallback"));
                continue;
            }

            if (disabledByHealth)
            {
                AddPpOcr();
                decisions.Add(FastOcrAssistDecision.PpOcr(itemIndex, roiIndex, fieldKey, rarity, "health_disabled_low_accept_rate", source: "fallback"));
                continue;
            }

            if (!FastOcrTemplateIndex.IsSupportedField(fieldKey))
            {
                AddPpOcr();
                decisions.Add(FastOcrAssistDecision.PpOcr(itemIndex, roiIndex, fieldKey, rarity, "unsupported_field"));
                continue;
            }

            var route = _index.DescribeRoute(fieldKey, VisualProfileId, _profileRoutingMode);
            if (_labelCoverageByField.TryGetValue(fieldKey, out var coverage) && !coverage.IsComplete)
            {
                AddPpOcr();
                decisions.Add(FastOcrAssistDecision.PpOcr(itemIndex, roiIndex, fieldKey, rarity, "template_label_coverage_incomplete", source: "fallback"));
                continue;
            }

            var policy = _index.PolicyForField(fieldKey, route.PolicyProfileId);
            if (!policy.AssistEnabled)
            {
                AddPpOcr();
                decisions.Add(FastOcrAssistDecision.PpOcr(itemIndex, roiIndex, fieldKey, rarity, $"assist_disabled:{route.Reason}", source: "fallback"));
                continue;
            }

            var sw = Stopwatch.StartNew();
            var match = _index.Match(fieldKey, image, rois[roiIndex], VisualProfileId, _profileRoutingMode);
            sw.Stop();
            fastMatchMs += sw.Elapsed.TotalMilliseconds;
            var accepted = _index.IsMatchAccepted(match, requireAssistEnabled: true);
            if (accepted)
            {
                results[roiIndex] = new OcrResult((float)match.Score, match.Label);
                hasResult[roiIndex] = true;
                fastAccepted++;
                decisions.Add(FastOcrAssistDecision.Fast(itemIndex, roiIndex, fieldKey, rarity, match, sw.Elapsed.TotalMilliseconds));
            }
            else
            {
                AddPpOcr();
                fastRejected++;
                decisions.Add(FastOcrAssistDecision.Rejected(itemIndex, roiIndex, fieldKey, rarity, match, sw.Elapsed.TotalMilliseconds));
            }

            void AddPpOcr()
            {
                ppOcrIndices.Add(roiIndex);
                ppOcrRois.Add(rois[roiIndex]);
            }
        }

        var plan = new FastOcrAssistPlan(
            results,
            hasResult,
            ppOcrIndices.ToArray(),
            ppOcrRois.ToArray(),
            decisions,
            fastMatchMs,
            fastAccepted,
            fastRejected);
        ObserveHealth(plan);
        return plan;
    }

    private string FieldKey(int roiIndex)
    {
        return roiIndex < _fieldKeys.Count ? _fieldKeys[roiIndex] : $"roi{roiIndex:D2}";
    }

    private void LogProfileRoutes()
    {
        foreach (var fieldKey in _fieldKeys.Where(FastOcrTemplateIndex.IsSupportedField).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var route = _index.DescribeRoute(fieldKey, VisualProfileId, _profileRoutingMode);
            _log($"FAST_OCR_PROFILE_ROUTE field={fieldKey}, requestedProfile={route.RequestedProfileId}, policyProfile={route.PolicyProfileId}, profileFamily={route.ProfileFamilyId}, route={route.RouteName}, templates={route.TemplateCount}, mode={_profileRoutingMode}, reason={route.Reason}");
            _log($"FAST_OCR_PROFILE_FAMILY_ROUTE field={fieldKey}, requestedProfile={route.RequestedProfileId}, profileFamily={route.ProfileFamilyId}, route={route.RouteName}, templates={route.TemplateCount}, mode={_profileRoutingMode}");
            if (_labelCoverageByField.TryGetValue(fieldKey, out var coverage))
            {
                var missingLabels = string.Join("|", coverage.MissingLabels.Select(SanitizeLogLabel));
                _log($"FAST_OCR_TEMPLATE_COVERAGE field={fieldKey}, requestedProfile={coverage.RequestedProfileId}, policyProfile={coverage.PolicyProfileId}, profileFamily={coverage.ProfileFamilyId}, route={coverage.RouteName}, templates={coverage.TemplateCount}, labels={coverage.LabelCount}, missingCount={coverage.MissingLabels.Count}, complete={coverage.IsComplete.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}, missingLabels={missingLabels}");
            }
        }
    }

    private static string SanitizeLogLabel(string label)
    {
        return string.Concat(label
            .Where(character => !char.IsControl(character) && character != ',' && character != '|' && character != '=')
            .Take(64));
    }

    private bool IsDisabledByHealth()
    {
        lock (_healthSync)
        {
            return _disabledByHealth;
        }
    }

    private void ObserveHealth(FastOcrAssistPlan plan)
    {
        lock (_healthSync)
        {
            if (_disabledByHealth || _healthItems >= HealthWarmupItems)
            {
                return;
            }

            _healthItems++;
            _healthAccepted += plan.FastAcceptedCount;
            if (_healthItems < HealthWarmupItems)
            {
                return;
            }

            var acceptedPerItem = _healthAccepted / (double)_healthItems;
            if (acceptedPerItem < MinimumAcceptedPerItem)
            {
                _disabledByHealth = true;
                _log($"FAST_OCR_HEALTH_DEGRADED visualProfile={VisualProfileId}, warmupItems={_healthItems}, acceptedPerItem={acceptedPerItem:F3}, threshold={MinimumAcceptedPerItem:F3}, action=disable_assist_for_scan");
                _log($"PROFILE_HEALTH_DEGRADED source=fast_ocr, visualProfile={VisualProfileId}, warmupItems={_healthItems}, acceptedPerItem={acceptedPerItem:F3}, threshold={MinimumAcceptedPerItem:F3}, action=disable_fast_ocr_assist");
            }
            else
            {
                _log($"FAST_OCR_HEALTH_OK visualProfile={VisualProfileId}, warmupItems={_healthItems}, acceptedPerItem={acceptedPerItem:F3}, threshold={MinimumAcceptedPerItem:F3}");
                _log($"PROFILE_HEALTH_OK source=fast_ocr, visualProfile={VisualProfileId}, warmupItems={_healthItems}, acceptedPerItem={acceptedPerItem:F3}, threshold={MinimumAcceptedPerItem:F3}");
            }
        }
    }
}

public sealed class FastOcrAssistPlan
{
    public FastOcrAssistPlan(
        OcrResult[] results,
        bool[] hasResult,
        int[] ppOcrIndices,
        Rectangle[] ppOcrRois,
        IReadOnlyList<FastOcrAssistDecision> decisions,
        double fastMatchMs,
        int fastAcceptedCount,
        int fastRejectedCount)
    {
        Results = results;
        HasResult = hasResult;
        PpOcrIndices = ppOcrIndices;
        PpOcrRois = ppOcrRois;
        Decisions = decisions;
        FastMatchMs = fastMatchMs;
        FastAcceptedCount = fastAcceptedCount;
        FastRejectedCount = fastRejectedCount;
    }

    public OcrResult[] Results { get; }
    public bool[] HasResult { get; }
    public int[] PpOcrIndices { get; }
    public Rectangle[] PpOcrRois { get; }
    public IReadOnlyList<FastOcrAssistDecision> Decisions { get; }
    public double FastMatchMs { get; }
    public int FastAcceptedCount { get; }
    public int FastRejectedCount { get; }

    public IReadOnlyList<OcrResult> Merge(IReadOnlyList<OcrResult> ppOcrResults)
    {
        var merged = new OcrResult[Results.Length];
        Array.Copy(Results, merged, Results.Length);

        for (var i = 0; i < PpOcrIndices.Length && i < ppOcrResults.Count; i++)
        {
            var roiIndex = PpOcrIndices[i];
            merged[roiIndex] = ppOcrResults[i];
            HasResult[roiIndex] = true;
        }

        for (var i = 0; i < merged.Length; i++)
        {
            if (!HasResult[i])
            {
                merged[i] = new OcrResult(0, string.Empty);
            }
        }

        return merged;
    }

    public string SourceForRoi(int roiIndex)
    {
        return Decisions.FirstOrDefault(decision => decision.RoiIndex == roiIndex)?.Source ?? "ppocr";
    }
}

public sealed record FastOcrAssistDecision(
    int ItemIndex,
    int RoiIndex,
    string FieldKey,
    string Rarity,
    string Source,
    string FastLabel,
    double Score,
    string SourceProfileId,
    string SourceFamilyId,
    string Top2Label,
    double Top2Score,
    double Margin,
    bool Accepted,
    bool CanonicalCropSucceeded,
    bool CanonicalCropFallback,
    double FeatureMs,
    double ElapsedMs,
    string Reason)
{
    public static FastOcrAssistDecision Fast(int itemIndex, int roiIndex, string fieldKey, string rarity, FastOcrMatch match, double elapsedMs) =>
        FromMatch(itemIndex, roiIndex, fieldKey, rarity, "fast", match, accepted: true, elapsedMs);

    public static FastOcrAssistDecision Rejected(int itemIndex, int roiIndex, string fieldKey, string rarity, FastOcrMatch match, double elapsedMs) =>
        FromMatch(itemIndex, roiIndex, fieldKey, rarity, "rejected", match, accepted: false, elapsedMs);

    public static FastOcrAssistDecision PpOcr(int itemIndex, int roiIndex, string fieldKey, string rarity, string reason, string source = "ppocr") =>
        new(itemIndex, roiIndex, fieldKey, rarity, source, "", 0, "", "", "", 0, 0, false, false, false, 0, 0, reason);

    private static FastOcrAssistDecision FromMatch(
        int itemIndex,
        int roiIndex,
        string fieldKey,
        string rarity,
        string source,
        FastOcrMatch match,
        bool accepted,
        double elapsedMs) =>
        new(itemIndex, roiIndex, fieldKey, rarity, source, match.Label, match.Score, match.SourceProfileId, match.SourceFamilyId, match.Top2Label, match.Top2Score, match.Margin, accepted, match.CanonicalCropSucceeded, match.CanonicalCropFallback, match.FeatureElapsedMs, elapsedMs, match.Reason);
}

public sealed class FastOcrAssistRecorder : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;

    public FastOcrAssistRecorder(string outputDirectory)
    {
        _writer = new StreamWriter(Path.Combine(outputDirectory, "ocr_fast_assist.csv"), append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        _writer.WriteLine("timestamp,item_index,roi_index,field_key,source,rarity,fast_label,score,source_profile_id,source_family_id,top2_label,top2_score,margin,accepted,canonical_crop_succeeded,canonical_crop_fallback,feature_ms,final_text,elapsed_ms,reason");
        _writer.Flush();
    }

    public void Write(IReadOnlyList<FastOcrAssistDecision> decisions, IReadOnlyList<OcrResult> merged)
    {
        lock (_sync)
        {
            foreach (var decision in decisions)
            {
                var finalText = decision.RoiIndex >= 0 && decision.RoiIndex < merged.Count ? merged[decision.RoiIndex].Text : "";
                _writer.WriteLine(string.Join(",", [
                    DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
                    decision.ItemIndex.ToString(CultureInfo.InvariantCulture),
                    decision.RoiIndex.ToString(CultureInfo.InvariantCulture),
                    EscapeCsv(decision.FieldKey),
                    decision.Source,
                    decision.Rarity,
                    EscapeCsv(decision.FastLabel),
                    decision.Score.ToString("F6", CultureInfo.InvariantCulture),
                    EscapeCsv(decision.SourceProfileId),
                    EscapeCsv(decision.SourceFamilyId),
                    EscapeCsv(decision.Top2Label),
                    decision.Top2Score.ToString("F6", CultureInfo.InvariantCulture),
                    decision.Margin.ToString("F6", CultureInfo.InvariantCulture),
                    decision.Accepted.ToString(CultureInfo.InvariantCulture),
                    decision.CanonicalCropSucceeded.ToString(CultureInfo.InvariantCulture),
                    decision.CanonicalCropFallback.ToString(CultureInfo.InvariantCulture),
                    decision.FeatureMs.ToString("F3", CultureInfo.InvariantCulture),
                    EscapeCsv(finalText),
                    decision.ElapsedMs.ToString("F3", CultureInfo.InvariantCulture),
                    EscapeCsv(decision.Reason)
                ]));
            }

            _writer.Flush();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _writer.Dispose();
        }
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
