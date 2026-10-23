using Sterp.Modules.Fin.Common;
using Sterp.Modules.Fin.Journals;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Fin.Batch;

/// <summary>ACCRRELCA body (cobol/copy/ACCRRELCA.cpy) as RECURJNL fills and reads it.</summary>
internal sealed class AccrRelArea
{
    public string RecurCode { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Frequency { get; set; } = "";
    public int StartDate { get; set; }
    public int EndDate { get; set; }
    public decimal SourceAmount { get; set; }
    public int OccPlanned { get; set; }
    public int OccurrenceNo { get; set; }
    public int PostingDate { get; set; }
    public string EventCode { get; set; } = "";
    public string Mode { get; set; } = "";
    public decimal ReleaseAmount { get; set; }
    public decimal ReleasedTotal { get; set; }
    public decimal Remaining { get; set; }
    public int OccPosted { get; set; }
    public int NextDueDate { get; set; }
    public string JournalNumber { get; set; } = "";
    public string PeriodStatus { get; set; } = "";
    public int FiscalYear { get; set; }
    public int PeriodNo { get; set; }
    public string AlreadyPosted { get; set; } = "N";
    public string Blocked { get; set; } = "N";
    public string BlockReason { get; set; } = "";
    public long ExceptionId { get; set; }
    public string RecurStatus { get; set; } = "";
    public List<Occurrence> Schedule { get; } = [];

    public sealed class Occurrence
    {
        public int OccNo { get; set; }
        public int Date { get; set; }
        public decimal Amount { get; set; }
        public string Status { get; set; } = "";
        public string Journal { get; set; } = "";
    }
}

/// <summary>
/// ACCRREL (cobol/online/fin/ACCRREL.cob; FR-STERP-FIN-055): recurring journal, accrual and prepayment release schedule
/// (SCHEDULE/STATUS), next due occurrence (NEXTOCC, PERIODCK on its date) and RELEASE (stage the template lines scaled to
/// the occurrence, post through JNLPOST under RJ-&lt;co&gt;-&lt;code&gt;-&lt;occ&gt;, record the occurrence, refresh the header; a
/// closed period records the occurrence BLOCKED and raises a HOLD exception). No unit of work of its own: every statement
/// autocommits, the posting is atomic inside fin.usp_PostJournal.
/// </summary>
internal sealed class AccrualRelease(ISqlRunner sql, IFinKernel kernel, IBusinessCalendar calendar, IRoundingAllocator rndalloc,
    JournalPostingProgram jnlpost)
{
    private const string Program = "ACCRREL";

    private sealed class TemplateLine
    {
        public string Account = "";
        public decimal Debit;
        public decimal Credit;
        public string Cc = "";
        public string Proj = "";
        public string Narr = "";
    }

    private bool _rejected;
    private int _step = 1;
    private string _correl = "";
    private readonly List<TemplateLine> _lines = [];

    public async Task<CaHeader> RunAsync(CaRequest req, AccrRelArea ak, CancellationToken ct = default)
    {
        var ca = new CaHeader("FIN-007");
        _rejected = false;
        Initialise(ak);
        Edit(req, ca, ak);
        if (_rejected) return ca;
        await ReadTemplateAsync(req, ca, ak);
        if (_rejected) return ca;
        switch (req.Function)
        {
            case "SCHEDULE":
            case "STATUS":
                await ScheduleAsync(req, ca, ak);
                break;
            case "NEXTOCC":
                await NextOccAsync(req, ca, ak, ct);
                break;
            case "RELEASE":
                await ReleaseAsync(req, ca, ak, ct);
                break;
            default:
                ca.Message = "ACCRREL does not support function " + req.Function.Trim();
                ca.ReasonCode = "ACR0009";
                Reject(ca);
                break;
        }
        return ca;
    }

    // 0100-INITIALISE
    private void Initialise(AccrRelArea ak)
    {
        ak.ReleaseAmount = 0;
        ak.ReleasedTotal = 0;
        ak.Remaining = 0;
        ak.OccPosted = 0;
        ak.Schedule.Clear();
        ak.ExceptionId = 0;
        ak.JournalNumber = "";
        ak.BlockReason = "";
        ak.AlreadyPosted = "N";
        ak.Blocked = "N";
        _lines.Clear();
        ak.RecurCode = CobolText.Trimmed(CobolText.Upper(ak.RecurCode), 20);
    }

    // 0200-EDIT-REQUEST
    private void Edit(CaRequest req, CaHeader ca, AccrRelArea ak)
    {
        if (CobolText.IsSpaces(req.Company))
        {
            ca.ReasonCode = "ACR0001";
            ca.Message = "A company is required";
            Reject(ca);
            return;
        }
        if (CobolText.IsSpaces(ak.RecurCode))
        {
            ca.ReasonCode = "ACR0002";
            ca.Message = "A recurring journal code is required";
            Reject(ca);
        }
    }

    // 0300-READ-TEMPLATE
    private async Task ReadTemplateAsync(CaRequest req, CaHeader ca, AccrRelArea ak)
    {
        object?[]? row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT r.Name, r.JournalKind, r.Frequency, CONVERT(char(8), r.StartDate, 112), ISNULL(CONVERT(char(8), r.EndDate, 112),''), " +
                "ISNULL(r.SourceAmount,0), CAST(ISNULL(r.OccurrencesPlanned,0) AS varchar(10)), CAST(r.OccurrencesPosted AS varchar(10)), " +
                "r.ReleasedAmount, r.Status, ISNULL(r.EventCode,'') FROM fin.RecurringJournal r WHERE r.CompanyCode = @p1 AND " +
                "r.RecurringCode = @p2", req.Company, ak.RecurCode);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        if (row is null)
        {
            ca.ReasonCode = "ACR0003";
            ca.Message = "Recurring journal " + ak.RecurCode.Trim() + " is not held for " + req.Company.Trim();
            ca.ReturnCode = 16;
            _rejected = true;
            ca.MsgCount++;
            return;
        }
        ak.Name = Slot.Text(row[0], 80);
        ak.Kind = Slot.Text(row[1], 10);
        ak.Frequency = Slot.Text(row[2], 10);
        ak.RecurStatus = Slot.Text(row[9], 10);
        ak.EventCode = Slot.Text(row[10], 30);
        ak.StartDate = Date8(Slot.NumVal(Slot.Text(row[3], 8)));
        var end = Slot.Text(row[4], 8);
        if (!CobolText.IsSpaces(end)) ak.EndDate = Date8(Slot.NumVal(end));
        ak.SourceAmount = CobolNumeric.Store(Slot.Num(row[5]), 11, 2);
        ak.OccPlanned = (int)CobolNumeric.Store(Slot.Num(row[6]), 4, 0, signed: false);
        ak.OccPosted = (int)CobolNumeric.Store(Slot.Num(row[7]), 4, 0, signed: false);
        ak.ReleasedTotal = CobolNumeric.Store(Slot.Num(row[8]), 13, 2);
        ak.Remaining = CobolNumeric.Store(ak.SourceAmount - ak.ReleasedTotal, 13, 2);
        if (ak.OccPlanned == 0) ak.OccPlanned = 1;
        _step = ak.Frequency switch { "QUARTERLY" => 3, "ANNUAL" => 12, _ => 1 };
    }

    // 1000-SCHEDULE
    private async Task ScheduleAsync(CaRequest req, CaHeader ca, AccrRelArea ak)
    {
        SplitAmount(ak);
        ak.Schedule.Clear();
        var date = ak.StartDate;
        for (var occ = 1; occ <= ak.OccPlanned && ak.Schedule.Count < 36; occ++)
        {
            if (occ == 1)
            {
                date = ak.StartDate;
            }
            else
            {
                var dc = await calendar.AddMonthsAsync(date, _step);
                if (dc.Header.ReturnCode < 8) date = dc.ResultDate;
            }
            decimal amount;
            if (ak.Kind == "RECURRING") amount = 0;
            else if (occ == ak.OccPlanned) amount = CobolNumeric.Store(ak.SourceAmount - ak.ReleaseAmount * (ak.OccPlanned - 1), 11, 2);
            else amount = ak.ReleaseAmount;
            ak.Schedule.Add(new AccrRelArea.Occurrence { OccNo = occ, Date = date, Amount = amount, Status = "DUE", Journal = "" });
        }
        await ApplyHistoryAsync(req, ca, ak);
        ca.Message = CobolText.Trimmed(ak.Kind.Trim() + " schedule of " + ak.RecurCode.Trim() + ", " + FinEdit.Money(ak.Remaining) +
                                       " GBP remaining", 200);
    }

    // 1100-SPLIT-AMOUNT: an equal split through RNDALLOC (largest remainder), the first part is the release amount
    private void SplitAmount(AccrRelArea ak)
    {
        if (ak.Kind == "RECURRING" || ak.SourceAmount == 0)
        {
            ak.ReleaseAmount = 0;
            return;
        }
        var ra = rndalloc.Allocate(ak.SourceAmount, 2, "EQUAL", Enumerable.Repeat(0m, ak.OccPlanned).ToArray());
        ak.ReleaseAmount = ra.Header.ReturnCode > 4 || ra.Lines.Count == 0
            ? CobolNumeric.Store(CobolNumeric.RoundAway(ak.SourceAmount / ak.OccPlanned, 2), 11, 2)
            : CobolNumeric.Store(ra.Lines[0].Part, 11, 2);
    }

    // 1300-APPLY-HISTORY / 1350-MERGE-OCC
    private async Task ApplyHistoryAsync(CaRequest req, CaHeader ca, AccrRelArea ak)
    {
        List<object?[]> rows;
        try
        {
            rows = await sql.RowsAsync(
                "SELECT CAST(o.OccurrenceNo AS varchar(10)), CONVERT(char(8), o.ScheduledDate, 112), o.Amount, o.Status, " +
                "ISNULL(o.JournalNumber,'') FROM fin.RecurringOccurrence o WHERE o.CompanyCode = @p1 AND o.RecurringCode = @p2 " +
                "ORDER BY o.OccurrenceNo", 0, req.Company, ak.RecurCode);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        foreach (var r in rows)
        {
            var occ = (int)CobolNumeric.Store(Slot.NumVal(Slot.Text(r[0], 10)), 4, 0, signed: false);
            if (occ < 1 || occ > ak.Schedule.Count) continue;
            var s = ak.Schedule[occ - 1];
            s.Status = Slot.Text(r[3], 12);
            s.Journal = Slot.Text(r[4], 30);
            if (s.Status == "POSTED") s.Amount = CobolNumeric.Store(Slot.NumVal(Slot.Text(r[2], 22)), 11, 2);
        }
    }

    // 2000-NEXT-OCC
    private async Task NextOccAsync(CaRequest req, CaHeader ca, AccrRelArea ak, CancellationToken ct)
    {
        await ScheduleAsync(req, ca, ak);
        ak.OccurrenceNo = 0;
        var next = ak.Schedule.FirstOrDefault(s => s.Status != "POSTED");
        if (next is null)
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "ACR0020";
            ca.Message = "The schedule is complete. There is no further occ";
            ca.MsgCount++;
            return;
        }
        ak.OccurrenceNo = next.OccNo;
        ak.NextDueDate = next.Date;
        ak.ReleaseAmount = next.Amount;
        ak.PostingDate = ak.NextDueDate;
        await CheckPeriodAsync(req, ca, ak, ct);
    }

    // 2100-CHECK-PERIOD
    private async Task CheckPeriodAsync(CaRequest req, CaHeader ca, AccrRelArea ak, CancellationToken ct)
    {
        var pc = await kernel.PeriodCheckAsync(req.Callee("CHECK", site: false, correlation: false), ak.PostingDate, ak.PostingDate, "POST", ct);
        ak.PeriodStatus = CobolText.Trimmed(pc.PeriodStatus, 8);
        ak.FiscalYear = pc.FiscalYear;
        ak.PeriodNo = pc.PeriodNo;
        if (pc.Result.ReturnCode > 4)
        {
            ak.Blocked = "Y";
            ak.BlockReason = CobolText.Trimmed(pc.Result.Message, 200);
            ca.ReasonCode = pc.Result.ReasonCode;
            ca.Message = pc.Result.Message;
            ca.ReturnCode = pc.Result.ReturnCode;
            ca.MsgCount++;
        }
    }

    // 3000-RELEASE
    private async Task ReleaseAsync(CaRequest req, CaHeader ca, AccrRelArea ak, CancellationToken ct)
    {
        if (ak.OccurrenceNo == 0)
        {
            await NextOccAsync(req, ca, ak, ct);
            if (ca.Failed || ak.OccurrenceNo == 0) return;
        }
        else
        {
            await ScheduleAsync(req, ca, ak);
            if (ak.OccurrenceNo > ak.Schedule.Count)
            {
                ca.ReasonCode = "ACR0030";
                ca.Message = "That occurrence is beyond the approved schedu";
                Reject(ca);
                return;
            }
            ak.PostingDate = ak.Schedule[ak.OccurrenceNo - 1].Date;
            ak.ReleaseAmount = ak.Schedule[ak.OccurrenceNo - 1].Amount;
            await CheckPeriodAsync(req, ca, ak, ct);
        }
        var s = ak.Schedule[ak.OccurrenceNo - 1];
        if (s.Status == "POSTED")
        {
            ak.AlreadyPosted = "Y";
            ak.JournalNumber = s.Journal;
            ca.ReturnCode = 4;
            ca.ReasonCode = "ACR0031";
            ca.Message = CobolText.Trimmed("Occurrence " + Disp.U(ak.OccurrenceNo, 4) + " has already been posted as journal " +
                                           ak.JournalNumber.Trim() + ". It is not posted a second time", 200);
            ca.MsgCount++;
            return;
        }
        if (ak.Blocked == "Y")
        {
            await BlockOccurrenceAsync(req, ca, ak, ct);
            return;
        }
        if (ak.RecurStatus != "ACTIVE")
        {
            ca.ReasonCode = "ACR0032";
            ca.Message = CobolText.Trimmed("Recurring journal " + ak.RecurCode.Trim() + " is " + ak.RecurStatus.Trim() + " and does not release", 200);
            Reject(ca);
            return;
        }
        if (ak.Kind != "RECURRING" && ak.Remaining <= 0)
        {
            ca.ReasonCode = "ACR0033";
            ca.Message = "The approved source balance is fully released. No";
            Reject(ca);
            return;
        }
        await ReadLinesAsync(req, ca, ak);
        if (ca.Failed) return;
        _correl = CobolText.Trimmed("RJ-" + req.Company.Trim() + "-" + ak.RecurCode.Trim() + "-" + Disp.U(ak.OccurrenceNo, 4), 36);
        await StageLinesAsync(req, ca, ak);
        if (ca.Failed) return;
        await PostAsync(req, ca, ak, ct);
        if (ca.Failed) return;
        await RecordOccurrenceAsync(req, ca, ak);
        await UpdateHeaderAsync(req, ca, ak);
        ca.Message = CobolText.Trimmed("Occurrence " + Disp.U(ak.OccurrenceNo, 4) + " released as journal " + ak.JournalNumber.Trim() +
                                       " for " + FinEdit.Money(ak.ReleaseAmount) + " GBP", 200);
    }

    // 3100-READ-LINES / 3150-TAKE-LINE (at most 20 template lines)
    private async Task ReadLinesAsync(CaRequest req, CaHeader ca, AccrRelArea ak)
    {
        _lines.Clear();
        List<object?[]> rows;
        try
        {
            rows = await sql.RowsAsync(
                "SELECT CAST(l.LineNum AS varchar(10)), l.AccountCode, l.DebitAmount, l.CreditAmount, ISNULL(l.DimCostCentre,''), " +
                "ISNULL(l.DimProject,''), ISNULL(l.Narrative,'') FROM fin.RecurringLine l WHERE l.CompanyCode = @p1 AND " +
                "l.RecurringCode = @p2 ORDER BY l.LineNum", 20, req.Company, ak.RecurCode);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        foreach (var r in rows)
        {
            _lines.Add(new TemplateLine
            {
                Account = Slot.Text(r[1], 12),
                Debit = CobolNumeric.Store(Slot.NumVal(Slot.Text(r[2], 22)), 11, 2),
                Credit = CobolNumeric.Store(Slot.NumVal(Slot.Text(r[3], 22)), 11, 2),
                Cc = Slot.Text(r[4], 20),
                Proj = Slot.Text(r[5], 20),
                Narr = Slot.Text(r[6], 80),
            });
        }
        if (_lines.Count == 0)
        {
            ca.ReasonCode = "ACR0034";
            ca.Message = "The recurring journal has no template lines";
            Reject(ca);
        }
    }

    // 3300-STAGE-LINES / 3350-STAGE-ONE: the occurrence share of each template line, rounded half away from zero
    private async Task StageLinesAsync(CaRequest req, CaHeader ca, AccrRelArea ak)
    {
        var scale = 1m;
        if (ak.Kind != "RECURRING" && ak.SourceAmount != 0) scale = CobolNumeric.Store(ak.ReleaseAmount / ak.SourceAmount, 5, 8);
        try
        {
            await sql.ExecAsync("DELETE fin.JournalStage WHERE CorrelationId = @p1", _correl);
        }
        catch (SqlioException)
        {
            // the legacy paragraph does not test the DELETE
        }
        for (var i = 0; i < _lines.Count && !ca.Failed; i++)
        {
            var l = _lines[i];
            var dr = CobolNumeric.Store(CobolNumeric.RoundAway(l.Debit * scale, 2), 13, 2);
            var cr = CobolNumeric.Store(CobolNumeric.RoundAway(l.Credit * scale, 2), 13, 2);
            try
            {
                await sql.ExecAsync(
                    "INSERT fin.JournalStage (CorrelationId, LineNum, CompanyCode, AccountCode, DebitAmount, CreditAmount, CurrencyCode, " +
                    "DimCostCentre, DimProject, Narrative, CreatedBy) VALUES (@p1, @p2, @p3, @p4, @p5, @p6, 'GBP', NULLIF(@p7,''), " +
                    "NULLIF(@p8,''), @p9, @p10)",
                    _correl, CobolNumeric.SqlioInteger(i + 1), req.Company, l.Account, CobolNumeric.SqlioNumber(dr), CobolNumeric.SqlioNumber(cr),
                    l.Cc, l.Proj, l.Narr, req.User);
            }
            catch (SqlioException ex)
            {
                SqlFailed(ca, ex);
            }
        }
    }

    // 3400-POST: JNLPOST POST of the staged lines
    private async Task PostAsync(CaRequest req, CaHeader ca, AccrRelArea ak, CancellationToken ct)
    {
        var jp = new JnlPostArea
        {
            PostingDate = ak.PostingDate,
            DocumentDate = ak.PostingDate,
            Currency = "GBP",
            SourceModule = "FIN",
            SourceRef = ak.RecurCode,
            EventCode = ak.EventCode,
            SeriesCode = "JNL",
            JournalType = ak.Kind == "ACCRUAL" ? "ACCRUAL" : ak.Kind == "PREPAY" ? "PREPAY" : "RECUR",
            Description = CobolText.Trimmed(ak.Name.Trim() + " occurrence " + Disp.U(ak.OccurrenceNo, 4), 200),
            EvidenceRef = CobolText.Trimmed("fin.RecurringJournal/" + ak.RecurCode.Trim(), 120),
        };
        var jh = await jnlpost.RunAsync(new CaRequest("POST", req.Company, req.Site, req.User, req.BusinessDate, _correl), jp, ct);
        ak.JournalNumber = CobolText.Trimmed(jp.NewJournalNo, 30);
        if (jh.ReturnCode > 4)
        {
            ca.ReasonCode = jh.ReasonCode;
            ca.Message = jh.Message;
            ca.ReturnCode = jh.ReturnCode;
            ca.MsgCount++;
        }
    }

    // 3500-RECORD-OCC
    private async Task RecordOccurrenceAsync(CaRequest req, CaHeader ca, AccrRelArea ak)
    {
        var occ = Disp.U(ak.OccurrenceNo, 4);
        try
        {
            await sql.ExecAsync(
                "INSERT fin.RecurringOccurrence (CompanyCode, RecurringCode, OccurrenceNo, ScheduledDate, PostingDate, FiscalYear, PeriodNo, " +
                "Amount, Status, JournalNumber, CorrelationId, PostedBy, PostedAt) SELECT @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, 'POSTED', " +
                "@p9, @p10, @p11, SYSDATETIME() WHERE NOT EXISTS (SELECT 1 FROM fin.RecurringOccurrence x WHERE x.CompanyCode = @p12 " +
                "AND x.RecurringCode = @p13 AND x.OccurrenceNo = @p14)",
                req.Company, ak.RecurCode, occ, Disp.U(ak.PostingDate, 8), Disp.U(ak.PostingDate, 8), Disp.U(ak.FiscalYear, 4),
                Disp.U(ak.PeriodNo, 2), CobolNumeric.SqlioNumber(ak.ReleaseAmount), ak.JournalNumber, _correl, req.User, req.Company,
                ak.RecurCode, occ);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
        }
    }

    // 3600-UPDATE-HEADER
    private async Task UpdateHeaderAsync(CaRequest req, CaHeader ca, AccrRelArea ak)
    {
        try
        {
            await sql.ExecAsync(
                "UPDATE r SET OccurrencesPosted = x.Posted, ReleasedAmount = x.Released, RemainingAmount = ISNULL(r.SourceAmount,0) - " +
                "x.Released, LastOccurrenceNo = x.MaxOcc, Status = CASE WHEN r.OccurrencesPlanned IS NOT NULL AND x.Posted >= " +
                "r.OccurrencesPlanned THEN 'ENDED' ELSE r.Status END, ModifiedBy = @p1, ModifiedAt = SYSDATETIME() FROM " +
                "fin.RecurringJournal r CROSS APPLY (SELECT COUNT(*) AS Posted, ISNULL(SUM(o.Amount),0) AS Released, " +
                "ISNULL(MAX(o.OccurrenceNo),0) AS MaxOcc FROM fin.RecurringOccurrence o WHERE o.CompanyCode = r.CompanyCode AND " +
                "o.RecurringCode = r.RecurringCode AND o.Status = 'POSTED') x WHERE r.CompanyCode = @p2 AND r.RecurringCode = @p3",
                req.User, req.Company, ak.RecurCode);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        ak.OccPosted = (ak.OccPosted + 1) % 10_000;
        ak.ReleasedTotal = CobolNumeric.Store(ak.ReleasedTotal + ak.ReleaseAmount, 13, 2);
        ak.Remaining = CobolNumeric.Store(ak.SourceAmount - ak.ReleasedTotal, 13, 2);
    }

    // 3900-BLOCK-OCCURRENCE: recorded BLOCKED (not redated) and a HOLD exception for FINCTRL; RC 06 ACR0040
    private async Task BlockOccurrenceAsync(CaRequest req, CaHeader ca, AccrRelArea ak, CancellationToken ct)
    {
        var occ = Disp.U(ak.OccurrenceNo, 4);
        try
        {
            await sql.ExecAsync(
                "INSERT fin.RecurringOccurrence (CompanyCode, RecurringCode, OccurrenceNo, ScheduledDate, Amount, Status, BlockReason) " +
                "SELECT @p1, @p2, @p3, @p4, @p5, 'BLOCKED', @p6 WHERE NOT EXISTS (SELECT 1 FROM fin.RecurringOccurrence x WHERE " +
                "x.CompanyCode = @p7 AND x.RecurringCode = @p8 AND x.OccurrenceNo = @p9)",
                req.Company, ak.RecurCode, occ, Disp.U(ak.PostingDate, 8), CobolNumeric.SqlioNumber(ak.ReleaseAmount), ak.BlockReason,
                req.Company, ak.RecurCode, occ);
        }
        catch (SqlioException)
        {
            // the legacy paragraph does not test the INSERT
        }
        await kernel.RaiseExceptionAsync(req.Callee("RAISE", site: false, correlation: false), new ExceptionEntry(
            Module: "FIN", Severity: "HOLD", Category: "RECURRING OCCURRENCE BLOCKED", MessageText: ak.BlockReason,
            DocumentType: "RECURJNL", DocumentRef: ak.RecurCode, OwnerUser: "", OwnerRole: "FINCTRL", Priority: 2, DueAt: 0,
            AmountGbp: ak.ReleaseAmount, Quantity: 0), ct);
        ca.ReturnCode = 6;
        ca.ReasonCode = "ACR0040";
        ca.Message = CobolText.Trimmed("Occurrence " + occ + " cannot be released: " + ak.BlockReason.Trim() +
                                       " It is recorded as blocked and owned, not redated", 200);
        ca.MsgCount++;
    }

    // 9000-REJECT
    private void Reject(CaHeader ca)
    {
        _rejected = true;
        ca.ReturnCode = 8;
        ca.MsgCount++;
    }

    // 9300-SQL-FAILED
    private void SqlFailed(CaHeader ca, SqlioException ex)
    {
        ca.ReasonCode = "SQL0001";
        var text = CobolText.Fit(ex.DriverText, 512)[..200];
        ca.Message = CobolText.IsSpaces(text) ? "SQL failure in " + Program : text;
        ca.ReturnCode = 12;
        _rejected = true;
        ca.MsgCount++;
    }

    private static int Date8(decimal v) => (int)CobolNumeric.Store(v, 8, 0, signed: false);
}
