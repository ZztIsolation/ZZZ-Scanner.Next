using System.Globalization;
using System.Text.Json;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Core;

public sealed class RuntimeVisualProfile
{
    public string SchemaVersion { get; set; } = "2";
    public string CreatedAt { get; set; } = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
    public string ProfileId { get; set; } = "";
    public string TrainingProfileId { get; set; } = "";
    public string ProfileFamilyId { get; set; } = "";
    public string ProfileGeometryStatus { get; set; } = "";
    public string RequestedProfileId { get; set; } = "auto";
    public string DetectedProfileId { get; set; } = "";
    public string GeometryKey { get; set; } = "";
    public string RequestedQualityLabel { get; set; } = "current";
    public string ClientKind { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string QualityLabel { get; set; } = "current";
    public int ClientWidth { get; set; }
    public int ClientHeight { get; set; }
    public double AspectRatio { get; set; }
    public int Dpi { get; set; }
    public double CoordinateScale { get; set; }
    public string CaptureModeRequested { get; set; } = "";
    public string CaptureModeActive { get; set; } = "";
    public string CaptureFrameBackend { get; set; } = "";
    public string ProfileRoutingDecision { get; set; } = "not_evaluated";
    public string ScannerVersion { get; set; } = AppInfo.Version;

    public static RuntimeVisualProfile Create(
        string processName,
        string requestedProfileId,
        string qualityLabel,
        VisualProfileClientKind requestedClientKind,
        CaptureMode captureModeRequested,
        GameWindow window)
    {
        var clientKind = ResolveClientKind(processName, requestedClientKind);
        var requestedId = NormalizeToken(string.IsNullOrWhiteSpace(requestedProfileId) ? "auto" : requestedProfileId);
        var normalizedQuality = NormalizeToken(string.IsNullOrWhiteSpace(qualityLabel) ? "current" : qualityLabel);
        var width = window.ClientScreenRect.Width;
        var height = window.ClientScreenRect.Height;
        var geometryKey = $"{clientKind}-{width}x{height}-dpi{window.Dpi}-{normalizedQuality}";
        var detectedProfileId = $"{clientKind}-{width}x{height}-{normalizedQuality}";
        var profileId = ResolveEffectiveProfileId(requestedId, detectedProfileId, clientKind, width, height, normalizedQuality, out var geometryStatus);
        return new RuntimeVisualProfile
        {
            ProfileId = profileId,
            TrainingProfileId = profileId,
            ProfileFamilyId = BuildProfileFamilyId(profileId, clientKind, width, height, normalizedQuality),
            ProfileGeometryStatus = geometryStatus,
            RequestedProfileId = string.IsNullOrWhiteSpace(requestedProfileId) ? "auto" : NormalizeToken(requestedProfileId),
            DetectedProfileId = detectedProfileId,
            GeometryKey = geometryKey,
            RequestedQualityLabel = normalizedQuality,
            ClientKind = clientKind,
            ProcessName = processName,
            QualityLabel = normalizedQuality,
            ClientWidth = width,
            ClientHeight = height,
            AspectRatio = height <= 0 ? 0 : Math.Round(width / (double)height, 6),
            Dpi = window.Dpi,
            CoordinateScale = Math.Round(window.CoordinateScale, 4),
            CaptureModeRequested = captureModeRequested.ToString().ToLowerInvariant(),
            CaptureModeActive = window.ActiveCaptureMode,
            CaptureFrameBackend = window.ActiveFrameBackend
        };
    }

    public static RuntimeVisualProfile LoadOrLegacy(string scanDirectory)
    {
        var file = Path.Combine(scanDirectory, "visual_profile.json");
        if (File.Exists(file))
        {
            try
            {
                var profile = JsonSerializer.Deserialize<RuntimeVisualProfile>(File.ReadAllText(file), JsonDefaults.Read)
                    ?? Legacy(scanDirectory);
                profile.NormalizeForUse();
                return profile;
            }
            catch
            {
                return Legacy(scanDirectory);
            }
        }

        return Legacy(scanDirectory);
    }

    public void Save(string scanDirectory)
    {
        Directory.CreateDirectory(scanDirectory);
        File.WriteAllText(
            Path.Combine(scanDirectory, "visual_profile.json"),
            JsonSerializer.Serialize(this, JsonDefaults.Write));
    }

    private static RuntimeVisualProfile Legacy(string scanDirectory)
    {
        return new RuntimeVisualProfile
        {
            CreatedAt = File.Exists(Path.Combine(scanDirectory, "ocr_shadow.csv"))
                ? File.GetLastWriteTime(Path.Combine(scanDirectory, "ocr_shadow.csv")).ToString("O", CultureInfo.InvariantCulture)
                : DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            ProfileId = "legacy",
            TrainingProfileId = "legacy",
            ProfileFamilyId = "legacy",
            ProfileGeometryStatus = "legacy",
            RequestedProfileId = "legacy",
            DetectedProfileId = "legacy",
            GeometryKey = "legacy",
            ClientKind = "legacy",
            ProcessName = "",
            QualityLabel = "unknown"
        };
    }

    private static string ResolveClientKind(string processName, VisualProfileClientKind requestedClientKind)
    {
        if (requestedClientKind == VisualProfileClientKind.Local)
        {
            return "local";
        }

        if (requestedClientKind == VisualProfileClientKind.Cloud)
        {
            return "cloud";
        }

        if (requestedClientKind == VisualProfileClientKind.Unknown)
        {
            return "unknown";
        }

        if (processName.Contains("cloud", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("云", StringComparison.OrdinalIgnoreCase))
        {
            return "cloud";
        }

        return "local";
    }

    private static string NormalizeToken(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var normalized = new string(chars);
        while (normalized.Contains("--", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        }

        return normalized.Trim('-');
    }

    private void NormalizeForUse()
    {
        var quality = NormalizeToken(string.IsNullOrWhiteSpace(QualityLabel) ? RequestedQualityLabel : QualityLabel);
        var detected = string.IsNullOrWhiteSpace(DetectedProfileId)
            ? BuildDetectedProfileId(ClientKind, ClientWidth, ClientHeight, quality)
            : NormalizeToken(DetectedProfileId);
        var requested = string.IsNullOrWhiteSpace(RequestedProfileId) || RequestedProfileId.Equals("legacy", StringComparison.OrdinalIgnoreCase)
            ? ProfileId
            : RequestedProfileId;
        var effective = ResolveEffectiveProfileId(
            NormalizeToken(requested),
            detected,
            ClientKind,
            ClientWidth,
            ClientHeight,
            quality,
            out var status);

        ProfileId = effective;
        TrainingProfileId = effective;
        ProfileGeometryStatus = status;
        ProfileFamilyId = BuildProfileFamilyId(effective, ClientKind, ClientWidth, ClientHeight, quality);
    }

    private static string ResolveEffectiveProfileId(
        string requestedId,
        string detectedProfileId,
        string clientKind,
        int width,
        int height,
        string qualityLabel,
        out string status)
    {
        var normalizedRequested = NormalizeToken(string.IsNullOrWhiteSpace(requestedId) ? "auto" : requestedId);
        var normalizedDetected = NormalizeToken(detectedProfileId);
        if (string.IsNullOrWhiteSpace(normalizedDetected))
        {
            normalizedDetected = BuildDetectedProfileId(clientKind, width, height, qualityLabel);
        }

        if (string.IsNullOrWhiteSpace(normalizedRequested)
            || normalizedRequested.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            status = "auto_detected";
            return normalizedDetected;
        }

        if (normalizedRequested.Equals(normalizedDetected, StringComparison.OrdinalIgnoreCase)
            || RequestedProfileMatchesDetectedGeometry(normalizedRequested, clientKind, width, height, qualityLabel))
        {
            status = "requested_matches_detected";
            return normalizedRequested;
        }

        status = $"requested_mismatch_detected_fallback:{normalizedRequested}->{normalizedDetected}";
        return normalizedDetected;
    }

    private static bool RequestedProfileMatchesDetectedGeometry(string requestedId, string clientKind, int width, int height, string qualityLabel)
    {
        var parsed = TryParseProfileId(requestedId);
        if (string.IsNullOrWhiteSpace(parsed.client) || parsed.width <= 0 || parsed.height <= 0)
        {
            return false;
        }

        return parsed.client.Equals(clientKind, StringComparison.OrdinalIgnoreCase)
            && parsed.width == width
            && parsed.height == height
            && (string.IsNullOrWhiteSpace(parsed.quality)
                || parsed.quality.Equals(qualityLabel, StringComparison.OrdinalIgnoreCase));
    }

    private static string BuildDetectedProfileId(string clientKind, int width, int height, string qualityLabel)
    {
        if (string.IsNullOrWhiteSpace(clientKind) || width <= 0 || height <= 0)
        {
            return "legacy";
        }

        return NormalizeToken($"{clientKind}-{width}x{height}-{qualityLabel}");
    }

    private static string BuildProfileFamilyId(string profileId, string clientKind, int width, int height, string qualityLabel)
    {
        var parsed = TryParseProfileId(profileId);
        var effectiveClient = !string.IsNullOrWhiteSpace(parsed.client) ? parsed.client : clientKind;
        var effectiveWidth = parsed.width > 0 ? parsed.width : width;
        var effectiveHeight = parsed.height > 0 ? parsed.height : height;
        var effectiveQuality = !string.IsNullOrWhiteSpace(parsed.quality) ? parsed.quality : qualityLabel;
        var aspectBucket = effectiveWidth <= 0 || effectiveHeight <= 0
            ? "unknown"
            : NormalizeToken(Math.Round(effectiveWidth / (double)effectiveHeight, 2).ToString("F2", CultureInfo.InvariantCulture));
        return NormalizeToken($"{effectiveClient}-{aspectBucket}-dpi-{effectiveQuality}");
    }

    private static (string client, int width, int height, string quality) TryParseProfileId(string profileId)
    {
        var parts = profileId.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return ("", 0, 0, "");
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
            return (parts[0], width, height, quality);
        }

        return ("", 0, 0, "");
    }
}
