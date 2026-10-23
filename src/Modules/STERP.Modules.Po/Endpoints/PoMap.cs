using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Po.Programs;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Po.Endpoints;

/// <summary>
/// PoMap (dotnet/STERP.Business/Purchasing/PoMap.vb:13-40): the facades' conversions of the screen values into COMMAREA
/// values - code fields trimmed and upper-cased, free text trimmed, decimals parsed leniently (0 when invalid), absent
/// dates take the default (the business date).
/// </summary>
internal static class PoMap
{
    /// <summary>PoMap.S: a code field, trimmed and upper-cased, never null.</summary>
    public static string S(JsonObject v, string key) => T(v, key).ToUpperInvariant();

    /// <summary>PoMap.T: a free text field, trimmed, case preserved.</summary>
    public static string T(JsonObject v, string key) => CaJson.Find(v, key) is null ? "" : CaJson.Text(v, key).Trim();

    /// <summary>PoMap.D: Decimal.TryParse of the text without '£' and ',' (0 when blank or invalid).</summary>
    public static decimal D(JsonObject v, string key)
    {
        if (CaJson.Find(v, key) is JsonValue jv && jv.TryGetValue<decimal>(out var d)) return d;
        var s = T(v, key);
        if (s.Length == 0) return 0m;
        return decimal.TryParse(s.Replace("£", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal),
            NumberStyles.Number, CultureInfo.InvariantCulture, out var p) ? p : 0m;
    }

    /// <summary>PoMap.I: CInt(Decimal.Truncate(D)).</summary>
    public static int I(JsonObject v, string key) => (int)decimal.Truncate(D(v, key));

    /// <summary>PoMap.Dt: the date, or <paramref name="dflt"/> (yyyymmdd) when blank or unparseable.</summary>
    public static int Dt(JsonObject v, string key, int dflt)
    {
        var d = CaJson.DateIn(v, key);
        return d == 0 ? dflt : d;
    }

    public static int Business(OperationContext ctx) => CobolDate.ToYyyymmdd(ctx.Session.BusinessDate);

    /// <summary>
    /// The outcome envelope (RC 0/4/6) with the COMMAREA body as data, or the problem (RC 8/12/16); with rule R4
    /// (<paramref name="dataOnProblem"/>) the problem carries the body too (spec 7.1.2).
    /// </summary>
    public static IResult Answer(OperationContext ctx, CaHeader ca, JsonObject data, bool dataOnProblem = false) =>
        ctx.Envelope(ca.Result, ca.ReturnCode is 0 or 4 or 6 || dataOnProblem ? data : null);
}
