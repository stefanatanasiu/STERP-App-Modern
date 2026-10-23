using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Kit.Endpoints;

/// <summary>
/// Typed reads of request bodies and query strings (a value of the wrong type is 400 API0004, contracts 4.11) and the
/// contracts 4.4 normalization of COMMAREA inputs (Windows-1252, right truncation to the PIC length, numbers rounded half
/// away from zero to the field scale with high-order truncation).
/// </summary>
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

    public static decimal Decimal(JsonObject body, string name)
    {
        var node = Find(body, name);
        if (node is null) return 0m;
        if (node is JsonValue v)
        {
            if (v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<decimal>(out var d)) return d;
            if (v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var ds)) return ds;
        }
        throw ApiProblemException.BadBody($"Property {name} must be a number.");
    }

    public static int Int(JsonObject body, string name)
    {
        var node = Find(body, name);
        if (node is null) return 0;
        if (node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && v.TryGetValue<decimal>(out var d) && d == decimal.Truncate(d)
            && d is >= int.MinValue and <= int.MaxValue)
        {
            return (int)d;
        }
        throw ApiProblemException.BadBody($"Property {name} must be an integer.");
    }

    /// <summary>A Y/N flag property: a JSON boolean, or a string whose first character Y/y means yes.</summary>
    public static bool Flag(JsonObject body, string name)
    {
        var node = Find(body, name);
        return node switch
        {
            null => false,
            JsonValue v when v.TryGetValue<bool>(out var b) => b,
            JsonValue v when v.TryGetValue<string>(out var s) => s.Trim().StartsWith('Y') || s.Trim().StartsWith('y'),
            _ => throw ApiProblemException.BadBody($"Property {name} must be a boolean or Y/N."),
        };
    }

    public static DateOnly? Date(JsonObject body, string name)
    {
        var node = Find(body, name);
        if (node is null) return null;
        if (node is JsonValue v && v.TryGetValue<string>(out var s))
        {
            if (s.Length == 0) return null;
            if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        }
        throw ApiProblemException.BadBody($"Property {name} must be a date (yyyy-MM-dd).");
    }

    public static bool Has(JsonObject body, string name) => Find(body, name) is not null;

    private static JsonNode? Find(JsonObject body, string name)
    {
        foreach (var (k, v) in body)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v;
        }
        return null;
    }

    /// <summary>Contracts 4.4 for an alphanumeric COMMAREA field PIC X(n): the value as the field holds it, right-trimmed.</summary>
    public static string X(string? value, int n) => CobolText.Fit(value, n).TrimEnd(' ');

    /// <summary>Contracts 4.4 for a numeric COMMAREA field.</summary>
    public static decimal Num(decimal value, int totalDigits, int scale, bool signed = true) =>
        CobolNumeric.Normalize(value, totalDigits, scale, signed);
}

/// <summary>
/// SqlHelper.Params (dotnet/STERP.Data/SqlHelper.cs:21-37) for the procedure-backed operations: an empty string or null
/// becomes SQL NULL, dates are sent as date values, everything else as is.
/// </summary>
internal static class SqlHelperArgs
{
    public static DynamicParameters Of(params (string Name, object? Value)[] values)
    {
        var d = new DynamicParameters();
        foreach (var (name, value) in values)
        {
            var v = value switch
            {
                null => null,
                string s when s.Length == 0 => null,
                DateOnly date => date.ToDateTime(TimeOnly.MinValue),
                _ => value,
            };
            d.Add(name, v);
        }
        return d;
    }
}
