using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Bank;

/// <summary>BANKREC MATCH and UNMATCH (BANKREC.cob:228-584).</summary>
public sealed class BankMatchProgram(ISqlRunner sql, IFinKernel kernel)
{
    private const string Program = "BANKREC";

    public static JsonObject MatchJson(BankRecArea br) => new()
    {
        ["matchId"] = br.MatchId,
        ["matchRef"] = br.MatchRef,
        ["matchType"] = br.MatchType,
        ["stmtTotal"] = CobolNumeric.WithScale(br.StmtTotal, 2),
        ["bookTotal"] = CobolNumeric.WithScale(br.BookTotal, 2),
        ["difference"] = CobolNumeric.WithScale(br.Difference, 2),
        ["matchedCnt"] = br.MatchedCnt,
    };

    public static JsonObject UnmatchJson(BankRecArea br) => new()
    {
        ["matchId"] = br.MatchId,
        ["bankAccount"] = br.BankAccount,
    };

    public async Task<CaHeader> RunAsync(CaRequest req, BankRecArea br, CancellationToken ct = default)
    {
        var ca = new CaHeader("FIN-014");
        // 0100-INITIALISE
        br.StmtTotal = br.BookTotal = br.Difference = 0;
        br.BookBalance = br.StmtBalance = br.OutReceipts = br.OutPayments = br.BridgedBalance = br.Unexplained = 0;
        br.UnmatchedCnt = br.MatchedCnt = 0;
        br.MatchType = br.JournalNumber = br.ReconStatus = "";
        if (br.AsAtDate == 0) br.AsAtDate = req.BusinessDate;
        if (CobolText.IsSpaces(br.Currency)) br.Currency = "GBP";
        if (br.Statement.Count > 20) br.Statement.RemoveRange(20, br.Statement.Count - 20);
        if (br.Book.Count > 20) br.Book.RemoveRange(20, br.Book.Count - 20);
        // 0200-EDIT-REQUEST
        if (CobolText.IsSpaces(req.Company))
        {
            ca.Reject("BNK0001", "A company is required");
            return ca;
        }
        if (CobolText.IsSpaces(br.BankAccount))
        {
            ca.Reject("BNK0002", "A bank account code is required");
            return ca;
        }
        switch (req.Function)
        {
            case "MATCH":
                await MatchAsync(req, ca, br, ct);
                break;
            case "UNMATCH":
                await UnmatchAsync(req, ca, br, ct);
                break;
            default:
                ca.Reject("BNK0009", "BANKREC does not support function " + req.Function.Trim());
                break;
        }
        return ca;
    }

    // 2000-MATCH
    private async Task MatchAsync(CaRequest req, CaHeader ca, BankRecArea br, CancellationToken ct)
    {
        if (!await AuthorityAsync(req, ca, ct)) return;
        if (br.Statement.Count == 0 || br.Book.Count == 0)
        {
            ca.Reject("BNK0020", "A match needs at least one statement entry and on");
            return;
        }
        br.StmtTotal = CobolNumeric.Store(br.Statement.Sum(s => s.Amount), 13, 2);
        br.BookTotal = CobolNumeric.Store(br.Book.Sum(b => b.Amount), 13, 2);
        br.Difference = CobolNumeric.Store(br.StmtTotal - br.BookTotal, 13, 2);
        if (br.Difference != 0 && CobolText.IsSpaces(br.Narrative))
        {
            ca.Reject("BNK0021", "The grouped match is out by " + FinEdit.Money(br.Difference) + " GBP. Unequal totals need a written explanation");
            return;
        }
        br.MatchType = br.Difference == 0
            ? br.Statement.Count == 1 && br.Book.Count == 1 ? "EXACT" : "GROUPED"
            : "EXPLAINED";
        if (!await CheckFreeAsync(ca, br)) return;
        if (CobolText.IsSpaces(br.MatchRef))
        {
            br.MatchRef = CobolText.Trimmed("BRM" + req.Company.Trim() + "-" + D8(br.AsAtDate) + "-" + N3(br.Statement.Count) + N3(br.Book.Count), 30);
        }
        var unit = new OwnedUnit(sql);
        await unit.BeginAsync(ct);
        await WriteGroupAsync(req, ca, br);
        if (ca.Failed)
        {
            await unit.RollbackAsync(ct);
            return;
        }
        for (var i = 0; i < br.Statement.Count && !ca.Failed; i++)
        {
            await AddStatementAsync(req, ca, br, br.Statement[i]);
        }
        for (var i = 0; i < br.Book.Count && !ca.Failed; i++)
        {
            await AddBookAsync(req, ca, br, br.Book[i]);
        }
        if (ca.Failed)
        {
            await unit.RollbackAsync(ct);
            return;
        }
        await unit.CommitAsync(ct);
        br.MatchedCnt = (br.Statement.Count + br.Book.Count) % 1000;
        await AuditAsync(req, br, ct);
        ca.Message = "Matched " + FinEdit.Money(br.StmtTotal) + " across " + N3(br.Statement.Count) + " statement and " +
                     N3(br.Book.Count) + " book entries";
    }

    // 2100-CHECK-FREE: the count text of the misaligned row is read with the legacy NUMVAL (BR-BANKREC-019)
    private async Task<bool> CheckFreeAsync(CaHeader ca, BankRecArea br)
    {
        foreach (var (lineId, _) in br.Statement)
        {
            if (await MemberTakenAsync(ca,
                    "SELECT CAST(COUNT(*) AS varchar(10)), '0' FROM fin.BankReconMatchLine ml JOIN fin.BankReconMatch m ON " +
                    "m.MatchId = ml.MatchId AND m.Status = 'MATCHED' WHERE ml.StatementLineId = @p1", lineId))
            {
                ca.Reject("BNK0022", "That statement entry is already reconcile");
                return false;
            }
            if (ca.Failed) return false;
        }
        foreach (var (lineId, _) in br.Book)
        {
            if (await MemberTakenAsync(ca,
                    "SELECT CAST(COUNT(*) AS varchar(10)), '0' FROM fin.BankReconMatchLine ml JOIN fin.BankReconMatch m ON " +
                    "m.MatchId = ml.MatchId AND m.Status = 'MATCHED' WHERE ml.JournalLineId = @p1", lineId))
            {
                ca.Reject("BNK0023", "That book entry is already reconciled in ");
                return false;
            }
            if (ca.Failed) return false;
        }
        return true;
    }

    // 2150-RUN-COUNT: WS-K-COUNT X(18) receives the 10-character count slot followed by the first 8 of the next slot
    private async Task<bool> MemberTakenAsync(CaHeader ca, string text, int lineId)
    {
        object?[]? row;
        try
        {
            row = await sql.SingleAsync(text, lineId.ToString(CultureInfo.InvariantCulture));
        }
        catch (SqlioException ex)
        {
            ca.SqlFailed(ex, Program);
            return false;
        }
        if (row is null) return false;
        var buffer = CobolText.Slot(row[0], 10) + CobolText.Slot(row[1], 18);
        var count = buffer[..18];
        return !CobolText.IsSpaces(count) && Slot.NumVal(count) > 0;
    }

    // 2200-WRITE-GROUP
    private async Task WriteGroupAsync(CaRequest req, CaHeader ca, BankRecArea br)
    {
        object?[]? row;
        try
        {
            row = await sql.SingleAsync(
                "INSERT fin.BankReconMatch (CompanyCode, BankAccountCode, ReconId, MatchRef, MatchType, StatementTotal, BookTotal, " +
                "DifferenceAmount, CurrencyCode, MatchDate, Explanation, CreatedBy) OUTPUT inserted.MatchId, '0' VALUES (@p1, @p2, " +
                "NULLIF(CAST(@p3 AS int),0), @p4, @p5, @p6, @p7, @p8, @p9, @p10, NULLIF(@p11,''), @p12)",
                req.Company, br.BankAccount, "0", br.MatchRef, br.MatchType, br.StmtTotal, br.BookTotal, br.Difference, br.Currency,
                D8(br.AsAtDate), br.Narrative, req.User);
        }
        catch (SqlioException ex)
        {
            ca.SqlFailed(ex, Program);
            return;
        }
        // BC-FIN-01: the match id comes from this call's OUTPUT row; a missing row is RC 12 SQL0001
        if (row is null)
        {
            ca.Set(12, "SQL0001", "SQL failure in " + Program);
            return;
        }
        br.MatchId = (int)CobolNumeric.Store(Slot.NumVal(CobolText.Slot(row[0], 18)), 9, 0);
    }

    // 2300-ADD-STATEMENT
    private async Task AddStatementAsync(CaRequest req, CaHeader ca, BankRecArea br, (int LineId, decimal Amount) st)
    {
        try
        {
            await sql.ExecAsync(
                "INSERT fin.BankReconMatchLine (MatchId, CompanyCode, SideType, StatementLineId, Amount, CurrencyCode) VALUES " +
                "(@p1, @p2, 'STATEMENT', @p3, @p4, @p5)",
                br.MatchId.ToString(CultureInfo.InvariantCulture), req.Company, st.LineId.ToString(CultureInfo.InvariantCulture), st.Amount, br.Currency);
        }
        catch (SqlioException ex)
        {
            ca.SqlFailed(ex, Program);
            return;
        }
        try
        {
            await sql.ExecAsync(
                "UPDATE fin.BankStatementLine SET MatchStatus = 'MATCHED', MatchedAmount = Amount, ReconId = " +
                "COALESCE(NULLIF(CAST(@p1 AS int),0), ReconId), MatchedAt = SYSDATETIME(), MatchedBy = @p2 WHERE StatementLineId = @p3",
                "0", req.User, st.LineId.ToString(CultureInfo.InvariantCulture));
        }
        catch (SqlioException ex)
        {
            ca.SqlFailed(ex, Program);
        }
    }

    // 2400-ADD-BOOK
    private async Task AddBookAsync(CaRequest req, CaHeader ca, BankRecArea br, (int LineId, decimal Amount) bk)
    {
        try
        {
            await sql.ExecAsync(
                "INSERT fin.BankReconMatchLine (MatchId, CompanyCode, SideType, JournalLineId, Amount, CurrencyCode) VALUES " +
                "(@p1, @p2, 'BOOK', @p3, @p4, @p5)",
                br.MatchId.ToString(CultureInfo.InvariantCulture), req.Company, bk.LineId.ToString(CultureInfo.InvariantCulture), bk.Amount, br.Currency);
        }
        catch (SqlioException ex)
        {
            ca.SqlFailed(ex, Program);
            return;
        }
        try
        {
            await sql.ExecAsync("UPDATE fin.JournalLine SET ReconMatchId = @p1 WHERE JournalLineId = @p2",
                br.MatchId.ToString(CultureInfo.InvariantCulture), bk.LineId.ToString(CultureInfo.InvariantCulture));
        }
        catch (SqlioException ex)
        {
            ca.SqlFailed(ex, Program);
        }
    }

    // 3000-UNMATCH
    private async Task UnmatchAsync(CaRequest req, CaHeader ca, BankRecArea br, CancellationToken ct)
    {
        if (!await AuthorityAsync(req, ca, ct)) return;
        if (CobolText.IsSpaces(br.Reason))
        {
            ca.Reject("BNK0030", "Reopening a match needs a recorded reason");
            return;
        }
        var unit = new OwnedUnit(sql);
        await unit.BeginAsync(ct);
        var id = br.MatchId.ToString(CultureInfo.InvariantCulture);
        if (!await RunOneAsync(ca,
                "UPDATE fin.BankStatementLine SET MatchStatus = 'UNMATCHED', MatchedAmount = 0, MatchedAt = NULL, MatchedBy = NULL " +
                "WHERE StatementLineId IN (SELECT ml.StatementLineId FROM fin.BankReconMatchLine ml WHERE ml.MatchId = @p1 AND " +
                "ml.SideType = 'STATEMENT')", id))
        {
            await unit.RollbackAsync(ct);
            return;
        }
        if (!await RunOneAsync(ca,
                "UPDATE fin.JournalLine SET ReconMatchId = NULL WHERE JournalLineId IN (SELECT ml.JournalLineId FROM " +
                "fin.BankReconMatchLine ml WHERE ml.MatchId = @p1 AND ml.SideType = 'BOOK')", id))
        {
            await unit.RollbackAsync(ct);
            return;
        }
        try
        {
            await sql.ExecAsync(
                "UPDATE fin.BankReconMatch SET Status = 'REVERSED', ReversedBy = @p1, ReversedAt = SYSDATETIME(), ReverseReason = @p2 " +
                "WHERE MatchId = @p3 AND Status = 'MATCHED'", req.User, br.Reason, id);
        }
        catch (SqlioException ex)
        {
            ca.SqlFailed(ex, Program);
            await unit.RollbackAsync(ct);
            return;
        }
        await unit.CommitAsync(ct);
        await AuditAsync(req, br, ct);
        ca.Message = "The match is reopened and audited. The statement evid";
    }

    private async Task<bool> RunOneAsync(CaHeader ca, string text, string id)
    {
        try
        {
            await sql.ExecAsync(text, id);
            return true;
        }
        catch (SqlioException ex)
        {
            ca.SqlFailed(ex, Program);
            return false;
        }
    }

    // 8100-AUTHORITY (FIN.BANK.RECONCILE)
    private async Task<bool> AuthorityAsync(CaRequest req, CaHeader ca, CancellationToken ct)
    {
        var kh = await kernel.PermCheckAsync(req.Callee("CHECK", site: true, correlation: false), "FIN.BANK.RECONCILE", ct);
        if (kh.ReturnCode <= 4) return true;
        ca.ReasonCode = kh.ReasonCode;
        ca.Message = kh.Message;
        ca.ReturnCode = kh.ReturnCode;
        ca.MsgCount++;
        return false;
    }

    // 8200-AUDIT (keyed by BR-MATCH-REF)
    private Task AuditAsync(CaRequest req, BankRecArea br, CancellationToken ct) =>
        kernel.AuditAsync(req.Callee("WRITE", site: false, correlation: false), new AuditEntry(
            EntityType: "fin.BankReconMatch",
            EntityKey: br.MatchRef,
            Action: req.Function,
            NewValue: FinEdit.MoneyField(br.StmtTotal),
            Reason: br.Reason,
            Source: "COBOL"), ct);

    private static string D8(int yyyymmdd) => (yyyymmdd % 100_000_000).ToString("00000000", CultureInfo.InvariantCulture);

    private static string N3(int n) => (n % 1000).ToString("000", CultureInfo.InvariantCulture);
}
