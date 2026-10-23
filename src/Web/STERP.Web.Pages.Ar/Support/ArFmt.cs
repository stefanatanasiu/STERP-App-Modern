using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Web.Framework.Format;
using Sterp.Web.Framework.Session;

namespace Sterp.Web.Pages.Ar.Support;

/// <summary>
/// The legacy presentation helpers the Debtors forms use (STERP.Common.Fmt, DebtorsService.Amount/AreaMoney/AreaDate,
/// ServiceBase.Col), reproduced for the browser runtime: the client runs with invariant globalization, so the en-GB rules
/// (group ',', decimal '.', currency symbol '£', dd/MM/yyyy) are spelled out instead of taken from a culture.
/// </summary>
public static class ArFmt
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>en-GB number symbols (identical to the invariant ones) with the pound sign as currency symbol.</summary>
    private static readonly NumberFormatInfo Uk = CreateUk();

    private static NumberFormatInfo CreateUk()
    {
        var nfi = (NumberFormatInfo)NumberFormatInfo.InvariantInfo.Clone();
        nfi.CurrencySymbol = "£";
        nfi.CurrencyPositivePattern = 0;
        nfi.CurrencyNegativePattern = 1;
        nfi.NumberNegativePattern = 1;
        return nfi;
    }

    /// <summary>Fmt.Str: the text of a value, trailing blanks removed ("" for null).</summary>
    public static string Str(JsonNode? v) => Json.Raw(v).TrimEnd();

    /// <summary>Fmt.Dec(text): decimal.TryParse(NumberStyles.Any, en-GB), 0 when it does not parse.</summary>
    public static decimal Dec(string? text) =>
        decimal.TryParse(text ?? "", NumberStyles.Any, Uk, out var d) ? d : 0m;

    /// <summary>Fmt.Dec of a result-set or COMMAREA value.</summary>
    public static decimal Dec(JsonNode? v) => v is null ? 0m : Dec(Json.Raw(v));

    /// <summary>Fmt.Int(text): int.TryParse(NumberStyles.Any, en-GB), 0 when it does not parse.</summary>
    public static int Int(string? text) =>
        int.TryParse(text ?? "", NumberStyles.Any, Uk, out var i) ? i : 0;

    public static int Int(JsonNode? v) => v is null ? 0 : Int(Json.Raw(v));

    /// <summary>Fmt.Bool: True, 1, Y, YES, TRUE.</summary>
    public static bool Bool(JsonNode? v)
    {
        if (v is JsonValue jv && jv.TryGetValue<bool>(out var b)) return b;
        return Json.Raw(v).Trim().ToUpperInvariant() is "1" or "Y" or "YES" or "TRUE";
    }

    /// <summary>Fmt.Money: £#,##0.00;-£#,##0.00;£0.00.</summary>
    public static string Money(decimal v) => v.ToString("£#,##0.00;-£#,##0.00;£0.00", Invariant);

    /// <summary>Fmt.Money(v, currency): a foreign amount as "1,234.50 EUR", sterling with the pound sign.</summary>
    public static string Money(decimal v, string currencyCode)
    {
        var c = (currencyCode ?? "").TrimEnd().ToUpperInvariant();
        if (c.Length == 0 || c == "GBP") return Money(v);
        return v.ToString("#,##0.00;-#,##0.00;0.00", Invariant) + " " + c;
    }

    /// <summary>DebtorsService.Amount: money in the currency it was stated in (COM-024).</summary>
    public static string Amount(decimal v, string currencyCode) =>
        currencyCode.Length == 0 || currencyCode == "GBP" ? Money(v) : Money(v, currencyCode);

    /// <summary>Fmt.Date of a result-set value: dd/MM/yyyy, the text itself when it is not a date, "" for null.</summary>
    public static string Date(JsonNode? v)
    {
        if (v is null) return "";
        var raw = Json.Raw(v);
        return DateTime.TryParse(raw, Invariant, DateTimeStyles.None, out var d) ? d.ToString("dd/MM/yyyy", Invariant) : raw.TrimEnd();
    }

    public static string Date(DateOnly d) => d.ToString("dd/MM/yyyy", Invariant);

    /// <summary>
    /// Fmt.ParseDate: dd/MM/yyyy, dd/MM/yy, ddMMyyyy, yyyyMMdd, dd-MM-yyyy, dd.MM.yyyy, then the en-GB general parse
    /// (day first); null when the text is not a date.
    /// </summary>
    public static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var s = text.Trim();
        string[] exact = ["dd/MM/yyyy", "dd/MM/yy", "ddMMyyyy", "yyyyMMdd", "dd-MM-yyyy", "dd.MM.yyyy"];
        if (DateTime.TryParseExact(s, exact, Invariant, DateTimeStyles.None, out var d)) return DateOnly.FromDateTime(d);
        string[] general =
        [
            "d/M/yyyy", "d/M/yy", "d-M-yyyy", "d-M-yy", "d.M.yyyy", "d.M.yy", "d/M/yyyy H:mm", "d/M/yyyy H:mm:ss", "d MMM yyyy",
            "d MMMM yyyy", "d MMM yy", "yyyy-M-d", "yyyy/M/d", "yyyy-MM-ddTHH:mm:ss",
        ];
        return DateTime.TryParseExact(s, general, Invariant, DateTimeStyles.AllowWhiteSpaces, out d) ? DateOnly.FromDateTime(d) : null;
    }

    /// <summary>yyyy-MM-dd for a date parameter of the API.</summary>
    public static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", Invariant);

    // ------------------------------------------------------------------------- result sets and COMMAREA data

    /// <summary>
    /// ServiceBase.Col: the value of a result-set column (or COMMAREA field) when present and not null, otherwise null. Names
    /// are matched without regard to case, so the legacy column name finds the camelCase property of the API row.
    /// </summary>
    public static JsonNode? Col(JsonNode? row, string name)
    {
        if (row is not JsonObject o) return null;
        foreach (var p in o)
        {
            if (string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        }
        return null;
    }

    public static bool Has(JsonNode? row, string name) =>
        row is JsonObject o && o.Any(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>DebtorsService.AreaMoney: a COMMAREA money field as sterling, "-" when the area has no such field.</summary>
    public static string AreaMoney(JsonNode? area, string field) => Has(area, field) ? Money(Dec(Col(area, field))) : "-";

    /// <summary>DebtorsService.AreaDate: a PIC 9(8) date as dd/MM/yyyy, "-" when unset (blank or zeros).</summary>
    public static string AreaDate(JsonNode? area, string field)
    {
        if (!Has(area, field)) return "-";
        var v = Col(area, field);
        var raw = v is JsonValue n && n.TryGetValue<long>(out var digits) ? digits.ToString("D8", Invariant) : Json.Raw(v).Trim();
        if (raw.Length == 0 || raw == "00000000") return "-";
        var d = ParseDate(raw);
        return d.HasValue ? Date(d.Value) : raw;
    }

    /// <summary>CommArea.GetString: the text of a COMMAREA field without trailing blanks.</summary>
    public static string AreaText(JsonNode? area, string field) => Str(Col(area, field));

    /// <summary>
    /// GridHelper.Bind of a result set: the legacy columns in result-set order with their captions (GridHelper.Humanise of the
    /// legacy column name) and UK formats; the API rows carry the same columns under the camelCase names of the contract.
    /// </summary>
    public static GridData Grid(JsonNode? rows, params string[] legacyColumns)
    {
        var list = (rows as JsonArray ?? []).OfType<JsonObject>().ToList();
        var columns = legacyColumns.Select(l => new GridColumn(JsonName(list, l), legacyName: l)).ToArray();
        return new GridData(columns, list);
    }

    private static string JsonName(List<JsonObject> rows, string legacy)
    {
        foreach (var r in rows)
        {
            foreach (var p in r)
            {
                if (string.Equals(p.Key, legacy, StringComparison.OrdinalIgnoreCase)) return p.Key;
            }
        }
        return char.ToLowerInvariant(legacy[0]) + legacy[1..];
    }
}
