using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Po.Programs;

/// <summary>JSON conversions of COMMAREA fields (contracts 4.2-4.3): PIC 9(8) dates as ISO dates (0 = null).</summary>
internal static class CaJson
{
    /// <summary>A PIC 9(8) yyyymmdd field as an ISO date; 0 or an invalid date = null.</summary>
    public static string? Date(int yyyymmdd) =>
        CobolDate.FromYyyymmdd(yyyymmdd)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The low-order <paramref name="digits"/> digits of an integer field (absolute value when unsigned).</summary>
    public static long Digits(long value, int digits, bool unsigned = false)
    {
        var limit = 1L;
        for (var i = 0; i < digits && i < 18; i++) limit *= 10;
        var v = value % limit;
        return unsigned ? Math.Abs(v) : v;
    }

    public static string Text(JsonObject j, string name) => Find(j, name) switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        var n => n.ToJsonString(),
    };

    public static decimal Dec(JsonObject j, string name)
    {
        var n = Find(j, name);
        if (n is JsonValue v)
        {
            if (v.TryGetValue<decimal>(out var d)) return d;
            if (v.TryGetValue<string>(out var s) &&
                decimal.TryParse(s.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var p)) return p;
        }
        return 0m;
    }

    /// <summary>An ISO (or yyyymmdd) date into PIC 9(8); absent/null = 0.</summary>
    public static int DateIn(JsonObject j, string name)
    {
        var t = Text(j, name).Trim();
        if (t.Length == 0) return 0;
        if (DateOnly.TryParseExact(t, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return CobolDate.ToYyyymmdd(d);
        if (t.Length == 8 && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return n;
        return DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? CobolDate.ToYyyymmdd(dt) : 0;
    }

    public static JsonNode? Find(JsonObject j, string name)
    {
        if (j.TryGetPropertyValue(name, out var n)) return n;
        foreach (var (k, v) in j)
        {
            if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return v;
        }
        return null;
    }
}
