using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sterp.Platform.Kernel;

namespace Sterp.Modules.Bom.Endpoints;

/// <summary>Typed reads of request bodies and query strings; a value of the wrong type is 400 API0004 (contracts 4.11).</summary>
internal static class Input
{
    public static string Text(JsonObject body, string name)
    {
        var node = Find(body, name);
        return node switch
        {
            null => "",
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            JsonValue v when v.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.ToJsonString(),
            _ => throw ApiProblemException.BadBody($"Property {name} must be a string."),
        };
    }

    public static string? NullableText(JsonObject body, string name) => Find(body, name) is null ? null : Text(body, name);

    public static decimal Decimal(JsonObject body, string name, bool required)
    {
        var node = Find(body, name);
        if (node is null)
        {
            if (required) throw ApiProblemException.BadBody($"Property {name} is required.");
            return 0m;
        }
        if (node is JsonValue v)
        {
            if (v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<decimal>(out var d)) return d;
            if (v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var ds)) return ds;
        }
        throw ApiProblemException.BadBody($"Property {name} must be a number.");
    }

    public static int Int(JsonObject body, string name, bool required, int fallback = 0)
    {
        var node = Find(body, name);
        if (node is null)
        {
            if (required) throw ApiProblemException.BadBody($"Property {name} is required.");
            return fallback;
        }
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<decimal>(out var d) && d == decimal.Truncate(d)
            && d is >= int.MinValue and <= int.MaxValue)
        {
            return (int)d;
        }
        throw ApiProblemException.BadBody($"Property {name} must be an integer.");
    }

    public static bool Bool(JsonObject body, string name, bool required)
    {
        var node = Find(body, name);
        if (node is null)
        {
            if (required) throw ApiProblemException.BadBody($"Property {name} is required.");
            return false;
        }
        if (node is JsonValue v && v.TryGetValue<bool>(out var b)) return b;
        throw ApiProblemException.BadBody($"Property {name} must be a boolean.");
    }

    public static DateOnly? Date(JsonObject body, string name, bool required)
    {
        var node = Find(body, name);
        if (node is null)
        {
            if (required) throw ApiProblemException.BadBody($"Property {name} is required.");
            return null;
        }
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            if (s.Length == 0 && !required) return null;
            if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        }
        throw ApiProblemException.BadBody($"Property {name} must be a date (yyyy-MM-dd).");
    }

    private static JsonNode? Find(JsonObject body, string name)
    {
        foreach (var (k, v) in body)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v;
        }
        return null;
    }

    public static int Yyyymmdd(DateOnly d) => d.Year * 10000 + d.Month * 100 + d.Day;
}
