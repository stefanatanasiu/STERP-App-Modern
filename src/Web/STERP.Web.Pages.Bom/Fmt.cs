using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Web.Framework.Format;

namespace Sterp.Web.Pages.Bom;

/// <summary>
/// The legacy <c>STERP.Common.Fmt</c> conversions the BOM forms use for their status and boundary lines
/// (dotnet/STERP.Common/Fmt.cs): en-GB number shapes, the pound sign for GBP, dd/MM/yyyy dates.
/// </summary>
internal static class Fmt
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>Fmt.Str: the value as text, trailing spaces removed ("" for null).</summary>
    public static string Str(JsonNode? v) => v is null ? "" : Json(v).TrimEnd();

    private static string Json(JsonNode v) => v is JsonValue j && j.TryGetValue<string>(out var s) ? s : v.ToJsonString();

    /// <summary>Fmt.Dec (0 when not a number).</summary>
    public static decimal Dec(JsonNode? v) =>
        v is null ? 0m : decimal.TryParse(Json(v), NumberStyles.Any, C, out var d) ? d : 0m;

    /// <summary>Fmt.Int (0 when not an integer).</summary>
    public static int Int(JsonNode? v) => v is null ? 0 : int.TryParse(Json(v), NumberStyles.Any, C, out var n) ? n : 0;

    /// <summary>Fmt.Bool: true, 1, Y, YES, TRUE.</summary>
    public static bool Bool(JsonNode? v)
    {
        if (v is null) return false;
        if (v is JsonValue j && j.TryGetValue<bool>(out var b)) return b;
        var s = Json(v).Trim().ToUpperInvariant();
        return s is "1" or "Y" or "YES" or "TRUE";
    }

    /// <summary>Fmt.Date: dd/MM/yyyy (the text itself when it is not a date).</summary>
    public static string Date(JsonNode? v)
    {
        if (v is null) return "";
        var s = Json(v);
        return DateTime.TryParse(s, C, DateTimeStyles.None, out var d) ? d.ToString("dd/MM/yyyy", C) : s.TrimEnd();
    }

    public static string Date(DateOnly d) => d.ToString("dd/MM/yyyy", C);

    /// <summary>Fmt.Money(v, "GBP"): "£#,##0.00;-£#,##0.00;£0.00".</summary>
    public static string Money(decimal v) => v.ToString("£#,##0.00;-£#,##0.00;£0.00", C);

    /// <summary>Fmt.Qty: six decimals with trailing zeros trimmed, then the unit when one is given.</summary>
    public static string Qty(decimal v, string? uom)
    {
        var s = v.ToString("#,##0.000000", C);
        if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
        var u = (uom ?? "").TrimEnd();
        return u.Length == 0 ? s : s + " " + u;
    }

    /// <summary>Fmt.Rate: "#,##0.00000000".</summary>
    public static string Rate(decimal v) => v.ToString("#,##0.00000000", C);

    /// <summary>
    /// Fmt.ParseDate: dd/MM/yyyy, dd/MM/yy, ddMMyyyy, yyyyMMdd, dd-MM-yyyy, dd.MM.yyyy, then the en-GB lenient parse
    /// (day first).
    /// </summary>
    public static DateOnly? ParseDate(string? text)
    {
        var s = (text ?? "").Trim();
        if (s.Length == 0) return null;
        string[] exact = ["dd/MM/yyyy", "dd/MM/yy", "ddMMyyyy", "yyyyMMdd", "dd-MM-yyyy", "dd.MM.yyyy"];
        if (DateTime.TryParseExact(s, exact, C, DateTimeStyles.None, out var d)) return DateOnly.FromDateTime(d);
        string[] lenient = ["d/M/yyyy", "d/M/yy", "d-M-yyyy", "d.M.yyyy", "d MMM yyyy", "d MMMM yyyy", "yyyy-MM-dd", "yyyy-M-d",
            "d/M/yyyy H:mm", "d/M/yyyy H:mm:ss"];
        return DateTime.TryParseExact(s, lenient, C, DateTimeStyles.AllowWhiteSpaces, out d) ? DateOnly.FromDateTime(d) : null;
    }

    /// <summary>Decimal.TryParse(text, NumberStyles.Number, Fmt.UK).</summary>
    public static bool TryNumber(string? text, out decimal value) =>
        decimal.TryParse((text ?? "").Trim(), NumberStyles.Number, C, out value);

    /// <summary>The ISO date the API takes for a legacy Date value.</summary>
    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", C);

    /// <summary>A legacy grid column of a facade DataTable: its name, its type decides the format (GridHelper.GuessType).</summary>
    public static GridColumn Col(string jsonName, string kind = "TEXT") =>
        new(jsonName, UkFormat.Humanise(UkFormat.LegacyName(jsonName)), kind, UkFormat.LegacyName(jsonName));
}
