using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Web.Framework.Session;

namespace Sterp.Web.Pages.Fin.Common;

/// <summary>
/// STERP.Common.Fmt as the legacy finance forms used it (dotnet/STERP.Common/Fmt.cs): en-GB numbers, money with the pound
/// sign, NumberStyles.Any parsing, dd/MM/yyyy dates, the 3270 date habits of ParseDate and the "yyyy/pp" period caption.
/// </summary>
public static class LegacyFmt
{
    /// <summary>Fmt.UK: the en-GB number and date conventions (built on the invariant data, which the browser always has).</summary>
    public static readonly CultureInfo Uk = CreateUk();

    private static CultureInfo CreateUk()
    {
        var c = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        c.NumberFormat.CurrencySymbol = "£";
        c.NumberFormat.CurrencyNegativePattern = 1;
        c.DateTimeFormat.ShortDatePattern = "dd/MM/yyyy";
        c.DateTimeFormat.LongDatePattern = "dd MMMM yyyy";
        return c;
    }

    /// <summary>Fmt.Money: "£#,##0.00;-£#,##0.00;£0.00".</summary>
    public static string Money(decimal value) => value.ToString("£#,##0.00;-£#,##0.00;£0.00", Uk);

    /// <summary>Fmt.Money of a typed text (Fmt.Dec first, so anything unreadable is £0.00).</summary>
    public static string Money(string? text) => Money(Dec(text));

    /// <summary>Fmt.Dec of a typed text: NumberStyles.Any in en-GB, 0 when unreadable.</summary>
    public static decimal Dec(string? text) => decimal.TryParse(text, NumberStyles.Any, Uk, out var d) ? d : 0m;

    /// <summary>Fmt.Dec of a JSON value (COMMAREA or result-set field).</summary>
    public static decimal Dec(JsonNode? node)
    {
        if (node is not JsonValue v) return 0m;
        if (v.TryGetValue<decimal>(out var d)) return d;
        return v.TryGetValue<string>(out var s) ? Dec(s) : 0m;
    }

    /// <summary>Fmt.Int of a typed text: NumberStyles.Any in en-GB, 0 when unreadable.</summary>
    public static int Int(string? text) => int.TryParse(text, NumberStyles.Any, Uk, out var n) ? n : 0;

    /// <summary>Fmt.Str: the value as text with trailing blanks removed ("" for null).</summary>
    public static string Str(JsonNode? node) => Json.Raw(node).TrimEnd();

    /// <summary>Fmt.Pct: "#,##0.00##" followed by "%".</summary>
    public static string Pct(decimal value) => value.ToString("#,##0.00##", Uk) + "%";

    /// <summary>Fmt.Period: fiscal period caption "yyyy/pp" ("" for year 0).</summary>
    public static string Period(int fiscalYear, int periodNo) =>
        fiscalYear <= 0 ? "" : fiscalYear.ToString("0000", Uk) + "/" + periodNo.ToString("00", Uk);

    /// <summary>Fmt.Date of a business date.</summary>
    public static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", Uk);

    /// <summary>Fmt.Date of a date the service returned ("" when absent).</summary>
    public static string Date(JsonNode? node)
    {
        var text = Json.Raw(node).Trim();
        if (text.Length == 0) return "";
        return DateTime.TryParse(text, Uk, DateTimeStyles.None, out var d) ? d.ToString("dd/MM/yyyy", Uk) : text;
    }

    /// <summary>Fmt.ParseDate: dd/MM/yyyy, dd/MM/yy, ddMMyyyy, yyyyMMdd, dd-MM-yyyy, dd.MM.yyyy, then a lenient en-GB parse.</summary>
    public static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var s = text.Trim();
        string[] formats = ["dd/MM/yyyy", "dd/MM/yy", "ddMMyyyy", "yyyyMMdd", "dd-MM-yyyy", "dd.MM.yyyy"];
        if (DateTime.TryParseExact(s, formats, Uk, DateTimeStyles.None, out var d)) return DateOnly.FromDateTime(d);
        if (DateTime.TryParse(s, Uk, DateTimeStyles.None, out d)) return DateOnly.FromDateTime(d);
        return null;
    }

    /// <summary>The ISO form of a date for an operation (null stays null, so the service applies its default).</summary>
    public static string? Iso(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A PIC 9(8) YYYYMMDD field as CommArea.GetTable put it in a DataTable (a decimal; 0 when absent).</summary>
    public static decimal Yyyymmdd(JsonNode? node)
    {
        var text = Json.Raw(node).Trim();
        if (text.Length == 0) return 0m;
        return DateTime.TryParseExact(text.Length > 10 ? text[..10] : text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? decimal.Parse(d.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture)
            : Dec(text);
    }
}
