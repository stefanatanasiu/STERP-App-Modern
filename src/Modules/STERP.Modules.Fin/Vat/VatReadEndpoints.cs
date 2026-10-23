using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Vat;

/// <summary>VAT reads of VatService (VatPeriods, CanAct, LoadReturn, Rate).</summary>
public static class VatReadEndpoints
{
    /// <summary>fin_listVatPeriods: VatService.VatPeriods (VatService.vb:205-208).</summary>
    public static async Task<IResult> ListPeriodsAsync(OperationContext ctx)
    {
        var rows = await ctx.Get<IProcedureRunner>().TableAsync(
            "SELECT VatPeriodKey, StartDate, EndDate, FilingDueDate, PaymentDueDate, Frequency, Status, " +
            "SubmissionRef FROM fin.VatPeriod WHERE CompanyCode = @c ORDER BY StartDate DESC",
            new Dictionary<string, object?> { ["c"] = ctx.Session.CompanyCode }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>fin_getVatReturnAuthorities: the three fresh VatService.CanAct calls (VatService.vb:65-69; frmVatReturn.vb:84-89).</summary>
    public static async Task<IResult> GetAuthoritiesAsync(OperationContext ctx)
    {
        var sql = ctx.Get<IProcedureRunner>();
        var s = ctx.Session;
        var prepare = await sql.CanActAsync(s.UserCode, "FIN.VAT.PREPARE", s.CompanyCode, s.SiteCode, ctx.Aborted);
        var approve = await sql.CanActAsync(s.UserCode, "FIN.VAT.APPROVE", s.CompanyCode, s.SiteCode, ctx.Aborted);
        var submit = await sql.CanActAsync(s.UserCode, "FIN.VAT.SUBMIT", s.CompanyCode, s.SiteCode, ctx.Aborted);
        return ctx.Direct(new JsonObject { ["canPrepare"] = prepare, ["canApprove"] = approve, ["canSubmit"] = submit });
    }

    /// <summary>fin_getSavedVatReturn: VatService.LoadReturn (VatService.vb:72-96).</summary>
    public static async Task<IResult> GetSavedReturnAsync(OperationContext ctx)
    {
        var saved = await LoadReturnAsync(ctx.Get<IProcedureRunner>(), ctx.Session, ctx.Route("vatPeriodKey"), ctx.Aborted);
        return ctx.Direct(saved);
    }

    /// <summary>
    /// VatService.LoadReturn: fail-closed review authority, then the latest version of the return with the period dates and
    /// the distinct detail record and late-entry counts. Refusals are the legacy .NET exceptions (Guard -> SYS0001).
    /// </summary>
    public static async Task<JsonObject> LoadReturnAsync(IProcedureRunner sql, SterpContext s, string periodKey, CancellationToken ct)
    {
        if (!(await sql.CanActAsync(s.UserCode, "FIN.VAT.PREPARE", s.CompanyCode, s.SiteCode, ct) ||
              await sql.CanActAsync(s.UserCode, "FIN.VAT.APPROVE", s.CompanyCode, s.SiteCode, ct) ||
              await sql.CanActAsync(s.UserCode, "FIN.VAT.SUBMIT", s.CompanyCode, s.SiteCode, ct)))
        {
            throw new UnauthorizedAccessException("VAT return review is not permitted for this company/site.");
        }
        var row = await sql.RowAsync(
            "SELECT TOP (1) r.*, p.StartDate, p.EndDate, p.FilingDueDate, " +
            "(SELECT COUNT(DISTINCT d.VatTxnId) FROM fin.VatReturnDetail d WHERE d.VatReturnId=r.VatReturnId) AS Records, " +
            "(SELECT COUNT(DISTINCT d.VatTxnId) FROM fin.VatReturnDetail d WHERE d.VatReturnId=r.VatReturnId AND d.IsLateEntry=1) AS Late " +
            "FROM fin.VatReturn r JOIN fin.VatPeriod p ON p.CompanyCode=r.CompanyCode AND p.VatPeriodKey=r.VatPeriodKey " +
            "WHERE r.CompanyCode=@c AND r.VatPeriodKey=@p ORDER BY r.Version DESC",
            new Dictionary<string, object?> { ["c"] = s.CompanyCode, ["p"] = periodKey }, ct)
            ?? throw new InvalidOperationException("No prepared VAT return exists for " + s.CompanyCode + "/" + periodKey + ".");
        var box5 = row["box5"] is JsonValue v && v.TryGetValue<decimal>(out var b5) ? b5 : 0m;
        row["isRepayment"] = box5 < 0m;
        return row;
    }

    /// <summary>fin_getVatRate: frmVatReturn.OnLookup -> VatService.Rate (VatService.vb:178-187) -> VATDET RATE.</summary>
    public static async Task<IResult> GetRateAsync(OperationContext ctx)
    {
        var s = ctx.Session;
        s.LegacyProgram = "VATDET";
        var vd = new VatDetArea
        {
            VatCode = In.X(ctx.Route("vatCode"), 4),
            TaxPoint = CobolDate.ToYyyymmdd(ctx.QueryDate("taxPoint") ?? s.BusinessDate),
            Direction = "OUTPUT",
        };
        var ca = await ctx.Get<VatDetermination>().RunAsync(CaRequest.From(s, "RATE"), vd, ctx.Aborted);
        return ctx.Envelope(ca.ToResult(), vd.ToJson());
    }
}
