using System.Diagnostics;
using System.Globalization;
using System.Text;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Ocr;

public static class FastOcrEvaluator
{
    private const double DefaultAssistMinAcceptRate = 0.60;
    private const double NameAssistMinAcceptRate = 0.95;

    private static readonly double[] ScoreCandidates = [0.99, 0.98, 0.97, 0.96, 0.95, 0.94, 0.93, 0.92, 0.91, 0.90, 0.88, 0.86, 0.84, 0.82];
    private static readonly double[] MarginCandidates = [0.20, 0.15, 0.10, 0.08, 0.06, 0.04, 0.02, 0.01, 0.00];

    public static int RunEval(string indexFile, string shadowPath)
    {
        if (!File.Exists(indexFile))
        {
            Console.Error.WriteLine($"Fast OCR index not found: {indexFile}");
            return 2;
        }

        var rows = OcrShadowDataset.ReadRows(shadowPath);
        if (rows.Count == 0)
        {
            Console.Error.WriteLine($"No OCR shadow rows found: {shadowPath}");
            return 2;
        }

        var index = FastOcrTemplateIndex.Load(indexFile);
        var evaluations = EvaluateRows("eval", rows, index);
        var outputFile = ResolveOutputFile(shadowPath);
        WriteReport(outputFile, evaluations);
        var confusionFile = ResolveSidecarOutputFile(outputFile, "ocr_fast_confusion.csv");
        WriteConfusionReport(confusionFile, evaluations);
        WriteSummary(evaluations);
        Console.WriteLine($"fast_eval.file={outputFile}");
        Console.WriteLine($"fast_eval.confusion_file={confusionFile}");
        return 0;
    }

    public static int RunCalibrate(string shadowParent, string outputFile, string? featureName = null)
    {
        var rowsByCsv = ReadNonEmptyShadowRuns(shadowParent);
        if (rowsByCsv.Length == 0)
        {
            Console.Error.WriteLine($"No non-empty ocr_shadow.csv files found: {shadowParent}");
            return 2;
        }

        var allRows = rowsByCsv.SelectMany(item => item.rows).ToArray();
        var feature = ResolveFeatureName(featureName);
        var index = FastOcrTemplateIndex.Build(allRows, Console.Error.WriteLine, feature);
        var hasCrossValidation = rowsByCsv.Length >= 2;
        var evaluations = hasCrossValidation
            ? BuildCrossValidationEvaluations(rowsByCsv, feature)
            : EvaluateRows("calibrate_self", allRows, index);
        var calibration = CalibratePolicies(index, evaluations, hasCrossValidation);

        var fullOutputFile = Path.GetFullPath(outputFile);
        index.Save(fullOutputFile);

        var outputDirectory = Path.GetDirectoryName(fullOutputFile) ?? ".";
        var evalFile = Path.Combine(outputDirectory, "ocr_fast_eval.csv");
        var confusionFile = Path.Combine(outputDirectory, "ocr_fast_confusion.csv");
        var calibrationFile = Path.Combine(outputDirectory, "ocr_fast_calibration.csv");
        WriteReport(evalFile, evaluations);
        WriteConfusionReport(confusionFile, evaluations);
        WriteCalibrationReport(calibrationFile, calibration);
        WriteSummary(evaluations);
        WriteCalibrationSummary(calibration);
        Console.WriteLine($"fast_calibrate.index_file={fullOutputFile}");
        Console.WriteLine($"fast_calibrate.eval_file={evalFile}");
        Console.WriteLine($"fast_calibrate.confusion_file={confusionFile}");
        Console.WriteLine($"fast_calibrate.calibration_file={calibrationFile}");
        Console.WriteLine($"fast_calibrate.shadow_runs={rowsByCsv.Length}");
        Console.WriteLine($"fast_calibrate.feature={feature}");
        Console.WriteLine($"fast_calibrate.cross_validation={hasCrossValidation.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
        if (!hasCrossValidation)
        {
            Console.Error.WriteLine("Calibration kept assist disabled because at least two shadow runs are required for cross-run validation.");
        }

        return 0;
    }

    public static int RunCalibrateVisualProfiles(string shadowParent, string outputFile, string? featureName = null)
    {
        var rowsByCsv = ReadNonEmptyShadowRuns(shadowParent);
        if (rowsByCsv.Length == 0)
        {
            Console.Error.WriteLine($"No non-empty ocr_shadow.csv files found: {shadowParent}");
            return 2;
        }

        var allRows = rowsByCsv.SelectMany(item => item.rows).ToArray();
        var feature = ResolveFeatureName(featureName);
        var index = FastOcrTemplateIndex.Build(allRows, Console.Error.WriteLine, feature);
        var profileCount = allRows
            .Select(row => FastOcrTemplateIndex.NormalizeProfileId(row.VisualProfileId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        var hasCrossValidation = rowsByCsv.Length >= 2;
        var evaluations = hasCrossValidation
            ? BuildCrossValidationEvaluations(rowsByCsv, feature, ProfileRoutingMode.Family)
            : EvaluateRows("calibrate_self", allRows, index);
        var crossProfileEvaluations = profileCount >= 2
            ? BuildCrossProfileEvaluations(rowsByCsv, feature, ProfileRoutingMode.Family)
            : Array.Empty<FastOcrEvaluationRow>();
        var familyCalibrationEvaluations = evaluations
            .Concat(crossProfileEvaluations.Where(ShouldUseCrossProfileEvaluationForFamilyCalibration))
            .ToArray();
        var calibration = CalibratePolicies(index, evaluations, hasCrossValidation);
        var profileCalibration = CalibrateProfilePolicies(index, evaluations, hasCrossValidation);
        var familyCalibration = CalibrateFamilyPolicies(index, familyCalibrationEvaluations, hasCrossValidation);

        var fullOutputFile = Path.GetFullPath(outputFile);
        index.Save(fullOutputFile);

        var outputDirectory = Path.GetDirectoryName(fullOutputFile) ?? ".";
        var evalFile = Path.Combine(outputDirectory, "ocr_fast_eval.csv");
        var crossProfileEvalFile = Path.Combine(outputDirectory, "ocr_fast_cross_profile_eval.csv");
        var confusionFile = Path.Combine(outputDirectory, "ocr_fast_confusion.csv");
        var calibrationFile = Path.Combine(outputDirectory, "ocr_fast_calibration.csv");
        var profileCalibrationFile = Path.Combine(outputDirectory, "ocr_fast_profile_calibration.csv");
        var familyCalibrationFile = Path.Combine(outputDirectory, "ocr_fast_family_calibration.csv");
        WriteReport(evalFile, evaluations);
        if (crossProfileEvaluations.Count > 0)
        {
            WriteReport(crossProfileEvalFile, crossProfileEvaluations);
        }

        WriteConfusionReport(confusionFile, evaluations);
        WriteCalibrationReport(calibrationFile, calibration);
        WriteProfileCalibrationReport(profileCalibrationFile, profileCalibration);
        WriteFamilyCalibrationReport(familyCalibrationFile, familyCalibration);
        WriteSummary(evaluations);
        WriteCalibrationSummary(calibration);
        foreach (var row in profileCalibration)
        {
            Console.WriteLine($"profile.{SanitizeKey(row.VisualProfileId)}.field.{SanitizeKey(row.FieldKey)}.assist_enabled={row.AssistEnabled.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
            Console.WriteLine($"profile.{SanitizeKey(row.VisualProfileId)}.field.{SanitizeKey(row.FieldKey)}.false_accepts={row.FalseAccepts}");
            Console.WriteLine($"profile.{SanitizeKey(row.VisualProfileId)}.field.{SanitizeKey(row.FieldKey)}.accept_rate={row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture)}");
        }
        foreach (var row in familyCalibration)
        {
            Console.WriteLine($"family.{SanitizeKey(row.ProfileFamilyId)}.field.{SanitizeKey(row.FieldKey)}.assist_enabled={row.AssistEnabled.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
            Console.WriteLine($"family.{SanitizeKey(row.ProfileFamilyId)}.field.{SanitizeKey(row.FieldKey)}.false_accepts={row.FalseAccepts}");
            Console.WriteLine($"family.{SanitizeKey(row.ProfileFamilyId)}.field.{SanitizeKey(row.FieldKey)}.accept_rate={row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture)}");
        }

        Console.WriteLine($"fast_calibrate_visual.index_file={fullOutputFile}");
        Console.WriteLine($"fast_calibrate_visual.eval_file={evalFile}");
        Console.WriteLine($"fast_calibrate_visual.cross_profile_eval_file={(crossProfileEvaluations.Count > 0 ? crossProfileEvalFile : "")}");
        Console.WriteLine($"fast_calibrate_visual.confusion_file={confusionFile}");
        Console.WriteLine($"fast_calibrate_visual.calibration_file={calibrationFile}");
        Console.WriteLine($"fast_calibrate_visual.profile_calibration_file={profileCalibrationFile}");
        Console.WriteLine($"fast_calibrate_visual.family_calibration_file={familyCalibrationFile}");
        Console.WriteLine($"fast_calibrate_visual.shadow_runs={rowsByCsv.Length}");
        Console.WriteLine($"fast_calibrate_visual.visual_profiles={profileCount}");
        Console.WriteLine($"fast_calibrate_visual.feature={feature}");
        Console.WriteLine($"fast_calibrate_visual.cross_validation={hasCrossValidation.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
        return 0;
    }

    private static bool ShouldUseCrossProfileEvaluationForFamilyCalibration(FastOcrEvaluationRow row)
    {
        return row.Reason.Contains("profile_family:", StringComparison.OrdinalIgnoreCase);
    }

    public static int RunCrossValidate(string shadowParent)
    {
        var csvFiles = OcrShadowDataset.FindCsvFiles(shadowParent);
        if (csvFiles.Count < 2)
        {
            Console.Error.WriteLine($"Cross validation needs at least two ocr_shadow.csv files. Found={csvFiles.Count}, path={shadowParent}");
            return 2;
        }

        var rowsByCsv = ReadNonEmptyShadowRuns(shadowParent);
        if (rowsByCsv.Length < 2)
        {
            Console.Error.WriteLine($"Cross validation needs at least two non-empty ocr_shadow.csv files. Found={rowsByCsv.Length}, path={shadowParent}");
            return 2;
        }

        var allEvaluations = BuildCrossValidationEvaluations(rowsByCsv);

        var outputFile = Path.Combine(Path.GetFullPath(shadowParent), "ocr_fast_eval.csv");
        WriteReport(outputFile, allEvaluations);
        var confusionFile = ResolveSidecarOutputFile(outputFile, "ocr_fast_confusion.csv");
        var calibrationFile = ResolveSidecarOutputFile(outputFile, "ocr_fast_calibration.csv");
        var allRows = rowsByCsv.SelectMany(item => item.rows).ToArray();
        var calibrationIndex = FastOcrTemplateIndex.Build(allRows, Console.Error.WriteLine);
        var calibration = CalibratePolicies(calibrationIndex, allEvaluations, hasCrossValidation: true);
        WriteConfusionReport(confusionFile, allEvaluations);
        WriteCalibrationReport(calibrationFile, calibration);
        WriteSummary(allEvaluations);
        WriteCalibrationSummary(calibration);
        Console.WriteLine($"fast_eval.file={outputFile}");
        Console.WriteLine($"fast_eval.confusion_file={confusionFile}");
        Console.WriteLine($"fast_eval.calibration_file={calibrationFile}");
        Console.WriteLine($"fast_eval.folds={rowsByCsv.Length}");
        return 0;
    }

    public static int RunFeatureEval(string shadowParent)
    {
        var rowsByCsv = ReadNonEmptyShadowRuns(shadowParent);
        if (rowsByCsv.Length < 2)
        {
            Console.Error.WriteLine($"Feature evaluation needs at least two non-empty ocr_shadow.csv files. Found={rowsByCsv.Length}, path={shadowParent}");
            return 2;
        }

        var allRows = rowsByCsv.SelectMany(item => item.rows).ToArray();
        var features = new[]
        {
            FastOcrTemplateIndex.CurrentFeature,
            FastOcrTemplateIndex.ExperimentalFeature,
            FastOcrTemplateIndex.CanonicalFeature
        };
        var reportRows = new List<FastOcrFeatureEvalRow>();
        foreach (var feature in features)
        {
            var evaluations = BuildCrossValidationEvaluations(rowsByCsv, feature);
            var index = FastOcrTemplateIndex.Build(allRows, Console.Error.WriteLine, feature);
            var calibration = CalibratePolicies(index, evaluations, hasCrossValidation: true);
            foreach (var row in calibration
                .Where(row => row.FieldKey.Equals("name", StringComparison.OrdinalIgnoreCase)
                    || row.FieldKey.Equals("subStat4", StringComparison.OrdinalIgnoreCase))
                .OrderBy(row => row.FieldKey, StringComparer.OrdinalIgnoreCase))
            {
                var fieldRows = evaluations
                    .Where(value => value.FieldKey.Equals(row.FieldKey, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                reportRows.Add(new FastOcrFeatureEvalRow(
                    feature,
                    row.FieldKey,
                    row.Rows,
                    row.Accepted,
                    row.FalseAccepts,
                    row.AcceptRate,
                    row.MatchRate,
                    row.MinScore,
                    row.MinMargin,
                    row.AssistEnabled,
                    row.Reason,
                    fieldRows.Length == 0 ? 0 : fieldRows.Average(value => value.Score),
                    fieldRows.Length == 0 ? 0 : fieldRows.Average(value => value.Margin)));
            }
        }

        var outputFile = Path.Combine(Path.GetFullPath(shadowParent), "ocr_fast_feature_eval.csv");
        WriteFeatureEvalReport(outputFile, reportRows);
        foreach (var row in reportRows)
        {
            var featureKey = SanitizeKey(row.Feature);
            var fieldKey = SanitizeKey(row.FieldKey);
            Console.WriteLine($"feature_eval.{featureKey}.{fieldKey}.false_accepts={row.FalseAccepts}");
            Console.WriteLine($"feature_eval.{featureKey}.{fieldKey}.accept_rate={row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture)}");
            Console.WriteLine($"feature_eval.{featureKey}.{fieldKey}.assist_enabled={row.AssistEnabled.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
        }

        Console.WriteLine($"feature_eval.file={outputFile}");
        Console.WriteLine($"feature_eval.folds={rowsByCsv.Length}");
        return 0;
    }

    private static (string file, IReadOnlyList<OcrShadowDatasetRow> rows)[] ReadNonEmptyShadowRuns(string shadowParent)
    {
        return OcrShadowDataset.FindCsvFiles(shadowParent)
            .Select(file => (file, rows: OcrShadowDataset.ReadRowsFromCsv(file)))
            .Where(item => item.rows.Count > 0)
            .ToArray();
    }

    private static string ResolveFeatureName(string? featureName)
    {
        if (string.IsNullOrWhiteSpace(featureName)
            || string.Equals(featureName, "v3", StringComparison.OrdinalIgnoreCase)
            || string.Equals(featureName, FastOcrTemplateIndex.CurrentFeature, StringComparison.OrdinalIgnoreCase))
        {
            return FastOcrTemplateIndex.CurrentFeature;
        }

        if (string.Equals(featureName, "v4", StringComparison.OrdinalIgnoreCase)
            || string.Equals(featureName, FastOcrTemplateIndex.ExperimentalFeature, StringComparison.OrdinalIgnoreCase))
        {
            return FastOcrTemplateIndex.ExperimentalFeature;
        }

        if (string.Equals(featureName, "v6", StringComparison.OrdinalIgnoreCase)
            || string.Equals(featureName, "canonical", StringComparison.OrdinalIgnoreCase)
            || string.Equals(featureName, FastOcrTemplateIndex.CanonicalFeature, StringComparison.OrdinalIgnoreCase))
        {
            return FastOcrTemplateIndex.CanonicalFeature;
        }

        throw new ArgumentException($"Unsupported fast OCR feature: {featureName}");
    }

    private static IReadOnlyList<FastOcrEvaluationRow> BuildCrossValidationEvaluations(
        IReadOnlyList<(string file, IReadOnlyList<OcrShadowDatasetRow> rows)> rowsByCsv,
        string featureName = FastOcrTemplateIndex.CurrentFeature,
        ProfileRoutingMode routingMode = ProfileRoutingMode.Family)
    {
        var allEvaluations = new List<FastOcrEvaluationRow>();
        foreach (var test in rowsByCsv)
        {
            var trainRows = rowsByCsv
                .Where(item => !string.Equals(item.file, test.file, StringComparison.OrdinalIgnoreCase))
                .SelectMany(item => item.rows)
                .ToArray();
            var index = FastOcrTemplateIndex.Build(trainRows, Console.Error.WriteLine, featureName);
            var fold = Path.GetFileName(Path.GetDirectoryName(test.file) ?? test.file);
            allEvaluations.AddRange(EvaluateRows(fold, test.rows, index, routingMode));
        }

        return allEvaluations;
    }

    private static IReadOnlyList<FastOcrEvaluationRow> BuildCrossProfileEvaluations(
        IReadOnlyList<(string file, IReadOnlyList<OcrShadowDatasetRow> rows)> rowsByCsv,
        string featureName = FastOcrTemplateIndex.CurrentFeature,
        ProfileRoutingMode routingMode = ProfileRoutingMode.Family)
    {
        var allRows = rowsByCsv.SelectMany(item => item.rows).ToArray();
        var profiles = allRows
            .Select(row => FastOcrTemplateIndex.NormalizeProfileId(row.VisualProfileId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(profile => profile, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var allEvaluations = new List<FastOcrEvaluationRow>();
        foreach (var profile in profiles)
        {
            var trainRows = allRows
                .Where(row => !FastOcrTemplateIndex.NormalizeProfileId(row.VisualProfileId).Equals(profile, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var testRows = allRows
                .Where(row => FastOcrTemplateIndex.NormalizeProfileId(row.VisualProfileId).Equals(profile, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var index = FastOcrTemplateIndex.Build(trainRows, Console.Error.WriteLine, featureName);
            allEvaluations.AddRange(EvaluateRows($"profile_{profile}", testRows, index, routingMode));
        }

        return allEvaluations;
    }

    private static IReadOnlyList<FastOcrEvaluationRow> EvaluateRows(
        string fold,
        IReadOnlyList<OcrShadowDatasetRow> rows,
        FastOcrTemplateIndex index,
        ProfileRoutingMode routingMode = ProfileRoutingMode.Family)
    {
        var evaluations = new List<FastOcrEvaluationRow>();
        foreach (var row in rows)
        {
            if (!FastOcrTemplateIndex.IsSupportedField(row.FieldKey))
            {
                continue;
            }

            var sw = Stopwatch.StartNew();
            FastOcrMatch match;
            if (string.IsNullOrWhiteSpace(row.ImageFile) || !File.Exists(row.ResolvedImageFile))
            {
                match = FastOcrMatch.Empty(row.FieldKey, "missing_image");
            }
            else
            {
                using var bitmap = new Bitmap(row.ResolvedImageFile);
                match = index.Match(row.FieldKey, bitmap, new OpenCvSharp.Rect(0, 0, bitmap.Width, bitmap.Height), row.VisualProfileId, routingMode);
            }
            sw.Stop();

            var accepted = index.IsMatchAccepted(match, requireAssistEnabled: false);
            var matchesClean = accepted && string.Equals(match.Label, row.CleanLabel, StringComparison.OrdinalIgnoreCase);
            evaluations.Add(new FastOcrEvaluationRow(
                fold,
                row.CsvFile,
                row.ItemIndex,
                row.RoiIndex,
                FastOcrTemplateIndex.NormalizeProfileId(row.VisualProfileId),
                FastOcrTemplateIndex.ProfileFamilyId(row.VisualProfileId),
                row.FieldKey,
                row.CleanLabel,
                match.Label,
                match.Score,
                match.Top2Label,
                match.Top2Score,
                match.Margin,
                match.AssistEnabled,
                accepted,
                matchesClean,
                accepted && !matchesClean,
                sw.Elapsed.TotalMilliseconds,
                match.SourceFamilyId,
                match.CanonicalCropSucceeded,
                match.CanonicalCropFallback,
                match.FeatureElapsedMs,
                match.Reason));
        }

        return evaluations;
    }

    private static void WriteReport(string outputFile, IReadOnlyList<FastOcrEvaluationRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        using var writer = new StreamWriter(outputFile, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("fold,csv_file,item_index,roi_index,visual_profile_id,profile_family_id,field_key,clean_label,fast_label,score,top2_label,top2_score,margin,assist_enabled,accepted,matches_clean,false_accept,elapsed_ms,source_family_id,canonical_crop_succeeded,canonical_crop_fallback,feature_ms,reason");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", [
                EscapeCsv(row.Fold),
                EscapeCsv(row.CsvFile),
                row.ItemIndex.ToString(CultureInfo.InvariantCulture),
                row.RoiIndex.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(row.VisualProfileId),
                EscapeCsv(row.ProfileFamilyId),
                EscapeCsv(row.FieldKey),
                EscapeCsv(row.CleanLabel),
                EscapeCsv(row.FastLabel),
                row.Score.ToString("F6", CultureInfo.InvariantCulture),
                EscapeCsv(row.Top2Label),
                row.Top2Score.ToString("F6", CultureInfo.InvariantCulture),
                row.Margin.ToString("F6", CultureInfo.InvariantCulture),
                row.AssistEnabled.ToString(CultureInfo.InvariantCulture),
                row.Accepted.ToString(CultureInfo.InvariantCulture),
                row.MatchesClean.ToString(CultureInfo.InvariantCulture),
                row.FalseAccept.ToString(CultureInfo.InvariantCulture),
                row.ElapsedMs.ToString("F3", CultureInfo.InvariantCulture),
                EscapeCsv(row.SourceFamilyId),
                row.CanonicalCropSucceeded.ToString(CultureInfo.InvariantCulture),
                row.CanonicalCropFallback.ToString(CultureInfo.InvariantCulture),
                row.FeatureMs.ToString("F3", CultureInfo.InvariantCulture),
                EscapeCsv(row.Reason)
            ]));
        }
    }

    private static void WriteConfusionReport(string outputFile, IReadOnlyList<FastOcrEvaluationRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        using var writer = new StreamWriter(outputFile, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("kind,field_key,clean_label,fast_label,count,avg_score,avg_margin,folds");

        var falseAccepts = rows
            .Where(row => row.FalseAccept)
            .GroupBy(ConfusionKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => FastOcrConfusionRow.From("false_accept", group))
            .ToArray();
        var rejects = rows
            .Where(row => !row.Accepted)
            .GroupBy(ConfusionKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => FastOcrConfusionRow.From("reject", group))
            .ToArray();

        foreach (var row in falseAccepts.Concat(rejects)
            .OrderBy(row => row.Kind, StringComparer.Ordinal)
            .ThenBy(row => row.FieldKey, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(row => row.Count)
            .ThenBy(row => row.CleanLabel, StringComparer.OrdinalIgnoreCase))
        {
            writer.WriteLine(string.Join(",", [
                EscapeCsv(row.Kind),
                EscapeCsv(row.FieldKey),
                EscapeCsv(row.CleanLabel),
                EscapeCsv(row.FastLabel),
                row.Count.ToString(CultureInfo.InvariantCulture),
                row.AvgScore.ToString("F6", CultureInfo.InvariantCulture),
                row.AvgMargin.ToString("F6", CultureInfo.InvariantCulture),
                EscapeCsv(row.Folds)
            ]));
        }
    }

    private static IReadOnlyList<FastOcrCalibrationRow> CalibratePolicies(
        FastOcrTemplateIndex index,
        IReadOnlyList<FastOcrEvaluationRow> evaluations,
        bool hasCrossValidation)
    {
        var rows = new List<FastOcrCalibrationRow>();
        foreach (var field in FastOcrTemplateIndex.SupportedFields.OrderBy(field => field, StringComparer.OrdinalIgnoreCase))
        {
            var values = evaluations
                .Where(row => row.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var selected = SelectThreshold(values);
            var policy = index.PolicyForField(field);
            var minAcceptRate = field.Equals("name", StringComparison.OrdinalIgnoreCase)
                ? NameAssistMinAcceptRate
                : DefaultAssistMinAcceptRate;
            var eligibleField = FastOcrTemplateIndex.IsDefaultAssistField(field);
            var enabled = hasCrossValidation
                && eligibleField
                && selected.FalseAccepts == 0
                && selected.Accepted > 0
                && selected.AcceptRate >= minAcceptRate;
            var reason = enabled
                ? ""
                : CalibrationDisableReason(hasCrossValidation, eligibleField, selected, minAcceptRate);

            policy.AssistEnabled = enabled;
            policy.MinScore = selected.MinScore;
            policy.MinMargin = selected.MinMargin;
            index.FieldPolicies[field] = policy;

            rows.Add(new FastOcrCalibrationRow(
                field,
                values.Length,
                selected.Accepted,
                selected.FalseAccepts,
                selected.AcceptRate,
                selected.MatchRate,
                selected.MinScore,
                selected.MinMargin,
                enabled,
                minAcceptRate,
                reason));
        }

        return rows;
    }

    private static IReadOnlyList<FastOcrProfileCalibrationRow> CalibrateProfilePolicies(
        FastOcrTemplateIndex index,
        IReadOnlyList<FastOcrEvaluationRow> evaluations,
        bool hasCrossValidation)
    {
        var rows = new List<FastOcrProfileCalibrationRow>();
        foreach (var profileGroup in evaluations
            .GroupBy(row => FastOcrTemplateIndex.NormalizeProfileId(row.VisualProfileId), StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var field in FastOcrTemplateIndex.SupportedFields.OrderBy(field => field, StringComparer.OrdinalIgnoreCase))
            {
                var values = profileGroup
                    .Where(row => row.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var selected = SelectThreshold(values);
                var minAcceptRate = field.Equals("name", StringComparison.OrdinalIgnoreCase)
                    ? NameAssistMinAcceptRate
                    : DefaultAssistMinAcceptRate;
                var eligibleField = FastOcrTemplateIndex.IsDefaultAssistField(field);
                var enabled = hasCrossValidation
                    && eligibleField
                    && selected.FalseAccepts == 0
                    && selected.Accepted > 0
                    && selected.AcceptRate >= minAcceptRate;
                var reason = enabled
                    ? ""
                    : CalibrationDisableReason(hasCrossValidation, eligibleField, selected, minAcceptRate);
                var policy = index.PolicyForField(field);
                var profilePolicy = new FastOcrFieldPolicy
                {
                    AssistEnabled = enabled,
                    MinScore = selected.MinScore,
                    MinMargin = selected.MinMargin,
                    TemplateCount = index.Templates.Count(template =>
                        template.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase)
                        && FastOcrTemplateIndex.NormalizeProfileId(template.VisualProfileId).Equals(profileGroup.Key, StringComparison.OrdinalIgnoreCase)),
                    LabelCount = index.Templates
                        .Where(template => template.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase)
                            && FastOcrTemplateIndex.NormalizeProfileId(template.VisualProfileId).Equals(profileGroup.Key, StringComparison.OrdinalIgnoreCase))
                        .Select(template => template.Label)
                        .Where(label => !string.IsNullOrWhiteSpace(label))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count()
                };

                if (profilePolicy.TemplateCount == 0)
                {
                    profilePolicy.TemplateCount = policy.TemplateCount;
                    profilePolicy.LabelCount = policy.LabelCount;
                }

                index.ProfileFieldPolicies[FastOcrTemplateIndex.ProfilePolicyKey(profileGroup.Key, field)] = profilePolicy;
                rows.Add(new FastOcrProfileCalibrationRow(
                    profileGroup.Key,
                    field,
                    values.Length,
                    selected.Accepted,
                    selected.FalseAccepts,
                    selected.AcceptRate,
                    selected.MatchRate,
                    selected.MinScore,
                    selected.MinMargin,
                    enabled,
                    minAcceptRate,
                    reason));
            }
        }

        return rows;
    }

    private static IReadOnlyList<FastOcrFamilyCalibrationRow> CalibrateFamilyPolicies(
        FastOcrTemplateIndex index,
        IReadOnlyList<FastOcrEvaluationRow> evaluations,
        bool hasCrossValidation)
    {
        var rows = new List<FastOcrFamilyCalibrationRow>();
        foreach (var familyGroup in evaluations
            .GroupBy(row => string.IsNullOrWhiteSpace(row.ProfileFamilyId) ? "unknown" : row.ProfileFamilyId, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var field in FastOcrTemplateIndex.SupportedFields.OrderBy(field => field, StringComparer.OrdinalIgnoreCase))
            {
                var values = familyGroup
                    .Where(row => row.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var selected = SelectThreshold(values);
                var minAcceptRate = field.Equals("name", StringComparison.OrdinalIgnoreCase)
                    ? NameAssistMinAcceptRate
                    : DefaultAssistMinAcceptRate;
                var eligibleField = FastOcrTemplateIndex.IsDefaultAssistField(field);
                var enabled = hasCrossValidation
                    && eligibleField
                    && selected.FalseAccepts == 0
                    && selected.Accepted > 0
                    && selected.AcceptRate >= minAcceptRate;
                var reason = enabled
                    ? ""
                    : CalibrationDisableReason(hasCrossValidation, eligibleField, selected, minAcceptRate);

                var familyPolicy = new FastOcrFieldPolicy
                {
                    AssistEnabled = enabled,
                    MinScore = selected.MinScore,
                    MinMargin = selected.MinMargin,
                    TemplateCount = index.Templates.Count(template =>
                        template.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase)
                        && FastOcrTemplateIndex.ProfileFamilyId(template.VisualProfileId).Equals(familyGroup.Key, StringComparison.OrdinalIgnoreCase)),
                    LabelCount = index.Templates
                        .Where(template => template.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase)
                            && FastOcrTemplateIndex.ProfileFamilyId(template.VisualProfileId).Equals(familyGroup.Key, StringComparison.OrdinalIgnoreCase))
                        .Select(template => template.Label)
                        .Where(label => !string.IsNullOrWhiteSpace(label))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count()
                };

                index.FamilyFieldPolicies[FastOcrTemplateIndex.ProfilePolicyKey(familyGroup.Key, field)] = familyPolicy;
                rows.Add(new FastOcrFamilyCalibrationRow(
                    familyGroup.Key,
                    field,
                    values.Length,
                    selected.Accepted,
                    selected.FalseAccepts,
                    selected.AcceptRate,
                    selected.MatchRate,
                    selected.MinScore,
                    selected.MinMargin,
                    enabled,
                    minAcceptRate,
                    reason));
            }
        }

        return rows;
    }

    private static string CalibrationDisableReason(
        bool hasCrossValidation,
        bool eligibleField,
        FastOcrThresholdChoice selected,
        double minAcceptRate)
    {
        if (!hasCrossValidation)
        {
            return "needs_at_least_two_shadow_runs";
        }

        if (!eligibleField)
        {
            return "field_not_assist_eligible";
        }

        if (selected.FalseAccepts > 0)
        {
            return "false_accepts";
        }

        if (selected.Accepted == 0)
        {
            return "no_safe_accepts";
        }

        if (selected.AcceptRate < minAcceptRate)
        {
            return $"accept_rate_below_{minAcceptRate.ToString("F2", CultureInfo.InvariantCulture)}";
        }

        return "disabled";
    }

    private static FastOcrThresholdChoice SelectThreshold(IReadOnlyList<FastOcrEvaluationRow> rows)
    {
        FastOcrThresholdChoice? best = null;
        foreach (var minScore in ScoreCandidates)
        {
            foreach (var minMargin in MarginCandidates)
            {
                var accepted = rows
                    .Where(row => IsCandidateAccepted(row, minScore, minMargin))
                    .ToArray();
                var falseAccepts = accepted.Count(row => !LabelMatches(row));
                if (falseAccepts > 0)
                {
                    continue;
                }

                var matches = accepted.Count(LabelMatches);
                var choice = new FastOcrThresholdChoice(
                    minScore,
                    minMargin,
                    accepted.Length,
                    falseAccepts,
                    rows.Count == 0 ? 0 : accepted.Length / (double)rows.Count,
                    rows.Count == 0 ? 0 : matches / (double)rows.Count);
                if (best is null
                    || choice.Accepted > best.Accepted
                    || (choice.Accepted == best.Accepted && choice.MinMargin > best.MinMargin)
                    || (choice.Accepted == best.Accepted && Math.Abs(choice.MinMargin - best.MinMargin) < 0.000001 && choice.MinScore > best.MinScore))
                {
                    best = choice;
                }
            }
        }

        return best ?? new FastOcrThresholdChoice(
            FastOcrTemplateIndex.DefaultMinScore,
            FastOcrTemplateIndex.DefaultMinMargin,
            0,
            rows.Count(row => IsCandidateAccepted(row, FastOcrTemplateIndex.DefaultMinScore, FastOcrTemplateIndex.DefaultMinMargin) && !LabelMatches(row)),
            0,
            0);
    }

    private static bool IsCandidateAccepted(FastOcrEvaluationRow row, double minScore, double minMargin)
    {
        return !string.IsNullOrWhiteSpace(row.FastLabel)
            && row.Score >= minScore
            && row.Margin >= minMargin;
    }

    private static bool LabelMatches(FastOcrEvaluationRow row)
    {
        return !string.IsNullOrWhiteSpace(row.CleanLabel)
            && string.Equals(row.FastLabel, row.CleanLabel, StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteCalibrationReport(string outputFile, IReadOnlyList<FastOcrCalibrationRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        using var writer = new StreamWriter(outputFile, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("field_key,rows,accepted,false_accepts,accept_rate,match_rate,min_score,min_margin,assist_enabled,min_accept_rate,reason");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", [
                EscapeCsv(row.FieldKey),
                row.Rows.ToString(CultureInfo.InvariantCulture),
                row.Accepted.ToString(CultureInfo.InvariantCulture),
                row.FalseAccepts.ToString(CultureInfo.InvariantCulture),
                row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MatchRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MinScore.ToString("F6", CultureInfo.InvariantCulture),
                row.MinMargin.ToString("F6", CultureInfo.InvariantCulture),
                row.AssistEnabled.ToString(CultureInfo.InvariantCulture),
                row.MinAcceptRate.ToString("F6", CultureInfo.InvariantCulture),
                EscapeCsv(row.Reason)
            ]));
        }
    }

    private static void WriteProfileCalibrationReport(string outputFile, IReadOnlyList<FastOcrProfileCalibrationRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        using var writer = new StreamWriter(outputFile, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("visual_profile_id,field_key,rows,accepted,false_accepts,accept_rate,match_rate,min_score,min_margin,assist_enabled,min_accept_rate,reason");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", [
                EscapeCsv(row.VisualProfileId),
                EscapeCsv(row.FieldKey),
                row.Rows.ToString(CultureInfo.InvariantCulture),
                row.Accepted.ToString(CultureInfo.InvariantCulture),
                row.FalseAccepts.ToString(CultureInfo.InvariantCulture),
                row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MatchRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MinScore.ToString("F6", CultureInfo.InvariantCulture),
                row.MinMargin.ToString("F6", CultureInfo.InvariantCulture),
                row.AssistEnabled.ToString(CultureInfo.InvariantCulture),
                row.MinAcceptRate.ToString("F6", CultureInfo.InvariantCulture),
                EscapeCsv(row.Reason)
            ]));
        }
    }

    private static void WriteFamilyCalibrationReport(string outputFile, IReadOnlyList<FastOcrFamilyCalibrationRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        using var writer = new StreamWriter(outputFile, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("profile_family_id,field_key,rows,accepted,false_accepts,accept_rate,match_rate,min_score,min_margin,assist_enabled,min_accept_rate,reason");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", [
                EscapeCsv(row.ProfileFamilyId),
                EscapeCsv(row.FieldKey),
                row.Rows.ToString(CultureInfo.InvariantCulture),
                row.Accepted.ToString(CultureInfo.InvariantCulture),
                row.FalseAccepts.ToString(CultureInfo.InvariantCulture),
                row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MatchRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MinScore.ToString("F6", CultureInfo.InvariantCulture),
                row.MinMargin.ToString("F6", CultureInfo.InvariantCulture),
                row.AssistEnabled.ToString(CultureInfo.InvariantCulture),
                row.MinAcceptRate.ToString("F6", CultureInfo.InvariantCulture),
                EscapeCsv(row.Reason)
            ]));
        }
    }

    private static void WriteFeatureEvalReport(string outputFile, IReadOnlyList<FastOcrFeatureEvalRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputFile) ?? ".");
        using var writer = new StreamWriter(outputFile, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        writer.WriteLine("feature,field_key,rows,accepted,false_accepts,accept_rate,match_rate,min_score,min_margin,assist_enabled,reason,avg_score,avg_margin");
        foreach (var row in rows)
        {
            writer.WriteLine(string.Join(",", [
                EscapeCsv(row.Feature),
                EscapeCsv(row.FieldKey),
                row.Rows.ToString(CultureInfo.InvariantCulture),
                row.Accepted.ToString(CultureInfo.InvariantCulture),
                row.FalseAccepts.ToString(CultureInfo.InvariantCulture),
                row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MatchRate.ToString("F6", CultureInfo.InvariantCulture),
                row.MinScore.ToString("F6", CultureInfo.InvariantCulture),
                row.MinMargin.ToString("F6", CultureInfo.InvariantCulture),
                row.AssistEnabled.ToString(CultureInfo.InvariantCulture),
                EscapeCsv(row.Reason),
                row.AvgScore.ToString("F6", CultureInfo.InvariantCulture),
                row.AvgMargin.ToString("F6", CultureInfo.InvariantCulture)
            ]));
        }
    }

    private static void WriteCalibrationSummary(IReadOnlyList<FastOcrCalibrationRow> rows)
    {
        foreach (var row in rows)
        {
            var prefix = $"field.{SanitizeKey(row.FieldKey)}";
            Console.WriteLine($"{prefix}.calibrated_assist={row.AssistEnabled.ToString(CultureInfo.InvariantCulture).ToLowerInvariant()}");
            Console.WriteLine($"{prefix}.calibrated_min_score={row.MinScore.ToString("F6", CultureInfo.InvariantCulture)}");
            Console.WriteLine($"{prefix}.calibrated_min_margin={row.MinMargin.ToString("F6", CultureInfo.InvariantCulture)}");
            Console.WriteLine($"{prefix}.calibrated_accept_rate={row.AcceptRate.ToString("F6", CultureInfo.InvariantCulture)}");
        }
    }

    private static void WriteSummary(IReadOnlyList<FastOcrEvaluationRow> rows)
    {
        Console.WriteLine($"fast_eval.rows={rows.Count}");
        Console.WriteLine($"fast_eval.accepted={rows.Count(row => row.Accepted)}");
        Console.WriteLine($"fast_eval.false_accepts={rows.Count(row => row.FalseAccept)}");
        Console.WriteLine($"fast_eval.rejects={rows.Count(row => !row.Accepted)}");

        foreach (var group in rows.GroupBy(row => row.FieldKey, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var values = group.ToArray();
            var accepted = values.Count(row => row.Accepted);
            var matches = values.Count(row => row.MatchesClean);
            var falseAccepts = values.Count(row => row.FalseAccept);
            var prefix = $"field.{SanitizeKey(group.Key)}";
            Console.WriteLine($"{prefix}.rows={values.Length}");
            Console.WriteLine($"{prefix}.accepted={accepted}");
            Console.WriteLine($"{prefix}.false_accepts={falseAccepts}");
            Console.WriteLine($"{prefix}.rejects={values.Length - accepted}");
            Console.WriteLine($"{prefix}.accept_rate={Rate(accepted, values.Length)}");
            Console.WriteLine($"{prefix}.match_rate={Rate(matches, values.Length)}");
            Console.WriteLine($"{prefix}.avg_ms={Average(values.Select(row => row.ElapsedMs))}");
            Console.WriteLine($"{prefix}.p90_ms={Percentile(values.Select(row => row.ElapsedMs), 0.9)}");
        }
    }

    private static string ResolveOutputFile(string shadowPath)
    {
        if (File.Exists(shadowPath))
        {
            return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(shadowPath)) ?? ".", "ocr_fast_eval.csv");
        }

        var direct = Path.Combine(shadowPath, "ocr_shadow.csv");
        return File.Exists(direct)
            ? Path.Combine(Path.GetFullPath(shadowPath), "ocr_fast_eval.csv")
            : Path.Combine(Path.GetFullPath(shadowPath), "ocr_fast_eval.csv");
    }

    private static string ResolveSidecarOutputFile(string outputFile, string name)
    {
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputFile)) ?? ".", name);
    }

    private static string ConfusionKey(FastOcrEvaluationRow row)
    {
        return $"{row.FieldKey}\0{row.CleanLabel}\0{row.FastLabel}";
    }

    private static string Rate(int numerator, int denominator)
    {
        return denominator == 0 ? "N/A" : (numerator / (double)denominator).ToString("F6", CultureInfo.InvariantCulture);
    }

    private static string Average(IEnumerable<double> values)
    {
        var array = values.ToArray();
        return array.Length == 0 ? "N/A" : array.Average().ToString("F3", CultureInfo.InvariantCulture);
    }

    private static string Percentile(IEnumerable<double> values, double percentile)
    {
        var array = values.OrderBy(value => value).ToArray();
        if (array.Length == 0)
        {
            return "N/A";
        }

        var index = Math.Clamp((int)Math.Ceiling(array.Length * percentile) - 1, 0, array.Length - 1);
        return array[index].ToString("F3", CultureInfo.InvariantCulture);
    }

    private static string SanitizeKey(string key)
    {
        return string.Concat(key.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
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

public sealed record FastOcrEvaluationRow(
    string Fold,
    string CsvFile,
    int ItemIndex,
    int RoiIndex,
    string VisualProfileId,
    string ProfileFamilyId,
    string FieldKey,
    string CleanLabel,
    string FastLabel,
    double Score,
    string Top2Label,
    double Top2Score,
    double Margin,
    bool AssistEnabled,
    bool Accepted,
    bool MatchesClean,
    bool FalseAccept,
    double ElapsedMs,
    string SourceFamilyId,
    bool CanonicalCropSucceeded,
    bool CanonicalCropFallback,
    double FeatureMs,
    string Reason);

public sealed record FastOcrProfileCalibrationRow(
    string VisualProfileId,
    string FieldKey,
    int Rows,
    int Accepted,
    int FalseAccepts,
    double AcceptRate,
    double MatchRate,
    double MinScore,
    double MinMargin,
    bool AssistEnabled,
    double MinAcceptRate,
    string Reason);

public sealed record FastOcrFamilyCalibrationRow(
    string ProfileFamilyId,
    string FieldKey,
    int Rows,
    int Accepted,
    int FalseAccepts,
    double AcceptRate,
    double MatchRate,
    double MinScore,
    double MinMargin,
    bool AssistEnabled,
    double MinAcceptRate,
    string Reason);

public sealed record FastOcrCalibrationRow(
    string FieldKey,
    int Rows,
    int Accepted,
    int FalseAccepts,
    double AcceptRate,
    double MatchRate,
    double MinScore,
    double MinMargin,
    bool AssistEnabled,
    double MinAcceptRate,
    string Reason);

public sealed record FastOcrFeatureEvalRow(
    string Feature,
    string FieldKey,
    int Rows,
    int Accepted,
    int FalseAccepts,
    double AcceptRate,
    double MatchRate,
    double MinScore,
    double MinMargin,
    bool AssistEnabled,
    string Reason,
    double AvgScore,
    double AvgMargin);

public sealed record FastOcrThresholdChoice(
    double MinScore,
    double MinMargin,
    int Accepted,
    int FalseAccepts,
    double AcceptRate,
    double MatchRate);

public sealed record FastOcrConfusionRow(
    string Kind,
    string FieldKey,
    string CleanLabel,
    string FastLabel,
    int Count,
    double AvgScore,
    double AvgMargin,
    string Folds)
{
    public static FastOcrConfusionRow From(
        string kind,
        IGrouping<string, FastOcrEvaluationRow> group)
    {
        var rows = group.ToArray();
        var first = rows[0];
        return new FastOcrConfusionRow(
            kind,
            first.FieldKey,
            first.CleanLabel,
            first.FastLabel,
            rows.Length,
            rows.Average(row => row.Score),
            rows.Average(row => row.Margin),
            string.Join("|", rows.Select(row => row.Fold).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase)));
    }
}
