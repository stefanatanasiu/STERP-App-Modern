using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Fin.Batch;

/// <summary>
/// RECURJNL (cobol/batch/fin/RECURJNL.cob; FR-STERP-FIN-055): ACTIVE recurring templates due on or before ASAT (optionally
/// one CODE), each asked ACCRREL NEXTOCC and, in MODE=COMMIT, released through ACCRREL RELEASE (JNLPOST). RC 4 when any
/// release is blocked; MODE other than PREVIEW/COMMIT RC 8. RD: CONTROLS FINAL, PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9,
/// LAST DETAIL 52, FOOTING 58. No unit of work: each ACCRREL/JNLPOST statement autocommits.
/// </summary>
internal sealed class RecurjnlJob(FinBatch b)
{
    private sealed record Item(string Code, string Name, string Kind, int Due, decimal Source, decimal Remain);

    private string _mode = "";
    private string _code = "";
    private readonly List<Item> _list = [];

    private string _dCode = "";
    private string _dName = "";
    private string _dKind = "";
    private int _dOcc;
    private string _dDue = "";
    private decimal _dAmount, _dRemain;
    private string _dJnl = "";
    private string _dOutcome = "";
    private string _dReason = "";
    private decimal _tReleased;
    private long _tSkipped, _tBlocked, _tPosted;
    private string _status = "";

    public static async Task<int> RunAsync(FinBatch b) => await new RecurjnlJob(b).MainAsync();

    private async Task<int> MainAsync()
    {
        ReadArgs();
        if (b.Rc > 4) return b.End();
        if (!await b.OpenSpoolAsync("RECURJNL")) return b.End();
        var w = b.Report(60, 1, 9, 52, null,
        [
            RwLine.PlusN(2, () => new string('=', 132)),
            RwLine.Plus1(() => new RwText().Put(1, "Released this run   ", 20).Num(22, _tPosted, "ZZZ,ZZ9").Put(34, "occurrence(s) ", 14)
                .Num(50, _tReleased, "Z,ZZZ,ZZZ,ZZ9.99-").Text),
            RwLine.Plus1(() => new RwText().Put(1, "Already posted      ", 20).Num(22, _tSkipped, "ZZZ,ZZ9")
                .Put(34, "(a repeated run never posts twice)", 40).Text),
            RwLine.Plus1(() => new RwText().Put(1, "Blocked             ", 20).Num(22, _tBlocked, "ZZZ,ZZ9")
                .Put(34, "(closed period or release beyond the source)", 50).Text),
            RwLine.PlusN(2, () => new RwText().Put(3, _status, 60).Text),
        ]);
        w.Initiate();
        await SelectDueAsync();
        await ReleaseAllAsync(w);
        w.Terminate();
        b.CloseSpool();
        return b.End();
    }

    /// <summary>0100-READ-ARGS: CODE, MODE default PREVIEW.</summary>
    private void ReadArgs()
    {
        _code = CobolText.Trimmed(b.Arg("CODE"), 20);
        _mode = CobolText.IsSpaces(b.Mode) ? "PREVIEW" : b.Mode;
        if (_mode != "COMMIT" && _mode != "PREVIEW")
        {
            b.Rc = 8;
            b.Display("STERP-E-0101 MODE must be PREVIEW or COMMIT");
            return;
        }
        b.Display("STERP-I-0100 RECURJNL " + _mode.Trim() + " run for " + b.Co.Trim());
    }

    /// <summary>1000-SELECT-DUE: at most 200 ACTIVE templates due on or before ASAT.</summary>
    private async Task SelectDueAsync()
    {
        List<object?[]> rows;
        try
        {
            rows = await b.Sql.RowsAsync(
                "SELECT RecurringCode, Name, JournalKind, CONVERT(char(8),NextDueDate,112), CONVERT(varchar(24),SourceAmount), " +
                "CONVERT(varchar(24),RemainingAmount) FROM fin.RecurringJournal WHERE CompanyCode = @p1 AND Status = 'ACTIVE' AND " +
                "NextDueDate IS NOT NULL AND CONVERT(char(8),NextDueDate,112) <= @p2 AND (@p3 = '' OR RecurringCode = @p4) ORDER BY " +
                "NextDueDate, RecurringCode", 0, b.Co, Disp.U(b.AsAt, 8), _code, _code);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.SqlError(ex);
            return;
        }
        foreach (var r in rows.Take(200))
        {
            _list.Add(new Item(
                CobolText.Trimmed(Slot.Text(r[0], 120), 20),
                CobolText.Trimmed(Slot.Text(r[1], 120), 80),
                CobolText.Trimmed(Slot.Text(r[2], 120), 10),
                (int)CobolNumeric.Store(Slot.NumVal(Slot.Text(r[3], 120)), 8, 0, signed: false),
                CobolNumeric.Store(Slot.Num(r[4]), 11, 2),
                CobolNumeric.Store(Slot.Num(r[5]), 11, 2)));
        }
        b.Display("STERP-I-1002 " + Disp.S(_list.Count, 4) + " schedule(s) due");
    }

    /// <summary>2000-RELEASE-ALL.</summary>
    private async Task ReleaseAllAsync(GnuReportWriter w)
    {
        b.Heading.Title = "Recurring journal and accrual release";
        b.Heading.Subtitle = "Schedules due on or before " + FinBatch.UkDate(b.AsAt);
        b.Heading.AsAtText = "Mode " + _mode.Trim();
        w.Generate(
            RwLine.Plus1(() => new RwText().Put(1, "Code", 4).Put(22, "Name", 4).Put(57, "Kind", 4).Put(68, "Occ", 3).Put(74, "Due date", 8)
                .Put(88, "Amount", 6).Put(101, "Remaining", 9).Put(114, "Outcome", 7).Text),
            RwLine.Plus1(() => new string('-', 132)));
        var deLine = RwLine.Plus1(() => new RwText().Put(1, _dCode, 20).Put(22, _dName, 34).Put(57, _dKind, 10).Num(68, _dOcc, "ZZZ9")
            .Put(74, _dDue, 10).Num(85, _dAmount, "ZZZ,ZZ9.99-").Num(98, _dRemain, "ZZZ,ZZ9.99-").Put(112, _dOutcome, 16).Text);
        var deWhy = RwLine.Plus1(() => new RwText().Put(22, "          ", 10).Put(24, _dJnl, 20).Put(46, _dReason, 50).Text);
        foreach (var item in _list) await OneScheduleAsync(w, item, deLine, deWhy);
        _status = _mode == "PREVIEW"
            ? CobolText.Trimmed("PREVIEW ONLY - nothing has been posted", 20)
            : CobolText.Trimmed("Postings are complete and committed", 20);
        b.Posted = _tPosted;
        b.Blocked = _tBlocked;
        b.RowsOut = _list.Count;
        if (_tBlocked > 0)
        {
            b.Rc = 4;
            b.Display("STERP-W-2001 " + Disp.S(_tBlocked, 5) + " schedule(s) blocked - see the exception log");
        }
        b.Display("STERP-I-2002 " + Disp.S(_tPosted, 5) + " released, " + Disp.S(_tSkipped, 5) + " already posted");
    }

    /// <summary>2100-ONE-SCHEDULE: NEXTOCC, then the outcome (D-OCC keeps the previous value on a NOT DUE line).</summary>
    private async Task OneScheduleAsync(GnuReportWriter w, Item item, RwLine deLine, RwLine deWhy)
    {
        _dCode = item.Code;
        _dName = CobolText.Trimmed(item.Name, 34);
        _dKind = item.Kind;
        _dDue = FinBatch.UkDate(item.Due);
        _dJnl = "";
        _dReason = "";
        var next = new AccrRelArea { RecurCode = item.Code, PostingDate = b.AsAt };
        var ah = await Accrrel(new CaRequest("NEXTOCC", b.Co, "", b.User, b.AsAt, ""), next);
        if (ah.ReturnCode > 4)
        {
            _dOutcome = "NOT DUE";
            _dReason = CobolText.Trimmed(ah.Message, 50);
            _dAmount = 0;
            _dRemain = item.Remain;
            w.Generate(deLine);
            w.Generate(deWhy);
            return;
        }
        _dOcc = next.OccurrenceNo;
        _dAmount = next.ReleaseAmount;
        _dRemain = CobolNumeric.Store(next.Remaining, 11, 2);
        if (next.AlreadyPosted == "Y")
        {
            _dOutcome = "ALREADY POSTED";
            _dJnl = CobolText.Trimmed(next.JournalNumber, 20);
            _dReason = "repeat run - no second posting made";
            _tSkipped = (_tSkipped + 1) % 100_000;
            w.Generate(deLine);
            w.Generate(deWhy);
            return;
        }
        if (next.Blocked == "Y")
        {
            _dOutcome = "BLOCKED";
            _dReason = CobolText.Trimmed(next.BlockReason, 50);
            _tBlocked = (_tBlocked + 1) % 100_000;
            w.Generate(deLine);
            w.Generate(deWhy);
            return;
        }
        if (_mode == "PREVIEW")
        {
            _dOutcome = "DUE (PREVIEW)";
            w.Generate(deLine);
            return;
        }
        await ReleaseAsync(item, next.OccurrenceNo);
        w.Generate(deLine);
        if (!CobolText.IsSpaces(_dReason)) w.Generate(deWhy);
    }

    /// <summary>2300-RELEASE: ACCRREL RELEASE in COMMIT mode for the occurrence NEXTOCC named.</summary>
    private async Task ReleaseAsync(Item item, int occurrenceNo)
    {
        var correl = CobolText.Trimmed("RECURJNL-" + item.Code.Trim() + "-", 36);
        correl = CobolText.Trimmed(correl + Disp.U(occurrenceNo, 6), 36);
        var rel = new AccrRelArea { RecurCode = item.Code, OccurrenceNo = _dOcc, PostingDate = b.AsAt, Mode = "COMMIT" };
        var ah = await Accrrel(new CaRequest("RELEASE", b.Co, "", b.User, b.AsAt, correl), rel);
        if (ah.ReturnCode is 0 or 4)
        {
            if (rel.AlreadyPosted == "Y")
            {
                _dOutcome = "ALREADY POSTED";
                _tSkipped = (_tSkipped + 1) % 100_000;
            }
            else
            {
                _dOutcome = "RELEASED";
                _tPosted = (_tPosted + 1) % 100_000;
                _tReleased = CobolNumeric.Store(_tReleased + rel.ReleaseAmount, 13, 2);
            }
            _dJnl = CobolText.Trimmed(rel.JournalNumber, 20);
            _dRemain = CobolNumeric.Store(rel.Remaining, 11, 2);
        }
        else
        {
            _dOutcome = "BLOCKED";
            _dReason = CobolText.Trimmed(ah.Message, 50);
            _tBlocked = (_tBlocked + 1) % 100_000;
        }
    }

    private Task<CaHeader> Accrrel(CaRequest req, AccrRelArea area) => b.Ctx.Get<AccrualRelease>().RunAsync(req, area, b.Ct);
}
