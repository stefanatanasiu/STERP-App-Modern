using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Web.Framework.Session;

namespace Sterp.Web.Pages.So.Support;

/// <summary>
/// The legacy presentation helpers the Sales forms use (STERP.Common.Fmt; SalesOrderService.Amount, AreaMoney, AreaDate,
/// AreaText, StatusText; ServiceBase.Col), reproduced for the browser runtime. The client runs with invariant globalization,
/// so the en-GB rules (group ',', decimal '.', currency symbol '£', dd/MM/yyyy) are spelled out instead of taken from a culture.
/// </summary>
public static class SoFmt
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

    /// <summary>Fmt.Money: £#,##0.00;-£#,##0.00;£0.00.</summary>
    public static string Money(decimal v) => v.ToString("£#,##0.00;-£#,##0.00;£0.00", Invariant);

    /// <summary>Fmt.Money(v, currency): a foreign amount as "1,234.50 EUR", sterling with the pound sign.</summary>
    public static string Money(decimal v, string currencyCode)
    {
        var c = (currencyCode ?? "").TrimEnd().ToUpperInvariant();
        if (c.Length == 0 || c == "GBP") return Money(v);
        return v.ToString("#,##0.00;-#,##0.00;0.00", Invariant) + " " + c;
    }

    /// <summary>SalesOrderService.Amount: money in the currency it was stated in - never a pound sign on a foreign amount (COM-024).</summary>
    public static string Amount(decimal v, string currencyCode) =>
        currencyCode.Length == 0 || currencyCode == "GBP" ? Money(v) : Money(v, currencyCode);

    /// <summary>Fmt.Qty: six decimals with trailing zeros trimmed, then the unit when one is given.</summary>
    public static string Qty(decimal v, string? uom)
    {
        var s = v.ToString("#,##0.000000", Invariant);
        if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');
        var u = (uom ?? "").TrimEnd();
        return u.Length == 0 ? s : s + " " + u;
    }

    /// <summary>Fmt.Pct: "#,##0.00##" followed by "%".</summary>
    public static string Pct(decimal v) => v.ToString("#,##0.00##", Invariant) + "%";

    /// <summary>The QTY cell format of GridHelper.FormatColumn ("#,##0.000000").</summary>
    public static string QtyCell(decimal v) => v.ToString("#,##0.000000", Invariant);

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

    /// <summary>Fmt.Yyyymmdd: the KEY=VALUE date of a batch argument.</summary>
    public static string Yyyymmdd(DateOnly d) => d.ToString("yyyyMMdd", Invariant);

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

    /// <summary>ServiceBase.Col(r, name, default) shown as text: Fmt.Str of the value, the default when absent or null.</summary>
    public static string ColText(JsonNode? row, string name, string dflt) => Col(row, name) is { } v ? Str(v) : dflt;

    /// <summary>SalesOrderService.AreaText: a COMMAREA text field without trailing blanks ("" when the area has no such field).</summary>
    public static string AreaText(JsonNode? area, string field) => Str(Col(area, field)).Trim();

    /// <summary>
    /// SalesOrderService.AreaDate of a PIC 9(8) field: "-" when the area has no such field, otherwise the text CommArea.GetString
    /// gave for the number - dd/MM/yyyy for a date and "0" for an unset (zero) date, which the legacy test for "" or "00000000"
    /// never caught. The API presents a zero PIC 9(8) date as null.
    /// </summary>
    public static string AreaDate(JsonNode? area, string field)
    {
        if (!Has(area, field)) return "-";
        var v = Col(area, field);
        if (v is null) return "0";
        var raw = Json.Raw(v).Trim();
        if (raw.Length == 0 || raw == "00000000") return "-";
        var d = ParseDate(raw);
        return d.HasValue ? Date(d.Value) : raw;
    }

    /// <summary>SalesOrderService.StatusText: a state shown in words, "(not set)" when blank, " - reason" when one is given.</summary>
    public static string StatusText(string? status, string? reason)
    {
        var s = (status ?? "").Trim();
        var r = (reason ?? "").Trim();
        if (s.Length == 0) s = "(not set)";
        return r.Length == 0 ? s : s + " - " + r;
    }
}
