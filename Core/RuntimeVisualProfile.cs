using System.Globalization;
using System.Text.Json;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Core;

public sealed class RuntimeVisualProfile
{
    public string SchemaVersion { get; set; } = "2";
    public string CreatedAt { get; set; } = DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
    public string ProfileId { get; set; } = "";
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
        return new RuntimeVisualProfile
        {
            ProfileId = requestedId.Length == 0 || string.Equals(requestedId, "auto", StringComparison.OrdinalIgnoreCase)
                ? detectedProfileId
                : requestedId,
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
                return JsonSerializer.Deserialize<RuntimeVisualProfile>(File.ReadAllText(file), JsonDefaults.Read)
                    ?? Legacy(scanDirectory);
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
}
