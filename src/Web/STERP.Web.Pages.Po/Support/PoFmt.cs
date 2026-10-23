using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Web.Framework.Format;
using Sterp.Web.Framework.Session;

namespace Sterp.Web.Pages.Po.Support;

/// <summary>
/// The legacy presentation and conversion helpers the purchasing forms use (dotnet/STERP.Common/Fmt.cs and
/// dotnet/STERP.Business/Purchasing/PoMap.vb), reproduced for the browser runtime: the client runs with invariant
/// globalization, so the en-GB rules (group ',', decimal '.', '£', dd/MM/yyyy) are spelled out.
/// </summary>
public static class PoFmt
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    // ------------------------------------------------------------------------------------- PoMap (screen values)

    /// <summary>PoMap.T: a free text field, trimmed, case preserved, never null.</summary>
    public static string T(string? text) => (text ?? "").Trim();

    /// <summary>PoMap.S: a code field, trimmed and upper-cased.</summary>
    public static string S(string? text) => T(text).ToUpperInvariant();

    /// <summary>PoMap.D: Decimal.TryParse (NumberStyles.Number, en-GB) of the text without '£' and ','; 0 when blank or invalid.</summary>
    public static decimal D(string? text)
    {
        var s = T(text);
        if (s.Length == 0) return 0m;
        return decimal.TryParse(s.Replace("£", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal),
            NumberStyles.Number, C, out var d) ? d : 0m;
    }

    /// <summary>PoMap.I: CInt(Decimal.Truncate(D)).</summary>
    public static int I(string? text)
    {
        var d = decimal.Truncate(D(text));
        return d is > int.MaxValue or < int.MinValue ? 0 : (int)d;
    }

    /// <summary>PoMap.Dt: the UK date in the box, or <paramref name="dflt"/> when the box is empty or not a date.</summary>
    public static DateOnly Dt(string? text, DateOnly dflt) => ParseDate(T(text)) ?? dflt;

    // --------------------------------------------------------------------------------------------- Fmt (output)

    /// <summary>Fmt.Str: the value as text, trailing blanks removed ("" for null); a decimal keeps its scale.</summary>
    public static string Str(JsonNode? v) => Json.Raw(v).TrimEnd();

    /// <summary>Fmt.Str of a Decimal (Convert.ToString keeps the scale: 10.270800).</summary>
    public static string Str(decimal d) => d.ToString(C);

    /// <summary>Fmt.Dec: a COMMAREA or result-set value as Decimal (0 when absent or not a number).</summary>
    public static decimal Dec(JsonNode? v)
    {
        if (v is JsonValue jv && jv.TryGetValue<decimal>(out var d)) return d;
        return decimal.TryParse(Json.Raw(v), NumberStyles.Any, C, out var p) ? p : 0m;
    }

    /// <summary>CommArea.GetInt: the integer part of a numeric field.</summary>
    public static int Int(JsonNode? v)
    {
        var d = decimal.Truncate(Dec(v));
        return d is > int.MaxValue or < int.MinValue ? 0 : (int)d;
    }

    /// <summary>Fmt.Money: £#,##0.00;-£#,##0.00;£0.00.</summary>
    public static string Money(decimal v) => v.ToString("£#,##0.00;-£#,##0.00;£0.00", C);

    /// <summary>Fmt.Money(v, currency): sterling with the pound sign, any other currency as "1,234.50 EUR".</summary>
    public static string Money(decimal v, string? currencyCode)
    {
        var c = (currencyCode ?? "").TrimEnd().ToUpperInvariant();
        if (c.Length == 0 || c == "GBP") return Money(v);
        return v.ToString("#,##0.00;-#,##0.00;0.00", C) + " " + c;
    }

    /// <summary>Fmt.Qty: six decimals with trailing zeros trimmed, then the unit when one is given.</summary>
    public static string Qty(decimal v, string? uom)
    {
        var s = v.ToString("#,##0.000000", C);
        if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
        var u = (uom ?? "").TrimEnd();
        return u.Length == 0 ? s : s + " " + u;
    }

    /// <summary>Fmt.Pct: #,##0.00## followed by '%'.</summary>
    public static string Pct(decimal v) => v.ToString("#,##0.00##", C) + "%";

    /// <summary>Fmt.Date: dd/MM/yyyy.</summary>
    public static string Date(DateOnly d) => d.ToString("dd/MM/yyyy", C);

    /// <summary>The ISO date the API takes for a COMMAREA date (PIC 9(8) yyyymmdd).</summary>
    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", C);

    /// <summary>A PIC 9(8) date field of an API answer (ISO date or null) as the yyyymmdd number the COMMAREA held.</summary>
    public static decimal Yyyymmdd(JsonNode? v)
    {
        var s = Json.Raw(v).Trim();
        if (s.Length == 0) return 0m;
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", C, DateTimeStyles.None, out var d)) return d.Year * 10000 + d.Month * 100 + d.Day;
        return Dec(v);
    }

    /// <summary>
    /// Fmt.ParseDate: dd/MM/yyyy, dd/MM/yy, ddMMyyyy, yyyyMMdd, dd-MM-yyyy, dd.MM.yyyy, then the en-GB general parse (day
    /// first); null when the text is not a date.
    /// </summary>
    public static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var s = text.Trim();
        string[] exact = ["dd/MM/yyyy", "dd/MM/yy", "ddMMyyyy", "yyyyMMdd", "dd-MM-yyyy", "dd.MM.yyyy"];
        if (DateTime.TryParseExact(s, exact, C, DateTimeStyles.None, out var d)) return DateOnly.FromDateTime(d);
        string[] general =
        [
            "d/M/yyyy", "d/M/yy", "d-M-yyyy", "d-M-yy", "d.M.yyyy", "d.M.yy", "d/M/yyyy H:mm", "d/M/yyyy H:mm:ss", "d MMM yyyy",
            "d MMMM yyyy", "d MMM yy", "yyyy-M-d", "yyyy/M/d", "yyyy-MM-ddTHH:mm:ss",
        ];
        return DateTime.TryParseExact(s, general, C, DateTimeStyles.AllowWhiteSpaces, out d) ? DateOnly.FromDateTime(d) : null;
    }

    // -------------------------------------------------------------------------------------------------- grids

    /// <summary>A grid column bound by GridHelper.Bind: the caption is GridHelper.Humanise of the legacy column name.</summary>
    public static GridColumn Col(string name, string kind) => new(name, UkFormat.Humanise(name), kind, name);

    /// <summary>The value of a property of an API object, matched without regard to case (null when absent).</summary>
    public static JsonNode? Get(JsonNode? obj, string name)
    {
        if (obj is not JsonObject o) return null;
        if (o.TryGetPropertyValue(name, out var v)) return v;
        foreach (var p in o)
        {
            if (string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        }
        return null;
    }

    /// <summary>CommArea.GetString: a text field without trailing blanks ("" when absent).</summary>
    public static string Text(JsonNode? obj, string name) => Str(Get(obj, name));
}
