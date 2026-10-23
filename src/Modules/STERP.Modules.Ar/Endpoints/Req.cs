using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Ar.Endpoints;

/// <summary>Typed reads of a JSON request body with the binding schema types (a type mismatch is 400 API0004).</summary>
public static class Req
{
    private static JsonNode? Node(JsonObject body, string name) =>
        body.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static ApiProblemException Bad(string name, string type) =>
        ApiProblemException.BadBody($"Property {name} must be {type}.");

    /// <summary>A string property; absent or null -> "".</summary>
    public static string Str(JsonObject body, string name) => Node(body, name) switch
    {
        null => "",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        _ => throw Bad(name, "a string"),
    };

    /// <summary>A number property; absent or null -> 0.</summary>
    public static decimal Dec(JsonObject body, string name)
    {
        var n = Node(body, name);
        if (n is null) return 0m;
        if (n is JsonValue v && v.GetValueKind() == JsonValueKind.Number)
        {
            try
            {
                return v.GetValue<decimal>();
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException)
            {
                throw Bad(name, "a decimal number");
            }
        }
        throw Bad(name, "a number");
    }

    /// <summary>An integer property; absent or null -> 0.</summary>
    public static int Int(JsonObject body, string name)
    {
        var n = Node(body, name);
        if (n is null) return 0;
        if (n is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<int>(out var i)) return i;
        throw Bad(name, "an integer");
    }

    /// <summary>A date property (yyyy-MM-dd) as PIC 9(8) yyyymmdd; absent or null -> 0.</summary>
    public static int Date(JsonObject body, string name)
    {
        var n = Node(body, name);
        if (n is null) return 0;
        if (n is JsonValue v && v.GetValueKind() == JsonValueKind.String &&
            DateOnly.TryParseExact(v.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            return CobolDate.ToYyyymmdd(d);
        }
        throw Bad(name, "a date (yyyy-MM-dd)");
    }

    /// <summary>An array of objects; absent or null -> empty.</summary>
    public static IReadOnlyList<JsonObject> Objects(JsonObject body, string name) => Node(body, name) switch
    {
        null => [],
        JsonArray a => a.Select(x => x as JsonObject ?? throw Bad(name, "an array of objects")).ToList(),
        _ => throw Bad(name, "an array"),
    };
}
