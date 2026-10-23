using System.Globalization;
using System.Text.Json.Nodes;

namespace Sterp.Modules.Itm.Online;

/// <summary>Lenient reads of request JSON (the legacy facades took text and parsed it).</summary>
internal static class Json
{
    /// <summary>The property as text (strings as given, numbers/booleans as their JSON text); null when absent.</summary>
    public static string? Text(JsonObject body, string name)
    {
        var node = Find(body, name);
        return node switch
        {
            null => null,
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
            _ => node.ToJsonString(),
        };
    }

    /// <summary>StockService.S: the value trimmed, "" when absent (StockService.vb:150-153).</summary>
    public static string S(JsonObject body, string name) => (Text(body, name) ?? "").Trim();

    /// <summary>StockService.D: Decimal.TryParse of the trimmed text, 0 when unparseable (StockService.vb:155-159).</summary>
    public static decimal D(JsonObject body, string name)
    {
        var node = Find(body, name);
        if (node is JsonValue v && v.TryGetValue<decimal>(out var d)) return d;
        return decimal.TryParse(S(body, name), NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : 0m;
    }

    /// <summary>An ISO date (yyyy-MM-dd) or null.</summary>
    public static DateOnly? Date(JsonObject body, string name)
    {
        var t = S(body, name);
        if (t.Length == 0) return null;
        if (DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        return DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? DateOnly.FromDateTime(dt) : null;
    }

    private static JsonNode? Find(JsonObject body, string name)
    {
        if (body.TryGetPropertyValue(name, out var n)) return n;
        foreach (var (k, v) in body)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v;
        }
        return null;
    }
}
