using Sterp.Modules.Po.Programs;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Po.Jobs;

/// <summary>
/// OVERDUE (cobol/batch/po/OVERDUE.cob; FR-STERP-PO-042): overdue purchase orders and exceptions (RPT-022) - open schedules
/// late against the original or the current promise by at least DAYS (default 1) at ASAT, by buyer; RC 4 when anything is
/// listed.
/// </summary>
internal static class OverdueJob
{
    public static readonly JobDefinition Definition = new()
    {
        JobCode = "OVERDUE",
        Slice = "STERP-PO",
        SubmitOperationId = "po_runOverdue",
        ParameterOrder = ["CO", "SITE", "SUPP", "BUYER", "DAYS", "ASAT", "USER"],
        SelfRegistration = pre =>
        {
            var a = Arguments.Read(pre.Args, DateTime.Now);
            return PoBatch.JoblogStart("OVERDUE", a.Co, a.User, pre.Args.Text);
        },
        // 0500-JOB-END INITIALIZEs WS-JOBLOG before JOBLOG END (OVERDUE.cob:543-561), so JOBLOG refuses the END without a run id
        // (JOBLOG.cob:100-107, JOB0002): the program's own row stays RUNNING with no return code or spool (SME-009); the exit
        // code is the run's. REQGEN/POPIPE/PODOC/SUPPPERF share the legacy defect but keep the END their binding requirements
        // and parity tests state (PODOC's reprint detection reads earlier OK/WARNING runs) - carried as an Agent 06/07 finding.
        EndValues = (ctx, exit) => new JobEnd(null, exit),
        Execute = ctx => new Run(ctx).MainAsync(),
    };

    private sealed record Arguments(string Co, string Site, string Supp, string Buyer, string User, string AsAt, int MinDays)
    {
        /// <summary>0100-READ-ARGS: CO, SITE, USER, ASAT, SUPP, BUYER, DAYS (default 1); CO STC, USER BUYER1, ASAT today.</summary>
        public static Arguments Read(Sterp.Platform.Kernel.Jobs.CommandLineArgs a, DateTime now)
        {
            var co = Cob.X(a.Co, 10);
            var user = Cob.X(a.User, 20);
            var asat = Cob.X(a.AsAt, 8);
            var supp = a.Get("SUPP") is { } s ? Cob.X(s, 20) : "";
            var buyer = a.Get("BUYER") is { } b ? Cob.X(b, 20) : "";
            var days = a.Get("DAYS") is { } d ? (int)Cob.T(Cob.NumVal(d), 4, 0) : 1;
            if (Cob.Spaces(co)) co = "STC";
            if (Cob.Spaces(user)) user = "BUYER1";
            if (Cob.Spaces(asat)) asat = Cob.D8(PoBatch.Ymd(now));
            return new Arguments(co, Cob.X(a.Site, 10), supp, buyer, user, asat, days);
        }
    }

    private sealed class Run(JobRunContext ctx)
    {
        private readonly DateTime _now = PoBatch.Now();
        private Arguments _a = null!;
        private int _rc;
        private int _rowsRead;

        // WS-DET
        private string _buyer = "";
        private string _supp = "";
        private string _order = "";
        private decimal _line;
        private string _item = "";
        private decimal _outstanding;
        private string _origProm = "";
        private string _currProm = "";
        private decimal _daysOrig;
        private decimal _daysCurr;
        private decimal _valueGbp;
        private string _status = "";
        private string _exception = "";
        private string _salesOrder = "";
        private string _jobRef = "";
        private string _actionNote = "";

        // WS-TOT
        private int _tLines;
        private decimal _tValueGbp;
        private int _tOnlyOrig;
        private int _tNoAck;
        private int _tCustomerHit;

        // CF-BUYER SUM counters (SUM D-ONE, SUM D-VALUE-GBP)
        private decimal _sumOne;
        private decimal _sumValue;

        public async Task<int> MainAsync()
        {
            var ct = ctx.Cancellation;
            _a = Arguments.Read(ctx.Args, _now);
            ctx.Display("STERP-I-1720 OVERDUE late purchase orders for " + Cob.Trim(_a.Co));
            var sql = PoBatch.Sql(ctx);
            var spool = PoBatch.OpenSpool(ctx, "OVERDUE", _a.Co, out var pathFailed, out var spoolName);
            if (!pathFailed) ctx.SpoolFile = spoolName;
            if (spool is null)
            {
                ctx.Display(pathFailed ? "STERP-E-1721 cannot build the spool path" : "STERP-E-1722 cannot open " + CobolText.Fit(spoolName, 200));
                ctx.RowsProcessed = 0;
                return 12;
            }
            var subtitle = Cob.Spaces(_a.Buyer)
                ? "All buyers, at least " + Cob.Trim(PoBatch.Ed(_a.MinDays, "Z(3)9")) + " day(s) late"
                : "Buyer " + Cob.Trim(_a.Buyer);
            var heading = await PoBatch.HeadingAsync(sql, _a.Co, "RPT-022", "Overdue purchase orders and exceptions (PO-022)", subtitle,
                PoBatch.AsAt(_a.AsAt), _a.User, _now, ct);
            using (spool)
            {
                var rw = Writer(spool, heading);
                rw.Initiate();
                await ProduceAsync(sql, rw, ct);
                rw.Terminate();
            }
            ctx.Display("STERP-I-1724 " + PoBatch.Disp(_tLines, 7) + " overdue schedule(s) from " + PoBatch.Disp(_rowsRead, 9) + " open schedule(s)");
            if (_tOnlyOrig > 0)
            {
                ctx.Display("STERP-W-1725 " + PoBatch.Disp(_tOnlyOrig, 7) + " line(s) late on the original promise only");
            }
            if (_tLines > 0 && _rc < 4) _rc = 4;
            ctx.AnnounceSpool(spoolName);
            ctx.RowsProcessed = _tLines;
            return _rc;
        }

        /// <summary>R-OVERDUE: CONTROLS ARE FINAL D-BUYER; CH-BUYER, CF-BUYER, CF-FINAL.</summary>
        private GnuReportWriter Writer(SpoolWriter spool, ReportHeading heading)
        {
            var buyer = new RwControl(() => _buyer, v => _buyer = v, 20)
            {
                Heading =
                [
                    RwLine.PlusN(2, () => PoBatch.Line().Lit(1, "Buyer: ").Put(8, _buyer, 20)
                        .Lit(32, "Days late are shown against the ORIGINAL and").Lit(77, "the CURRENT promise - re-dating never clears it.").Text),
                    RwLine.Plus1(() => PoBatch.Line().Lit(1, "Order").Lit(16, "Line").Lit(21, "Supplier").Lit(34, "Item").Lit(53, "Outstandng")
                        .Lit(64, "Orig prom ").Lit(75, "Curr prom ").Lit(86, "Lt/Or").Lit(92, "Lt/Cu").Lit(98, " Value at risk")
                        .Lit(112, "Status").Lit(125, "Flag").Text),
                    RwLine.Plus1(() => PoBatch.Line().Lit(1, PoBatch.Rule('-')).Text),
                ],
                Footing =
                [
                    RwLine.Plus1(() =>
                    {
                        var text = PoBatch.Line().Lit(5, "Buyer total late lines: ").Num(29, _sumOne, "ZZZZ9").Lit(40, "Value at risk: ")
                            .Num(56, _sumValue, "Z(11)9.99-").Text;
                        _sumOne = 0;
                        _sumValue = 0;
                        return text;
                    }),
                ],
            };
            RwLine[] final =
            [
                RwLine.PlusN(2, () => PoBatch.Line().Lit(1, PoBatch.Rule('=')).Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(5, "Overdue lines in total  : ").Num(31, _tLines, "ZZZZ9")
                    .Lit(45, "Value at risk           : ").Num(71, _tValueGbp, "Z(11)9.99-").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(5, "Late on original only   : ").Num(31, _tOnlyOrig, "ZZZZ9")
                    .Lit(45, "these were re-dated and look on time today").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(5, "Never acknowledged      : ").Num(31, _tNoAck, "ZZZZ9")
                    .Lit(45, "issued but no supplier acknowledgement held").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(5, "Holding up a customer   : ").Num(31, _tCustomerHit, "ZZZZ9")
                    .Lit(45, "linked to a sales order or a works job").Text),
                RwLine.PlusN(2, () => PoBatch.Line().Lit(5, "Every line names an owner.  A line with no buyer")
                    .Lit(68, "is listed under (unassigned) and is itself an").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(5, "exception: nobody is chasing it.").Text),
            ];
            return PoBatch.Report(spool, heading, [buyer], final);
        }

        /// <summary>DE-LINE.</summary>
        private RwLine[] Detail() =>
        [
            RwLine.Plus1(() => PoBatch.Line().Put(1, _order, 14).Num(16, _line, "ZZZ9").Put(21, _supp, 12).Put(34, _item, 18)
                .Num(53, _outstanding, "Z(6)9.999").Put(64, _origProm, 10).Put(75, _currProm, 10).Num(86, _daysOrig, "ZZZ9-")
                .Num(92, _daysCurr, "ZZZ9-").Num(98, _valueGbp, "Z(9)9.99-").Put(112, _status, 12).Put(125, _exception, 8).Text),
            RwLine.Plus1(() => PoBatch.Line().Lit(5, "Impact: ").Put(13, _salesOrder, 14).Put(28, _jobRef, 12).Lit(41, "Note: ")
                .Put(47, _actionNote, 80).Text),
        ];

        /// <summary>1000-PRODUCE: one row per open schedule past its promise (both promises, CR1402).</summary>
        private async Task ProduceAsync(PoSql sql, GnuReportWriter rw, CancellationToken ct)
        {
            var r = await sql.Cursor(
                "SELECT ISNULL(NULLIF(o.BuyerUser,''),'(unassigned)'), o.SupplierCode, l.OrderNumber, CAST(l.LineNum AS varchar(10)), " +
                "ISNULL(l.ItemCode,LEFT(l.Description,18)), CAST(s.Quantity - s.ReceivedQty AS varchar(30)), ISNULL(CONVERT(char(8)," +
                "s.OriginalConfirmedDate,112),''), ISNULL(CONVERT(char(8),s.CurrentConfirmedDate,112),''), CAST(DATEDIFF(day," +
                "ISNULL(s.OriginalConfirmedDate,s.RequestedDate),CAST(? AS date)) AS varchar(10)), CAST(DATEDIFF(day," +
                "ISNULL(s.CurrentConfirmedDate,s.RequestedDate),CAST(? AS date)) AS varchar(10)), CAST((s.Quantity - s.ReceivedQty) * " +
                "l.UnitPrice / NULLIF(l.PricePerQty,0) AS varchar(30)), o.Status, CASE WHEN o.Status = 'ISSUED' AND o.AcknowledgedAt " +
                "IS NULL THEN 'NOACK' WHEN s.CurrentConfirmedDate > s.OriginalConfirmedDate THEN 'REDATED' WHEN s.ChangeCount > 0 THEN " +
                "'RESCHED' ELSE 'LATE' END, ISNULL(NULLIF(l.LinkedSalesOrderNumber,''),ISNULL(o.LinkedSalesOrderNumber,'')), " +
                "ISNULL(l.JobRef,''), ISNULL((SELECT TOP 1 ISNULL(a.ActionNote,ISNULL(a.Reason,'')) FROM po.OrderAmendment a WHERE " +
                "a.CompanyCode = l.CompanyCode AND a.OrderNumber = l.OrderNumber AND (a.LineNum IS NULL OR a.LineNum = l.LineNum) " +
                "ORDER BY a.AmendmentNo DESC),'') FROM po.PurchaseSchedule s JOIN po.PurchaseOrderLine l ON l.PurchaseOrderLineId = " +
                "s.PurchaseOrderLineId JOIN po.PurchaseOrder o ON o.PurchaseOrderId = l.PurchaseOrderId WHERE s.CompanyCode = ? AND " +
                "l.Status = 'OPEN' AND s.Status IN ('OPEN','PARTIAL') AND s.Quantity > s.ReceivedQty AND o.Status IN ('APPROVED'," +
                "'ISSUED','ACKNOWLEDGED','AMENDED') AND (? = '' OR o.SupplierCode = ?) AND (? = '' OR ISNULL(o.BuyerUser,'') = ?) AND " +
                "(? = '' OR o.SiteCode = ?) ORDER BY 1, o.SupplierCode, l.OrderNumber, l.LineNum, s.ScheduleNo",
                [PoSql.Tx(_a.AsAt), PoSql.Tx(_a.AsAt), PoSql.Tx(_a.Co), PoSql.Tx(_a.Supp), PoSql.Tx(_a.Supp), PoSql.Tx(_a.Buyer),
                 PoSql.Tx(_a.Buyer), PoSql.Tx(_a.Site), PoSql.Tx(_a.Site)], Enumerable.Repeat(100, 16).ToArray(), ct);
            if (r.Error)
            {
                _rc = 12;
                ctx.Display("STERP-E-1723 " + Cob.Trim(r.Err));
                return;
            }
            var detail = Detail();
            foreach (var row in r.Rows)
            {
                _rowsRead++;
                OneRow(row, rw, detail);
            }
        }

        /// <summary>1100-ONE-ROW: reported when it has missed EITHER promise by at least DAYS.</summary>
        private void OneRow(SqlioRow row, GnuReportWriter rw, RwLine[] detail)
        {
            _daysOrig = Cob.T(row.NumVal(8), 5, 0);
            _daysCurr = Cob.T(row.NumVal(9), 5, 0);
            if (_daysOrig < _a.MinDays && _daysCurr < _a.MinDays) return;
            _buyer = CobolText.Fit(row.Raw(0), 20);
            _supp = CobolText.Fit(row.Raw(1), 12);
            _order = CobolText.Fit(row.Raw(2), 14);
            _line = Cob.T(row.NumVal(3), 4, 0, signed: false);
            _item = CobolText.Fit(row.Raw(4), 18);
            _outstanding = Cob.T(row.NumVal(5), 11, 3);
            _origProm = PoBatch.DateEd(Date8(row.Raw(6)));
            _currProm = PoBatch.DateEd(Date8(row.Raw(7)));
            _valueGbp = Cob.R(row.NumVal(10), 11, 2);
            _status = CobolText.Fit(row.Raw(11), 12);
            _exception = CobolText.Fit(row.Raw(12), 8);
            _salesOrder = CobolText.Fit(row.Raw(13), 14);
            _jobRef = CobolText.Fit(row.Raw(14), 12);
            _actionNote = CobolText.Fit(row.Raw(15), 80);
            if (Cob.Spaces(_actionNote)) _actionNote = CobolText.Fit("(no chase recorded)", 80);
            _tLines = (int)Cob.T(_tLines + 1, 7, 0);
            _tValueGbp = Cob.T(_tValueGbp + _valueGbp, 13, 2);
            if (_daysOrig >= _a.MinDays && _daysCurr < _a.MinDays) _tOnlyOrig++;
            if (_exception.TrimEnd(' ') == "NOACK") _tNoAck++;
            if (!Cob.Spaces(_salesOrder) || !Cob.Spaces(_jobRef)) _tCustomerHit++;
            rw.Generate(detail);
            _sumOne += 1;
            _sumValue = Cob.T(_sumValue + _valueGbp, 12, 2);
        }

        /// <summary>MOVE PO-COL(n)(1:8) TO WS-WORK-DATE (PIC 9(8)).</summary>
        private static int Date8(string raw) => (int)Cob.T(Cob.NumVal(CobolText.Fit(raw, 8)), 8, 0, signed: false);
    }
}
