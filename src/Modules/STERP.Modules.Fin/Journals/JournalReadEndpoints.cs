using Microsoft.AspNetCore.Http;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Journals;

public sealed class FinJournalLineValidationRequest
{
    public string? Account { get; set; }
    public string? CostCentre { get; set; }
    public string? Project { get; set; }
    public string? VatCode { get; set; }
    public string? DrCr { get; set; }
    public decimal? Amount { get; set; }
}

/// <summary>Journal enquiry and line validation (JournalService.ValidateLine / Enquiry / Lines).</summary>
public static class JournalReadEndpoints
{
    /// <summary>fin_validateJournalLine: JournalService.ValidateLine (JournalService.vb:32-47) -> GLVALID LINE.</summary>
    public static async Task<IResult> ValidateLineAsync(OperationContext ctx)
    {
        var body = await ctx.BodyAsAsync<FinJournalLineValidationRequest>();
        var s = ctx.Session;
        s.LegacyProgram = "GLVALID";
        var gv = new GlValidArea
        {
            Account = In.X(body.Account, 12),
            PostingDate = CobolDate.ToYyyymmdd(s.BusinessDate),
            DimCc = In.X(body.CostCentre, 20),
            DimProj = In.X(body.Project, 20),
            VatCode = In.X(body.VatCode, 4),
            DrCr = In.X(body.DrCr, 2),
            SourceModule = "FIN",
            ManualEntry = "Y",
            Amount = In.N(body.Amount, 11, 2),
        };
        var ca = await ctx.Get<GlValidator>().RunAsync(CaRequest.From(s, "LINE"), gv, ctx.Aborted);
        return ctx.Envelope(ca.ToResult(), gv.ToJson());
    }

    /// <summary>fin_listJournals: JournalService.Enquiry (JournalService.vb:114-117) -> fin.usp_JournalEnquiry.</summary>
    public static async Task<IResult> ListJournalsAsync(OperationContext ctx)
    {
        var rows = await ctx.Get<IProcedureRunner>().TableAsync("fin.usp_JournalEnquiry", new
        {
            CompanyCode = ctx.Session.CompanyCode,
            JournalNumber = ctx.Query("journalNumber"),
            Status = ctx.Query("status"),
            AccountCode = ctx.Query("account"),
        }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>fin_listJournalLines: JournalService.Lines (JournalService.vb:120-124), no company predicate (BC-FIN-05).</summary>
    public static async Task<IResult> ListJournalLinesAsync(OperationContext ctx)
    {
        if (!long.TryParse(ctx.Route("journalId"), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var id))
        {
            throw Sterp.Platform.Kernel.ApiProblemException.BadBody("Path parameter journalId must be an integer.");
        }
        var rows = await ctx.Get<IProcedureRunner>().TableAsync(
            "SELECT LineNum, AccountCode, DebitAmount, CreditAmount, CurrencyCode, DebitAmountGBP, " +
            "CreditAmountGBP, DimCostCentre, DimProject, VatCode, SubledgerRef, Narrative " +
            "FROM fin.JournalLine WHERE JournalId = @j ORDER BY LineNum",
            new Dictionary<string, object?> { ["j"] = id }, ctx.Aborted);
        return ctx.List(rows);
    }
}
