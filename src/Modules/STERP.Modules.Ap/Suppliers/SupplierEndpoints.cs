using Microsoft.AspNetCore.Http;
using Sterp.Modules.Ap.Engine;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Ap.Suppliers;

/// <summary>SupplierService COBOL-backed members (dotnet/STERP.Business/Creditors/SupplierService.vb).</summary>
public static class SupplierEndpoints
{
    /// <summary>ap_validateSupplier - SupplierService.Validate + ReadSupplier -> APSUPVAL VALIDATE (FR-STERP-AP-012).</summary>
    public static Task<IResult> ValidateAsync(OperationContext ctx) => SupplierCheckAsync(ctx, "VALIDATE");

    /// <summary>ap_checkSupplierCanPay - SupplierService.PaymentCheck + ReadSupplier -> APSUPVAL CANPAY (FR-STERP-AP-013).</summary>
    public static Task<IResult> CanPayAsync(OperationContext ctx) => SupplierCheckAsync(ctx, "CANPAY");

    private static async Task<IResult> SupplierCheckAsync(OperationContext ctx, string function)
    {
        var ca = CaHeader.For(ctx.Session, "APSUPVAL", function);
        var sv = new SupplierValidationArea { Supplier = CobolText.Trimmed(ctx.Route("supplierCode"), 20) };
        await new ApSupVal(ctx.Get<ISqlRunner>()).RunAsync(ca, sv, ctx.Aborted);
        return ctx.Envelope(ca.Result, sv.ToData());
    }

    /// <summary>ap_prepareSupplierBank - SupplierService.PrepareBank (SupplierService.vb:23-38) -> APBANKVR PREPARE (FR-STERP-AP-014).</summary>
    public static async Task<IResult> PrepareBankAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var bv = new BankVerificationArea();
        var invalid = SetBankInputs(bv,
            ("BV-SUPPLIER", ctx.Route("supplierCode")), ("BV-BANK-REF", Text(body, "bankRef")), ("BV-BENEFICIARY", Text(body, "beneficiary")),
            ("BV-COUNTRY", "GB"), ("BV-CURRENCY", "GBP"), ("BV-SORT-CODE", Text(body, "sortCode")), ("BV-ACCOUNT-NO", Text(body, "accountNo")),
            ("BV-BANK-NAME", Text(body, "bankName")), ("BV-PAY-METHOD", "BACS"), ("BV-REQUEST-SOURCE", Text(body, "requestSource")));
        if (invalid is not null) return ctx.Envelope(invalid);
        bv.Set("BV-PREPARED-BY", ctx.Session.UserCode);
        return await LinkBankAsync(ctx, bv, "PREPARE");
    }

    /// <summary>ap_recordBankConfirmationOfPayee - SupplierService.ConfirmationOfPayee (SupplierService.vb:82-95) -> APBANKVR COP (FR-STERP-AP-015).</summary>
    public static async Task<IResult> ConfirmationOfPayeeAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var bv = new BankVerificationArea();
        var invalid = SetBankInputs(bv,
            ("BV-SUPPLIER", ctx.Route("supplierCode")), ("BV-COP-RESULT", Text(body, "copResult") ?? ""),
            ("BV-COP-REFERENCE", Text(body, "copReference") ?? ""), ("BV-COP-DECISION", Text(body, "copDecision") ?? ""));
        if (invalid is not null) return ctx.Envelope(invalid);
        bv.BankId = BankId(ctx);
        return await LinkBankAsync(ctx, bv, "COP");
    }

    /// <summary>ap_recordBankCallback - SupplierService.RecordCallback (SupplierService.vb:97-110) -> APBANKVR CALLBACK (FR-STERP-AP-016).</summary>
    public static async Task<IResult> RecordCallbackAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var bv = new BankVerificationArea();
        var invalid = SetBankInputs(bv,
            ("BV-SUPPLIER", ctx.Route("supplierCode")), ("BV-CALLBACK-SOURCE", Text(body, "callbackSource")),
            ("BV-CALLBACK-NOTE", Text(body, "callbackNote")));
        if (invalid is not null) return ctx.Envelope(invalid);
        bv.BankId = BankId(ctx);
        bv.Set("BV-CALLBACK-ON-FILE", string.Equals(Text(body, "callbackOnFile"), "Y", StringComparison.Ordinal) ? "Y" : "N");
        bv.Set("BV-CALLBACK-BY", ctx.Session.UserCode);
        return await LinkBankAsync(ctx, bv, "CALLBACK");
    }

    /// <summary>ap_approveSupplierBank - SupplierService.ApproveBank (SupplierService.vb:133-143) -> APBANKVR APPROVE (FR-STERP-AP-017).</summary>
    public static async Task<IResult> ApproveBankAsync(OperationContext ctx)
    {
        var body = await ctx.BodyObjectAsync();
        var decision = Text(body, "decision") ?? "APPROVE";
        var bv = new BankVerificationArea();
        bv.Set("BV-SUPPLIER", ctx.Route("supplierCode"));
        bv.BankId = BankId(ctx);
        bv.Set("BV-APPROVER", ctx.Session.UserCode);
        bv.Set("BV-DECISION", decision == "APPROVE" ? "APPROVED" : decision == "REJECT" ? "REJECTED" : decision);
        return await LinkBankAsync(ctx, bv, "APPROVE");
    }

    private static async Task<IResult> LinkBankAsync(OperationContext ctx, BankVerificationArea bv, string function)
    {
        var ca = CaHeader.For(ctx.Session, "APBANKVR", function);
        await new ApBankVr(ctx.Get<ISqlRunner>(), new ApSvc(ctx.Services)).RunAsync(ca, bv, ctx.Aborted);
        return ctx.Envelope(ca.Result, bv.ToData());
    }

    /// <summary>
    /// SupplierService.SetBankInputs (SupplierService.vb:112-128): every value is checked against its COMMAREA width before any is
    /// moved; the first over-length value refuses with RC 8 APBINPUT (label "Bank reference" for BV-BANK-REF, else the field name).
    /// </summary>
    private static LegacyResult? SetBankInputs(BankVerificationArea bv, params (string Field, string? Value)[] pairs)
    {
        foreach (var (field, value) in pairs)
        {
            var width = BankVerificationArea.Widths[field];
            if ((value ?? "").Length > width)
            {
                var label = field == "BV-BANK-REF" ? "Bank reference" : field;
                return new LegacyResult(8, "APBINPUT",
                    label + " must be at most " + width.ToString(System.Globalization.CultureInfo.InvariantCulture) + " characters. Nothing was recorded.");
            }
        }
        foreach (var (field, value) in pairs) bv.Set(field, value);
        return null;
    }

    private static int BankId(OperationContext ctx) =>
        int.TryParse(ctx.Route("supplierBankId"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id)
            ? id
            : throw ApiProblemException.BadBody("supplierBankId must be an integer.");

    private static string? Text(System.Text.Json.Nodes.JsonObject body, string name) =>
        body.TryGetPropertyValue(name, out var node) && node is System.Text.Json.Nodes.JsonValue v
            ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString())
            : null;
}
