using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Web.Framework.Api;
using Sterp.Web.Framework.Format;
using Sterp.Web.Framework.Session;

namespace Sterp.Web.Pages.Kit;

/// <summary>One column of a kit facade "spec" (KitArea.vb NewTable/Copy): display name, data type (I, D, T or text) and the response property.</summary>
public sealed record KitSpecColumn(string Name, char Type, string Property);

/// <summary>
/// The presentation helpers the kit forms used: STERP.Common.Fmt (Money "£#,##0.00", Qty six decimals with trailing zeros
/// trimmed, Str, Int), the kit facade specs that shaped the COBOL windows into grid tables (KitService.LineSpec/TrialSpec,
/// KitExecutionService.GeneaSpec/CostSpec/CommSpec) and the column sets of the two procedure-backed lists. A spec table
/// holds only its own columns, so a value looked up by a column the table does not have is blank (GridHelper.Selected).
/// </summary>
public static class KitLegacy
{
    private static readonly CultureInfo Uk = CultureInfo.InvariantCulture;

    /// <summary>KitService.LineSpec (KitService.vb:17-24): the kit lines of KITCREAT ENQUIRE/GETLINE.</summary>
    public static readonly KitSpecColumn[] LineSpec =
    [
        new("LineNum", 'I', "lineNum"), new("Path", 'S', "path"), new("Item", 'S', "item"), new("Revision", 'S', "revision"),
        new("Description", 'S', "description"), new("Basis", 'S', "basis"), new("Unit", 'S', "uom"),
        new("Original", 'D', "originalRequirement"), new("Required", 'D', "currentRequirement"), new("ScrapPct", 'D', "scrapPct"),
        new("Reserved", 'D', "reserved"), new("Picked", 'D', "picked"), new("Issued", 'D', "netIssued"),
        new("Returned", 'D', "returned"), new("Scrapped", 'D', "scrapped"), new("Outstanding", 'D', "remaining"),
        new("Excess", 'D', "excess"), new("Substitute", 'S', "substitute"), new("NeedDate", 'T', "needDate"),
        new("Location", 'S', "location"), new("UnitCost", 'D', "unitCost"), new("Status", 'S', "status"),
    ];

    /// <summary>KitService.TrialSpec (KitService.vb:27-32): the KITTRIAL result lines.</summary>
    public static readonly KitSpecColumn[] TrialSpec =
    [
        new("LineNum", 'I', "lineNum"), new("Depth", 'I', "depth"), new("Path", 'S', "path"), new("Item", 'S', "item"),
        new("Revision", 'S', "revision"), new("Unit", 'S', "uom"), new("Gross", 'D', "gross"), new("Required", 'D', "netRequired"),
        new("Free", 'D', "free"), new("OwnReserved", 'D', "ownReserved"), new("OtherReserved", 'D', "otherReserved"),
        new("Inbound", 'D', "inbound"), new("InboundDate", 'T', "inboundDate"), new("Allocated", 'D', "allocated"),
        new("Shortage", 'D', "shortage"), new("ShortDate", 'T', "shortDate"), new("Limiting", 'S', "limiting"),
        new("Action", 'S', "action"), new("Warning", 'S', "warning"),
    ];

    /// <summary>KitExecutionService.GeneaSpec (KitExecutionService.vb:19-23): the GENEALOG trace lines.</summary>
    public static readonly KitSpecColumn[] GeneaSpec =
    [
        new("Seq", 'I', "seq"), new("Depth", 'I', "depth"), new("KitRef", 'S', "kitRef"), new("Parent", 'S', "parent"),
        new("ParentLot", 'S', "parentLot"), new("ParentSerial", 'S', "parentSerial"), new("Item", 'S', "item"), new("Lot", 'S', "lot"),
        new("Serial", 'S', "serial"), new("Quantity", 'D', "quantity"), new("Unit", 'S', "uom"), new("PlannedItem", 'S', "plannedItem"),
        new("Substitute", 'S', "substitute"), new("Event", 'S', "event"), new("Happened", 'T', "happened"),
        new("Operator", 'S', "operator"), new("Note", 'S', "note"),
    ];

    /// <summary>KitExecutionService.CostSpec (KitExecutionService.vb:27-28): the KITCOST elements.</summary>
    public static readonly KitSpecColumn[] CostSpec =
    [
        new("Element", 'S', "element"), new("Description", 'S', "description"), new("Planned", 'D', "planned"),
        new("Actual", 'D', "actual"), new("Variance", 'D', "variance"), new("Quantity", 'D', "quantity"), new("Unit", 'S', "uom"),
    ];

    /// <summary>KitExecutionService.CommSpec (KitExecutionService.vb:32-37): the KITCOMM component lines.</summary>
    public static readonly KitSpecColumn[] CommSpec =
    [
        new("LineNum", 'I', "lineNum"), new("Item", 'S', "item"), new("Description", 'S', "description"), new("QtyPer", 'D', "qtyPer"),
        new("Unit", 'S', "uom"), new("Required", 'D', "required"), new("Free", 'D', "free"), new("Delivered", 'D', "delivered"),
        new("Sets", 'D', "sets"), new("StandalonePrice", 'D', "standalonePrice"), new("AllocationPct", 'D', "allocationPct"),
        new("AllocatedNet", 'D', "allocatedNet"), new("UnitCost", 'D', "unitCost"), new("CostGBP", 'D', "costGbp"),
        new("VatCode", 'S', "vatCode"), new("CustomerSpecified", 'S', "customerSpecified"), new("SubstituteOk", 'S', "substituteOk"),
        new("Limiting", 'S', "limiting"),
    ];

    /// <summary>kit.usp_KitReadiness result set (usp_KitEnquiry.sql:23-50) as KitService.Worklist bound it (SQL types drive the formats).</summary>
    public static readonly (string Name, string Kind, string Property)[] WorklistColumns =
    [
        ("KitRef", "TEXT", "kitRef"), ("SiteCode", "TEXT", "siteCode"), ("ParentItemCode", "TEXT", "parentItemCode"),
        ("Status", "TEXT", "status"), ("PlannedGoodQty", "QTY", "plannedGoodQty"), ("CompletedGoodQty", "QTY", "completedGoodQty"),
        ("NeedDate", "DATE", "needDate"), ("WipValueGBP", "MONEY", "wipValueGBP"), ("LineCount", "INT", "lineCount"),
        ("ShortLines", "INT", "shortLines"), ("CoveredLines", "INT", "coveredLines"), ("ReadyPct", "MONEY", "readyPct"),
        ("IsReady", "BOOL", "isReady"), ("IssuedQty", "QTY", "issuedQty"), ("ReturnedQty", "QTY", "returnedQty"),
        ("ScrappedQty", "QTY", "scrappedQty"),
    ];

    /// <summary>kit.usp_KitGenealogy result set (usp_KitEnquiry.sql:175-180) as KitExecutionService.GenealogyList bound it.</summary>
    public static readonly (string Name, string Kind, string Property)[] RecallColumns =
    [
        ("Depth", "INT", "depth"), ("GenealogyId", "INT", "genealogyId"), ("KitRef", "TEXT", "kitRef"),
        ("ParentItemCode", "TEXT", "parentItemCode"), ("ParentLotCode", "TEXT", "parentLotCode"), ("ParentSerialNo", "TEXT", "parentSerialNo"),
        ("ComponentItemCode", "TEXT", "componentItemCode"), ("ComponentLotCode", "TEXT", "componentLotCode"),
        ("ComponentSerialNo", "TEXT", "componentSerialNo"), ("ConsumedQty", "QTY", "consumedQty"), ("WasSubstitute", "BOOL", "wasSubstitute"),
        ("PlannedItemCode", "TEXT", "plannedItemCode"),
    ];

    /// <summary>
    /// KitArea.NewTable + Copy: a grid table with exactly the spec columns (":I" integer, ":D" decimal, ":T" date, else text),
    /// formatted as GridHelper.GuessType does (decimal: QTY when the name starts or ends with "Qty", else MONEY; date:
    /// STAMP when the name ends with "At" or "Time", else DATE).
    /// </summary>
    public static GridData Table(IReadOnlyList<KitSpecColumn> spec, JsonNode? lines)
    {
        var columns = spec.Select(c => new GridColumn(c.Name, kind: Kind(c), legacyName: c.Name)).ToList();
        var rows = new List<JsonObject>();
        foreach (var line in lines as JsonArray ?? [])
        {
            if (line is not JsonObject source) continue;
            var row = new JsonObject();
            foreach (var c in spec) row[c.Name] = source[c.Property]?.DeepClone();
            rows.Add(row);
        }
        return new GridData(columns, rows);
    }

    /// <summary>A procedure result set bound with its column names and SQL types (SqlHelper.ExecuteDataTable + GridHelper.Bind).</summary>
    public static GridData ResultSet(IReadOnlyList<(string Name, string Kind, string Property)> columns, JsonNode? items)
    {
        var cols = columns.Select(c => new GridColumn(c.Name, kind: c.Kind, legacyName: c.Name)).ToList();
        var rows = new List<JsonObject>();
        foreach (var item in items as JsonArray ?? items?["items"] as JsonArray ?? [])
        {
            if (item is not JsonObject source) continue;
            var row = new JsonObject();
            foreach (var c in columns) row[c.Name] = Find(source, c.Property)?.DeepClone();
            rows.Add(row);
        }
        return new GridData(cols, rows);
    }

    private static JsonNode? Find(JsonObject source, string property) =>
        source.FirstOrDefault(p => string.Equals(p.Key, property, StringComparison.OrdinalIgnoreCase)).Value;

    private static string Kind(KitSpecColumn c) => c.Type switch
    {
        'I' => "INT",
        'D' => c.Name.EndsWith("Qty", StringComparison.Ordinal) || c.Name.StartsWith("Qty", StringComparison.Ordinal) ? "QTY" : "MONEY",
        'T' => c.Name.EndsWith("At", StringComparison.Ordinal) || c.Name.EndsWith("Time", StringComparison.Ordinal) ? "STAMP" : "DATE",
        _ => "TEXT",
    };

    /// <summary>The payload of a response: the envelope data of a success, the problem data member of a refusal (contracts 4.7).</summary>
    public static JsonNode? Payload(ApiResponse r) => r.IsSuccess ? r.Data : r.Problem?.Data;

    /// <summary>KitFacade.Text: a text field of the last COMMAREA ("" when absent).</summary>
    public static string Text(JsonNode? data, string property) => data is JsonObject o ? Json.Str(Find(o, property)).TrimEnd() : "";

    /// <summary>KitFacade.Num: a numeric field of the last COMMAREA (0 when absent).</summary>
    public static decimal Num(JsonNode? data, string property) =>
        data is JsonObject o && decimal.TryParse(Json.Raw(Find(o, property)), NumberStyles.Float, Uk, out var d) ? d : 0m;

    /// <summary>A count field concatenated into a status text (VB Decimal to String of a PIC S9(n) field: plain digits).</summary>
    public static string Count(JsonNode? data, string property) => decimal.Truncate(Num(data, property)).ToString(Uk);

    /// <summary>Fmt.Money (STERP.Common/Fmt.cs:57): "£#,##0.00;-£#,##0.00;£0.00", UK culture.</summary>
    public static string Money(decimal v) => v.ToString("£#,##0.00;-£#,##0.00;£0.00", Uk);

    /// <summary>Fmt.Qty(v, uom) (Fmt.cs:68-74): six decimals, trailing zeros and point trimmed, the unit appended when given.</summary>
    public static string Qty(decimal v, string uom = "")
    {
        var s = v.ToString("#,##0.000000", Uk);
        if (s.Contains('.', StringComparison.Ordinal)) s = s.TrimEnd('0').TrimEnd('.');
        var u = (uom ?? "").TrimEnd();
        return u.Length == 0 ? s : s + " " + u;
    }

    /// <summary>The forms' Code(TextBox): the text trimmed and upper-cased.</summary>
    public static string Code(string? text) => (text ?? "").Trim().ToUpperInvariant();

    /// <summary>Fmt.Int (Fmt.cs:24-29).</summary>
    public static int Int(string? text) =>
        int.TryParse(text, NumberStyles.Any, Uk, out var n) ? n : 0;

    /// <summary>Decimal.TryParse of a TextBox text (NumberStyles.Number, UK separators).</summary>
    public static bool TryDecimal(string? text, out decimal value) =>
        decimal.TryParse((text ?? "").Trim(), NumberStyles.Number, Uk, out value);

    private static readonly string[] DateForms =
    [
        "d/M/yyyy", "d/M/yy", "d-M-yyyy", "d-M-yy", "d.M.yyyy", "d.M.yy", "yyyy-M-d", "yyyy/M/d", "yyyy.M.d",
        "d MMM yyyy", "d MMMM yyyy", "d-MMM-yyyy", "d-MMM-yy", "d MMM yy", "MMM d, yyyy", "MMMM d, yyyy", "dddd, d MMMM yyyy",
        "d/M", "d MMM", "d MMMM",
    ];

    private static readonly string[] DateTimeForms =
        [.. DateForms.SelectMany(d => new[] { d, d + " H:mm", d + " H:mm:ss" }), "yyyy-M-d'T'H:mm", "yyyy-M-d'T'H:mm:ss"];

    /// <summary>
    /// Date.TryParse of a TextBox text under the en-GB culture (frmKitTrial.When): day-first numeric dates with "/", "-" or
    /// "." and a two- or four-digit year, year-first dates, dates with an English month name and a day/month without a
    /// year (current year), each optionally with a time; the compact digit forms are not dates. The browser runtime has no
    /// en-GB culture data, so the accepted forms are listed. Null when the text is not a date.
    /// </summary>
    public static DateOnly? ParseDate(string? text)
    {
        var t = (text ?? "").Trim();
        return DateTime.TryParseExact(t, DateTimeForms, Uk, DateTimeStyles.AllowInnerWhite, out var d) ? DateOnly.FromDateTime(d) : null;
    }
}
