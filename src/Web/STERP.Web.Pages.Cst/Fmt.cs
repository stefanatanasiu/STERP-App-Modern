using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Web.Framework.Format;
using Sterp.Web.Framework.Session;

namespace Sterp.Web.Pages.Cst;

/// <summary>
/// The presentation helpers the costing screens use (dotnet/STERP.Common/Fmt.cs, CostingService.Text/Num): money as
/// <c>£#,##0.00;-£#,##0.00;£0.00</c> (en-GB separators equal the invariant ones), the fiscal period caption, COMMAREA text
/// (trailing spaces removed) and numbers, and the legacy decimal parse of the filter boxes (Decimal.TryParse, en-GB).
/// </summary>
internal static class Fmt
{
    private static readonly CultureInfo Uk = CultureInfo.InvariantCulture;

    /// <summary>Fmt.Money.</summary>
    public static string Money(decimal value) => value.ToString("£#,##0.00;-£#,##0.00;£0.00", Uk);

    public static string Money(JsonNode? value) => Money(Dec(value));

    /// <summary>Fmt.Period: "2026/07"; empty when there is no fiscal year.</summary>
    public static string Period(int fiscalYear, int periodNo) =>
        fiscalYear <= 0 ? "" : fiscalYear.ToString("0000", Uk) + "/" + periodNo.ToString("00", Uk);

    /// <summary>CostingService.Num: a COMMAREA field as a number (0 when absent).</summary>
    public static decimal Dec(JsonNode? value) =>
        decimal.TryParse(Json.Raw(value), NumberStyles.Float, Uk, out var d) ? d : 0m;

    /// <summary>A COMMAREA number concatenated into a status text (Decimal.ToString of the field's value).</summary>
    public static string Num(JsonNode? value) => Dec(value).ToString(Uk);

    /// <summary>CostingService.Text / Fmt.Str: the field as text without trailing spaces.</summary>
    public static string Text(JsonNode? value) => Json.Str(value).TrimEnd();

    /// <summary>Decimal.TryParse of a filter box (NumberStyles.Number, en-GB); null when it does not parse.</summary>
    public static decimal? ParseDecimal(string? text) =>
        decimal.TryParse(text, NumberStyles.Number, Uk, out var d) ? d : null;

    /// <summary>A grid column bound from a camelCase JSON property under its legacy column name and GridHelper data type.</summary>
    public static GridColumn Col(string json, string legacy, string kind = "TEXT") => new(json, kind: kind, legacyName: legacy);

    /// <summary>True when the bound grid has the column (GridHelper.Selected answers Nothing for a missing column).</summary>
    public static bool HasColumn(GridData data, string json) =>
        data.Columns.Any(c => string.Equals(c.Name, json, StringComparison.OrdinalIgnoreCase));
}
