using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Ar.Application;
using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.Ar.Domain;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Endpoints;

/// <summary>
/// The DebtorsService engine members (dotnet/STERP.Business/Debtors/DebtorsService.vb:60-219): each fills a COMMAREA
/// exactly as the facade did, applies the contracts 4.4 input normalization and links the AR engine; the response is the
/// outcome envelope with the COMMAREA body as data (contracts 4.6).
/// </summary>
public static class EngineEndpoints
{
    /// <summary>CobolGateway.PrepareHeader with the request context; the correlation id is the Idempotency-Key (or a new one).</summary>
    private static void Prepare(Commarea area, OperationContext ctx, string program, string function) =>
        area.Header.PrepareLink(ctx.Session, program, function);

    private static int BusinessDate(OperationContext ctx) => CobolDate.ToYyyymmdd(ctx.Session.BusinessDate);

    /// <summary>Envelope (RC 0/4/6 with data) or problem; <paramref name="dataOnProblem"/> when the legacy page read the area after a refusal.</summary>
    private static IResult Outcome(OperationContext ctx, LegacyResult r, Commarea area, bool dataOnProblem = false) =>
        ctx.Envelope(r, r.ReturnCode is 0 or 4 or 6 || dataOnProblem ? area.ToJson() : null);

    // ------------------------------------------------------------------ ARENQ

    /// <summary>ar_getCustomerMetrics: DebtorsService.Metrics -> ARENQ METRICS (DebtorsService.vb:62-71).</summary>
    public static async Task<IResult> GetCustomerMetricsAsync(OperationContext ctx)
    {
        var asOf = ctx.QueryDate("asAt") ?? ctx.Session.BusinessDate;
        var eq = new ArenqArea
        {
            Customer = ctx.Route("customerCode"),
            AsAt = CobolDate.ToYyyymmdd(asOf),
            FromDate = CobolDate.ToYyyymmdd(asOf.AddMonths(-12)),
            ToDate = CobolDate.ToYyyymmdd(asOf),
        };
        Prepare(eq, ctx, "ARENQ", "METRICS");
        CommareaCodec.Normalize(eq);
        ctx.Session.LegacyProgram = "ARENQ";
        var r = await ctx.Get<CustomerMetricsEngine>().CallAsync(eq, ctx.Aborted);
        return Outcome(ctx, r, eq);
    }

    // ------------------------------------------------------------------ CREDCHK

    /// <summary>ar_checkCustomerCredit: DebtorsService.CreditCheck -> CREDCHK CHECK (DebtorsService.vb:76-88).</summary>
    public static async Task<IResult> CheckCustomerCreditAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var checkPoint = Req.Str(body, "checkPoint");
        var currency = Req.Str(body, "currency");
        var ca = new CredchkArea
        {
            Customer = ctx.Route("customerCode"),
            CheckPoint = checkPoint.Length == 0 ? "ORDER" : checkPoint,
            DocumentRef = Req.Str(body, "documentRef"),
            EvalDate = BusinessDate(ctx),
            Currency = currency.Length == 0 ? ctx.Session.CompanyAccess?.BaseCurrency ?? "" : currency,
            OrderValue = Req.Dec(body, "orderValue"),
        };
        Prepare(ca, ctx, "CREDCHK", CreditCheckFunctions.Check);
        CommareaCodec.Normalize(ca);
        ctx.Session.LegacyProgram = "CREDCHK";
        var r = await ctx.Get<ICreditCheckService>().CallAsync(ca, ctx.Aborted);
        return Outcome(ctx, r, ca);
    }

    /// <summary>ar_requestCreditOverride: DebtorsService.RequestCreditOverride -> CREDCHK OVERRIDE (DebtorsService.vb:98-111).</summary>
    public static async Task<IResult> RequestCreditOverrideAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var ca = new CredchkArea
        {
            Customer = Req.Str(body, "customer"),
            CheckPoint = "ORDER",
            DocumentRef = Req.Str(body, "documentRef"),
            Currency = "GBP",
            EvalDate = BusinessDate(ctx),
            OverrideScope = "ORDER",
            OverrideAmt = Req.Dec(body, "overrideAmt"),
            OverrideReason = Req.Str(body, "overrideReason"),
        };
        Prepare(ca, ctx, "CREDCHK", CreditCheckFunctions.Override);
        CommareaCodec.Normalize(ca);
        ctx.Session.LegacyProgram = "CREDCHK";
        var r = await ctx.Get<ICreditCheckService>().CallAsync(ca, ctx.Aborted);
        return Outcome(ctx, r, ca);
    }

    /// <summary>ar_approveCreditOverride: DebtorsService.ApproveCreditOverride -> CREDCHK APPROVE (DebtorsService.vb:113-120).</summary>
    public static async Task<IResult> ApproveCreditOverrideAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var ca = new CredchkArea
        {
            Customer = Req.Str(body, "customer"),
            EvalDate = BusinessDate(ctx),
            OverrideId = RouteInt(ctx, "overrideId"),
        };
        Prepare(ca, ctx, "CREDCHK", CreditCheckFunctions.Approve);
        CommareaCodec.Normalize(ca);
        ctx.Session.LegacyProgram = "CREDCHK";
        var r = await ctx.Get<ICreditCheckService>().CallAsync(ca, ctx.Aborted);
        return Outcome(ctx, r, ca);
    }

    private static int RouteInt(OperationContext ctx, string name) =>
        int.TryParse(ctx.Route(name), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : throw ApiProblemException.BadBody($"Path parameter {name} must be an integer.");

    // ------------------------------------------------------------------ RCPTALOC

    /// <summary>DebtorsService.NewReceipt (DebtorsService.vb:124-145).</summary>
    private static RcptalocArea NewReceipt(OperationContext ctx, JsonObject body)
    {
        var customer = Req.Str(body, "customer");
        var receiptDate = Req.Date(body, "receiptDate");
        return new RcptalocArea
        {
            Customer = customer,
            Payer = customer,
            Method = Req.Str(body, "method"),
            BankAccount = Req.Str(body, "bankAccount"),
            ReceiptDate = receiptDate,
            ValueDate = receiptDate,
            PostingDate = BusinessDate(ctx),
            EffectiveDate = receiptDate,
            Currency = Req.Str(body, "currency"),
            Gross = Req.Dec(body, "gross"),
            BankRef = Req.Str(body, "bankRef"),
            ExternalRef = Req.Str(body, "externalRef"),
            SourceType = "RECEIPT",
            ConfirmState = "CONFIRMED",
            AllocCnt = 0,
        };
    }

    /// <summary>DebtorsService.AddAllocation (DebtorsService.vb:147-166) for each line in grid order.</summary>
    private static void AddAllocations(RcptalocArea ra, JsonObject body)
    {
        var lines = Req.Objects(body, "lines");
        for (var i = 0; i < lines.Count; i++)
        {
            if (i < RcptalocArea.MaxAllocs)
            {
                var a = ra.Allocs[i];
                a.AItemId = Req.Int(lines[i], "openItemId");
                a.ADocNo = Req.Str(lines[i], "documentNumber");
                a.ACurrency = Req.Str(lines[i], "currency");
                a.AApply = Req.Dec(lines[i], "apply");
                a.AWriteoff = Req.Dec(lines[i], "writeOff");
                a.AWoReason = Req.Str(lines[i], "writeOffReason");
            }
            ra.AllocCnt = i + 1;
        }
    }

    /// <summary>ar_recordReceipt: NewReceipt + RunReceipt RECEIPT with the Idempotency-Key as correlation id.</summary>
    public static async Task<IResult> RecordReceiptAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var ra = NewReceipt(ctx, body);
        return await RunReceiptAsync(ctx, ra, "RECEIPT", dataOnProblem: true);
    }

    /// <summary>ar_previewAllocation: NewReceipt + AddAllocation + RunReceipt PREVIEW (no business effect).</summary>
    public static async Task<IResult> PreviewAllocationAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var ra = NewReceipt(ctx, body);
        ra.SourceItemId = Req.Int(body, "sourceItemId");
        ra.SourceDoc = Req.Str(body, "sourceDoc");
        AddAllocations(ra, body);
        return await RunReceiptAsync(ctx, ra, "PREVIEW", dataOnProblem: true);
    }

    /// <summary>ar_allocateReceipt: the named source (as ar_recordReceipt returned it) + AddAllocation + RunReceipt ALLOCATE.</summary>
    public static async Task<IResult> AllocateReceiptAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var ra = new RcptalocArea
        {
            SourceItemId = Req.Int(body, "sourceItemId"),
            SourceDoc = Req.Str(body, "sourceDoc"),
            ReceiptDate = Req.Date(body, "receiptDate"),
            EffectiveDate = Req.Date(body, "effectiveDate"),
            PostingDate = BusinessDate(ctx),
            SourceType = "RECEIPT",
            ConfirmState = "CONFIRMED",
        };
        AddAllocations(ra, body);
        return await RunReceiptAsync(ctx, ra, "ALLOCATE", dataOnProblem: true);
    }

    /// <summary>ar_unallocateAllocation: DebtorsService.CorrectAllocation -> RCPTALOC UNALLOC (DebtorsService.vb:177-184).</summary>
    public static async Task<IResult> UnallocateAllocationAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var ra = new RcptalocArea
        {
            AllocId = RouteInt(ctx, "allocationId"),
            CorrectReason = Req.Str(body, "correctReason"),
            CorrectDate = BusinessDate(ctx),
        };
        return await RunReceiptAsync(ctx, ra, "UNALLOC", dataOnProblem: false);
    }

    private static async Task<IResult> RunReceiptAsync(OperationContext ctx, RcptalocArea ra, string function, bool dataOnProblem)
    {
        Prepare(ra, ctx, "RCPTALOC", function);
        CommareaCodec.Normalize(ra);
        ctx.Session.LegacyProgram = "RCPTALOC";
        var r = await ctx.Get<ReceiptAllocationEngine>().CallAsync(ra, ctx.Aborted);
        return Outcome(ctx, r, ra, dataOnProblem);
    }

    // ------------------------------------------------------------------ ARDISPUT

    /// <summary>ar_raiseDispute: DebtorsService.RaiseDispute -> ARDISPUT DISPUTE (DebtorsService.vb:188-202).</summary>
    public static async Task<IResult> RaiseDisputeAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var ds = new ArdisputArea
        {
            Customer = Req.Str(body, "customer"),
            CaseType = "DISPUTE",
            DocumentNo = Req.Str(body, "documentNo"),
            OpenItemId = Req.Int(body, "openItemId"),
            ReasonCode = Req.Str(body, "reasonCode"),
            Reason = Req.Str(body, "reason"),
            DisputedAmt = Req.Dec(body, "disputedAmt"),
            OwnerUser = ctx.Session.UserCode,
        };
        return await RunDisputeAsync(ctx, ds, "DISPUTE");
    }

    /// <summary>ar_recordPromiseToPay: DebtorsService.RecordPromise -> ARDISPUT PROMISE (DebtorsService.vb:204-219).</summary>
    public static async Task<IResult> RecordPromiseToPayAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var ds = new ArdisputArea
        {
            Customer = Req.Str(body, "customer"),
            CaseRef = Req.Str(body, "caseRef"),
            CaseType = "COLLECTION",
            ActionType = "PROMISE",
            ActionDate = BusinessDate(ctx),
            PromiseAmt = Req.Dec(body, "promiseAmt"),
            PromiseDate = Req.Date(body, "promiseDate"),
            Narrative = Req.Str(body, "narrative"),
            OwnerUser = ctx.Session.UserCode,
        };
        return await RunDisputeAsync(ctx, ds, "PROMISE");
    }

    private static async Task<IResult> RunDisputeAsync(OperationContext ctx, ArdisputArea ds, string function)
    {
        Prepare(ds, ctx, "ARDISPUT", function);
        CommareaCodec.Normalize(ds);
        ctx.Session.LegacyProgram = "ARDISPUT";
        var r = await ctx.Get<DisputeEngine>().CallAsync(ds, ctx.Aborted);
        return Outcome(ctx, r, ds);
    }
}
