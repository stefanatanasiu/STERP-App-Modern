using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Ap.Engine;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Ap.Invoices;

/// <summary>InvoiceService COBOL-backed members (dotnet/STERP.Business/Creditors/InvoiceService.vb).</summary>
public static class InvoiceEndpoints
{
    /// <summary>ap_checkDuplicateInvoice - InvoiceService.DuplicateCheck (InvoiceService.vb:32-48) -> APINVREG DUPCHK (FR-STERP-AP-022).</summary>
    public static async Task<IResult> DuplicateCheckAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var gross = Decimal(body, "statedGross") ?? 0m;
        var docDate = DateValue(body, "documentDate") ?? ctx.Session.BusinessDate;
        var ir = new InvoiceRegisterArea
        {
            Supplier = CobolText.Trimmed(Text(body, "supplierCode"), 20),
            DocType = CobolText.Trimmed(Text(body, "documentType"), 12),
            SupplierRef = CobolText.Trimmed(Text(body, "supplierDocRef"), 40),
            StatedGross = CobolNumeric.Normalize(gross, 15, 2, signed: true),
            DocumentDate = CobolDate.ToYyyymmdd(docDate),
        };
        var ca = CaHeader.For(ctx.Session, "APINVREG", "DUPCHK");
        await new ApInvReg(ctx.Get<ISqlRunner>(), new ApSvc(ctx.Services)).RunAsync(ca, ir, ctx.Aborted);
        return ctx.Envelope(ca.Result, ir.ToData());
    }

    /// <summary>ap_matchPurchaseInvoice - InvoiceService.MatchAll (InvoiceService.vb:61-73) -> APMATCH MATCHALL (FR-STERP-AP-023).</summary>
    public static async Task<IResult> MatchAllAsync(OperationContext ctx)
    {
        var mt = new MatchArea { RegisterNumber = CobolText.Trimmed(ctx.Route("registerNumber"), 30) };
        var ca = CaHeader.For(ctx.Session, "APMATCH", "MATCHALL");
        await new ApMatch(ctx.Get<ISqlRunner>(), new ApSvc(ctx.Services)).RunAsync(ca, mt, ctx.Aborted);
        return ctx.Envelope(ca.Result, mt.ToData());
    }

    internal static string? Text(JsonObject body, string name) =>
        body.TryGetPropertyValue(name, out var node) && node is JsonValue v
            ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString())
            : null;

    internal static decimal? Decimal(JsonObject body, string name)
    {
        if (!body.TryGetPropertyValue(name, out var node) || node is null) return null;
        if (node is JsonValue v)
        {
            if (v.TryGetValue<decimal>(out var d)) return d;
            if (v.TryGetValue<string>(out var s) && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var p)) return p;
        }
        throw ApiProblemException.BadBody($"{name} must be a number.");
    }

    internal static DateOnly? DateValue(JsonObject body, string name)
    {
        var t = Text(body, name);
        if (string.IsNullOrWhiteSpace(t)) return null;
        return DateOnly.TryParseExact(t.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw ApiProblemException.BadBody($"{name} must be a date (yyyy-MM-dd).");
    }
}
