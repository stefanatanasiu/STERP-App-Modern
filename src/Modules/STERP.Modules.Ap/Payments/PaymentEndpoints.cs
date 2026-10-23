using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Ap.Engine;
using Sterp.Modules.Ap.Invoices;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Ap.Payments;

/// <summary>PaymentService COBOL-backed members (dotnet/STERP.Business/Creditors/PaymentService.vb).</summary>
public static class PaymentEndpoints
{
    private const string NotHeld = "The payment proposal is not held for this company.";

    /// <summary>ap_selectPaymentProposal - PaymentService.Select_ (PaymentService.vb:33-48) -> APPAYPRP SELECT (FR-STERP-AP-032).</summary>
    public static async Task<IResult> SelectAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var pp = new PaymentProposalArea
        {
            ProposalRef = "",
            CutoffDate = CobolDate.ToYyyymmdd(InvoiceEndpoints.DateValue(body, "cutoffDate")),
            PayDate = CobolDate.ToYyyymmdd(InvoiceEndpoints.DateValue(body, "payDate")),
            Method = CobolText.Trimmed(InvoiceEndpoints.Text(body, "method"), 10),
            BankAccount = CobolText.Trimmed(InvoiceEndpoints.Text(body, "bankAccount"), 20),
            Mode = "DRAFT",
        };
        return await LinkProposalAsync(ctx, pp, "SELECT");
    }

    /// <summary>ap_getPaymentProposalTotals - PaymentService.Totals (PaymentService.vb:50-83) -> APPAYPRP TOTALS (FR-STERP-AP-033).</summary>
    public static async Task<IResult> TotalsAsync(OperationContext ctx)
    {
        var proposalRef = ctx.Route("proposalRef");
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync(
            "SELECT p.ProposalId, p.Status, p.CashPrincipalGBP, p.CreditsAppliedGBP, p.TotalOutflowGBP, p.ExcludedCount, p.ContentHash, " +
            "SelectedCount = (SELECT COUNT(*) FROM ap.PaymentProposalLine l WHERE l.ProposalId = p.ProposalId AND l.IsExcluded = 0) " +
            "FROM ap.PaymentProposal p WHERE p.CompanyCode = @c AND p.ProposalRef = @r",
            new { c = ctx.Session.CompanyCode, r = proposalRef }, ctx.Aborted);
        if (rows.Count == 0) return ctx.Envelope(new LegacyResult(8, "APP0000", NotHeld));
        var row = (JsonObject)rows[0]!;
        var pp = new PaymentProposalArea
        {
            ProposalRef = CobolText.Trimmed(proposalRef, 30),
            ProposalId = Int(row["proposalId"]),
            Status = CobolText.Trimmed(Str(row["status"]), 12),
            ContentHash = CobolText.Trimmed(Str(row["contentHash"]), 64),
            CashPrincipal = CobolNumeric.Normalize(Dec(row["cashPrincipalGBP"]), 15, 2, signed: true),
            CreditsApplied = CobolNumeric.Normalize(Dec(row["creditsAppliedGBP"]), 15, 2, signed: true),
            TotalOutflow = CobolNumeric.Normalize(Dec(row["totalOutflowGBP"]), 15, 2, signed: true),
            SelectedCount = (int)CobolNumeric.Normalize(Int(row["selectedCount"]), 6, 0, signed: false),
            ExcludedCount = (int)CobolNumeric.Normalize(Int(row["excludedCount"]), 6, 0, signed: false),
        };
        return await LinkProposalAsync(ctx, pp, "TOTALS");
    }

    /// <summary>ap_reservePaymentProposal - PaymentService.Reserve (PaymentService.vb:125-157) -> APPAYPRP RESERVE (FR-STERP-AP-034).</summary>
    public static Task<IResult> ReserveAsync(OperationContext ctx) => ReservationAsync(ctx, "RESERVE");

    /// <summary>ap_releasePaymentProposal - PaymentService.Release (PaymentService.vb:125-157) -> APPAYPRP RELEASE (FR-STERP-AP-034).</summary>
    public static Task<IResult> ReleaseAsync(OperationContext ctx) => ReservationAsync(ctx, "RELEASE");

    private static async Task<IResult> ReservationAsync(OperationContext ctx, string action)
    {
        var proposalRef = ctx.Route("proposalRef");
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync(
            "SELECT ProposalId, SelectionCutoffDate, PayDate, PaymentMethod, BankAccountCode, CurrencyCode FROM ap.PaymentProposal " +
            "WHERE CompanyCode = @c AND ProposalRef = @r",
            new { c = ctx.Session.CompanyCode, r = proposalRef }, ctx.Aborted);
        if (rows.Count == 0) return ctx.Envelope(new LegacyResult(8, "APP0000", NotHeld));
        var row = (JsonObject)rows[0]!;
        var pp = new PaymentProposalArea
        {
            ProposalRef = CobolText.Trimmed(proposalRef, 30),
            ProposalId = Int(row["proposalId"]),
            CutoffDate = DateInt(row["selectionCutoffDate"]),
            PayDate = DateInt(row["payDate"]),
            Method = CobolText.Trimmed(Str(row["paymentMethod"]), 10),
            BankAccount = CobolText.Trimmed(Str(row["bankAccountCode"]), 20),
            Currency = CobolText.Trimmed(Str(row["currencyCode"]), 3),
        };
        return await LinkProposalAsync(ctx, pp, action);
    }

    private static async Task<IResult> LinkProposalAsync(OperationContext ctx, PaymentProposalArea pp, string function)
    {
        var ca = CaHeader.For(ctx.Session, "APPAYPRP", function);
        await new ApPayPrp(ctx.Get<ISqlRunner>(), new ApSvc(ctx.Services)).RunAsync(ca, pp, ctx.Aborted);
        return ctx.Envelope(ca.Result, pp.ToData());
    }

    /// <summary>ap_requestPaymentApproval - PaymentService.RequestApproval (PaymentService.vb:87-99) -> APPAYAPR REQUEST (FR-STERP-AP-035).</summary>
    public static async Task<IResult> RequestApprovalAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var proposalRef = ctx.Route("proposalRef");
        var pa = new PaymentApprovalArea
        {
            ProposalRef = CobolText.Trimmed(proposalRef, 30),
            DocumentType = "PAYPROPOSAL",
            DocumentRef = CobolText.Trimmed(proposalRef, 40),
            AmountGbp = CobolNumeric.Normalize(InvoiceEndpoints.Decimal(body, "amountGbp") ?? 0m, 15, 2, signed: true),
            RequestedBy = CobolText.Trimmed(ctx.Session.UserCode, 20),
        };
        return await LinkApprovalAsync(ctx, pa, "REQUEST");
    }

    /// <summary>ap_decidePaymentApproval - PaymentService.Decide (PaymentService.vb:101-122) -> APPAYAPR DECIDE (FR-STERP-AP-036).</summary>
    public static async Task<IResult> DecideAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var proposalRef = ctx.Route("proposalRef");
        var decision = InvoiceEndpoints.Text(body, "decision") ?? "";
        var pa = new PaymentApprovalArea();
        var rows = await ctx.Get<IProcedureRunner>().QueryJsonAsync(
            "SELECT ProposalId, ApprovalId FROM ap.PaymentProposal WHERE CompanyCode = @c AND ProposalRef = @r",
            new { c = ctx.Session.CompanyCode, r = proposalRef }, ctx.Aborted);
        if (rows.Count > 0)
        {
            var row = (JsonObject)rows[0]!;
            pa.ProposalId = Int(row["proposalId"]);
            pa.ApprovalId = Int(row["approvalId"]);
        }
        pa.ProposalRef = CobolText.Trimmed(proposalRef, 30);
        pa.DocumentType = "PAYPROPOSAL";
        pa.DocumentRef = CobolText.Trimmed(proposalRef, 40);
        pa.AmountGbp = CobolNumeric.Normalize(InvoiceEndpoints.Decimal(body, "amountGbp") ?? 0m, 15, 2, signed: true);
        pa.Approver = CobolText.Trimmed(ctx.Session.UserCode, 20);
        pa.Decision = CobolText.Trimmed(decision == "APPROVE" ? "APPROVED" : decision == "REJECT" ? "REJECTED" : decision, 10);
        pa.Comment = CobolText.Trimmed(InvoiceEndpoints.Text(body, "comment"), 200);
        return await LinkApprovalAsync(ctx, pa, "DECIDE");
    }

    private static async Task<IResult> LinkApprovalAsync(OperationContext ctx, PaymentApprovalArea pa, string function)
    {
        var ca = CaHeader.For(ctx.Session, "APPAYAPR", function);
        await new ApPayApr(ctx.Get<ISqlRunner>(), new ApSvc(ctx.Services)).RunAsync(ca, pa, ctx.Aborted);
        return ctx.Envelope(ca.Result, pa.ToData());
    }

    /// <summary>ap_createPaymentInstructions - PaymentService.CreateInstructions (PaymentService.vb:160-171) -> APPAYRUN CREATE (FR-STERP-AP-037).</summary>
    public static async Task<IResult> CreateInstructionsAsync(OperationContext ctx)
    {
        var proposalRef = ctx.Route("proposalRef");
        var id = await ctx.Get<IProcedureRunner>().ScalarAsync(
            "SELECT ProposalId FROM ap.PaymentProposal WHERE CompanyCode = @c AND ProposalRef = @r",
            new { c = ctx.Session.CompanyCode, r = proposalRef }, ctx.Aborted);
        var pr = new PaymentRunArea
        {
            ProposalRef = CobolText.Trimmed(proposalRef, 30),
            ProposalId = id is null or DBNull ? 0 : Convert.ToInt32(id, System.Globalization.CultureInfo.InvariantCulture),
        };
        var ca = CaHeader.For(ctx.Session, "APPAYRUN", "CREATE");
        await new ApPayRun(ctx.Get<ISqlRunner>(), new ApSvc(ctx.Services)).RunAsync(ca, pr, ctx.Aborted);
        return ctx.Envelope(ca.Result, pr.ToData());
    }

    private static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString() ?? "";

    private static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : n is JsonValue v2 && v2.TryGetValue<long>(out var l) ? (int)l : 0;

    private static decimal Dec(JsonNode? n) => n is JsonValue v && v.TryGetValue<decimal>(out var d) ? d : 0m;

    private static int DateInt(JsonNode? n)
    {
        var s = Str(n);
        return DateOnly.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? CobolDate.ToYyyymmdd(d)
            : 0;
    }
}
