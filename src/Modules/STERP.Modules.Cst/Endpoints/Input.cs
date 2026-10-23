using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Cst.Endpoints;

/// <summary>
/// Typed reads of request bodies (property names case-insensitive); a value of the wrong JSON type is 400 API0004. COMMAREA
/// fields are then normalized as the legacy CobolCodec marshalled them (contracts 4.4).
/// </summary>
internal static class Input
{
    public static JsonNode? Find(JsonObject body, string name)
    {
        foreach (var (k, v) in body)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v;
        }
        return null;
    }

    public static bool Has(JsonObject body, string name) => Find(body, name) is not null;

    /// <summary>A string property ("" when absent or null).</summary>
    public static string Text(JsonObject body, string name) => Find(body, name) switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.GetValueKind() is JsonValueKind.Number => v.ToJsonString(),
        _ => throw ApiProblemException.BadBody($"Property {name} must be a string."),
    };

    /// <summary>A number property (null when absent or null).</summary>
    public static decimal? Number(JsonObject body, string name)
    {
        var node = Find(body, name);
        if (node is null) return null;
        if (node is JsonValue v)
        {
            if (v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<decimal>(out var d)) return d;
            if (v.TryGetValue<string>(out var s) && decimal.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ds)) return ds;
        }
        throw ApiProblemException.BadBody($"Property {name} must be a number.");
    }

    /// <summary>CobolCodec numeric marshalling: round half away from zero to the scale, low-order digits, sign rule.</summary>
    public static decimal Commarea(decimal value, int totalDigits, int scale, bool signed = true) =>
        CobolNumeric.Normalize(value, totalDigits, scale, signed);

    /// <summary>CobolCodec alphanumeric marshalling into PIC X(n), carried right-trimmed.</summary>
    public static string Commarea(string? value, int length) => CobolText.Fit(value, length).TrimEnd(' ');

    public static int Yyyymmdd(DateOnly d) => d.Year * 10000 + d.Month * 100 + d.Day;
}
