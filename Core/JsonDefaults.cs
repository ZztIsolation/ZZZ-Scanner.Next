using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZZZScannerNext.Cleaning;

namespace ZZZScannerNext.Core;

public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Read = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = true,
        Converters = { new StatValueRangeJsonConverter() }
    };

    public static readonly JsonSerializerOptions Write = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static readonly JsonSerializerOptions Wire = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
