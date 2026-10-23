using Microsoft.AspNetCore.Http;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Hosting.Pipeline;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Bank;

/// <summary>Bank reconciliation reads (BankService.Accounts / StatementLines / BookLines / Bridge).</summary>
public static class BankReadEndpoints
{
    /// <summary>fin_listBankAccounts: BankService.Accounts (BankService.vb:99-103).</summary>
    public static async Task<IResult> ListAccountsAsync(OperationContext ctx)
    {
        var rows = await ctx.Get<IProcedureRunner>().TableAsync(
            "SELECT BankAccountCode, Name, CurrencyCode, AccountCode, StatementBalance, StatementDate, " +
            "LastReconciledDate, Status FROM fin.BankAccount WHERE CompanyCode = @c ORDER BY BankAccountCode",
            new Dictionary<string, object?> { ["c"] = ctx.Session.CompanyCode }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>fin_listBankStatementLines: BankService.StatementLines (BankService.vb:81-87).</summary>
    public static async Task<IResult> ListStatementLinesAsync(OperationContext ctx)
    {
        var rows = await ctx.Get<IProcedureRunner>().TableAsync(
            "SELECT TOP (300) StatementLineId, EntryDate, ValueDate, StatementRef, LineNum, Description, " +
            "BankReference, EntryType, Amount, CurrencyCode, MatchStatus, MatchedAmount, MatchedBy " +
            "FROM fin.BankStatementLine WHERE CompanyCode = @c AND BankAccountCode = @b " +
            "AND (@s = 'ALL' OR MatchStatus = @s) ORDER BY EntryDate, LineNum",
            new Dictionary<string, object?>
            {
                ["c"] = ctx.Session.CompanyCode,
                ["b"] = ctx.Route("bankAccountCode"),
                ["s"] = ctx.Query("status") ?? "ALL",
            }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>fin_listCashBookLines: BankService.BookLines (BankService.vb:90-96); as-at defaults to the business date.</summary>
    public static async Task<IResult> ListCashBookLinesAsync(OperationContext ctx)
    {
        var asAt = ctx.QueryDate("asAt") ?? ctx.Session.BusinessDate;
        var outstanding = ctx.QueryBool("outstandingOnly") ?? true;
        var rows = await ctx.Get<IProcedureRunner>().TableAsync(
            "SELECT TOP (300) JournalLineId, PostingDate, JournalNumber, SourceModule, SourceRef, Narrative, " +
            "MovementType, SignedGBP, CurrencyCode, ReconMatchId, ReconState FROM fin.vw_CashBook " +
            "WHERE CompanyCode = @c AND BankAccountCode = @b AND PostingDate <= @d " +
            "AND (@o = 0 OR ReconMatchId IS NULL) ORDER BY PostingDate, JournalLineId",
            new Dictionary<string, object?>
            {
                ["c"] = ctx.Session.CompanyCode,
                ["b"] = ctx.Route("bankAccountCode"),
                ["d"] = asAt.ToDateTime(TimeOnly.MinValue),
                ["o"] = outstanding ? 1 : 0,
            }, ctx.Aborted);
        return ctx.List(rows);
    }

    /// <summary>fin_getBankBridge: BankService.Bridge (BankService.vb:28-39) -> BANKREC BRIDGE.</summary>
    public static async Task<IResult> GetBridgeAsync(OperationContext ctx)
    {
        var s = ctx.Session;
        s.LegacyProgram = "BANKREC";
        var asAt = ctx.QueryDate("asAt") ?? s.BusinessDate;
        var br = new BankRecArea
        {
            BankAccount = In.X(ctx.Route("bankAccountCode"), 20),
            AsAtDate = CobolDate.ToYyyymmdd(asAt),
            Currency = In.X(s.CompanyAccess?.BaseCurrency, 3),
        };
        var ca = await ctx.Get<BankReconciliation>().RunAsync(CaRequest.From(s, "BRIDGE"), br, ctx.Aborted);
        return ctx.Envelope(ca.ToResult(), br.BridgeJson());
    }
}
