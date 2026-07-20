using System.Drawing;
using System.Text.Json;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Scanning;

public sealed class ScanProfileFile
{
    public string Version { get; set; } = "";
    public List<ScanProfile> Profiles { get; set; } = new();

    public static ScanProfileFile Load()
    {
        return JsonSerializer.Deserialize<ScanProfileFile>(
            File.ReadAllText(AppPaths.DataFile("scan_profiles.json")), JsonDefaults.Read)
            ?? throw new InvalidDataException("Cannot load scan_profiles.json.");
    }

    public ScanProfile? Find(string profileName)
    {
        return Profiles.FirstOrDefault(
            profile => string.Equals(profile.Name, profileName, StringComparison.OrdinalIgnoreCase));
    }

    public ScanProfile ResolveRequired(string profileName)
    {
        if (Profiles.Count == 0)
        {
            throw new InvalidDataException("scan_profiles.json does not contain any profiles.");
        }

        return Find(profileName)
            ?? throw new ArgumentException(
                $"Unknown scan profile: {profileName}. Available profiles: {string.Join(", ", Profiles.Select(profile => profile.Name))}.",
                nameof(profileName));
    }
}

public sealed class ScanProfile
{
    public string Name { get; set; } = "";
    public string TraversalMode { get; set; } = "SafeBandViewport";
    public int[] StandardScreen { get; set; } = [1920, 1080];
    public int VisibleRows { get; set; } = 4;
    public int VisibleColumns { get; set; } = 9;
    public int WaitForBackpackSeconds { get; set; } = 10;
    public int ClickDelayMs { get; set; } = 90;
    public int LoadTimeoutMs { get; set; } = 1200;
    public int LoadPollMs { get; set; } = 50;
    public int PanelSettleDelayMs { get; set; } = 90;
    public int MinPanelSettleDelayMs { get; set; } = 40;
    public int PanelChangedMinimumAcceptMs { get; set; } = 120;
    public int PanelUnchangedFallbackMs { get; set; } = 180;
    public int MinPanelUnchangedFallbackMs { get; set; } = 80;
    public int ListStableTimeoutMs { get; set; } = 500;
    public int MinListStableTimeoutMs { get; set; } = 180;
    public int MinScrollTickDelayMs { get; set; } = 40;
    public int ListStableConfirmFrames { get; set; } = 1;
    public int WheelDelayMs { get; set; } = 420;
    public int ScrollWheelDelta { get; set; } = -120;
    public int ScrollTickDelta { get; set; } = -120;
    public int ScrollTickDelayMs { get; set; } = 80;
    public bool AllowScrollRecovery { get; set; } = false;
    public int ScrollMaxTicksPerRow { get; set; } = 25;
    public int CalibrationRows { get; set; } = 5;
    public int DuplicateRowThreshold { get; set; } = 9;
    public int ResetToTopWheelTicks { get; set; } = 45;
    public int ResetToTopWheelDelayMs { get; set; } = 6;
    public int ColorTolerance { get; set; } = 26;
    public VisualProbeOptions VisualProbes { get; set; } = new();
    public Dictionary<string, int[]> Points { get; set; } = new();
    public Dictionary<string, int[]> Colors { get; set; } = new();
    public Dictionary<string, int[]> Rectangles { get; set; } = new();

    public PointF Point(string key)
    {
        var point = Points[key];
        return new PointF(point[0] / (float)StandardScreen[0], point[1] / (float)StandardScreen[1]);
    }

    public RectangleF Rectangle(string key)
    {
        var rect = Rectangles[key];
        var x = rect[0] / (float)StandardScreen[0];
        var y = rect[1] / (float)StandardScreen[1];
        var width = (rect[2] - rect[0]) / (float)StandardScreen[0];
        var height = (rect[3] - rect[1]) / (float)StandardScreen[1];
        return new RectangleF(x, y, width, height);
    }

    public bool HasRectangle(string key)
    {
        return Rectangles.ContainsKey(key);
    }

    public Color Color(string key)
    {
        return ColorMath.FromArgbArray(Colors[key]);
    }

    public string? MatchRarity(Color color)
    {
        if (color.IsCloseTo(Color("rarityS"), ColorTolerance))
        {
            return "S";
        }

        if (color.IsCloseTo(Color("rarityA"), ColorTolerance))
        {
            return "A";
        }

        if (color.IsCloseTo(Color("rarityB"), ColorTolerance))
        {
            return "B";
        }

        return null;
    }

    public IReadOnlyList<string> OrderedRoiKeys()
    {
        return
        [
            "name",
            "level",
            "mainStat",
            "mainStatValue",
            "subStat1",
            "subStatValue1",
            "subStat2",
            "subStatValue2",
            "subStat3",
            "subStatValue3",
            "subStat4",
            "subStatValue4"
        ];
    }
}

public sealed class VisualProbeOptions
{
    public int RequiredSignals { get; set; } = 2;
    public int RequiredStableFrames { get; set; } = 2;
    public int PollMilliseconds { get; set; } = 250;
    public ChromaticProbePolicy BackpackReady { get; set; } = new();
    public RarityProbePolicy Rarity { get; set; } = new();
    public RowPresenceProbePolicy RowPresence { get; set; } = new();
}

public sealed class ChromaticProbePolicy
{
    public int Radius { get; set; } = 24;
    public int HueToleranceDegrees { get; set; } = 35;
    public double MinimumSaturation { get; set; } = 0.45;
    public double MinimumValue { get; set; } = 0.30;
    public double MinimumCoverage { get; set; } = 0.12;
}

public sealed class RarityProbePolicy
{
    public int MaximumScore { get; set; } = 42;
    public int MinimumCandidateMargin { get; set; } = 8;
}

public sealed class RowPresenceProbePolicy
{
    public int PatchRadius { get; set; } = 3;
    public int MinimumLuminanceTolerance { get; set; } = 18;
    public double RelativeLuminanceTolerance { get; set; } = 0.35;
    public int EdgeThreshold { get; set; } = 18;
    public double MinimumEdgeDensity { get; set; } = 0.003;
}
