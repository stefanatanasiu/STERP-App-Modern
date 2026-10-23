using System.Text.Json.Nodes;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Bank;

/// <summary>BANKRECCA body fields used by the exposed functions (cobol/copy/BANKRECCA.cpy).</summary>
public sealed class BankRecArea
{
    public string BankAccount { get; set; } = "";
    public int AsAtDate { get; set; }
    public string Currency { get; set; } = "";
    public string Narrative { get; set; } = "";
    public string Reason { get; set; } = "";
    public int MatchId { get; set; }
    public List<(int LineId, decimal Amount)> Statement { get; } = [];
    public List<(int LineId, decimal Amount)> Book { get; } = [];

    public decimal StmtTotal { get; set; }
    public decimal BookTotal { get; set; }
    public decimal Difference { get; set; }
    public string MatchType { get; set; } = "";
    public decimal BookBalance { get; set; }
    public decimal StmtBalance { get; set; }
    public decimal OutReceipts { get; set; }
    public decimal OutPayments { get; set; }
    public decimal BridgedBalance { get; set; }
    public decimal Unexplained { get; set; }
    public int UnmatchedCnt { get; set; }
    public string MatchRef { get; set; } = "";
    public string JournalNumber { get; set; } = "";
    public string ReconStatus { get; set; } = "";
    public int MatchedCnt { get; set; }

    public JsonObject BridgeJson() => new()
    {
        ["bankAccount"] = BankAccount,
        ["asAtDate"] = CobolDateText(AsAtDate),
        ["currency"] = Currency,
        ["bookBalance"] = CobolNumeric.WithScale(BookBalance, 2),
        ["stmtBalance"] = CobolNumeric.WithScale(StmtBalance, 2),
        ["outReceipts"] = CobolNumeric.WithScale(OutReceipts, 2),
        ["outPayments"] = CobolNumeric.WithScale(OutPayments, 2),
        ["bridgedBalance"] = CobolNumeric.WithScale(BridgedBalance, 2),
        ["unexplained"] = CobolNumeric.WithScale(Unexplained, 2),
        ["unmatchedCnt"] = UnmatchedCnt,
    };

    public static string? CobolDateText(int yyyymmdd) => CobolDate.FromYyyymmdd(yyyymmdd)?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>BANKREC (cobol/online/fin/BANKREC.cob): statement-to-book bridge, n:m match and unmatch.</summary>
public sealed partial class BankReconciliation(ISqlRunner sql)
{
    private const string Program = "BANKREC";

    public async Task<CaHeader> RunAsync(CaRequest req, BankRecArea br, CancellationToken ct = default)
    {
        var ca = new CaHeader("FIN-014");
        Initialise(req, br);
        if (!Edit(req, ca, br)) return ca;
        try
        {
            switch (req.Function)
            {
                case "BRIDGE":
                    await BridgeAsync(req, ca, br, ct);
                    break;
                default:
                    ca.Reject("BNK0009", "BANKREC does not support function " + req.Function.Trim());
                    break;
            }
        }
        catch (SqlioException ex)
        {
            ca.SqlFailed(ex, Program);
        }
        return ca;
    }

    // 0100-INITIALISE
    private static void Initialise(CaRequest req, BankRecArea br)
    {
        br.StmtTotal = br.BookTotal = br.Difference = 0;
        br.BookBalance = br.StmtBalance = br.OutReceipts = br.OutPayments = br.BridgedBalance = br.Unexplained = 0;
        br.UnmatchedCnt = br.MatchedCnt = 0;
        br.MatchType = br.JournalNumber = br.ReconStatus = "";
        if (br.AsAtDate == 0) br.AsAtDate = req.BusinessDate;
        if (CobolText.IsSpaces(br.Currency)) br.Currency = "GBP";
        if (br.Statement.Count > 20) br.Statement.RemoveRange(20, br.Statement.Count - 20);
        if (br.Book.Count > 20) br.Book.RemoveRange(20, br.Book.Count - 20);
    }

    // 0200-EDIT-REQUEST
    private static bool Edit(CaRequest req, CaHeader ca, BankRecArea br)
    {
        if (CobolText.IsSpaces(req.Company))
        {
            ca.Reject("BNK0001", "A company is required");
            return false;
        }
        if (CobolText.IsSpaces(br.BankAccount))
        {
            ca.Reject("BNK0002", "A bank account code is required");
            return false;
        }
        return true;
    }

    // 1000-BRIDGE
    private async Task BridgeAsync(CaRequest req, CaHeader ca, BankRecArea br, CancellationToken ct)
    {
        var row = await sql.SingleAsync("EXEC fin.usp_BankOutstanding @p1, @p2, @p3",
            req.Company, br.BankAccount, CobolDate.FromYyyymmdd(br.AsAtDate));
        if (row is null)
        {
            ca.Set(16, "BNK0003", "That bank account is not held for the company");
            return;
        }
        // columns: CompanyCode, BankAccountCode, Name, CurrencyCode, AccountCode, StatementBalance, StatementDate,
        // LastReconciledDate, BookBalanceGBP, OutstandingReceipts, OutstandingPayments, UnmatchedStatementValue,
        // UnmatchedStatementCount, BridgedBookBalance
        br.Currency = CobolText.Slot(row[3], 4)[..3].TrimEnd();
        BankGlAccount = Slot.Text(row[4], 12);
        br.StmtBalance = Store15(Slot.Num(row[5]));
        br.BookBalance = Store15(Slot.Num(row[8]));
        br.OutReceipts = Store15(Slot.Num(row[9]));
        br.OutPayments = Store15(Slot.Num(row[10]));
        br.UnmatchedCnt = (int)(Slot.Int(row[12]) % 100000);
        br.BridgedBalance = Store15(Slot.Num(row[13]));
        br.Unexplained = Store15(br.BookBalance - br.BridgedBalance);
        ca.Message = "";
        if (br.Unexplained == 0)
        {
            ca.Message = "Statement and book balances bridge exactly";
        }
        else
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "BNK0010";
            ca.Message = FinEdit.Money(br.Unexplained) + " GBP of the difference is still unexplained";
            ca.MsgCount++;
        }
    }

    /// <summary>WS-BANK-ACCOUNT: the GL account of the bank read by the last BRIDGE.</summary>
    public string BankGlAccount { get; private set; } = "";

    /// <summary>COMPUTE into PIC S9(13)V99 (truncation).</summary>
    private static decimal Store15(decimal v) => CobolNumeric.Store(v, 13, 2);
}
