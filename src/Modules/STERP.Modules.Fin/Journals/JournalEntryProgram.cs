using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Journals;

/// <summary>One JE-LINES entry of JNLENTRYCA.</summary>
public sealed class JnlEntryLine
{
    public int Num { get; set; }
    public string Account { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
    public string DimCc { get; set; } = "";
    public string DimProj { get; set; } = "";
    public string Narrative { get; set; } = "";
    public string VatCode { get; set; } = "";
    public string SubledgerType { get; set; } = "";
    public string SubledgerRef { get; set; } = "";
    public string BankAccount { get; set; } = "";
    public string Status { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>JNLENTRYCA body (cobol/copy/JNLENTRYCA.cpy).</summary>
public sealed class JnlEntryArea
{
    public string JournalRef { get; set; } = "";
    public string JournalType { get; set; } = "";
    public string Description { get; set; } = "";
    public string SourceModule { get; set; } = "";
    public string SourceRef { get; set; } = "";
    public string EventCode { get; set; } = "";
    public int DocumentDate { get; set; }
    public int PostingDate { get; set; }
    public string Currency { get; set; } = "";
    public string EvidenceRef { get; set; } = "";
    public string PreparedBy { get; set; } = "";
    public List<JnlEntryLine> Lines { get; } = [];

    public decimal TotalDebit { get; set; }
    public decimal TotalCredit { get; set; }
    public decimal OutOfBalance { get; set; }
    public string Status { get; set; } = "";
    public int FiscalYear { get; set; }
    public int PeriodNo { get; set; }
    public string PeriodStatus { get; set; } = "";
    public int ErrorCnt { get; set; }
    public int FirstBadLine { get; set; }
    public int StagedCnt { get; set; }

    /// <summary>The FinJournalDraftResult data object.</summary>
    public JsonObject ToJson()
    {
        var lines = new JsonArray();
        foreach (var l in Lines)
        {
            lines.Add(new JsonObject
            {
                ["lineNum"] = l.Num,
                ["account"] = l.Account,
                ["debit"] = CobolNumeric.WithScale(l.Debit, 2),
                ["credit"] = CobolNumeric.WithScale(l.Credit, 2),
                ["costCentre"] = l.DimCc,
                ["project"] = l.DimProj,
                ["vatCode"] = l.VatCode,
                ["narrative"] = l.Narrative,
                ["lineStatus"] = l.Status,
                ["lineMessage"] = l.Message,
            });
        }
        return new JsonObject
        {
            ["totalDebit"] = CobolNumeric.WithScale(TotalDebit, 2),
            ["totalCredit"] = CobolNumeric.WithScale(TotalCredit, 2),
            ["outOfBalance"] = CobolNumeric.WithScale(OutOfBalance, 2),
            ["periodStatus"] = PeriodStatus,
            ["fiscalYear"] = FiscalYear,
            ["periodNo"] = PeriodNo,
            ["errorCnt"] = ErrorCnt,
            ["firstBadLine"] = FirstBadLine,
            ["stagedCnt"] = StagedCnt,
            ["status"] = Status,
            ["lines"] = lines,
        };
    }
}

/// <summary>
/// JNLENTRY (cobol/online/fin/JNLENTRY.cob) functions VALIDAT and SAVE: header edits, per line GLVALID LINE, PERIODCK,
/// balance, and for SAVE the draft and stage rows in one unit owned when outermost.
/// </summary>
public sealed class JournalEntryProgram(ISqlRunner sql, GlValidator glvalid, IFinKernel kernel)
{
    private const string Program = "JNLENTRY";

    public async Task<CaHeader> RunAsync(CaRequest req, JnlEntryArea je, CancellationToken ct = default)
    {
        var ca = new CaHeader("FIN-004");
        Initialise(req, je);
        if (!EditHeader(req, ca, je)) return ca;
        switch (req.Function)
        {
            case "VALIDAT":
                await ValidateAsync(req, ca, je, ct);
                break;
            case "SAVE":
                await SaveAsync(req, ca, je, ct);
                break;
            default:
                ca.Reject("JNL0009", "JNLENTRY does not support function " + req.Function.Trim());
                break;
        }
        return ca;
    }

    // 0100-INITIALISE
    private static void Initialise(CaRequest req, JnlEntryArea je)
    {
        je.ErrorCnt = je.FirstBadLine = je.StagedCnt = 0;
        je.TotalDebit = je.TotalCredit = je.OutOfBalance = 0;
        je.PeriodStatus = "";
        if (Blank(je.Currency)) je.Currency = "GBP";
        if (CobolText.IsSpaces(je.JournalType)) je.JournalType = "GL";
        if (CobolText.IsSpaces(je.SourceModule)) je.SourceModule = "FIN";
        if (je.PostingDate == 0) je.PostingDate = req.BusinessDate;
        if (je.DocumentDate == 0) je.DocumentDate = je.PostingDate;
        if (CobolText.IsSpaces(je.PreparedBy)) je.PreparedBy = req.User;
        if (je.Lines.Count > 50) je.Lines.RemoveRange(50, je.Lines.Count - 50);
    }

    private static bool Blank(string s) => CobolText.IsSpaces(s) || s.All(c => c == '\0');

    // 0200-EDIT-HEADER
    private static bool EditHeader(CaRequest req, CaHeader ca, JnlEntryArea je)
    {
        if (CobolText.IsSpaces(req.Company))
        {
            ca.Reject("JNL0001", "A company is required on a journal");
            return false;
        }
        if (Blank(req.CorrelationId))
        {
            ca.Reject("JNL0002", "A correlation id identifies the journal being pre");
            return false;
        }
        if (Blank(je.Description))
        {
            ca.Reject("JNL0003", "A journal needs a description that explains it");
            return false;
        }
        if (je.Lines.Count == 0)
        {
            ca.Reject("JNL0004", "A journal must have at least two lines");
            return false;
        }
        return true;
    }

    private decimal _dr;
    private decimal _cr;

    // 1000-VALIDATE
    private async Task ValidateAsync(CaRequest req, CaHeader ca, JnlEntryArea je, CancellationToken ct)
    {
        _dr = 0;
        _cr = 0;
        je.ErrorCnt = 0;
        for (var idx = 1; idx <= je.Lines.Count; idx++)
        {
            await ValidateOneAsync(req, ca, je, idx, ct);
        }
        je.TotalDebit = CobolNumeric.Store(_dr, 13, 2);
        je.TotalCredit = CobolNumeric.Store(_cr, 13, 2);
        je.OutOfBalance = CobolNumeric.Store(_dr - _cr, 13, 2);
        await CheckPeriodAsync(req, ca, je, ct);
        CheckBalance(ca, je);
        if (je.ErrorCnt == 0 && ca.ReturnCode < 8)
        {
            ca.Message = "Journal validated, " + FinEdit.Money(je.TotalDebit) + " each side, period " + je.PeriodStatus.Trim();
        }
    }

    // 1100-VALIDATE-ONE
    private async Task ValidateOneAsync(CaRequest req, CaHeader ca, JnlEntryArea je, int idx, CancellationToken ct)
    {
        var line = je.Lines[idx - 1];
        line.Message = "";
        line.Status = "OK";
        if (line.Num == 0) line.Num = idx;
        if (line.Debit != 0 && line.Credit != 0)
        {
            ca.ReasonCode = "JNL0010";
            line.Message = "A line carries a debit or a credit, not both";
            LineError(ca, je, line);
            return;
        }
        if (line.Debit == 0 && line.Credit == 0)
        {
            ca.ReasonCode = "JNL0011";
            line.Message = "A line with no value cannot be posted";
            LineError(ca, je, line);
            return;
        }
        if (line.Debit < 0 || line.Credit < 0)
        {
            ca.ReasonCode = "JNL0012";
            line.Message = "Use the opposite side rather than a negative amou";
            LineError(ca, je, line);
            return;
        }
        if (await CallGlvalidAsync(req, ca, je, line, ct)) return;
        _dr += line.Debit;
        _cr += line.Credit;
    }

    // 1200-CALL-GLVALID: true when the line was refused
    private async Task<bool> CallGlvalidAsync(CaRequest req, CaHeader ca, JnlEntryArea je, JnlEntryLine line, CancellationToken ct)
    {
        var gv = new GlValidArea
        {
            Account = line.Account,
            PostingDate = je.PostingDate,
            DimCc = line.DimCc,
            DimProj = line.DimProj,
            VatCode = line.VatCode,
            SourceModule = je.SourceModule,
            LineNum = line.Num,
            ManualEntry = je.SourceModule == "FIN" && je.JournalType == "GL" ? "Y" : "N",
        };
        var gh = await glvalid.RunAsync(req.Callee("LINE", site: true, correlation: false), gv, ct);
        if (gh.ReturnCode <= 4) return false;
        ca.ReasonCode = gh.ReasonCode;
        line.Message = CobolText.Trimmed(gh.Message, 80);
        LineError(ca, je, line);
        return true;
    }

    // 1500-CHECK-PERIOD
    private async Task CheckPeriodAsync(CaRequest req, CaHeader ca, JnlEntryArea je, CancellationToken ct)
    {
        var pc = await kernel.PeriodCheckAsync(req.Callee("CHECK", site: false, correlation: false), je.PostingDate, je.DocumentDate, "POST", ct);
        je.PeriodStatus = CobolText.Trimmed(pc.PeriodStatus, 8);
        je.FiscalYear = Math.Abs(pc.FiscalYear) % 10000;
        je.PeriodNo = Math.Abs(pc.PeriodNo) % 100;
        if (pc.Result.ReturnCode > 4)
        {
            ca.ReasonCode = pc.Result.ReasonCode;
            ca.Message = pc.Result.Message;
            ca.ReturnCode = pc.Result.ReturnCode;
            je.ErrorCnt++;
            ca.MsgCount++;
        }
    }

    // 1600-CHECK-BALANCE
    private static void CheckBalance(CaHeader ca, JnlEntryArea je)
    {
        if (je.OutOfBalance == 0) return;
        ca.ReasonCode = "JNL0020";
        ca.Message = "The journal does not balance. Debits and credits differ by " + FinEdit.Money(je.OutOfBalance) + " " + je.Currency.Trim();
        je.ErrorCnt++;
        ca.ReturnCode = 8;
        ca.MsgCount++;
    }

    // 1900-LINE-ERROR
    private static void LineError(CaHeader ca, JnlEntryArea je, JnlEntryLine line)
    {
        line.Status = "ER";
        je.ErrorCnt++;
        if (je.FirstBadLine == 0)
        {
            je.FirstBadLine = line.Num;
            ca.Message = line.Message;
        }
        ca.ReturnCode = 8;
        ca.MsgCount++;
    }

    // 2000-SAVE
    private async Task SaveAsync(CaRequest req, CaHeader ca, JnlEntryArea je, CancellationToken ct)
    {
        await ValidateAsync(req, ca, je, ct);
        if (ca.ReturnCode == 12) return;
        var hash = ContentHash(je);
        var unit = new OwnedUnit(sql);
        await unit.BeginAsync(ct);
        await WriteDraftAsync(req, ca, je, hash);
        if (ca.ReturnCode == 12)
        {
            await unit.RollbackAsync(ct);
            return;
        }
        await RunAsync(ca, () => sql.ExecAsync("DELETE fin.JournalStage WHERE CorrelationId = @p1", req.CorrelationId));
        for (var idx = 1; idx <= je.Lines.Count && ca.ReturnCode != 12; idx++)
        {
            var l = je.Lines[idx - 1];
            await RunAsync(ca, () => sql.ExecAsync(
                "INSERT fin.JournalStage (CorrelationId, LineNum, CompanyCode, AccountCode, DebitAmount, CreditAmount, CurrencyCode, " +
                "DimCostCentre, DimProject, Narrative, VatCode, SubledgerType, SubledgerRef, BankAccountCode, CreatedBy) VALUES " +
                "(@p1, @p2, @p3, @p4, @p5, @p6, @p7, NULLIF(@p8,''), NULLIF(@p9,''), NULLIF(@p10,''), NULLIF(@p11,''), " +
                "NULLIF(@p12,''), NULLIF(@p13,''), NULLIF(@p14,''), @p15)",
                req.CorrelationId, l.Num, req.Company, l.Account, l.Debit, l.Credit, je.Currency, l.DimCc, l.DimProj,
                l.Narrative, l.VatCode, l.SubledgerType, l.SubledgerRef, l.BankAccount, req.User));
        }
        if (ca.ReturnCode == 12)
        {
            await unit.RollbackAsync(ct);
            return;
        }
        await unit.CommitAsync(ct);
        je.StagedCnt = je.Lines.Count;
        if (CobolText.IsSpaces(je.Status)) je.Status = "DRAFT";
        if (ca.ReturnCode < 8)
        {
            ca.Message = "Draft journal saved with " + je.PeriodStatus.Trim() + " period. No ledger effect until it is posted";
        }
    }

    /// <summary>2050-CONTENT-HASH: MOD(hash * 31 + idx * 7 + NUMVAL(account) + (debit - credit) * 100, 9999999999999999) as 16 digits.</summary>
    public static string ContentHash(JnlEntryArea je)
    {
        const decimal m = 9999999999999999m;
        var hash = 0m;
        for (var idx = 1; idx <= je.Lines.Count; idx++)
        {
            var l = je.Lines[idx - 1];
            var amt = CobolNumeric.Store(l.Debit - l.Credit, 13, 2);
            var a = hash * 31 + idx * 7 + Slot.NumVal(l.Account) + amt * 100;
            var r = a % m;
            if (r < 0) r += m;
            hash = decimal.Truncate(r);
        }
        return hash.ToString("0000000000000000", CultureInfo.InvariantCulture);
    }

    // 2100-WRITE-DRAFT
    private async Task WriteDraftAsync(CaRequest req, CaHeader ca, JnlEntryArea je, string hash)
    {
        int rows;
        try
        {
            rows = await sql.ExecAsync(
                "UPDATE fin.JournalDraft SET JournalType = @p1, Description = @p2, SourceModule = @p3, SourceRef = @p4, " +
                "EventCode = NULLIF(@p5,''), DocumentDate = @p6, PostingDate = @p7, CurrencyCode = @p8, TotalDebit = @p9, " +
                "TotalCredit = @p10, EvidenceRef = NULLIF(@p11,''), LineCount = @p12, ContentHash = @p13, Status = CASE WHEN Status IN " +
                "('SUBMITTED','APPROVED') AND ContentHash <> @p14 THEN 'DRAFT' ELSE Status END, ApprovedBy = CASE WHEN ContentHash <> @p15 " +
                "THEN NULL ELSE ApprovedBy END, ModifiedBy = @p16, ModifiedAt = SYSDATETIME() WHERE CorrelationId = @p17",
                je.JournalType, je.Description, je.SourceModule, je.SourceRef, je.EventCode, D8(je.DocumentDate), D8(je.PostingDate),
                je.Currency, je.TotalDebit, je.TotalCredit, je.EvidenceRef, je.Lines.Count, hash, hash, hash, req.User, req.CorrelationId);
        }
        catch (SqlioException ex)
        {
            ca.SqlFailed(ex, Program);
            return;
        }
        if (rows > 0) return;
        await RunAsync(ca, () => sql.ExecAsync(
            "INSERT fin.JournalDraft (CompanyCode, CorrelationId, JournalType, Description, SourceModule, SourceRef, EventCode, " +
            "DocumentDate, PostingDate, CurrencyCode, TotalDebit, TotalCredit, EvidenceRef, LineCount, ContentHash, Status, " +
            "PreparedBy, CreatedBy) VALUES (@p1, @p2, @p3, @p4, @p5, @p6, NULLIF(@p7,''), @p8, @p9, @p10, @p11, @p12, NULLIF(@p13,''), " +
            "@p14, @p15, 'DRAFT', @p16, @p17)",
            req.Company, req.CorrelationId, je.JournalType, je.Description, je.SourceModule, je.SourceRef, je.EventCode,
            D8(je.DocumentDate), D8(je.PostingDate), je.Currency, je.TotalDebit, je.TotalCredit, je.EvidenceRef, je.Lines.Count, hash,
            je.PreparedBy, req.User));
    }

    private static string D8(int yyyymmdd) => (yyyymmdd % 100_000_000).ToString("00000000", CultureInfo.InvariantCulture);

    /// <summary>A statement whose SQLIO failure ends in 9300-SQL-FAILED (RC 12 SQL0001) without stopping the paragraph flow.</summary>
    private static async Task RunAsync(CaHeader ca, Func<Task<int>> statement)
    {
        try
        {
            await statement();
        }
        catch (SqlioException ex)
        {
            ca.SqlFailed(ex, Program);
        }
    }
}
