using Microsoft.AspNetCore.Http;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Fin.Bank;

public sealed class FinBankEntryRef
{
    public decimal? LineId { get; set; }
    public decimal? Amount { get; set; }
}

public sealed class FinBankMatchRequest
{
    public string? Narrative { get; set; }
    public List<FinBankEntryRef>? StatementEntries { get; set; }
    public List<FinBankEntryRef>? BookEntries { get; set; }
}

public sealed class FinBankUnmatchRequest
{
    public string? Reason { get; set; }
}

/// <summary>BankService.Match / Unmatch (BankService.vb:51-78) -> BANKREC MATCH / UNMATCH.</summary>
public static class BankMatchEndpoints
{
    private static BankRecArea Area(OperationContext ctx) => new()
    {
        BankAccount = In.X(ctx.Route("bankAccountCode"), 20),
        AsAtDate = CobolDate.ToYyyymmdd(ctx.Session.BusinessDate),
        Currency = In.X(ctx.Session.CompanyAccess?.BaseCurrency, 3),
    };

    /// <summary>fin_matchBankEntries.</summary>
    public static async Task<IResult> MatchAsync(OperationContext ctx)
    {
        var body = await ctx.BodyAsAsync<FinBankMatchRequest>();
        ctx.Session.LegacyProgram = "BANKREC";
        var br = Area(ctx);
        br.Narrative = In.X(body.Narrative, 200);
        foreach (var e in (body.StatementEntries ?? []).Take(20))
        {
            br.Statement.Add(((int)In.N(e.LineId, 9, 0), In.N(e.Amount, 11, 2)));
        }
        foreach (var e in (body.BookEntries ?? []).Take(20))
        {
            br.Book.Add(((int)In.N(e.LineId, 9, 0), In.N(e.Amount, 11, 2)));
        }
        var ca = await ctx.Get<BankMatchProgram>().RunAsync(CaRequest.From(ctx.Session, "MATCH"), br, ctx.Aborted);
        return ctx.Envelope(ca.ToResult(), BankMatchProgram.MatchJson(br));
    }

    /// <summary>fin_unmatchBankGroup.</summary>
    public static async Task<IResult> UnmatchAsync(OperationContext ctx)
    {
        var body = await ctx.BodyAsAsync<FinBankUnmatchRequest>();
        ctx.Session.LegacyProgram = "BANKREC";
        var br = Area(ctx);
        br.MatchId = (int)In.N(Periods.PeriodEndpoints.RouteLong(ctx, "matchId"), 9, 0);
        br.Reason = In.X(body.Reason, 200);
        var ca = await ctx.Get<BankMatchProgram>().RunAsync(CaRequest.From(ctx.Session, "UNMATCH"), br, ctx.Aborted);
        return ctx.Envelope(ca.ToResult(), BankMatchProgram.UnmatchJson(br));
    }
}
