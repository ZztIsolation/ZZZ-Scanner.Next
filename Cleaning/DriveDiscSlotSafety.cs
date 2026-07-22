using System.Globalization;
using System.Text.Json;
using ZZZScannerNext.Scanning;

namespace ZZZScannerNext.Cleaning;

public static class DriveDiscSlotSafety
{
    public const string SlotOutOfRange = "slot_out_of_range";
    public const string SlotMainStatViolation = "slot_mainstat_violation";
    public const string SlotFixedValueViolation = "slot_fixed_value_violation";
    public const string MissingMainStat = "missing_main_stat";

    public static IReadOnlyList<DriveDiscSlotSafetyIssue> ValidateAndRepair(DriveDiscExport item, StatRules rules)
    {
        if (item.Slot == 0 && TryInferSlot(item, out var inferredSlot))
        {
            item.Slot = inferredSlot;
        }

        return Validate(item, rules);
    }

    public static IReadOnlyList<DriveDiscSlotSafetyIssue> Validate(DriveDiscExport item, StatRules rules)
    {
        var issues = new List<DriveDiscSlotSafetyIssue>();
        var (mainStat, mainValue) = MainStat(item);
        if (string.IsNullOrWhiteSpace(mainStat))
        {
            issues.Add(new DriveDiscSlotSafetyIssue(MissingMainStat, "主属性缺失。"));
            return issues;
        }

        if (item.Slot < 1 || item.Slot > 6)
        {
            issues.Add(new DriveDiscSlotSafetyIssue(SlotOutOfRange, $"槽位超出范围：{item.Slot}。"));
            return issues;
        }

        if (!rules.SlotMainStats.TryGetValue(item.Slot.ToString(CultureInfo.InvariantCulture), out var allowedStats)
            || !allowedStats.Contains(mainStat, StringComparer.OrdinalIgnoreCase))
        {
            issues.Add(new DriveDiscSlotSafetyIssue(SlotMainStatViolation, $"{item.Slot}号位不允许主属性 {mainStat}。"));
        }

        if (item.Slot is 1 or 2 or 3)
        {
            ValidateFixedSlotValue(item, rules, mainStat, mainValue, issues);
        }

        return issues;
    }

    public static string FormatIssues(IEnumerable<DriveDiscSlotSafetyIssue> issues)
    {
        return string.Join("; ", issues.Select(issue => $"{issue.Code}:{issue.Message}"));
    }

    private static void ValidateFixedSlotValue(
        DriveDiscExport item,
        StatRules rules,
        string mainStat,
        object? mainValue,
        ICollection<DriveDiscSlotSafetyIssue> issues)
    {
        var expectedStat = item.Slot switch
        {
            1 => "生命值",
            2 => "攻击力",
            3 => "防御力",
            _ => ""
        };
        if (!string.Equals(mainStat, expectedStat, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new DriveDiscSlotSafetyIssue(SlotFixedValueViolation, $"{item.Slot}号位固定主属性应为 {expectedStat}，实际为 {mainStat}。"));
            return;
        }

        if (IsPercentValue(mainValue))
        {
            issues.Add(new DriveDiscSlotSafetyIssue(SlotFixedValueViolation, $"{item.Slot}号位固定主属性不应为百分比值：{ValueText(mainValue)}。"));
            return;
        }

        if (item.Level == item.MaxLevel
            && rules.MainStatValues.TryGetValue(item.Rarity, out var rarityValues)
            && rarityValues.TryGetValue(expectedStat, out var range)
            && TryReadNumber(mainValue, out var value)
            && Math.Abs(value - range.Stop) > 0.01)
        {
            issues.Add(new DriveDiscSlotSafetyIssue(SlotFixedValueViolation, $"{item.Slot}号位满级 {expectedStat} 应为 {range.Stop.ToString(CultureInfo.InvariantCulture)}，实际为 {ValueText(mainValue)}。"));
        }
    }

    private static bool TryInferSlot(DriveDiscExport item, out int slot)
    {
        slot = 0;
        var (mainStat, mainValue) = MainStat(item);
        if (string.IsNullOrWhiteSpace(mainStat))
        {
            return false;
        }

        var percent = IsPercentValue(mainValue);
        slot = mainStat switch
        {
            "生命值" when !percent => 1,
            "攻击力" when !percent => 2,
            "防御力" when !percent => 3,
            "暴击率" or "暴击伤害" or "异常精通" => 4,
            "穿透率" or "物理伤害加成" or "火属性伤害加成" or "冰属性伤害加成" or "电属性伤害加成" or "以太伤害加成" or "风属性伤害加成" => 5,
            "能量自动回复" or "异常掌控" or "冲击力" => 6,
            _ => 0
        };

        return slot is >= 1 and <= 6;
    }

    private static (string Stat, object? Value) MainStat(DriveDiscExport item)
    {
        if (item.MainStat.Count == 0)
        {
            return ("", null);
        }

        var pair = item.MainStat.First();
        return (pair.Key, pair.Value);
    }

    private static bool IsPercentValue(object? value)
    {
        return ValueText(value).Contains('%', StringComparison.Ordinal);
    }

    private static bool TryReadNumber(object? value, out double parsed)
    {
        parsed = 0;
        return value switch
        {
            JsonElement { ValueKind: JsonValueKind.Number } element => element.TryGetDouble(out parsed),
            JsonElement { ValueKind: JsonValueKind.String } element => double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed),
            IConvertible convertible => double.TryParse(convertible.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out parsed),
            _ => false
        };
    }

    private static string ValueText(object? value)
    {
        return value switch
        {
            null => "",
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
            JsonElement { ValueKind: JsonValueKind.Number } element => element.GetRawText(),
            JsonElement element => element.ToString(),
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };
    }
}

public sealed record DriveDiscSlotSafetyIssue(string Code, string Message);
