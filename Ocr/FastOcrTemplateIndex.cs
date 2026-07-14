using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using ZZZScannerNext.Core;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Ocr;

public sealed class FastOcrTemplateIndex
{
    public const string CurrentVersion = "6";
    public const string LegacyFeature = "ahash-16x16-grayscale-v1";
    public const string CurrentFeature = "ahash-dhash-16x16-v3";
    public const string ExperimentalFeature = "ahash-dhash-vhash-16x16-v4";
    public const string CanonicalFeature = "canonical-ahash-dhash-vhash-edge-16x16-v6";
    public const double DefaultMinScore = 0.90;
    public const double DefaultMinMargin = 0.02;

    public string Version { get; set; } = CurrentVersion;
    public string CreatedAt { get; set; } = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
    public string Feature { get; set; } = CurrentFeature;
    public List<FastOcrTemplate> Templates { get; set; } = new();
    public Dictionary<string, FastOcrFieldPolicy> FieldPolicies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FastOcrFieldPolicy> ProfileFieldPolicies { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FastOcrFieldPolicy> FamilyFieldPolicies { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private Dictionary<string, List<FastOcrTemplate>>? _byField;
    private Dictionary<string, List<FastOcrTemplate>>? _byFieldAndProfile;
    private Dictionary<string, List<FastOcrTemplate>>? _byFieldAndFamily;

    public static IReadOnlySet<string> SupportedFields { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "level",
        "mainStat",
        "subStat1",
        "subStat2",
        "subStat3",
        "subStat4"
    };

    public static string DefaultIndexFile => AppPaths.DataFile("ocr_fast_templates.json");

    public static bool IsSupportedField(string fieldKey)
    {
        return SupportedFields.Contains(fieldKey);
    }

    public static bool IsDefaultAssistField(string fieldKey)
    {
        return IsSupportedField(fieldKey) && !fieldKey.Equals("name", StringComparison.OrdinalIgnoreCase);
    }

    public static FastOcrTemplateIndex Load(string file)
    {
        var index = JsonSerializer.Deserialize<FastOcrTemplateIndex>(File.ReadAllText(file), JsonDefaults.Read)
            ?? throw new InvalidDataException($"Cannot load fast OCR template index: {file}");
        index.NormalizeForUse();
        return index;
    }

    public void Save(string file)
    {
        NormalizeForUse();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file)) ?? ".");
        File.WriteAllText(file, JsonSerializer.Serialize(this, JsonDefaults.Write));
    }

    public static bool TryValidateFastModeIndex(string? file, out string resolvedFile, out string reason)
    {
        resolvedFile = string.IsNullOrWhiteSpace(file)
            ? DefaultIndexFile
            : Path.GetFullPath(file);
        reason = "";

        if (!File.Exists(resolvedFile))
        {
            reason = $"fast OCR index not found: {resolvedFile}";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(resolvedFile));
            var root = document.RootElement;
            var versionText = root.TryGetProperty("Version", out var versionElement)
                ? versionElement.ValueKind == JsonValueKind.Number
                    ? versionElement.GetRawText()
                    : versionElement.GetString()
                : "";
            if (!int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version)
                || version < 3)
            {
                reason = $"fast OCR index must be v3 or newer; found Version={versionText}";
                return false;
            }

            var index = Load(resolvedFile);
            if (!string.Equals(index.Feature, CurrentFeature, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(index.Feature, ExperimentalFeature, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(index.Feature, CanonicalFeature, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"unsupported fast OCR feature: {index.Feature}";
                return false;
            }

            if (index.Templates.Count == 0)
            {
                reason = "fast OCR index has no templates";
                return false;
            }

            var enabledFields = index.FieldPolicies
                .Concat(index.ProfileFieldPolicies.Select(pair => new KeyValuePair<string, FastOcrFieldPolicy>(FieldFromPolicyKey(pair.Key), pair.Value)))
                .Concat(index.FamilyFieldPolicies.Select(pair => new KeyValuePair<string, FastOcrFieldPolicy>(FieldFromPolicyKey(pair.Key), pair.Value)))
                .Where(pair => pair.Value.AssistEnabled && pair.Value.TemplateCount > 0)
                .Select(pair => pair.Key)
                .Where(IsDefaultAssistField)
                .Where(field => !string.IsNullOrWhiteSpace(field))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(field => field, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (enabledFields.Length == 0)
            {
                reason = "fast OCR index has no assist-enabled fields";
                return false;
            }

            reason = $"enabled_fields={string.Join("|", enabledFields)}";
            return true;
        }
        catch (Exception ex)
        {
            reason = $"cannot validate fast OCR index: {ex.Message}";
            return false;
        }
    }

    public static FastOcrTemplateIndex Build(IEnumerable<OcrShadowDatasetRow> rows, Action<string>? log = null, string featureName = CurrentFeature)
    {
        var index = new FastOcrTemplateIndex
        {
            Feature = string.IsNullOrWhiteSpace(featureName) ? CurrentFeature : featureName
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (!IsSupportedField(row.FieldKey) || string.IsNullOrWhiteSpace(row.CleanLabel))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(row.ImageFile) || !File.Exists(row.ResolvedImageFile))
            {
                log?.Invoke($"skip_missing_image field={row.FieldKey} item={row.ItemIndex} image={row.ResolvedImageFile}");
                continue;
            }

            try
            {
                using var bitmap = new Bitmap(row.ResolvedImageFile);
                var feature = FastOcrImageFeature.FromBitmap(bitmap, index.Feature);
                var visualProfileId = NormalizeProfileId(row.VisualProfileId);
                var familyId = ProfileFamilyId(visualProfileId);
                var key = $"{visualProfileId}\0{row.FieldKey}\0{row.CleanLabel}\0{feature.ToKey()}";
                if (!seen.Add(key))
                {
                    continue;
                }

                index.Templates.Add(new FastOcrTemplate
                {
                    FieldKey = row.FieldKey,
                    Label = row.CleanLabel,
                    VisualProfileId = visualProfileId,
                    ProfileFamilyId = familyId,
                    Bits = feature.ToHexWords(),
                    SourceImage = Path.GetFullPath(row.ResolvedImageFile)
                });
            }
            catch (Exception ex)
            {
                log?.Invoke($"skip_bad_image field={row.FieldKey} item={row.ItemIndex} image={row.ResolvedImageFile} error={ex.Message}");
            }
        }

        index.Templates = index.Templates
            .OrderBy(template => template.FieldKey, StringComparer.OrdinalIgnoreCase)
            .ThenBy(template => template.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(template => template.VisualProfileId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(template => string.Join("", template.Bits), StringComparer.Ordinal)
            .ToList();
        index.NormalizeForUse();
        return index;
    }

    public FastOcrMatch Match(string fieldKey, Bitmap source, Rectangle roi)
    {
        return Match(fieldKey, source, roi, "");
    }

    public FastOcrMatch Match(string fieldKey, Bitmap source, Rectangle roi, string visualProfileId)
    {
        return Match(fieldKey, source, roi, visualProfileId, ProfileRoutingMode.Auto);
    }

    public FastOcrMatch Match(string fieldKey, Bitmap source, Rectangle roi, string visualProfileId, ProfileRoutingMode routingMode)
    {
        var normalizedProfileId = NormalizeProfileId(visualProfileId);
        var route = ResolveTemplateRoute(fieldKey, normalizedProfileId, routingMode);
        var candidates = route.Templates;
        if (candidates.Count == 0)
        {
            return FastOcrMatch.Empty(fieldKey, route.Reason);
        }

        var feature = FastOcrImageFeature.FromBitmap(source, roi, Feature);
        FastOcrTemplate? best = null;
        var bestDistance = int.MaxValue;
        FastOcrTemplate? secondDifferentLabel = null;
        var secondDifferentDistance = int.MaxValue;

        foreach (var template in candidates)
        {
            var distance = feature.DistanceTo(FastOcrImageFeature.FromHexWords(template.Bits));
            if (distance < bestDistance)
            {
                if (best is not null && !template.Label.Equals(best.Label, StringComparison.OrdinalIgnoreCase))
                {
                    secondDifferentLabel = best;
                    secondDifferentDistance = bestDistance;
                }

                best = template;
                bestDistance = distance;
                continue;
            }

            if (best is not null
                && !template.Label.Equals(best.Label, StringComparison.OrdinalIgnoreCase)
                && distance < secondDifferentDistance)
            {
                secondDifferentLabel = template;
                secondDifferentDistance = distance;
            }
        }

        if (best is null)
        {
            return FastOcrMatch.Empty(fieldKey, "no_match");
        }

        if (secondDifferentLabel is null)
        {
            foreach (var template in candidates.Where(template => !template.Label.Equals(best.Label, StringComparison.OrdinalIgnoreCase)))
            {
                var distance = feature.DistanceTo(FastOcrImageFeature.FromHexWords(template.Bits));
                if (distance < secondDifferentDistance)
                {
                    secondDifferentLabel = template;
                    secondDifferentDistance = distance;
                }
            }
        }

        var score = ScoreFromDistance(bestDistance, feature.BitCount);
        var top2Score = secondDifferentLabel is null ? 0 : ScoreFromDistance(secondDifferentDistance, feature.BitCount);
        var margin = secondDifferentLabel is null ? 1 : score - top2Score;
        var policy = PolicyForField(fieldKey, route.PolicyProfileId);
        return new FastOcrMatch(
            fieldKey,
            best.Label,
            score,
            bestDistance,
            candidates.Count,
            best.SourceImage,
            best.VisualProfileId,
            ProfileFamilyId(best.VisualProfileId),
            secondDifferentLabel?.Label ?? "",
            top2Score,
            margin,
            policy.AssistEnabled,
            policy.MinScore,
            policy.MinMargin,
            feature.CanonicalCropSucceeded,
            feature.CanonicalCropFallback,
            feature.FeatureElapsedMs,
            route.Reason);
    }

    public bool IsMatchAccepted(FastOcrMatch match, bool requireAssistEnabled)
    {
        if (string.IsNullOrWhiteSpace(match.Label))
        {
            return false;
        }

        if (requireAssistEnabled && !match.AssistEnabled)
        {
            return false;
        }

        return match.Score >= match.MinScore && match.Margin >= match.MinMargin;
    }

    public FastOcrFieldPolicy PolicyForField(string fieldKey)
    {
        return PolicyForField(fieldKey, "");
    }

    public FastOcrFieldPolicy PolicyForField(string fieldKey, string visualProfileId)
    {
        if (FieldPolicies is null || FieldPolicies.Count == 0)
        {
            NormalizeForUse();
        }

        var normalizedProfileId = NormalizeProfileId(visualProfileId);
        if (!string.IsNullOrWhiteSpace(normalizedProfileId)
            && ProfileFieldPolicies.TryGetValue(ProfilePolicyKey(normalizedProfileId, fieldKey), out var profilePolicy))
        {
            DisableNameAssist(fieldKey, profilePolicy);
            return profilePolicy;
        }

        var familyId = ProfileFamilyId(normalizedProfileId);
        if (!string.IsNullOrWhiteSpace(familyId)
            && FamilyFieldPolicies.TryGetValue(ProfilePolicyKey(familyId, fieldKey), out var familyPolicy))
        {
            DisableNameAssist(fieldKey, familyPolicy);
            return familyPolicy;
        }

        var policies = FieldPolicies ?? new Dictionary<string, FastOcrFieldPolicy>(StringComparer.OrdinalIgnoreCase);
        var selectedPolicy = policies.TryGetValue(fieldKey, out var policy)
            ? policy
            : FastOcrFieldPolicy.Default(fieldKey);
        DisableNameAssist(fieldKey, selectedPolicy);
        return selectedPolicy;
    }

    private IReadOnlyList<FastOcrTemplate> TemplatesForField(string fieldKey)
    {
        _byField ??= Templates
            .GroupBy(template => template.FieldKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        return _byField.TryGetValue(fieldKey, out var templates) ? templates : [];
    }

    public FastOcrProfileRoute DescribeRoute(string fieldKey, string visualProfileId, ProfileRoutingMode routingMode)
    {
        var route = ResolveTemplateRoute(fieldKey, NormalizeProfileId(visualProfileId), routingMode);
        return new FastOcrProfileRoute(
            fieldKey,
            NormalizeProfileId(visualProfileId),
            route.PolicyProfileId,
            route.RouteName,
            route.Templates.Count,
            route.ProfileFamilyId,
            route.Reason);
    }

    private TemplateRoute ResolveTemplateRoute(string fieldKey, string visualProfileId, ProfileRoutingMode routingMode)
    {
        var normalizedProfileId = NormalizeProfileId(visualProfileId);
        var requestedFamily = ProfileFamilyId(normalizedProfileId);
        var exactTemplates = TemplatesForFieldAndProfile(fieldKey, normalizedProfileId);
        if (exactTemplates.Count > 0)
        {
            return new TemplateRoute(
                exactTemplates,
                normalizedProfileId,
                "exact",
                requestedFamily,
                $"profile_exact:{normalizedProfileId}");
        }

        if (routingMode == ProfileRoutingMode.Strict)
        {
            return new TemplateRoute(
                [],
                normalizedProfileId,
                "strict_missing",
                requestedFamily,
                $"profile_strict_missing:{normalizedProfileId}");
        }

        if (!string.IsNullOrWhiteSpace(requestedFamily))
        {
            var familyTemplates = TemplatesForFieldAndFamily(fieldKey, requestedFamily);
            if (familyTemplates.Count > 0)
            {
                return new TemplateRoute(
                    familyTemplates,
                    requestedFamily,
                    "family",
                    requestedFamily,
                    $"profile_family:{normalizedProfileId}->{requestedFamily}");
            }
        }

        if (routingMode == ProfileRoutingMode.Family)
        {
            return new TemplateRoute(
                [],
                requestedFamily,
                "family_missing",
                requestedFamily,
                $"profile_family_missing:{normalizedProfileId}->{requestedFamily}");
        }

        var compatibleProfile = FindCompatibleProfile(normalizedProfileId, fieldKey);
        if (!string.IsNullOrWhiteSpace(compatibleProfile))
        {
            var compatibleTemplates = TemplatesForFieldAndProfile(fieldKey, compatibleProfile);
            if (compatibleTemplates.Count > 0)
            {
                return new TemplateRoute(
                    compatibleTemplates,
                    compatibleProfile,
                    "compatible",
                    ProfileFamilyId(compatibleProfile),
                    $"profile_compatible:{normalizedProfileId}->{compatibleProfile}");
            }
        }

        if (routingMode == ProfileRoutingMode.Auto)
        {
            var allTemplates = TemplatesForField(fieldKey);
            if (allTemplates.Count > 0)
            {
                return new TemplateRoute(
                    allTemplates,
                    "",
                    "global",
                    "",
                    $"profile_global_fallback:{normalizedProfileId}");
            }
        }

        return new TemplateRoute(
            [],
            normalizedProfileId,
            "no_templates",
            requestedFamily,
            $"profile_no_templates:{normalizedProfileId}");
    }

    private IReadOnlyList<FastOcrTemplate> TemplatesForFieldAndFamily(string fieldKey, string profileFamilyId)
    {
        _byFieldAndFamily ??= Templates
            .GroupBy(template => ProfilePolicyKey(ProfileFamilyId(template.VisualProfileId), template.FieldKey), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        return _byFieldAndFamily.TryGetValue(ProfilePolicyKey(profileFamilyId, fieldKey), out var templates) ? templates : [];
    }

    private IReadOnlyList<FastOcrTemplate> TemplatesForFieldAndProfile(string fieldKey, string visualProfileId)
    {
        _byFieldAndProfile ??= Templates
            .GroupBy(template => ProfilePolicyKey(NormalizeProfileId(template.VisualProfileId), template.FieldKey), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);
        return _byFieldAndProfile.TryGetValue(ProfilePolicyKey(visualProfileId, fieldKey), out var templates) ? templates : [];
    }

    private string FindCompatibleProfile(string visualProfileId, string fieldKey)
    {
        var requested = VisualProfileKey.Parse(visualProfileId);
        if (!requested.IsUsable)
        {
            return "";
        }

        var candidates = Templates
            .Where(template => template.FieldKey.Equals(fieldKey, StringComparison.OrdinalIgnoreCase))
            .Select(template => NormalizeProfileId(template.VisualProfileId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(profile => (profile, key: VisualProfileKey.Parse(profile)))
            .Where(item => item.key.IsUsable
                && item.key.ClientKind.Equals(requested.ClientKind, StringComparison.OrdinalIgnoreCase)
                && item.key.QualityLabel.Equals(requested.QualityLabel, StringComparison.OrdinalIgnoreCase))
            .Select(item => (item.profile, delta: Math.Abs(item.key.AspectRatio - requested.AspectRatio), pixels: Math.Abs(item.key.PixelCount - requested.PixelCount)))
            .OrderBy(item => item.delta)
            .ThenBy(item => item.pixels)
            .FirstOrDefault();
        return candidates.delta <= 0.03 ? candidates.profile : "";
    }

    private void NormalizeForUse()
    {
        Version = CurrentVersion;
        Feature = string.IsNullOrWhiteSpace(Feature) ? LegacyFeature : Feature;
        FieldPolicies = new Dictionary<string, FastOcrFieldPolicy>(FieldPolicies ?? new(), StringComparer.OrdinalIgnoreCase);
        ProfileFieldPolicies = new Dictionary<string, FastOcrFieldPolicy>(ProfileFieldPolicies ?? new(), StringComparer.OrdinalIgnoreCase);
        FamilyFieldPolicies = new Dictionary<string, FastOcrFieldPolicy>(FamilyFieldPolicies ?? new(), StringComparer.OrdinalIgnoreCase);
        foreach (var template in Templates)
        {
            template.VisualProfileId = NormalizeProfileId(template.VisualProfileId);
            template.ProfileFamilyId = string.IsNullOrWhiteSpace(template.ProfileFamilyId)
                ? ProfileFamilyId(template.VisualProfileId)
                : NormalizeProfileId(template.ProfileFamilyId);
        }

        foreach (var field in SupportedFields)
        {
            var templates = Templates
                .Where(template => template.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            var policy = FieldPolicies.TryGetValue(field, out var existing)
                ? existing
                : FastOcrFieldPolicy.Default(field);
            policy.TemplateCount = templates.Length;
            policy.LabelCount = templates
                .Select(template => template.Label)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            DisableNameAssist(field, policy);
            FieldPolicies[field] = policy;
        }

        foreach (var profileId in Templates
            .Select(template => NormalizeProfileId(template.VisualProfileId))
            .Where(profile => !string.IsNullOrWhiteSpace(profile))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var field in SupportedFields)
            {
                var key = ProfilePolicyKey(profileId, field);
                var templates = Templates
                    .Where(template => template.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase)
                        && NormalizeProfileId(template.VisualProfileId).Equals(profileId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (!ProfileFieldPolicies.TryGetValue(key, out var policy))
                {
                    continue;
                }

                policy.TemplateCount = templates.Length;
                policy.LabelCount = templates
                    .Select(template => template.Label)
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                DisableNameAssist(field, policy);
                ProfileFieldPolicies[key] = policy;
            }
        }

        foreach (var familyId in Templates
            .Select(template => ProfileFamilyId(template.VisualProfileId))
            .Where(family => !string.IsNullOrWhiteSpace(family))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var field in SupportedFields)
            {
                var key = ProfilePolicyKey(familyId, field);
                var templates = Templates
                    .Where(template => template.FieldKey.Equals(field, StringComparison.OrdinalIgnoreCase)
                        && ProfileFamilyId(template.VisualProfileId).Equals(familyId, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (!FamilyFieldPolicies.TryGetValue(key, out var policy))
                {
                    continue;
                }

                policy.TemplateCount = templates.Length;
                policy.LabelCount = templates
                    .Select(template => template.Label)
                    .Where(label => !string.IsNullOrWhiteSpace(label))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();
                DisableNameAssist(field, policy);
                FamilyFieldPolicies[key] = policy;
            }
        }

        _byField = null;
        _byFieldAndProfile = null;
        _byFieldAndFamily = null;
    }

    public static string NormalizeProfileId(string? visualProfileId)
    {
        return string.IsNullOrWhiteSpace(visualProfileId)
            ? "legacy"
            : visualProfileId.Trim().ToLowerInvariant();
    }

    public static string ProfilePolicyKey(string visualProfileId, string fieldKey)
    {
        return $"{NormalizeProfileId(visualProfileId)}|{fieldKey}";
    }

    private static void DisableNameAssist(string fieldKey, FastOcrFieldPolicy policy)
    {
        if (fieldKey.Equals("name", StringComparison.OrdinalIgnoreCase))
        {
            policy.AssistEnabled = false;
        }
    }

    private static string FieldFromPolicyKey(string policyKey)
    {
        var separator = policyKey.LastIndexOf('|');
        return separator >= 0 && separator + 1 < policyKey.Length
            ? policyKey[(separator + 1)..]
            : policyKey;
    }

    public static string ProfileFamilyId(string? visualProfileId)
    {
        var normalized = NormalizeProfileId(visualProfileId);
        var key = VisualProfileKey.Parse(normalized);
        if (!key.IsUsable)
        {
            return normalized.Equals("legacy", StringComparison.OrdinalIgnoreCase) ? "legacy" : "";
        }

        return $"{key.ClientKind}-{key.AspectBucket}-{key.DpiBucket}-{key.QualityLabel}".ToLowerInvariant();
    }

    private static double ScoreFromDistance(int distance, int bitCount)
    {
        return bitCount <= 0 ? 0 : 1.0 - distance / (double)bitCount;
    }
}

public sealed record FastOcrProfileRoute(
    string FieldKey,
    string RequestedProfileId,
    string PolicyProfileId,
    string RouteName,
    int TemplateCount,
    string ProfileFamilyId,
    string Reason);

internal sealed record TemplateRoute(
    IReadOnlyList<FastOcrTemplate> Templates,
    string PolicyProfileId,
    string RouteName,
    string ProfileFamilyId,
    string Reason);

internal readonly record struct VisualProfileKey(string ClientKind, int Width, int Height, string QualityLabel)
{
    public bool IsUsable => !string.IsNullOrWhiteSpace(ClientKind) && Width > 0 && Height > 0;

    public double AspectRatio => Height <= 0 ? 0 : Width / (double)Height;

    public string AspectBucket => Width <= 0 || Height <= 0
        ? "unknown"
        : NormalizeBucket(Math.Round(AspectRatio, 2).ToString("F2", CultureInfo.InvariantCulture));

    public string DpiBucket => "dpi";

    public int PixelCount => Math.Max(0, Width) * Math.Max(0, Height);

    public string ToFamilyId(string qualityLabel)
    {
        var normalizedQuality = string.IsNullOrWhiteSpace(qualityLabel)
            ? "current"
            : qualityLabel.Trim().ToLowerInvariant();
        return $"{ClientKind}-{AspectBucket}-{DpiBucket}-{normalizedQuality}";
    }

    public static VisualProfileKey Parse(string profileId)
    {
        var parts = profileId.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return new VisualProfileKey("", 0, 0, "");
        }

        for (var i = 1; i < parts.Length; i++)
        {
            var sizeParts = parts[i].Split('x', StringSplitOptions.RemoveEmptyEntries);
            if (sizeParts.Length != 2
                || !int.TryParse(sizeParts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width)
                || !int.TryParse(sizeParts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
            {
                continue;
            }

            var quality = i + 1 < parts.Length
                ? string.Join("-", parts.Skip(i + 1))
                : "current";
            return new VisualProfileKey(parts[0], width, height, quality);
        }

        return new VisualProfileKey("", 0, 0, "");
    }

    private static string NormalizeBucket(string value)
    {
        return string.Concat(value.Select(c => char.IsLetterOrDigit(c) ? c : '-'))
            .Trim('-')
            .ToLowerInvariant();
    }
}

public sealed class FastOcrFieldPolicy
{
    public bool AssistEnabled { get; set; }
    public double MinScore { get; set; } = FastOcrTemplateIndex.DefaultMinScore;
    public double MinMargin { get; set; } = FastOcrTemplateIndex.DefaultMinMargin;
    public int TemplateCount { get; set; }
    public int LabelCount { get; set; }

    public static FastOcrFieldPolicy Default(string fieldKey)
    {
        return new FastOcrFieldPolicy
        {
            AssistEnabled = FastOcrTemplateIndex.IsDefaultAssistField(fieldKey),
            MinScore = FastOcrTemplateIndex.DefaultMinScore,
            MinMargin = FastOcrTemplateIndex.DefaultMinMargin
        };
    }
}

public sealed class FastOcrTemplate
{
    public string FieldKey { get; set; } = "";
    public string Label { get; set; } = "";
    public string VisualProfileId { get; set; } = "legacy";
    public string ProfileFamilyId { get; set; } = "";
    public string[] Bits { get; set; } = [];
    public string SourceImage { get; set; } = "";
}

public sealed record FastOcrMatch(
    string FieldKey,
    string Label,
    double Score,
    int Distance,
    int CandidateCount,
    string SourceImage,
    string SourceProfileId,
    string SourceFamilyId,
    string Top2Label,
    double Top2Score,
    double Margin,
    bool AssistEnabled,
    double MinScore,
    double MinMargin,
    bool CanonicalCropSucceeded,
    bool CanonicalCropFallback,
    double FeatureElapsedMs,
    string Reason)
{
    public static FastOcrMatch Empty(string fieldKey, string reason) =>
        new(fieldKey, "", 0, FastOcrImageFeature.CurrentBitCount, 0, "", "", "", "", 0, 0, false, FastOcrTemplateIndex.DefaultMinScore, FastOcrTemplateIndex.DefaultMinMargin, false, false, 0, reason);
}

public sealed class FastOcrImageFeature
{
    public const int Size = 16;
    public const int LegacyBitCount = Size * Size;
    public const int CurrentBitCount = LegacyBitCount * 2;
    public const int ExperimentalBitCount = LegacyBitCount * 3;
    public const int CanonicalBitCount = LegacyBitCount * 4;

    private FastOcrImageFeature(IReadOnlyList<ulong> words)
    {
        Words = words.ToArray();
    }

    private FastOcrImageFeature(IReadOnlyList<ulong> words, bool canonicalCropSucceeded, bool canonicalCropFallback, double featureElapsedMs)
    {
        Words = words.ToArray();
        CanonicalCropSucceeded = canonicalCropSucceeded;
        CanonicalCropFallback = canonicalCropFallback;
        FeatureElapsedMs = featureElapsedMs;
    }

    public IReadOnlyList<ulong> Words { get; }
    public bool CanonicalCropSucceeded { get; }
    public bool CanonicalCropFallback { get; }
    public double FeatureElapsedMs { get; }

    public int BitCount => Words.Count * 64;

    public static FastOcrImageFeature FromBitmap(Bitmap source)
    {
        return FromBitmap(source, new Rectangle(0, 0, source.Width, source.Height), FastOcrTemplateIndex.CurrentFeature);
    }

    public static FastOcrImageFeature FromBitmap(Bitmap source, string featureName)
    {
        return FromBitmap(source, new Rectangle(0, 0, source.Width, source.Height), featureName);
    }

    public static FastOcrImageFeature FromBitmap(Bitmap source, Rectangle sourceRect)
    {
        return FromBitmap(source, sourceRect, FastOcrTemplateIndex.CurrentFeature);
    }

    public static FastOcrImageFeature FromBitmap(Bitmap source, Rectangle sourceRect, string featureName)
    {
        var sw = Stopwatch.StartNew();
        var bounds = new Rectangle(0, 0, source.Width, source.Height);
        var rect = Rectangle.Intersect(bounds, sourceRect);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            sw.Stop();
            return new FastOcrImageFeature(EmptyWordsForFeature(featureName), false, true, sw.Elapsed.TotalMilliseconds);
        }

        if (string.Equals(featureName, FastOcrTemplateIndex.LegacyFeature, StringComparison.OrdinalIgnoreCase))
        {
            sw.Stop();
            return new FastOcrImageFeature(BuildAverageHash(source, rect), false, false, sw.Elapsed.TotalMilliseconds);
        }

        var useCanonical = string.Equals(featureName, FastOcrTemplateIndex.CanonicalFeature, StringComparison.OrdinalIgnoreCase);
        var canonicalSucceeded = false;
        var canonicalFallback = false;
        Rectangle? canonical = null;
        if (useCanonical)
        {
            canonical = TryCanonicalize(source, rect, out _, out canonicalSucceeded, out canonicalFallback);
        }
        else
        {
            canonicalSucceeded = false;
            canonicalFallback = false;
        }

        var featureRect = canonical ?? rect;
        var words = new List<ulong>(16);
        words.AddRange(BuildAverageHash(source, featureRect));
        words.AddRange(BuildHorizontalDifferenceHash(source, featureRect));
        if (string.Equals(featureName, FastOcrTemplateIndex.ExperimentalFeature, StringComparison.OrdinalIgnoreCase)
            || useCanonical)
        {
            words.AddRange(BuildVerticalDifferenceHash(source, featureRect));
        }

        if (useCanonical)
        {
            words.AddRange(BuildEdgeDensityHash(source, featureRect));
        }

        sw.Stop();
        return new FastOcrImageFeature(words, canonicalSucceeded, canonicalFallback, sw.Elapsed.TotalMilliseconds);
    }

    public static FastOcrImageFeature FromHexWords(IReadOnlyList<string> words)
    {
        var parsed = words
            .Select(Parse)
            .ToArray();
        return new FastOcrImageFeature(parsed.Length == 0 ? [0, 0, 0, 0] : parsed);
    }

    public string[] ToHexWords()
    {
        return Words
            .Select(word => word.ToString("X16", CultureInfo.InvariantCulture))
            .ToArray();
    }

    public string ToKey()
    {
        return string.Join("", ToHexWords());
    }

    public int DistanceTo(FastOcrImageFeature other)
    {
        var count = Math.Max(Words.Count, other.Words.Count);
        var distance = 0;
        for (var i = 0; i < count; i++)
        {
            var left = i < Words.Count ? Words[i] : 0;
            var right = i < other.Words.Count ? other.Words[i] : 0;
            distance += BitOperations.PopCount(left ^ right);
        }

        return distance;
    }

    private static ulong[] BuildAverageHash(Bitmap source, Rectangle rect)
    {
        var pixels = RenderLuma(source, rect, Size, Size);
        var total = 0.0;
        foreach (var value in pixels)
        {
            total += value;
        }

        var average = total / LegacyBitCount;
        var words = new ulong[4];
        for (var i = 0; i < pixels.Length; i++)
        {
            if (pixels[i] < average)
            {
                continue;
            }

            words[i / 64] |= 1UL << (i % 64);
        }

        return words;
    }

    private static ulong[] BuildHorizontalDifferenceHash(Bitmap source, Rectangle rect)
    {
        var pixels = RenderLuma(source, rect, Size + 1, Size);
        var words = new ulong[4];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var bitIndex = y * Size + x;
                var left = pixels[y * (Size + 1) + x];
                var right = pixels[y * (Size + 1) + x + 1];
                if (left >= right)
                {
                    words[bitIndex / 64] |= 1UL << (bitIndex % 64);
                }
            }
        }

        return words;
    }

    private static ulong[] BuildVerticalDifferenceHash(Bitmap source, Rectangle rect)
    {
        var pixels = RenderLuma(source, rect, Size, Size + 1);
        var words = new ulong[4];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var bitIndex = y * Size + x;
                var top = pixels[y * Size + x];
                var bottom = pixels[(y + 1) * Size + x];
                if (top >= bottom)
                {
                    words[bitIndex / 64] |= 1UL << (bitIndex % 64);
                }
            }
        }

        return words;
    }

    private static ulong[] BuildEdgeDensityHash(Bitmap source, Rectangle rect)
    {
        var pixels = RenderLuma(source, rect, Size, Size);
        var words = new ulong[4];
        for (var y = 1; y < Size - 1; y++)
        {
            for (var x = 1; x < Size - 1; x++)
            {
                var idx = y * Size + x;
                var left = pixels[idx - 1];
                var right = pixels[idx + 1];
                var up = pixels[idx - Size];
                var down = pixels[idx + Size];
                var gradient = Math.Abs(left - right) + Math.Abs(up - down);
                if (gradient >= 36)
                {
                    words[idx / 64] |= 1UL << (idx % 64);
                }
            }
        }

        return words;
    }

    private static ulong[] EmptyWordsForFeature(string featureName)
    {
        if (string.Equals(featureName, FastOcrTemplateIndex.CanonicalFeature, StringComparison.OrdinalIgnoreCase))
        {
            return new ulong[16];
        }

        if (string.Equals(featureName, FastOcrTemplateIndex.ExperimentalFeature, StringComparison.OrdinalIgnoreCase))
        {
            return new ulong[12];
        }

        if (string.Equals(featureName, FastOcrTemplateIndex.LegacyFeature, StringComparison.OrdinalIgnoreCase))
        {
            return new ulong[4];
        }

        return new ulong[8];
    }

    private static double[] RenderLuma(Bitmap source, Rectangle rect, int width, int height)
    {
        using var resized = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(resized))
        {
            graphics.DrawImage(source, new Rectangle(0, 0, width, height), rect, GraphicsUnit.Pixel);
        }

        var pixels = new double[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = resized.GetPixel(x, y);
                pixels[y * width + x] = color.R * 0.299 + color.G * 0.587 + color.B * 0.114;
            }
        }

        return pixels;
    }

    private static Rectangle? TryCanonicalize(Bitmap source, Rectangle rect, out Rectangle canonicalRect, out bool succeeded, out bool fallback)
    {
        canonicalRect = rect;
        succeeded = false;
        fallback = false;

        var insetX = Math.Max(1, (int)Math.Round(rect.Width * 0.08, MidpointRounding.AwayFromZero));
        var insetYTop = Math.Max(1, (int)Math.Round(rect.Height * 0.18, MidpointRounding.AwayFromZero));
        var insetYBottom = Math.Max(1, (int)Math.Round(rect.Height * 0.18, MidpointRounding.AwayFromZero));
        var cropped = Rectangle.FromLTRB(rect.Left + insetX, rect.Top + insetYTop, rect.Right - insetX, rect.Bottom - insetYBottom);
        if (cropped.Width < 4 || cropped.Height < 4)
        {
            fallback = true;
            return null;
        }

        const int sampleWidth = 48;
        const int sampleHeight = 24;
        var luminance = RenderLuma(source, cropped, sampleWidth, sampleHeight);
        var total = luminance.Length;
        if (total <= 0)
        {
            fallback = true;
            return null;
        }

        var darkPixels = luminance.Count(value => value < 210);
        var darkRate = darkPixels / (double)total;
        if (darkRate < 0.02)
        {
            fallback = true;
            return null;
        }

        var threshold = Math.Min(220, Math.Max(90, luminance.Average() - 12));
        var minX = sampleWidth;
        var minY = sampleHeight;
        var maxX = -1;
        var maxY = -1;
        for (var y = 0; y < sampleHeight; y++)
        {
            for (var x = 0; x < sampleWidth; x++)
            {
                var idx = y * sampleWidth + x;
                var center = luminance[idx];
                var left = x > 0 ? luminance[idx - 1] : center;
                var right = x + 1 < sampleWidth ? luminance[idx + 1] : center;
                var up = y > 0 ? luminance[idx - sampleWidth] : center;
                var down = y + 1 < sampleHeight ? luminance[idx + sampleWidth] : center;
                var gradient = Math.Abs(left - right) + Math.Abs(up - down);
                if (center <= threshold || gradient >= 42)
                {
                    minX = Math.Min(minX, x);
                    minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x);
                    maxY = Math.Max(maxY, y);
                }
            }
        }

        if (maxX < minX || maxY < minY)
        {
            fallback = true;
            return null;
        }

        var leftPx = cropped.Left + (int)Math.Floor(minX * cropped.Width / (double)sampleWidth);
        var topPx = cropped.Top + (int)Math.Floor(minY * cropped.Height / (double)sampleHeight);
        var rightPx = cropped.Left + (int)Math.Ceiling((maxX + 1) * cropped.Width / (double)sampleWidth);
        var bottomPx = cropped.Top + (int)Math.Ceiling((maxY + 1) * cropped.Height / (double)sampleHeight);
        var textBounds = Rectangle.FromLTRB(leftPx, topPx, rightPx, bottomPx);
        var padX = Math.Max(1, (int)Math.Round(textBounds.Width * 0.12, MidpointRounding.AwayFromZero));
        var padY = Math.Max(1, (int)Math.Round(textBounds.Height * 0.20, MidpointRounding.AwayFromZero));
        canonicalRect = Rectangle.Intersect(cropped, Rectangle.FromLTRB(
            textBounds.Left - padX,
            textBounds.Top - padY,
            textBounds.Right + padX,
            textBounds.Bottom + padY));
        if (canonicalRect.Width < 4 || canonicalRect.Height < 4)
        {
            fallback = true;
            canonicalRect = cropped;
            return null;
        }

        succeeded = true;
        return canonicalRect;
    }

    private static ulong Parse(string word)
    {
        return ulong.TryParse(word, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;
    }
}
