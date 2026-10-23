using Sterp.Modules.Po.Programs;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Po.Jobs;

/// <summary>
/// SUPPPERF (cobol/batch/po/SUPPPERF.cob; FR-STERP-PO-043): supplier performance - OTIF, quality, price (RPT-023) per
/// supplier and posting month over confirmed receipt lines between FROM and TO, measured against the ORIGINAL and the
/// CURRENT promise (D-19 early window from po.ToleranceRule, DATECALC ADDBUS); MODE=COMMIT (also any value other than
/// PREVIEW) replaces the po.SupplierPerformance rows (DELETE + INSERT per supplier, month and basis, each statement on its
/// own).
/// </summary>
internal static class SuppperfJob
{
    public static readonly JobDefinition Definition = new()
    {
        JobCode = "SUPPPERF",
        Slice = "STERP-PO",
        SubmitOperationId = "po_runSuppperf",
        ParameterOrder = ["CO", "SUPP", "FROM", "TO", "MODE", "USER"],
        SelfRegistration = pre =>
        {
            var a = Arguments.Read(pre.Args, DateTime.Now);
            return PoBatch.JoblogStart("SUPPPERF", a.Co, a.User, pre.Args.Text);
        },
        EndValues = (ctx, exit) => PoBatch.JoblogEnd(exit, ctx.RowsProcessed ?? 0, ctx.SpoolFile),
        Execute = ctx => new Run(ctx).MainAsync(),
    };

    private sealed record Arguments(string Co, string Supp, string User, string From, string To, string Mode)
    {
        public bool Commit => Mode == "COMMIT";

        /// <summary>0100-READ-ARGS: CO, USER, SUPP, FROM (19000101), TO (today), MODE (not PREVIEW/COMMIT -> COMMIT).</summary>
        public static Arguments Read(Sterp.Platform.Kernel.Jobs.CommandLineArgs a, DateTime now)
        {
            var co = Cob.X(a.Co, 10);
            var user = Cob.X(a.User, 20);
            var supp = a.Get("SUPP") is { } s ? Cob.X(s, 20) : "";
            var from = a.Get("FROM") is { } f ? Cob.X(f, 8) : "";
            var to = a.Get("TO") is { } t ? Cob.X(t, 8) : "";
            var mode = a.Get("MODE") is { } m ? Cob.X(m, 8) : "COMMIT";
            if (Cob.Spaces(co)) co = "STC";
            if (Cob.Spaces(user)) user = "BUYER1";
            if (mode != "PREVIEW" && mode != "COMMIT") mode = "COMMIT";
            if (Cob.Spaces(from)) from = "19000101";
            if (Cob.Spaces(to)) to = Cob.D8(PoBatch.Ymd(now));
            return new Arguments(co, supp, user, from, to, mode);
        }
    }

    private sealed class Run(JobRunContext ctx)
    {
        private readonly DateTime _now = PoBatch.Now();
        private Arguments _a = null!;
        private PoSql _sql = null!;
        private GnuReportWriter _rw = null!;
        private RwLine[] _detail = [];
        private int _rc;
        private int _tolDays = 5;
        private int _rowsRead;
        private int _rowsWritten;

        // WS-ACC: the supplier/month being built
        private bool _open;
        private string _prevKey = "";
        private string _accSupp = "";
        private string _accPeriod = "";
        private int _receipts;
        private int _inFull;
        private decimal _accepted;
        private decimal _rejected;
        private decimal _presented;
        private decimal _ppv;
        private decimal _value;
        private decimal _leadSum;
        private decimal _qleadSum;
        private int _leadCnt;
        private readonly int[] _dated = new int[2];
        private readonly int[] _onTime = new int[2];
        private readonly int[] _otif = new int[2];

        // WS-DET
        private string _dSupp = "";
        private string _dPeriod = "";
        private string _dBasis = "";
        private decimal _dReceipts;
        private string _dOnTimeTx = "";
        private string _dInFullTx = "";
        private string _dOtifTx = "";
        private string _dQualTx = "";
        private decimal _dPpv;
        private decimal _dValue;
        private decimal _dAvgLead;
        private decimal _dQuotedLead;

        // WS-TOT
        private int _tRows;
        private int _tReceipts;
        private int _tUndefined;
        private int _tDivergent;
        private decimal _tPpv;
        private decimal _tValue;

        // CF-SUPP SUM counters (SUM D-ONE, SUM D-PPV)
        private decimal _sumOne;
        private decimal _sumPpv;

        public async Task<int> MainAsync()
        {
            var ct = ctx.Cancellation;
            _a = Arguments.Read(ctx.Args, _now);
            ctx.Display("STERP-I-1730 SUPPPERF supplier performance " + Cob.Trim(_a.Co) + " mode " + Cob.Trim(_a.Mode));
            _sql = PoBatch.Sql(ctx);
            var spool = PoBatch.OpenSpool(ctx, "SUPPPERF", _a.Co, out var pathFailed, out var spoolName);
            if (!pathFailed) ctx.SpoolFile = spoolName;
            if (spool is null)
            {
                ctx.Display(pathFailed ? "STERP-E-1731 cannot build the spool path" : "STERP-E-1732 cannot open " + CobolText.Fit(spoolName, 200));
                ctx.RowsProcessed = 0;
                return 12;
            }
            var subtitle = Cob.Spaces(_a.Supp)
                ? "All suppliers, mode " + Cob.Trim(_a.Mode)
                : "Supplier " + Cob.Trim(_a.Supp) + ", mode " + Cob.Trim(_a.Mode);
            var from = CobolText.Fit(_a.From, 8);
            var to = CobolText.Fit(_a.To, 8);
            var asAt = CobolText.Fit("Receipts " + from[6..8] + "/" + from[4..6] + "/" + from[..4] + " to " + to[6..8] + "/" + to[4..6] + "/" +
                to[..4], 30).TrimEnd(' ');
            var heading = await PoBatch.HeadingAsync(_sql, _a.Co, "RPT-023", "Supplier performance - OTIF, quality, price (PO-023)", subtitle,
                asAt, _a.User, _now, ct);
            using (spool)
            {
                _rw = Writer(spool, heading);
                _detail = Detail();
                _rw.Initiate();
                await ReadToleranceAsync(ct);
                await ProduceAsync(ct);
                _rw.Terminate();
            }
            ctx.Display("STERP-I-1736 " + PoBatch.Disp(_tRows, 7) + " measure row(s) from " + PoBatch.Disp(_rowsRead, 9) + " receipt line(s)");
            if (_a.Commit)
            {
                ctx.Display("STERP-I-1737 " + PoBatch.Disp(_rowsWritten, 9) + " row(s) stored in po.SupplierPerformance");
            }
            else
            {
                ctx.Display("STERP-I-1738 PREVIEW - nothing stored");
            }
            if (_tDivergent > 0)
            {
                ctx.Display("STERP-W-1739 " + PoBatch.Disp(_tDivergent, 7) + " measure(s) better on the current promise");
            }
            ctx.AnnounceSpool(spoolName);
            ctx.RowsProcessed = _tRows;
            return _rc;
        }

        /// <summary>R-SUPPPERF: CONTROLS ARE FINAL D-SUPP; CH-SUPP, CF-SUPP, CF-FINAL.</summary>
        private GnuReportWriter Writer(SpoolWriter spool, ReportHeading heading)
        {
            var supp = new RwControl(() => _dSupp, v => _dSupp = v, 12)
            {
                Heading =
                [
                    RwLine.PlusN(2, () => PoBatch.Line().Lit(1, "Supplier: ").Put(11, _dSupp, 12)
                        .Lit(28, "ORIGINAL is the D-19 measure.  CURRENT is shown").Lit(79, "only to expose re-dating; it is not the KPI.").Text),
                    RwLine.Plus1(() => PoBatch.Line().Lit(1, "Period").Lit(10, "Basis").Lit(20, "Receipts").Lit(31, " On time")
                        .Lit(41, " In full").Lit(51, "    OTIF").Lit(61, " Quality").Lit(72, "Price varianc").Lit(88, "Purchase vale")
                        .Lit(104, "Lead act ").Lit(116, "Lead quot").Text),
                    RwLine.Plus1(() => PoBatch.Line().Lit(1, PoBatch.Rule('-')).Text),
                ],
                Footing =
                [
                    RwLine.Plus1(() =>
                    {
                        var text = PoBatch.Line().Lit(10, "Supplier measure rows : ").Num(34, _sumOne, "ZZZZ9")
                            .Lit(45, "Price variance total: ").Num(67, _sumPpv, "Z(11)9.99-").Text;
                        _sumOne = 0;
                        _sumPpv = 0;
                        return text;
                    }),
                ],
            };
            RwLine[] final =
            [
                RwLine.PlusN(2, () => PoBatch.Line().Lit(1, PoBatch.Rule('=')).Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(5, "Measure rows produced       : ").Num(35, _tRows, "ZZZZ9")
                    .Lit(48, "Receipts measured           : ").Num(78, _tReceipts, "ZZZZZZZ9").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(5, "Undefined measures (n/a)    : ").Num(35, _tUndefined, "ZZZZ9")
                    .Lit(48, "no denominator - reported as n/a, never as zero").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(5, "Bases that disagree         : ").Num(35, _tDivergent, "ZZZZ9")
                    .Lit(48, "current promise flatters the original: re-dated").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(5, "Price variance              : ").Num(35, _tPpv, "Z(11)9.99-")
                    .Lit(53, "Purchase value              : ").Num(83, _tValue, "Z(11)9.99-").Text),
                RwLine.PlusN(2, () => PoBatch.Line().Lit(5, "A cancelled schedule is not a supplier failure and")
                    .Lit(68, "is excluded; a short delivery is, and is counted.").Text),
            ];
            return PoBatch.Report(spool, heading, [supp], final);
        }

        /// <summary>DE-LINE.</summary>
        private RwLine[] Detail() =>
        [
            RwLine.Plus1(() => PoBatch.Line().Put(1, _dPeriod, 7).Put(10, _dBasis, 8).Num(20, _dReceipts, "ZZZZZZ9").Put(31, _dOnTimeTx, 8)
                .Put(41, _dInFullTx, 8).Put(51, _dOtifTx, 8).Put(61, _dQualTx, 8).Num(72, _dPpv, "Z(9)9.99-").Num(88, _dValue, "Z(9)9.99-")
                .Num(104, _dAvgLead, "Z(5)9.99").Num(116, _dQuotedLead, "Z(5)9.99").Text),
        ];

        /// <summary>0600-READ-TOLERANCE: the D-19 early window (EarlyDeliveryDays of the active COMPANY rule, default 5, never negative).</summary>
        private async Task ReadToleranceAsync(CancellationToken ct)
        {
            var r = await _sql.Sngl("SELECT CAST(EarlyDeliveryDays AS varchar(10)) FROM po.ToleranceRule WHERE CompanyCode = ? AND " +
                "ScopeType = 'COMPANY' AND IsActive = 1", [PoSql.Tx(_a.Co)], [100], ct);
            if (r.Ok && !Cob.Spaces(r.Row.Raw(0)))
            {
                _tolDays = (int)Cob.T(r.Row.NumVal(0), 4, 0);
            }
            if (_tolDays < 0) _tolDays = 0;
        }

        /// <summary>1000-PRODUCE: one row per confirmed receipt line, grouped by supplier (12) and posting month.</summary>
        private async Task ProduceAsync(CancellationToken ct)
        {
            var r = await _sql.Cursor(
                "SELECT r.SupplierCode, CONVERT(char(7),r.PostingDate,126), CONVERT(char(8),r.PhysicalDate,112), ISNULL(CONVERT(char(8)," +
                "s.OriginalConfirmedDate,112),''), ISNULL(CONVERT(char(8),s.CurrentConfirmedDate,112),''), CAST(rl.ReceivedQty AS " +
                "varchar(30)), CAST(ISNULL(s.Quantity,rl.ReceivedQty) AS varchar(30)), CAST(rl.AcceptedQty AS varchar(30)), " +
                "CAST(rl.RejectedQty + rl.ReturnedQty AS varchar(30)), CAST(rl.PriceVarianceGBP AS varchar(30)), " +
                "CAST(rl.ProvisionalValueGBP AS varchar(30)), CAST(DATEDIFF(day,o.OrderDate,r.PhysicalDate) AS varchar(10)), " +
                "CAST(ISNULL((SELECT TOP 1 ss.LeadTimeDays FROM po.SupplierSource ss WHERE ss.CompanyCode = r.CompanyCode AND " +
                "ss.SupplierCode = r.SupplierCode AND ss.ItemCode = rl.ItemCode AND ss.IsActive = 1 ORDER BY ss.Priority),0) AS " +
                "varchar(10)) FROM po.ReceiptLine rl JOIN po.Receipt r ON r.ReceiptId = rl.ReceiptId LEFT JOIN po.PurchaseOrderLine l " +
                "ON l.PurchaseOrderLineId = rl.PurchaseOrderLineId LEFT JOIN po.PurchaseOrder o ON o.PurchaseOrderId = " +
                "l.PurchaseOrderId LEFT JOIN po.PurchaseSchedule s ON s.CompanyCode = r.CompanyCode AND s.OrderNumber = " +
                "rl.PurchaseOrderNumber AND s.LineNum = rl.PurchaseOrderLine AND s.ScheduleNo = rl.ScheduleNo WHERE r.CompanyCode = ? " +
                "AND r.Status = 'CONFIRMED' AND CONVERT(char(8),r.PostingDate,112) BETWEEN ? AND ? AND (? = '' OR r.SupplierCode = ?) " +
                "ORDER BY r.SupplierCode, 2, r.ReceiptRef, rl.LineNum",
                [PoSql.Tx(_a.Co), PoSql.Tx(_a.From), PoSql.Tx(_a.To), PoSql.Tx(_a.Supp), PoSql.Tx(_a.Supp)],
                Enumerable.Repeat(100, 13).ToArray(), ct);
            if (r.Error)
            {
                _rc = 12;
                ctx.Display("STERP-E-1733 " + Cob.Trim(r.Err));
                return;
            }
            foreach (var row in r.Rows)
            {
                _rowsRead++;
                await OneRowAsync(row, ct);
            }
            if (_open) await FlushAsync(ct);
        }

        /// <summary>1100-ONE-ROW: WS-KEY = supplier (12) + period (7).</summary>
        private async Task OneRowAsync(SqlioRow row, CancellationToken ct)
        {
            var key = CobolText.Fit(CobolText.Fit(row.Raw(0), 12) + CobolText.Fit(row.Raw(1), 7), 20);
            if (_open && key != _prevKey) await FlushAsync(ct);
            if (!_open)
            {
                Clear();
                _accSupp = CobolText.Fit(row.Raw(0), 20);
                _accPeriod = CobolText.Fit(row.Raw(1), 7);
                _open = true;
            }
            await AccumulateAsync(row, ct);
            _prevKey = key;
        }

        /// <summary>INITIALIZE WS-ACC.</summary>
        private void Clear()
        {
            _accSupp = "";
            _accPeriod = "";
            _receipts = 0;
            _inFull = 0;
            _accepted = _rejected = _presented = _ppv = _value = _leadSum = _qleadSum = 0;
            _leadCnt = 0;
            Array.Clear(_dated);
            Array.Clear(_onTime);
            Array.Clear(_otif);
        }

        /// <summary>1150-ACCUMULATE.</summary>
        private async Task AccumulateAsync(SqlioRow row, CancellationToken ct)
        {
            _receipts = (int)Cob.T(_receipts + 1, 7, 0);
            _presented = Cob.T(_presented + row.NumVal(5), 13, 6);
            _accepted = Cob.T(_accepted + row.NumVal(7), 13, 6);
            _rejected = Cob.T(_rejected + row.NumVal(8), 13, 6);
            _ppv = Cob.T(_ppv + row.NumVal(9), 13, 2);
            _value = Cob.T(_value + row.NumVal(10), 13, 2);
            if (!Cob.Spaces(row.Raw(11)))
            {
                _leadCnt = (int)Cob.T(_leadCnt + 1, 7, 0);
                _leadSum = Cob.T(_leadSum + row.NumVal(11), 11, 2);
                _qleadSum = Cob.T(_qleadSum + row.NumVal(12), 11, 2);
            }
            var inFull = false;
            if (row.NumVal(5) >= row.NumVal(6))
            {
                inFull = true;
                _inFull = (int)Cob.T(_inFull + 1, 7, 0);
            }
            var phys = Date8(row.Raw(2));
            for (var b = 0; b < 2; b++)
            {
                var promiseText = CobolText.Fit(row.Raw(b == 0 ? 3 : 4), 8);
                var promise = Cob.Spaces(promiseText) ? 0 : Date8(promiseText);
                if (promise == 0) continue;
                _dated[b]++;
                if (await OnTimeAsync(phys, promise, ct))
                {
                    _onTime[b]++;
                    if (inFull) _otif[b]++;
                }
            }
        }

        /// <summary>MOVE PO-COL(n)(1:8) TO PIC 9(8).</summary>
        private static int Date8(string raw) => (int)Cob.T(Cob.NumVal(CobolText.Fit(raw, 8)), 8, 0, signed: false);

        /// <summary>1160-ON-TIME-TEST: on or before the promise and not before the promise less TOL working days (ADDBUS).</summary>
        private async Task<bool> OnTimeAsync(int phys, int promise, CancellationToken ct)
        {
            if (phys > promise) return false;
            var dc = await ctx.Get<IBusinessCalendar>().CallAsync("ADDBUS", d =>
            {
                d.Header.Company = _a.Co;
                d.Date1 = promise;
                d.Number = 0 - _tolDays;
            }, ct);
            var earlyLimit = dc.Header.ReturnCode > 4 ? promise : dc.ResultDate;
            return phys >= earlyLimit;
        }

        /// <summary>1300-FLUSH: two measure rows, ORIGINAL first.</summary>
        private async Task FlushAsync(CancellationToken ct)
        {
            _dSupp = CobolText.Fit(_accSupp, 12);
            _dPeriod = _accPeriod;
            var cmpOrig = 0m;
            for (var b = 0; b < 2; b++)
            {
                cmpOrig = await OneBasisAsync(b, cmpOrig, ct);
            }
            _tReceipts = (int)Cob.T(_tReceipts + _receipts, 9, 0);
            _open = false;
            Clear();
        }

        /// <summary>1320-ONE-BASIS: one measure row (zero denominators are n/a); returns CMP-ORIG-PCT.</summary>
        private async Task<decimal> OneBasisAsync(int b, decimal cmpOrig, CancellationToken ct)
        {
            _dBasis = b == 0 ? "ORIGINAL" : "CURRENT ";
            _dReceipts = _receipts;
            decimal onTimePct, otifPct, inFullPct, qualPct, avgLead, quotedLead;
            if (_dated[b] == 0)
            {
                onTimePct = otifPct = 0;
                _dOnTimeTx = _dOtifTx = "     n/a";
                _tUndefined += 2;
            }
            else
            {
                onTimePct = Cob.R(100m * _onTime[b] / _dated[b], 5, 4);
                otifPct = Cob.R(100m * _otif[b] / _dated[b], 5, 4);
                _dOnTimeTx = Pct(onTimePct);
                _dOtifTx = Pct(otifPct);
            }
            if (_receipts == 0)
            {
                inFullPct = 0;
                _dInFullTx = "     n/a";
                if (b == 0) _tUndefined++;
            }
            else
            {
                inFullPct = Cob.R(100m * _inFull / _receipts, 5, 4);
                _dInFullTx = Pct(inFullPct);
            }
            if (_presented == 0)
            {
                qualPct = 0;
                _dQualTx = "     n/a";
                if (b == 0) _tUndefined++;
            }
            else
            {
                qualPct = Cob.R(100m * _accepted / _presented, 5, 4);
                _dQualTx = Pct(qualPct);
            }
            if (_leadCnt == 0)
            {
                avgLead = quotedLead = 0;
            }
            else
            {
                avgLead = Cob.R(_leadSum / _leadCnt, 7, 2);
                quotedLead = Cob.R(_qleadSum / _leadCnt, 7, 2);
            }
            _dPpv = Cob.T(_ppv, 11, 2);
            _dValue = Cob.T(_value, 11, 2);
            _dAvgLead = avgLead;
            _dQuotedLead = quotedLead;
            if (b == 0)
            {
                cmpOrig = onTimePct;
                _tPpv = Cob.T(_tPpv + _ppv, 13, 2);
                _tValue = Cob.T(_tValue + _value, 13, 2);
            }
            else
            {
                if (onTimePct > cmpOrig) _tDivergent++;
                _dQualTx = _dInFullTx = "    same";
                _dPpv = _dValue = _dAvgLead = _dQuotedLead = 0;
            }
            _tRows = (int)Cob.T(_tRows + 1, 7, 0);
            if (_a.Commit)
            {
                await StoreAsync(_dBasis.TrimEnd(' '), b, onTimePct, inFullPct, otifPct, qualPct, avgLead, quotedLead, ct);
            }
            _rw.Generate(_detail);
            _sumOne += 1;
            _sumPpv = Cob.T(_sumPpv + _dPpv, 12, 2);
            return cmpOrig;
        }

        /// <summary>MOVE pct TO C-PCT-ED (Z(3)9.99), MOVE C-PCT-ED TO PIC X(8).</summary>
        private static string Pct(decimal pct) => CobolText.Fit(PoBatch.Ed(pct, "Z(3)9.99"), 8);

        private static string NumEd(decimal v) => Cob.Ed(v, "-(14)9.9(6)");

        private static string IntEd(decimal v) => Cob.Ed(v, "-(8)9");

        /// <summary>1400-STORE: the measure row is replaced (DELETE, unchecked, then INSERT), each statement on its own.</summary>
        private async Task StoreAsync(string basis, int b, decimal onTimePct, decimal inFullPct, decimal otifPct, decimal qualPct,
            decimal avgLead, decimal quotedLead, CancellationToken ct)
        {
            await _sql.Exec("DELETE FROM po.SupplierPerformance WHERE CompanyCode = ? AND SupplierCode = ? AND PeriodKey = ? AND MeasureBasis = ?",
                [PoSql.Tx(_a.Co), PoSql.Tx(_accSupp), PoSql.Tx(_accPeriod), PoSql.Tx(basis)], ct);
            var r = await _sql.Exec("INSERT po.SupplierPerformance (CompanyCode, SupplierCode, PeriodKey, MeasureBasis, ToleranceDays, " +
                "ReceiptCount, OnTimeCount, InFullCount, OtifCount, OnTimePct, InFullPct, OtifPct, AcceptedQty, RejectedQty, " +
                "QualityAcceptPct, PriceVarianceGBP, PurchaseValueGBP, AvgLeadTimeDays, QuotedLeadTimeDays) VALUES " +
                "(?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?,?)",
                [PoSql.Tx(_a.Co), PoSql.Tx(_accSupp), PoSql.Tx(_accPeriod), PoSql.Tx(basis), PoSql.Tx(IntEd(_tolDays)),
                 PoSql.Tx(IntEd(_receipts)), PoSql.Tx(IntEd(_onTime[b])), PoSql.Tx(IntEd(_inFull)), PoSql.Tx(IntEd(_otif[b])),
                 PoSql.Tx(NumEd(onTimePct)), PoSql.Tx(NumEd(inFullPct)), PoSql.Tx(NumEd(otifPct)), PoSql.Tx(NumEd(_accepted)),
                 PoSql.Tx(NumEd(_rejected)), PoSql.Tx(NumEd(qualPct)), PoSql.Tx(NumEd(_ppv)), PoSql.Tx(NumEd(_value)),
                 PoSql.Tx(NumEd(avgLead)), PoSql.Tx(NumEd(quotedLead))], ct);
            if (r.Error)
            {
                ctx.Display("STERP-W-1735 " + Cob.Trim(r.Err));
                if (_rc < 4) _rc = 4;
            }
            else
            {
                _rowsWritten++;
            }
        }
    }
}
