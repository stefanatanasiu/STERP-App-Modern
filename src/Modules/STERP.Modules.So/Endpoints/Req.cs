using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Platform.Kernel;

namespace Sterp.Modules.So.Endpoints;

/// <summary>Lenient reads of request JSON values (the legacy facade members took typed .NET arguments).</summary>
internal static class Req
{
    public static JsonNode? Find(JsonObject body, string name)
    {
        if (body.TryGetPropertyValue(name, out var n)) return n;
        foreach (var (k, v) in body)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v;
        }
        return null;
    }

    /// <summary>Text as given ("" when absent or null); numbers and booleans as their JSON text.</summary>
    public static string S(JsonObject body, string name) => Find(body, name) switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        var n => n.ToJsonString(),
    };

    public static bool Has(JsonObject body, string name) => Find(body, name) is not null;

    public static decimal D(JsonObject body, string name)
    {
        var node = Find(body, name);
        if (node is null) return 0m;
        if (node is JsonValue v && v.TryGetValue<decimal>(out var d)) return d;
        var t = S(body, name).Trim();
        if (t.Length == 0) return 0m;
        return decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var p)
            ? p
            : throw ApiProblemException.BadBody($"{name} must be a number.");
    }

    public static int I(JsonObject body, string name)
    {
        var node = Find(body, name);
        if (node is null) return 0;
        if (node is JsonValue v && v.TryGetValue<int>(out var i)) return i;
        var t = S(body, name).Trim();
        if (t.Length == 0) return 0;
        return int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out var p)
            ? p
            : throw ApiProblemException.BadBody($"{name} must be an integer.");
    }

    /// <summary>An ISO date (yyyy-MM-dd) or null when absent/empty.</summary>
    public static DateOnly? Date(JsonObject body, string name)
    {
        var t = S(body, name).Trim();
        if (t.Length == 0 || t == "null") return null;
        if (DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        if (DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return DateOnly.FromDateTime(dt);
        throw ApiProblemException.BadBody($"{name} must be a date (yyyy-MM-dd).");
    }

    public static JsonArray? Array(JsonObject body, string name) => Find(body, name) as JsonArray;
}
