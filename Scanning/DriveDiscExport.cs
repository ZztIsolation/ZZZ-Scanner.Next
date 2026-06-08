using System.Text.Json.Serialization;

namespace ZZZScannerNext.Scanning;

public sealed class DriveDiscExport
{
    [JsonPropertyName("序号")]
    public int Index { get; set; }

    [JsonPropertyName("名称")]
    public string Name { get; set; } = "";

    [JsonPropertyName("槽位")]
    public int Slot { get; set; }

    [JsonPropertyName("品质")]
    public string Rarity { get; set; } = "";

    [JsonPropertyName("等级")]
    public int Level { get; set; }

    [JsonPropertyName("最大等级")]
    public int MaxLevel { get; set; }

    [JsonPropertyName("主属性")]
    public Dictionary<string, object> MainStat { get; set; } = new();

    [JsonPropertyName("副属性")]
    public List<Dictionary<string, object>> SubStats { get; set; } = new();

    [JsonIgnore]
    public string RawOcr { get; set; } = "";
}
