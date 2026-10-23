using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Journals;

/// <summary>JNLPOSTCA body (cobol/copy/JNLPOSTCA.cpy).</summary>
public sealed class JnlPostArea
{
    public string JournalNumber { get; set; } = "";
    public string JournalType { get; set; } = "";
    public string Description { get; set; } = "";
    public string SourceModule { get; set; } = "";
    public string SourceRef { get; set; } = "";
    public string EventCode { get; set; } = "";
    public string SeriesCode { get; set; } = "";
    public int DocumentDate { get; set; }
    public int PostingDate { get; set; }
    public string Currency { get; set; } = "";
    public decimal FxRate { get; set; }
    public string EvidenceRef { get; set; } = "";
    public decimal ReversePct { get; set; }
    public decimal ReverseAmount { get; set; }
    public string Reason { get; set; } = "";

    public long JournalId { get; set; }
    public string NewJournalNo { get; set; } = "";
    public int FiscalYear { get; set; }
    public int PeriodNo { get; set; }
    public decimal TotalDrGbp { get; set; }
    public decimal TotalCrGbp { get; set; }
    public decimal ReversedGbp { get; set; }
    public decimal ReversibleGbp { get; set; }
    public string Status { get; set; } = "";
    public string WasDuplicate { get; set; } = "N";
    public string IsReversal { get; set; } = "N";
    public int LineCount { get; set; }

    public JsonObject PostJson() => new()
    {
        ["journalId"] = JournalId,
        ["journalNumber"] = NewJournalNo,
        ["fiscalYear"] = FiscalYear,
        ["periodNo"] = PeriodNo,
        ["totalDrGbp"] = CobolNumeric.WithScale(TotalDrGbp, 2),
        ["totalCrGbp"] = CobolNumeric.WithScale(TotalCrGbp, 2),
        ["status"] = Status,
        ["wasDuplicate"] = WasDuplicate,
    };

    public JsonObject ReverseJson() => new()
    {
        ["journalNumber"] = JournalNumber,
        ["newJournalNumber"] = NewJournalNo,
        ["journalId"] = JournalId,
        ["fiscalYear"] = FiscalYear,
        ["periodNo"] = PeriodNo,
        ["totalDrGbp"] = CobolNumeric.WithScale(TotalDrGbp, 2),
        ["totalCrGbp"] = CobolNumeric.WithScale(TotalCrGbp, 2),
        ["reversedGbp"] = CobolNumeric.WithScale(ReversedGbp, 2),
        ["reversibleGbp"] = CobolNumeric.WithScale(ReversibleGbp, 2),
        ["reverseAmount"] = CobolNumeric.WithScale(ReverseAmount, 2),
        ["isReversal"] = IsReversal,
        ["wasDuplicate"] = WasDuplicate,
    };
}

/// <summary>
/// JNLPOST (cobol/online/fin/JNLPOST.cob) functions POST and REVERSE: authority, the program's own period decision, the GL
/// posting gateway (fin.usp_PostJournal) or fin.usp_ReverseJournal, the draft mark and the AUDITLOG row. No unit of work is
/// opened: every statement autocommits on the caller's engine connection (an open caller unit is joined).
/// </summary>
public sealed class JournalPostingProgram(ISqlRunner sql, IFinKernel kernel)
{
    private const string Program = "JNLPOST";

    public async Task<CaHeader> RunAsync(CaRequest req, JnlPostArea jp, CancellationToken ct = default)
    {
        var ca = new CaHeader("FIN-005");
        var state = new State();
        Initialise(req, jp);
        if (!Edit(req, ca, state)) return ca;
        switch (req.Function)
        {
            case "POST":
                await PostAsync(req, ca, jp, state, ct);
                break;
            case "REVERSE":
                await ReverseAsync(req, ca, jp, state, ct);
                break;
            case "ENQUIRE":
                await EnquireAsync(req, ca, jp);
                break;
            default:
                Reject(ca, state, "JNP0009", "JNLPOST does not support function " + req.Function.Trim());
                break;
        }
        return ca;
    }

    private sealed class State
    {
        public bool Rejected;
    }

    // 0100-INITIALISE
    private static void Initialise(CaRequest req, JnlPostArea jp)
    {
        jp.JournalId = 0;
        jp.FiscalYear = jp.PeriodNo = jp.LineCount = 0;
        jp.TotalDrGbp = jp.TotalCrGbp = 0;
        jp.NewJournalNo = jp.Status = "";
        jp.WasDuplicate = jp.IsReversal = "N";
        if (CobolText.IsSpaces(jp.Currency) || jp.Currency.All(c => c == '\0')) jp.Currency = "GBP";
        if (CobolText.IsSpaces(jp.JournalType)) jp.JournalType = "GL";
        if (CobolText.IsSpaces(jp.SourceModule)) jp.SourceModule = "FIN";
        if (CobolText.IsSpaces(jp.SeriesCode)) jp.SeriesCode = req.Function == "REVERSE" ? "JNLR" : "JNL";
        if (jp.PostingDate == 0) jp.PostingDate = req.BusinessDate;
        if (jp.DocumentDate == 0) jp.DocumentDate = jp.PostingDate;
    }

    // 0200-EDIT-REQUEST
    private static bool Edit(CaRequest req, CaHeader ca, State state)
    {
        if (CobolText.IsSpaces(req.Company))
        {
            Reject(ca, state, "JNP0001", "A company is required to post a journal");
            return false;
        }
        if (CobolText.IsSpaces(req.CorrelationId) && req.Function != "ENQUIRE")
        {
            Reject(ca, state, "JNP0002", "A correlation id is required so a repeated postin");
            return false;
        }
        return true;
    }

    private static void Reject(CaHeader ca, State state, string reason, string message)
    {
        state.Rejected = true;
        ca.Reject(reason, message);
    }

    // 1000-POST
    private async Task PostAsync(CaRequest req, CaHeader ca, JnlPostArea jp, State state, CancellationToken ct)
    {
        await AuthorityAsync(req, ca, state, "FIN.JOURNAL.POST", ct);
        if (state.Rejected) return;
        await PeriodDecisionAsync(req, ca, jp, state);
        if (state.Rejected || ca.ReturnCode == 12) return;
        object?[]? row;
        try
        {
            row = await sql.SingleAsync(
                "EXEC fin.usp_PostJournal @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14",
                req.Company, req.CorrelationId, req.User, jp.Description, D8(jp.PostingDate), jp.JournalType, D8(jp.DocumentDate),
                jp.Currency, jp.SourceModule, CobolText.IsSpaces(jp.SourceRef) ? null : jp.SourceRef,
                CobolText.IsSpaces(jp.EventCode) ? null : jp.EventCode, jp.SeriesCode,
                jp.FxRate == 0 ? null : CobolNumeric.SqlioNumber(jp.FxRate),
                CobolText.IsSpaces(jp.EvidenceRef) ? null : jp.EvidenceRef);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, state, ex);
            return;
        }
        if (row is null)
        {
            ca.Set(12, "JNP0003", "The posting returned no result row");
            return;
        }
        TakeResult(ca, jp, row);
        await MarkDraftAsync(req, jp);
        await AuditAsync(req, jp, ct);
    }

    // 1100-AUTHORITY (PERMCHK with CA-SITE moved)
    private async Task AuthorityAsync(CaRequest req, CaHeader ca, State state, string permission, CancellationToken ct)
    {
        var kh = await kernel.PermCheckAsync(req.Callee("CHECK", site: true, correlation: false), permission, ct);
        if (kh.ReturnCode <= 4) return;
        ca.ReasonCode = kh.ReasonCode;
        ca.Message = kh.Message;
        ca.ReturnCode = kh.ReturnCode;
        state.Rejected = true;
        ca.MsgCount++;
    }

    // 1500-TAKE-RESULT (posting contract row: JournalId, JournalNumber, CompanyCode, FiscalYear, PeriodNo, TotalDebitGBP,
    // TotalCreditGBP, Status, WasDuplicate)
    private static void TakeResult(CaHeader ca, JnlPostArea jp, object?[] row)
    {
        jp.JournalId = (long)CobolNumeric.Store(Slot.Num(row[0]), 9, 0);
        jp.NewJournalNo = Slot.Text(row[1], 30);
        jp.JournalNumber = jp.NewJournalNo;
        jp.FiscalYear = (int)(Math.Abs(Slot.Int(row[3])) % 10000);
        jp.PeriodNo = (int)(Math.Abs(Slot.Int(row[4])) % 100);
        jp.TotalDrGbp = CobolNumeric.Store(Slot.Num(row[5]), 13, 2);
        jp.TotalCrGbp = CobolNumeric.Store(Slot.Num(row[6]), 13, 2);
        jp.Status = Slot.Text(row[7], 12);
        if (BitText(row[8]).StartsWith('1'))
        {
            jp.WasDuplicate = "Y";
            ca.ReturnCode = 4;
            ca.ReasonCode = "JNP0007";
            ca.Message = "That instruction had already been carried out. Journal " + jp.NewJournalNo.Trim() +
                         " is the original result, and no second entry has been made";
            ca.MsgCount++;
        }
        else
        {
            jp.WasDuplicate = "N";
            ca.Message = "Journal " + jp.NewJournalNo.Trim() + " posted, " + FinEdit.Money(jp.TotalDrGbp) + " GBP each side";
        }
    }

    private static string BitText(object? v) => v switch
    {
        bool b => b ? "1" : "0",
        null => "",
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    // 1600-MARK-DRAFT (outcome not tested)
    private async Task MarkDraftAsync(CaRequest req, JnlPostArea jp)
    {
        try
        {
            await sql.ExecAsync(
                "UPDATE fin.JournalDraft SET Status = 'POSTED', PostedJournalNo = @p1, ModifiedBy = @p2, ModifiedAt = " +
                "SYSDATETIME() WHERE CorrelationId = @p3 AND Status <> 'POSTED'",
                jp.NewJournalNo, req.User, req.CorrelationId);
        }
        catch (SqlioException)
        {
            // the legacy paragraph does not test SQLIO-SQLCODE
        }
    }

    // 1700-AUDIT (outcome not tested)
    private async Task AuditAsync(CaRequest req, JnlPostArea jp, CancellationToken ct)
    {
        var reverse = req.Function == "REVERSE";
        await kernel.AuditAsync(req.Callee("WRITE", site: false, correlation: true), new AuditEntry(
            EntityType: "fin.Journal",
            EntityKey: jp.NewJournalNo,
            Action: reverse ? "REVERSE" : "POST",
            NewValue: FinEdit.MoneyField(jp.TotalDrGbp),
            Reason: reverse ? jp.Reason : jp.Description,
            Source: "COBOL"), ct);
    }

    // 2000-REVERSE
    private async Task ReverseAsync(CaRequest req, CaHeader ca, JnlPostArea jp, State state, CancellationToken ct)
    {
        await AuthorityAsync(req, ca, state, "FIN.JOURNAL.REVERSE", ct);
        if (state.Rejected) return;
        if (CobolText.IsSpaces(jp.JournalNumber))
        {
            Reject(ca, state, "JNP0010", "The journal number to reverse is required");
            return;
        }
        if (CobolText.IsSpaces(jp.Reason) || jp.Reason.All(c => c == '\0'))
        {
            Reject(ca, state, "JNP0011", "A reversal must carry the reason for the correcti");
            return;
        }
        await EnquireAsync(req, ca, jp);
        if (ca.Failed) return;
        await PeriodDecisionAsync(req, ca, jp, state);
        if (state.Rejected || ca.ReturnCode == 12) return;
        var pct = EditAmount(ca, jp, state);
        if (state.Rejected) return;
        object?[]? row;
        try
        {
            row = await sql.SingleAsync("EXEC fin.usp_ReverseJournal @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8",
                req.Company, jp.JournalNumber, req.User, jp.Reason, D8(jp.PostingDate), CobolNumeric.SqlioNumber(pct),
                req.CorrelationId, jp.SeriesCode);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, state, ex);
            return;
        }
        if (row is null)
        {
            ca.Set(12, "JNP0012", "The reversal returned no result row");
            return;
        }
        jp.IsReversal = "Y";
        TakeResult(ca, jp, row);
        ca.Message = "Reversal " + jp.NewJournalNo.Trim() + " posted for " + FinEdit.Money(jp.TotalDrGbp) +
                     " GBP. The original entry is unchanged";
        await AuditAsync(req, jp, ct);
    }

    // 2050-PERIOD-DECISION
    private async Task PeriodDecisionAsync(CaRequest req, CaHeader ca, JnlPostArea jp, State state)
    {
        var postDate = jp.PostingDate;
        if (postDate == 0)
        {
            postDate = req.BusinessDate;
            jp.PostingDate = postDate;
        }
        object?[]? row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT p.Status, CAST(CASE WHEN p.Status = 'OPEN' THEN 1 WHEN p.Status = 'SOFT' AND " +
                "core.fn_HasPermission(@p1, 'FIN.PERIOD.SOFTPOST', @p2, NULL) = 1 THEN 1 ELSE 0 END AS char(1)), " +
                "CAST(p.FiscalYear AS varchar(8)), CAST(p.PeriodNo AS varchar(8)) FROM fin.Period p " +
                "WHERE p.CompanyCode = @p3 AND @p4 BETWEEN p.StartDate AND p.EndDate",
                req.User, req.Company, req.Company, D8(postDate));
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, state, ex);
            return;
        }
        var d8 = D8(postDate);
        if (row is null)
        {
            Reject(ca, state, "JNP0016", "No financial period covers posting date " + d8[6..8] + "/" + d8[4..6] + "/" + d8[..4] + " for this company");
            return;
        }
        var status = Slot.Text(row[0], 10);
        var periodText = Slot.Text(row[3], 8);
        jp.FiscalYear = (int)(Math.Abs(Slot.Int(Slot.Text(row[2], 8))) % 10000);
        jp.PeriodNo = (int)(Math.Abs(Slot.Int(periodText)) % 100);
        if (Slot.Text(row[1], 4).StartsWith('1')) return;
        ca.Message = "";
        if (status == "SOFT")
        {
            ca.ReasonCode = "JNP0017";
            ca.Message = "Period " + periodText.Trim() + " is soft closed. Only an authorised close adjustment may be posted into it";
            ca.ReturnCode = 6;
            state.Rejected = true;
            ca.MsgCount++;
        }
        else
        {
            ca.ReasonCode = "JNP0018";
            ca.Message = "Period " + periodText.Trim() + " is " + status.Trim() + " closed for posting. Post the entry in the current open period";
            Reject(ca, state, ca.ReasonCode, ca.Message);
        }
    }

    // 2100-EDIT-AMOUNT: returns WS-PCT
    private static decimal EditAmount(CaHeader ca, JnlPostArea jp, State state)
    {
        decimal pct;
        if (jp.ReverseAmount != 0)
        {
            pct = jp.TotalDrGbp == 0 ? 0 : CobolNumeric.Store(jp.ReverseAmount * 100 / jp.TotalDrGbp, 5, 4, rounded: true);
        }
        else
        {
            pct = jp.ReversePct == 0 ? 100 : jp.ReversePct;
            jp.ReverseAmount = CobolNumeric.Store(jp.TotalDrGbp * pct / 100, 13, 2, rounded: true);
        }
        if (pct <= 0)
        {
            Reject(ca, state, "JNP0013", "A reversal must have a positive value");
            return pct;
        }
        if (jp.ReverseAmount > jp.ReversibleGbp)
        {
            Reject(ca, state, "JNP0014", "The reversal exceeds the unreversed value of the original. Only " +
                FinEdit.Money(jp.ReversibleGbp) + " GBP remains reversible");
        }
        return pct;
    }

    // 3000-ENQUIRE
    private async Task EnquireAsync(CaRequest req, CaHeader ca, JnlPostArea jp)
    {
        object?[]? row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT CAST(j.JournalId AS varchar(18)), j.JournalType, j.Description, CONVERT(char(8), j.PostingDate, 112), " +
                "CAST(j.FiscalYear AS varchar(8)), CAST(j.PeriodNo AS varchar(8)), j.TotalDebitGBP, j.TotalCreditGBP, " +
                "j.ReversedAmountGBP, j.Status, CAST(j.IsReversal AS char(1)), CAST((SELECT COUNT(*) FROM fin.JournalLine l WHERE " +
                "l.JournalId = j.JournalId) AS varchar(10)) FROM fin.Journal j WHERE j.CompanyCode = @p1 AND j.JournalNumber = @p2",
                req.Company, jp.JournalNumber);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, new State(), ex);
            return;
        }
        if (row is null)
        {
            ca.Set(16, "JNP0015", "Journal " + jp.JournalNumber.Trim() + " is not held for " + req.Company.Trim());
            return;
        }
        jp.JournalId = (long)CobolNumeric.Store(Slot.Num(Slot.Text(row[0], 18)), 9, 0);
        jp.JournalType = Slot.Text(row[1], 12);
        jp.Description = Slot.Text(row[2], 200);
        jp.FiscalYear = (int)(Math.Abs(Slot.Int(Slot.Text(row[4], 8))) % 10000);
        jp.PeriodNo = (int)(Math.Abs(Slot.Int(Slot.Text(row[5], 8))) % 100);
        jp.TotalDrGbp = CobolNumeric.Store(Slot.Num(row[6]), 13, 2);
        jp.TotalCrGbp = CobolNumeric.Store(Slot.Num(row[7]), 13, 2);
        jp.ReversedGbp = CobolNumeric.Store(Slot.Num(row[8]), 13, 2);
        jp.ReversibleGbp = CobolNumeric.Store(jp.TotalDrGbp - jp.ReversedGbp, 13, 2);
        jp.Status = Slot.Text(row[9], 12);
        jp.LineCount = (int)(Math.Abs(Slot.Int(Slot.Text(row[11], 10))) % 1000);
        if (Slot.Text(row[10], 4).StartsWith('1')) jp.IsReversal = "Y";
    }

    // 9300-SQL-FAILED: a driver text beginning STERP-E- is RC 08 with reason = characters 7-12, otherwise RC 12 SQL0001
    private static void SqlFailed(CaHeader ca, State state, SqlioException ex)
    {
        var text = CobolText.Trimmed(ex.DriverText, 200);
        ca.Message = text.Length == 0 ? "SQL failure in " + Program : text;
        var msg = CobolText.Fit(ca.Message, 200);
        if (msg.StartsWith("STERP-E-", StringComparison.Ordinal))
        {
            ca.ReasonCode = msg.Substring(6, 6).TrimEnd();
            ca.ReturnCode = 8;
        }
        else
        {
            ca.ReasonCode = "SQL0001";
            ca.ReturnCode = 12;
        }
        state.Rejected = true;
        ca.MsgCount++;
    }

    private static string D8(int yyyymmdd) => (yyyymmdd % 100_000_000).ToString("00000000", CultureInfo.InvariantCulture);
}
