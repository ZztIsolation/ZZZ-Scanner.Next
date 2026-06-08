using ZZZScannerNext.Ocr;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Cleaning;

public sealed class DriveDiscCleaner
{
    private readonly WikiData _wikiData;
    private readonly List<string> _levelCandidates = new();

    public DriveDiscCleaner(WikiData wikiData)
    {
        _wikiData = wikiData;
        for (var max = 9; max <= 15; max += 3)
        {
            for (var level = 0; level <= max; level++)
            {
                _levelCandidates.Add($"{level:D2}/{max:D2}");
            }
        }
    }

    public DriveDiscExport Clean(int index, string rarity, IReadOnlyList<OcrResult> result)
    {
        if (result.Count < 4)
        {
            throw new InvalidDataException($"OCR结果不足：{result.Count}/4。");
        }

        var export = new DriveDiscExport
        {
            Index = index,
            Rarity = rarity,
            RawOcr = string.Join(" | ", result.Select(r => r.Text))
        };

        for (var i = 0; i < result.Count; i++)
        {
            var text = result[i].Text;
            switch (i)
            {
                case 0:
                    (export.Name, export.Slot) = CleanName(text);
                    break;
                case 1:
                    (export.Level, export.MaxLevel) = CleanLevel(text);
                    break;
                case 2 when i + 1 < result.Count:
                {
                    if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(result[i + 1].Text))
                    {
                        throw new InvalidDataException($"主属性OCR结果为空：{export.RawOcr}");
                    }

                    var key = CleanMainStat(text, export.Slot);
                    var value = CleanStatValue(result[i + 1].Text, key, rarity, true, export.Slot);
                    export.MainStat[key] = value;
                    i++;
                    break;
                }
                case 4:
                case 6:
                case 8:
                case 10:
                    if (i + 1 >= result.Count)
                    {
                        break;
                    }

                    if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(result[i + 1].Text))
                    {
                        break;
                    }

                    if (IsSetEffectText(text) || IsSetEffectText(result[i + 1].Text))
                    {
                        return export;
                    }

                    var subKey = CleanSubStat(text);
                    var subValue = CleanStatValue(result[i + 1].Text, subKey, rarity, false, export.Slot);
                    export.SubStats.Add(new Dictionary<string, object> { [subKey] = subValue });
                    i++;
                    break;
            }
        }

        return export;
    }

    private (string Name, int Slot) CleanName(string name)
    {
        var slot = name.FirstOrDefault(c => char.IsDigit(c) && "123456".Contains(c));
        var simplified = StringMatcher.SimplifyChinese(name, keepChineseAndDigits: true);
        var textOnly = string.Concat(simplified.Where(c => !char.IsDigit(c)));
        var match = StringMatcher.BestMatch(_wikiData.NameCandidates(), textOnly);
        return (match.Text, slot == default ? 0 : slot - '0');
    }

    private (int Level, int MaxLevel) CleanLevel(string level)
    {
        var raw = level;
        level = StringMatcher.NumericToken(level);
        if (TryParseLevel(level, out var parsed))
        {
            return parsed;
        }

        if (level.Length == 6 && level[3] == '/' && TryParseLevel(level[1..], out parsed))
        {
            return parsed;
        }

        level = StringMatcher.BestMatch(_levelCandidates, level, 0.3f).Text;
        if (TryParseLevel(level, out parsed))
        {
            return parsed;
        }

        throw new InvalidDataException($"等级识别失败：{raw}");
    }

    private static bool TryParseLevel(string level, out (int Level, int MaxLevel) result)
    {
        result = default;
        if (level.Length != 5 || level[2] != '/')
        {
            return false;
        }

        var parts = level.Split('/', 2);
        if (parts.Length != 2 || !int.TryParse(parts[0], out var current) || !int.TryParse(parts[1], out var max))
        {
            return false;
        }

        result = (current, max);
        return true;
    }

    private string CleanMainStat(string stat, int slot)
    {
        stat = NormalizeStatName(stat, _wikiData.StatRules.MainStatAliases);
        var candidates = _wikiData.StatRules.SlotMainStats.TryGetValue(slot.ToString(), out var slotStats)
            ? slotStats
            : _wikiData.StatRules.SlotMainStats.Values.SelectMany(x => x).Distinct().ToList();

        var match = StringMatcher.BestMatch(candidates, stat);
        return match.Text;
    }

    private string CleanSubStat(string stat)
    {
        stat = NormalizeStatName(stat, _wikiData.StatRules.SubStatAliases);
        return StringMatcher.BestMatch(_wikiData.StatRules.SubStats, stat).Text;
    }

    private object CleanStatValue(string value, string stat, string rarity, bool mainStat, int slot)
    {
        var source = mainStat
            ? _wikiData.StatRules.MainStatValues[rarity]
            : _wikiData.StatRules.SubStatValues[rarity];

        var token = StringMatcher.NumericToken(value);
        var keys = CandidateValueKeys(source, stat, token, mainStat, slot);
        var bestKey = "";
        var bestText = token;
        var bestScore = float.MinValue;
        StatValueRange? bestRange = null;

        foreach (var key in keys)
        {
            if (!source.TryGetValue(key, out var range))
            {
                continue;
            }

            var comparable = range.IsPercent && !token.Contains('%') ? $"{token}%" : token;
            var match = StringMatcher.BestMatch(range.All, comparable, 0.2f);
            if (match.Score > bestScore)
            {
                bestScore = match.Score;
                bestText = match.Text;
                bestRange = range;
                bestKey = key;
            }
        }

        if (bestRange is null)
        {
            throw new InvalidDataException($"No value rule for {rarity} {stat} ({value}).");
        }

        return bestRange.ToExportValue(bestText);
    }

    private static string NormalizeStatName(string stat, IReadOnlyDictionary<string, string> aliases)
    {
        stat = StringMatcher.SimplifyChinese(stat, keepChineseAndDigits: true);
        stat = stat.Replace("百分比", string.Empty, StringComparison.Ordinal);
        return aliases.TryGetValue(stat, out var alias) ? alias : stat;
    }

    private static bool IsSetEffectText(string text)
    {
        var simplified = StringMatcher.SimplifyChinese(text, keepChineseAndDigits: true);
        return simplified.Contains("套装", StringComparison.Ordinal)
            || simplified.Contains("效果", StringComparison.Ordinal);
    }

    private static IReadOnlyList<string> CandidateValueKeys(
        IReadOnlyDictionary<string, StatValueRange> source,
        string stat,
        string token,
        bool mainStat,
        int slot)
    {
        var keys = new List<string>();
        var percentKey = $"{stat}%";
        var forceMainPercent = mainStat && slot >= 4 && stat is "生命值" or "攻击力" or "防御力";

        if (forceMainPercent || token.Contains('%') || !source.ContainsKey(stat))
        {
            keys.Add(percentKey);
        }

        keys.Add(stat);

        if (!keys.Contains(percentKey))
        {
            keys.Add(percentKey);
        }

        return keys.Distinct().ToArray();
    }
}
