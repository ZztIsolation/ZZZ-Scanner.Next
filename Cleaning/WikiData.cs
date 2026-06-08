using System.Text.Json;
using ZZZScannerNext.Core;

namespace ZZZScannerNext.Cleaning;

public sealed class WikiData
{
    public DiscCatalog DiscCatalog { get; }
    public StatRules StatRules { get; }

    private WikiData(DiscCatalog discCatalog, StatRules statRules)
    {
        DiscCatalog = discCatalog;
        StatRules = statRules;
    }

    public static WikiData Load()
    {
        var catalog = JsonSerializer.Deserialize<DiscCatalog>(
            File.ReadAllText(AppPaths.DataFile("drive_discs.json")), JsonDefaults.Read)
            ?? throw new InvalidDataException("Cannot load drive_discs.json.");
        var rules = JsonSerializer.Deserialize<StatRules>(
            File.ReadAllText(AppPaths.DataFile("stat_rules.json")), JsonDefaults.Read)
            ?? throw new InvalidDataException("Cannot load stat_rules.json.");

        return new WikiData(catalog, rules);
    }

    public IReadOnlyList<string> NameCandidates()
    {
        return DiscCatalog.Sets.Select(s => s.Name)
            .Concat(DiscCatalog.ExtraNameCandidates)
            .Distinct()
            .ToArray();
    }
}

public sealed class DiscCatalog
{
    public string Version { get; set; } = "";
    public DiscCatalogSource Source { get; set; } = new();
    public List<DiscSet> Sets { get; set; } = new();
    public List<string> ExtraNameCandidates { get; set; } = new();
}

public sealed class DiscCatalogSource
{
    public List<string> PrimaryPages { get; set; } = new();
    public string Notes { get; set; } = "";
}

public sealed class DiscSet
{
    public string Name { get; set; } = "";
    public int WikiListOrder { get; set; }
    public string SourcePage { get; set; } = "";
    public string MinRarity { get; set; } = "";
    public string MaxRarity { get; set; } = "";
}

public sealed class StatRules
{
    public string Version { get; set; } = "";
    public StatRuleSource Source { get; set; } = new();
    public Dictionary<string, List<string>> SlotMainStats { get; set; } = new();
    public Dictionary<string, string> MainStatAliases { get; set; } = new();
    public List<string> SubStats { get; set; } = new();
    public Dictionary<string, string> SubStatAliases { get; set; } = new();
    public Dictionary<string, Dictionary<string, StatValueRange>> MainStatValues { get; set; } = new();
    public Dictionary<string, Dictionary<string, StatValueRange>> SubStatValues { get; set; } = new();
}

public sealed class StatRuleSource
{
    public string Page { get; set; } = "";
    public string Notes { get; set; } = "";
}
