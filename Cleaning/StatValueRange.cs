using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ZZZScannerNext.Cleaning;

public sealed class StatValueRange : IEnumerable<string>
{
    private string[]? _all;

    public float Start { get; }
    public float Step { get; }
    public float Stop { get; }
    public bool IsPercent { get; }

    public string[] All => _all ??= this.ToArray();

    public StatValueRange(float start, float step, float stop, bool isPercent)
    {
        Start = start;
        Step = step;
        Stop = stop;
        IsPercent = isPercent;
    }

    public IEnumerator<string> GetEnumerator()
    {
        if (Step == 0)
        {
            yield break;
        }

        var maxLevel = (int)Math.Round((Stop - Start) / Step);
        for (var i = 0; i <= maxLevel; i++)
        {
            var value = Start + i * Step;
            if (IsPercent)
            {
                yield return FormatPercent(value);
            }
            else
            {
                yield return ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public object ToExportValue(string text)
    {
        if (IsPercent)
        {
            return text;
        }

        return float.Parse(text, CultureInfo.InvariantCulture);
    }

    private static string FormatPercent(float value)
    {
        var rounded = Math.Round(value, 1);
        return rounded % 1 == 0
            ? $"{(int)rounded}%"
            : $"{rounded:F1}%";
    }
}

public sealed class StatValueRangeJsonConverter : JsonConverter<StatValueRange>
{
    public override StatValueRange Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Stat value ranges must be arrays.");
        }

        reader.Read();
        if (reader.TokenType == JsonTokenType.Number)
        {
            var start = reader.GetSingle();
            reader.Read();
            var step = reader.GetSingle();
            reader.Read();
            var stop = reader.GetSingle();
            reader.Read();
            return new StatValueRange(start, step, stop, false);
        }

        var s1 = reader.GetString() ?? throw new JsonException("Missing range start.");
        reader.Read();
        var s2 = reader.GetString() ?? throw new JsonException("Missing range step.");
        reader.Read();
        var s3 = reader.GetString() ?? throw new JsonException("Missing range stop.");
        reader.Read();
        return new StatValueRange(ToFloat(s1), ToFloat(s2), ToFloat(s3), true);
    }

    public override void Write(Utf8JsonWriter writer, StatValueRange value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        if (value.IsPercent)
        {
            writer.WriteStringValue($"{value.Start.ToString(CultureInfo.InvariantCulture)}%");
            writer.WriteStringValue($"{value.Step.ToString(CultureInfo.InvariantCulture)}%");
            writer.WriteStringValue($"{value.Stop.ToString(CultureInfo.InvariantCulture)}%");
        }
        else
        {
            writer.WriteNumberValue(value.Start);
            writer.WriteNumberValue(value.Step);
            writer.WriteNumberValue(value.Stop);
        }

        writer.WriteEndArray();
    }

    private static float ToFloat(string text)
    {
        return float.Parse(text.Trim().TrimEnd('%'), CultureInfo.InvariantCulture);
    }
}
