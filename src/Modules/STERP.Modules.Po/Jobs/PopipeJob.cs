using Sterp.Modules.Po.Programs;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Po.Jobs;

/// <summary>
/// POPIPE (cobol/batch/po/POPIPE.cob; FR-STERP-PO-041): purchasing pipeline and GRNI (RPT-009) - every OPEN order line with
/// the committed, accrued (GRNI) and invoiced stages valued separately; RC 4 when lines are overdue on the original promise.
/// </summary>
internal static class PopipeJob
{
    public static readonly JobDefinition Definition = new()
    {
        JobCode = "POPIPE",
        Slice = "STERP-PO",
        SubmitOperationId = "po_runPopipe",
        ParameterOrder = ["CO", "SITE", "SUPP", "ASAT", "USER"],
        SelfRegistration = pre =>
        {
            var a = Arguments.Read(pre.Args, DateTime.Now);
            return PoBatch.JoblogStart("POPIPE", a.Co, a.User, pre.Args.Text);
        },
        EndValues = (ctx, exit) => PoBatch.JoblogEnd(exit, ctx.RowsProcessed ?? 0, ctx.SpoolFile),
        Execute = ctx => new Run(ctx).MainAsync(),
    };

    private sealed record Arguments(string Co, string Site, string Supp, string User, string AsAt)
    {
        /// <summary>0100-READ-ARGS: CO, SITE, USER, ASAT, SUPP; CO STC, USER BUYER1, ASAT today.</summary>
        public static Arguments Read(Sterp.Platform.Kernel.Jobs.CommandLineArgs a, DateTime now)
        {
            var co = Cob.X(a.Co, 10);
            var user = Cob.X(a.User, 20);
            var asat = Cob.X(a.AsAt, 8);
            var supp = a.Get("SUPP") is { } s ? Cob.X(s, 20) : "";
            if (Cob.Spaces(co)) co = "STC";
            if (Cob.Spaces(user)) user = "BUYER1";
            if (Cob.Spaces(asat)) asat = Cob.D8(PoBatch.Ymd(now));
            return new Arguments(co, Cob.X(a.Site, 10), supp, user, asat);
        }
    }

    private sealed class Run(JobRunContext ctx)
    {
        private readonly DateTime _now = PoBatch.Now();
        private Arguments _a = null!;
        private int _rc;
        private int _rowsOut;

        // WS-DET
        private string _supp = "";
        private string _order = "";
        private decimal _line;
        private string _item = "";
        private decimal _ordered;
        private decimal _cancelled;
        private decimal _received;
        private decimal _invoiced;
        private decimal _outstanding;
        private decimal _commitGbp;
        private decimal _grniGbp;
        private decimal _invoicedGbp;
        private string _origProm = "";
        private string _currProm = "";
        private string _status = "";

        // WS-TOT
        private int _tLines;
        private decimal _tCommitGbp;
        private decimal _tGrniGbp;
        private decimal _tInvoicedGbp;
        private int _tOverdueLines;
        private int _tRedatedLines;

        public async Task<int> MainAsync()
        {
            var ct = ctx.Cancellation;
            _a = Arguments.Read(ctx.Args, _now);
            ctx.Display("STERP-I-1700 POPIPE purchasing pipeline for " + Cob.Trim(_a.Co));
            var sql = PoBatch.Sql(ctx);
            var spool = PoBatch.OpenSpool(ctx, "POPIPE", _a.Co, out var pathFailed, out var spoolName);
            if (!pathFailed) ctx.SpoolFile = spoolName;
            if (spool is null)
            {
                ctx.Display(pathFailed ? "STERP-E-1701 cannot build the spool path" : "STERP-E-1702 cannot open " + CobolText.Fit(spoolName, 200));
                ctx.RowsProcessed = 0;
                return 12;
            }
            var heading = await PoBatch.HeadingAsync(sql, _a.Co, "RPT-009", "Purchasing pipeline and GRNI (RPT-009)",
                Cob.Spaces(_a.Site) ? "All sites" : "Site " + Cob.Trim(_a.Site), PoBatch.AsAt(_a.AsAt), _a.User, _now, ct);
            using (spool)
            {
                var rw = Writer(spool, heading);
                rw.Initiate();
                await ProduceAsync(sql, rw, ct);
                rw.Terminate();
            }
            ctx.Display("STERP-I-1704 " + PoBatch.Disp(_rowsOut, 9) + " open order line(s) reported");
            if (_tOverdueLines > 0)
            {
                ctx.Display("STERP-W-1705 " + PoBatch.Disp(_tOverdueLines, 7) + " line(s) overdue on the original promise");
                if (_rc < 4) _rc = 4;
            }
            ctx.AnnounceSpool(spoolName);
            ctx.RowsProcessed = _tLines;
            return _rc;
        }

        /// <summary>R-POPIPE: CONTROLS ARE FINAL D-SUPP; CH-SUPP, CF-SUPP, CF-FINAL.</summary>
        private GnuReportWriter Writer(SpoolWriter spool, ReportHeading heading)
        {
            var supp = new RwControl(() => _supp, v => _supp = v, 14)
            {
                Heading =
                [
                    RwLine.PlusN(2, () => PoBatch.Line().Lit(1, "Supplier: ").Put(11, _supp, 14)
                        .Lit(30, "Committed, accrued and invoiced are STAGES -").Lit(77, "never added together for one portion").Text),
                    RwLine.Plus1(() => PoBatch.Line().Lit(1, "Order").Lit(16, "Line").Lit(21, "Item").Lit(40, "  Ordered").Lit(50, "Cancelled")
                        .Lit(60, " Received").Lit(70, " Invoiced").Lit(80, "Outstandg").Lit(90, "   Commitment").Lit(104, "         GRNI")
                        .Lit(118, "     Invoiced").Text),
                    RwLine.Plus1(() => PoBatch.Line().Lit(5, "Original promise").Lit(23, "Current promise").Lit(40, "Status").Text),
                    RwLine.Plus1(() => PoBatch.Line().Lit(1, PoBatch.Rule('-')).Text),
                ],
                Footing = [RwLine.Plus1(() => PoBatch.Line().Lit(1, PoBatch.Rule('-')).Text)],
            };
            RwLine[] final =
            [
                RwLine.PlusN(2, () => PoBatch.Line().Lit(1, "Open order lines").Num(28, _tLines, "Z(6)9").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(1, "Commitment still to come").Num(28, _tCommitGbp, "Z(11)9.99-").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(1, "Goods received not invcd").Num(28, _tGrniGbp, "Z(11)9.99-").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(1, "Invoiced and matched").Num(28, _tInvoicedGbp, "Z(11)9.99-").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(1, "Overdue on orig promise").Num(28, _tOverdueLines, "Z(6)9").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(1, "Promise re-dated at least").Num(28, _tRedatedLines, "Z(6)9").Text),
                RwLine.PlusN(2, () => PoBatch.Line().Lit(1, "Outstanding = ordered less cancelled less")
                    .Lit(54, "received; commitment values that quantity only.").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(1, "GRNI is the received but not yet invoiced value:")
                    .Lit(54, "it is the accrual the nominal ledger carries.").Text),
            ];
            return PoBatch.Report(spool, heading, [supp], final);
        }

        /// <summary>DE-LINE.</summary>
        private RwLine[] Detail() =>
        [
            RwLine.Plus1(() => PoBatch.Line().Put(1, _order, 14).Num(16, _line, "ZZZ9").Put(21, _item, 18).Num(40, _ordered, "Z(7)9.999")
                .Num(50, _cancelled, "Z(7)9.999").Num(60, _received, "Z(7)9.999").Num(70, _invoiced, "Z(7)9.999")
                .Num(80, _outstanding, "Z(7)9.999").Num(90, _commitGbp, "Z(9)9.99-").Num(104, _grniGbp, "Z(9)9.99-")
                .Num(118, _invoicedGbp, "Z(9)9.99-").Text),
            RwLine.Plus1(() => PoBatch.Line().Put(5, _origProm, 10).Put(23, _currProm, 10).Put(40, _status, 12).Text),
        ];

        /// <summary>1000-PRODUCE: one row per open order line (and schedule).</summary>
        private async Task ProduceAsync(PoSql sql, GnuReportWriter rw, CancellationToken ct)
        {
            var r = await sql.Cursor(
                "SELECT o.SupplierCode, l.OrderNumber, CAST(l.LineNum AS varchar(10)), ISNULL(l.ItemCode, LEFT(ISNULL(l.Description," +
                "'(no item)'),18)), CAST(l.OrderedQty AS varchar(30)), CAST(l.CancelledQty AS varchar(30)), CAST(l.ReceivedQty AS " +
                "varchar(30)), CAST(l.InvoicedQty AS varchar(30)), CAST(l.UnitPrice / NULLIF(l.PricePerQty,0) AS varchar(30)), " +
                "ISNULL(CONVERT(char(8),s.OriginalConfirmedDate,112),''), ISNULL(CONVERT(char(8),s.CurrentConfirmedDate,112),''), " +
                "l.Status, CASE WHEN s.CurrentConfirmedDate <> s.OriginalConfirmedDate THEN '1' ELSE '0' END, CASE WHEN " +
                "CONVERT(char(8),ISNULL(s.OriginalConfirmedDate,s.CurrentConfirmedDate),112) < ? THEN '1' ELSE '0' END FROM " +
                "po.PurchaseOrderLine l JOIN po.PurchaseOrder o ON o.PurchaseOrderId = l.PurchaseOrderId LEFT JOIN po.PurchaseSchedule " +
                "s ON s.CompanyCode = l.CompanyCode AND s.OrderNumber = l.OrderNumber AND s.LineNum = l.LineNum WHERE l.CompanyCode = ? " +
                "AND l.Status = 'OPEN' AND (? = '' OR o.SupplierCode = ?) ORDER BY o.SupplierCode, l.OrderNumber, l.LineNum, " +
                "s.ScheduleNo",
                [PoSql.Tx(_a.AsAt), PoSql.Tx(_a.Co), PoSql.Tx(_a.Supp), PoSql.Tx(_a.Supp)], Enumerable.Repeat(100, 14).ToArray(), ct);
            if (r.Error)
            {
                _rc = 12;
                ctx.Display("STERP-E-1703 " + Cob.Trim(r.Err));
                return;
            }
            var detail = Detail();
            foreach (var row in r.Rows)
            {
                OneRow(row, rw, detail);
                _rowsOut++;
            }
        }

        /// <summary>1100-ONE-ROW: the three stages valued separately at UnitPrice / PricePerQty.</summary>
        private void OneRow(SqlioRow row, GnuReportWriter rw, RwLine[] detail)
        {
            _supp = CobolText.Fit(row.Raw(0), 14);
            _order = CobolText.Fit(row.Raw(1), 14);
            _line = Cob.T(row.NumVal(2), 4, 0, signed: false);
            _item = CobolText.Fit(row.Raw(3), 18);
            _ordered = Cob.T(row.NumVal(4), 11, 3);
            _cancelled = Cob.T(row.NumVal(5), 11, 3);
            _received = Cob.T(row.NumVal(6), 11, 3);
            _invoiced = Cob.T(row.NumVal(7), 11, 3);
            _outstanding = Cob.T(_ordered - _cancelled - _received, 11, 3);
            if (_outstanding < 0) _outstanding = 0;
            var price = row.NumVal(8);
            _commitGbp = Cob.R(_outstanding * price, 11, 2);
            _grniGbp = Cob.R((_received - _invoiced) * price, 11, 2);
            if (_grniGbp < 0) _grniGbp = 0;
            _invoicedGbp = Cob.R(_invoiced * price, 11, 2);
            _origProm = PoBatch.DateEd(Date8(row.Raw(9)));
            _currProm = PoBatch.DateEd(Date8(row.Raw(10)));
            _status = CobolText.Fit(row.Raw(11), 12);
            _tLines = (int)Cob.T(_tLines + 1, 7, 0);
            _tCommitGbp = Cob.T(_tCommitGbp + _commitGbp, 13, 2);
            _tGrniGbp = Cob.T(_tGrniGbp + _grniGbp, 13, 2);
            _tInvoicedGbp = Cob.T(_tInvoicedGbp + _invoicedGbp, 13, 2);
            if (row.Raw(12).StartsWith('1')) _tRedatedLines++;
            if (row.Raw(13).StartsWith('1') && _outstanding > 0) _tOverdueLines++;
            rw.Generate(detail);
        }

        /// <summary>MOVE PO-COL(n)(1:8) TO WS-WORK-DATE (PIC 9(8)).</summary>
        private static int Date8(string raw) => (int)Cob.T(Cob.NumVal(CobolText.Fit(raw, 8)), 8, 0, signed: false);
    }
}
