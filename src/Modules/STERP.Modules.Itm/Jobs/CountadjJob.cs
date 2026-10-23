using System.Globalization;
using Sterp.Modules.Itm.Contracts;
using Sterp.Modules.Itm.Data;
using Sterp.Modules.Itm.Services;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Itm.Jobs;

/// <summary>
/// COUNTADJ (cobol/batch/itm/COUNTADJ.cob; FR-STERP-ITM-034): stock counting - SHEET opens a count with its cutoff and
/// prints the COUNTSHT sheet, ENTER/RECOUNT store a counted quantity with the movements since the cutoff, APPROVE (not by
/// the counter) and POST the variance through STKMOVE ADJUST. No transaction brackets: every statement commits on its own
/// and every STKMOVE call owns its unit of work (SME-013).
/// </summary>
internal static class CountadjJob
{
    public static readonly JobDefinition Definition = new()
    {
        JobCode = "COUNTADJ",
        Slice = "STERP-ITM",
        SubmitOperationId = "itm_submitCountadjRun",
        ParameterOrder = ["CO", "SITE", "USER", "MODE", "REF", "WH", "ITEM", "LINE", "QTY", "COUNT"],
        SelfRegistration = pre =>
        {
            var a = new Run(null, null, pre.Args);
            return ItmJobSubmission.JoblogStart("COUNTADJ", a.Co, a.User, "", pre.Args.Text);
        },
        EndValues = ItmJobSubmission.NeverEnded,
        Execute = RunAsync,
    };

    private static async Task<int> RunAsync(JobRunContext ctx)
    {
        var run = new Run(ctx, ctx.Get<Sqlio>(), ctx.Args);
        ctx.Display("STERP-I-0100 COUNTADJ " + Cob.Trim(run.Mode) + " for " + Cob.Trim(run.Co));
        switch (run.Mode)
        {
            case "SHEET":
                await run.OpenCountAsync();
                break;
            case "ENTER":
            case "RECOUNT":
                await run.EnterCountAsync();
                break;
            case "APPROVE":
                await run.ApproveAsync();
                break;
            case "POST":
                await run.PostAdjustmentsAsync();
                break;
            default:
                run.Rc = 8;
                ctx.Display("STERP-E-0001 MODE must be SHEET, ENTER, RECOUNT, APPROVE or POST");
                break;
        }
        ctx.RowsProcessed = run.RowCount;
        return run.Rc;
    }

    /// <summary>The program's working storage and sections.</summary>
    private sealed class Run
    {
        private readonly JobRunContext? _ctx;
        private readonly Sqlio? _sql;
        public int Rc;
        public string Co;
        public string Site;
        public string User;
        public string Mode;
        private string _ref;
        private readonly string _wh;
        private string _item;
        private int _lineNo;
        private readonly decimal _countQty;
        public int RowCount;
        private int _posted;
        private int _countId;
        private string _status = "";
        private decimal _expected;
        private decimal _intervene;
        private long _lastMovementId;

        /// <summary>0100-READ-ARGS.</summary>
        public Run(JobRunContext? ctx, Sqlio? sql, Sterp.Platform.Kernel.Jobs.CommandLineArgs a)
        {
            _ctx = ctx;
            _sql = sql;
            Co = Cob.X(a.Co, 10);
            Site = Cob.X(a.Site, 10);
            User = Cob.X(a.User, 20);
            Mode = Cob.X(a.Mode, 10);
            _ref = Cob.X(a.Get("REF"), 30);
            _wh = Cob.X(a.Get("WH"), 20);
            _item = Cob.X(a.Get("ITEM"), 40);
            if (a.Get("LINE") is { } line) _lineNo = Int9(Col.NumVal(line));
            if (a.Get("QTY") is { } qty) _countQty = Cob.Q(Col.NumVal(qty));
            if (Cob.Spaces(Co)) Co = "STC";
            if (Cob.Spaces(User)) User = "WHSE";
            if (Cob.Spaces(Mode)) Mode = "SHEET";
        }

        private JobRunContext Ctx => _ctx!;

        private Sqlio Sql => _sql!;

        private CancellationToken Ct => Ctx.Cancellation;

        private void Display(string text) => Ctx.Display(text);

        private static string ErrMsg(SqlioOutcome r) => Batch.Field(r.ErrorText, 100);

        /// <summary>COMPUTE into PIC S9(9): decimals and high-order digits truncated.</summary>
        private static int Int9(decimal v) => (int)CobolNumeric.Store(v, 9, 0);

        // ------------------------------------------------------------------ 1000 open a count

        public async Task OpenCountAsync()
        {
            if (Cob.Spaces(Site))
            {
                Rc = 8;
                Display("STERP-E-1001 SITE is required to open a count");
                return;
            }
            if (Cob.Spaces(_ref)) _ref = MakeRef();
            var scope = Cob.Str(256, "Cycle count ", Cob.Trim(Site), " ", Cob.Trim(_wh), " ", Cob.Trim(_item));
            var r = await Sql.SingleAsync(
                "INSERT inv.StockCount (CompanyCode, SiteCode, CountRef, CountType, ScopeDescription, WarehouseCode, CutoffAt, IsFrozen, " +
                "Status, CreatedBy) OUTPUT inserted.CountId VALUES (@p1, @p2, @p3, 'CYCLE', @p4, NULLIF(@p5,''), SYSDATETIME(), 0, " +
                "'OPEN', @p6)",
                [Co, Site, _ref, scope, _wh, User], Ct);
            if (r.IsError)
            {
                Rc = 12;
                Display("STERP-E-1002 " + ErrMsg(r));
                return;
            }
            _countId = r.NotFound ? 0 : Int9(Col.Num(r.Row, 0));
            await SnapshotLinesAsync();
            if (Rc > 4) return;
            await PrintSheetAsync();
            Display("STERP-I-1003 count " + Cob.Trim(_ref) + " opened with " + Batch.Disp(RowCount, 9) + " line(s)");
        }

        /// <summary>1050-MAKE-REF: CNT + YYMMDD + HHMM of the host clock.</summary>
        private static string MakeRef()
        {
            var now = DateTime.Now;
            return "CNT" + now.ToString("yyMMdd", CultureInfo.InvariantCulture) + now.ToString("HHmm", CultureInfo.InvariantCulture);
        }

        /// <summary>1100-SNAPSHOT-LINES: owned positive balances outside TRANSIT/SCRAP locations, frozen at the cutoff.</summary>
        private async Task SnapshotLinesAsync()
        {
            var r = await Sql.ExecAsync(
                "INSERT inv.StockCountLine (CountId, LineNum, ItemCode, Revision, WarehouseCode, LocationCode, LotCode, SerialNo, " +
                "ExpectedQty, Status) SELECT @p1, ROW_NUMBER() OVER (ORDER BY b.ItemCode, b.LocationCode, b.LotCode, b.SerialNo), " +
                "b.ItemCode, b.Revision, b.WarehouseCode, b.LocationCode, b.LotCode, b.SerialNo, b.QtyOnHand, 'OPEN' FROM " +
                "inv.StockBalance b JOIN inv.Location l ON l.CompanyCode = b.CompanyCode AND l.SiteCode = b.SiteCode AND " +
                "l.WarehouseCode = b.WarehouseCode AND l.LocationCode = b.LocationCode WHERE b.CompanyCode = @p2 AND b.SiteCode = @p3 " +
                "AND (@p4 = '' OR b.WarehouseCode = @p5) AND (@p6 = '' OR b.ItemCode = @p7) AND b.OwnerType = 'OWNED' AND " +
                "l.LocationType NOT IN ('TRANSIT','SCRAP') AND b.QtyOnHand > 0",
                [Cob.SqlInt(_countId), Co, Site, _wh, _wh, _item, _item], Ct);
            if (r.IsError)
            {
                Rc = 12;
                Display("STERP-E-1101 " + ErrMsg(r));
            }
        }

        /// <summary>1200-PRINT-SHEET: the COUNTSHT spool (R-COUNTSHT).</summary>
        private async Task PrintSheetAsync()
        {
            // 1250-OPEN-SPOOL (the RUNPATH outcome is not checked: a blank name fails the OPEN)
            var now = DateTime.Now;
            _ = Batch.OpenSpool(Ctx, "COUNTSHT", Co, out var opened, out var spoolName);
            if (opened is null)
            {
                Rc = 12;
                Display("STERP-E-1251 cannot open " + Batch.Field(spoolName, 200));
                return;
            }
            var spool = opened;
            var heading = await Batch.HeadingAsync(Sql, Co, "COUNTSHT", "Stock count sheet",
                Cob.Str(60, "Count ", Cob.Trim(_ref), " at site ", Cob.Trim(Site)),
                "Cutoff " + now.ToString("dd'/'MM'/'yyyy HH':'mm", CultureInfo.InvariantCulture), User, Ct);
            heading.RunAt = now;
            using (spool)
            {
                var r = await Sql.QueryAsync(
                    "SELECT LineNum, ItemCode, LocationCode, ISNULL(NULLIF(SerialNo,''), ISNULL(LotCode,'')), ExpectedQty, " +
                    "InterveningMovementQty, ISNULL(CountedQty,0), Status FROM inv.StockCountLine WHERE CountId = @p1 ORDER BY LineNum",
                    [Cob.SqlInt(_countId)], Ct);
                if (r.IsError)
                {
                    // GO TO 1200-EXIT: the listing is left without TERMINATE and is not announced
                    Rc = 12;
                    Display("STERP-E-1201 " + ErrMsg(r));
                    return;
                }
                RowCount = 0;
                var c = new SheetLine();
                var rw = new GnuReportWriter(spool.WriteLine, heading, 60, 1, 9, 56, controls: null,
                    finalFooting:
                    [
                        RwLine.PlusN(2, () => new RwText().Lit(1, "Lines on this count:").Ed(24, RowCount, "ZZZZ9").Text),
                        RwLine.PlusN(2, () => new RwText().Lit(1, "Count the physical quantity and enter it").Text),
                        RwLine.Plus1(() => new RwText().Lit(1, "against the line number.  Movements after").Text),
                        RwLine.Plus1(() => new RwText().Lit(1, "the cutoff are shown separately and are").Text),
                        RwLine.Plus1(() => new RwText().Lit(1, "added to expected at the count instant.").Text),
                    ],
                    pageHeading:
                    [
                        RwLine.At(8, () => new RwText().Lit(1, "Line").Lit(7, "Item code").Lit(49, "Location").Lit(63, "Lot/serial").Lit(83, "Expected")
                            .Lit(99, "Since cut").Lit(114, "Counted").Lit(124, "Variance").Text),
                    ]);
                rw.Initiate();
                var detail = RwLine.Plus1(() => new RwText().Ed(1, c.Line, "ZZZZ9").Src(7, c.Item, 40).Src(49, c.Loc, 12).Src(63, c.Lot, 18)
                    .Ed(82, c.Expect, "Z(8)9.999").Ed(97, c.Interv, "Z(8)9.999").Ed(110, c.Counted, "Z(6)9.999").Ed(121, c.Variance, "Z(6)9.999").Text);
                foreach (var row in r.Rows!)
                {
                    c.Line = (int)CobolNumeric.Store(Math.Abs(Col.Num(row, 0)), 5, 0);
                    c.Item = Col.Text(row, 1, 40);
                    c.Loc = Col.Text(row, 2, 12);
                    c.Lot = Col.Text(row, 3, 18);
                    c.Expect = CobolNumeric.Store(Col.Num(row, 4), 11, 3);
                    c.Interv = CobolNumeric.Store(Col.Num(row, 5), 11, 3);
                    c.Counted = CobolNumeric.Store(Col.Num(row, 6), 11, 3);
                    c.Variance = CobolNumeric.Store(c.Counted - c.Expect - c.Interv, 11, 3);
                    RowCount++;
                    rw.Generate(detail);
                }
                // 1290-CLOSE-SPOOL: TERMINATE R-COUNTSHT
                rw.Terminate();
            }
            Ctx.AnnounceSpool(spool.Path);
        }

        /// <summary>WS-DET (C- fields of DE-LINE).</summary>
        private sealed class SheetLine
        {
            public int Line;
            public string Item = "";
            public string Loc = "";
            public string Lot = "";
            public decimal Expect;
            public decimal Interv;
            public decimal Counted;
            public decimal Variance;
        }

        // ------------------------------------------------------------------ 2000 enter a counted or recounted quantity

        public async Task EnterCountAsync()
        {
            await ReadCountHeadAsync();
            if (Rc > 4) return;
            if (_lineNo == 0)
            {
                Rc = 8;
                Display("STERP-E-2001 LINE= is required");
                return;
            }
            await InterveningAsync();
            if (Rc > 4) return;
            var stmt = Mode == "RECOUNT"
                ? "UPDATE inv.StockCountLine SET RecountQty = @p1, InterveningMovementQty = @p2, Status = 'RECOUNT' WHERE CountId = @p3 AND LineNum = @p4"
                : "UPDATE inv.StockCountLine SET CountedQty = @p1, InterveningMovementQty = @p2, Status = 'COUNTED' WHERE CountId = @p3 AND LineNum = @p4";
            var r = await Sql.ExecAsync(stmt, [Cob.SqlNum(_countQty), Cob.SqlNum(_intervene), Cob.SqlInt(_countId), Cob.SqlInt(_lineNo)], Ct);
            if (r.IsError)
            {
                Rc = 12;
                Display("STERP-E-2002 " + ErrMsg(r));
                return;
            }
            await UpdateHeadAsync();
            var atCount = Cob.Q(_expected + _intervene);
            var adjust = Cob.Q(_countQty - atCount);
            RowCount++;
            Display("STERP-I-2003 line " + Batch.Disp(_lineNo, 9) + " variance " + Cob.NumEd(adjust));
        }

        /// <summary>2050-READ-COUNT-HEAD (the count's site replaces SITE=).</summary>
        private async Task ReadCountHeadAsync()
        {
            var r = await Sql.SingleAsync(
                "SELECT CountId, SiteCode, Status, ISNULL(CONVERT(varchar(23), CutoffAt, 126),'') FROM inv.StockCount WHERE CompanyCode = @p1 " +
                "AND CountRef = @p2",
                [Co, _ref], Ct);
            if (r.IsError)
            {
                Rc = 12;
                Display("STERP-E-2051 " + ErrMsg(r));
                return;
            }
            if (r.NotFound)
            {
                Rc = 8;
                Display("STERP-E-2052 count " + Cob.Trim(_ref) + " does not exist");
                return;
            }
            _countId = Int9(Col.Num(r.Row, 0));
            Site = Cob.X(Col.Text(r.Row, 1), 10);
            _status = Cob.X(Col.Text(r.Row, 2), 12);
        }

        /// <summary>2100-INTERVENING (CR0911): the expected quantity and the movements posted since the cutoff.</summary>
        private async Task InterveningAsync()
        {
            _intervene = 0;
            _expected = 0;
            var r = await Sql.SingleAsync(
                "SELECT cl.ExpectedQty, ISNULL((SELECT SUM(m.Quantity) FROM inv.StockMovement m WHERE m.CompanyCode = c.CompanyCode AND " +
                "m.SiteCode = c.SiteCode AND m.ItemCode = cl.ItemCode AND m.WarehouseCode = cl.WarehouseCode AND m.LocationCode = " +
                "cl.LocationCode AND ISNULL(m.LotCode,'') = ISNULL(cl.LotCode,'') AND m.CreatedAt > c.CutoffAt),0), cl.ItemCode, " +
                "cl.WarehouseCode, cl.LocationCode, ISNULL(cl.LotCode,''), ISNULL(cl.SerialNo,''), ISNULL(cl.Revision,'') FROM " +
                "inv.StockCountLine cl JOIN inv.StockCount c ON c.CountId = cl.CountId WHERE cl.CountId = @p1 AND cl.LineNum = @p2",
                [Cob.SqlInt(_countId), Cob.SqlInt(_lineNo)], Ct);
            if (r.IsError)
            {
                Rc = 12;
                Display("STERP-E-2101 " + ErrMsg(r));
                return;
            }
            if (r.NotFound)
            {
                Rc = 8;
                Display("STERP-E-2102 no such count line");
                return;
            }
            _expected = Cob.Q(Col.Num(r.Row, 0));
            _intervene = Cob.Q(Col.Num(r.Row, 1));
            _item = Cob.X(Col.Text(r.Row, 2), 40);
        }

        /// <summary>2150-UPDATE-HEAD (outcome not checked).</summary>
        private async Task UpdateHeadAsync()
        {
            _ = await Sql.ExecAsync(
                "UPDATE inv.StockCount SET Status = @p1, CountedBy = @p2, ModifiedBy = @p3, ModifiedAt = SYSDATETIME() WHERE CountId = @p4 " +
                "AND Status IN ('OPEN','COUNTED','RECOUNT')",
                [Mode == "RECOUNT" ? "RECOUNT" : "COUNTED", User, User, Cob.SqlInt(_countId)], Ct);
        }

        // ------------------------------------------------------------------ 3000 approve

        public async Task ApproveAsync()
        {
            await ReadCountHeadAsync();
            if (Rc > 4) return;
            if (_status != "COUNTED" && _status != "RECOUNT")
            {
                Rc = 8;
                Display("STERP-E-3001 a count must be counted before it is approved");
                return;
            }
            var r = await Sql.ExecAsync(
                "UPDATE inv.StockCount SET Status = 'APPROVED', ApprovedBy = @p1, ApprovedAt = SYSDATETIME(), ModifiedBy = @p2, ModifiedAt = " +
                "SYSDATETIME() WHERE CountId = @p3 AND ISNULL(CountedBy,'') <> @p4",
                [User, User, Cob.SqlInt(_countId), User], Ct);
            if (r.IsError)
            {
                Rc = 12;
                Display("STERP-E-3002 " + ErrMsg(r));
                return;
            }
            await ReadCountHeadAsync();
            if (_status != "APPROVED")
            {
                Rc = 4;
                Display("STERP-W-3003 the counter may not approve their own count");
            }
            else
            {
                Display("STERP-I-3004 count " + Cob.Trim(_ref) + " approved by " + Cob.Trim(User));
            }
        }

        // ------------------------------------------------------------------ 4000 post the approved variances

        public async Task PostAdjustmentsAsync()
        {
            await ReadCountHeadAsync();
            if (Rc > 4) return;
            if (_status != "APPROVED")
            {
                Rc = 8;
                Display("STERP-E-4001 only an approved count may be posted");
                return;
            }
            var r = await Sql.QueryAsync(
                "SELECT cl.LineNum, cl.ItemCode, ISNULL(cl.Revision,''), cl.WarehouseCode, cl.LocationCode, ISNULL(cl.LotCode,''), " +
                "ISNULL(cl.SerialNo,''), cl.ExpectedQty, cl.InterveningMovementQty, ISNULL(cl.RecountQty, cl.CountedQty) FROM " +
                "inv.StockCountLine cl WHERE cl.CountId = @p1 AND ISNULL(cl.RecountQty, cl.CountedQty) IS NOT NULL AND cl.Status <> " +
                "'POSTED' ORDER BY cl.LineNum",
                [Cob.SqlInt(_countId)], Ct);
            if (r.IsError)
            {
                Rc = 12;
                Display("STERP-E-4002 " + ErrMsg(r));
                return;
            }
            _posted = 0;
            for (var i = 0; i < r.Rows!.Count; i++)
            {
                // 4150-MARK-LINE leaves SQLIO-CURSOR at 4, which it closed: the next FTCH ends the loop (FR-COUNTADJ-015, SME-1487).
                // While cursor 3 still has rows to fetch, the STKMOVE transaction is refused on the shared MARS connection.
                if (await PostOneAsync(r.Rows[i], i < r.Rows.Count - 1)) break;
            }
            await CloseCountAsync();
            RowCount = _posted;
            Display("STERP-I-4003 " + Batch.Disp(_posted, 9) + " adjustment(s) posted for count " + Cob.Trim(_ref));
        }

        /// <summary>
        /// 4100-POST-ONE; true when the line was marked (4150-MARK-LINE ran). <paramref name="cursorOpen"/>: rows are still to be
        /// fetched on cursor 3, so the server refuses STKMOVE's transaction (error 3988 under MARS; the legacy runtime posts only
        /// the last fetched line of a POST run and reports W-4101 for the lines before it).
        /// </summary>
        private async Task<bool> PostOneAsync(object?[] row, bool cursorOpen)
        {
            _lineNo = Int9(Col.Num(row, 0));
            _item = Cob.X(Col.Text(row, 1), 40);
            var expected = Cob.Q(Col.Num(row, 7));
            var intervene = Cob.Q(Col.Num(row, 8));
            var counted = Cob.Q(Col.Num(row, 9));
            var atCount = Cob.Q(expected + intervene);
            var adjust = Cob.Q(counted - atCount);
            if (adjust == 0)
            {
                await MarkLineAsync(adjust);
                return true;
            }
            var reason = Cob.Str(200, "Approved stock count variance on ", Cob.Trim(_ref), " line ");
            var sm = new StkmoveCommarea
            {
                ItemCode = _item,
                Revision = Cob.X(Col.Text(row, 2), 4),
                Site = Site,
                Warehouse = Cob.X(Col.Text(row, 3), 20),
                Location = Cob.X(Col.Text(row, 4), 20),
                LotCode = Cob.X(Col.Text(row, 5), 30),
                SerialNo = Cob.X(Col.Text(row, 6), 40),
                Quantity = adjust,
                ReasonCode = "COUNT",
                Reason = Cob.Str(200, Cob.Trim(reason), Cob.IntEd(_lineNo)),
                SourceDocType = "STOCKCOUNT",
                SourceDocRef = Cob.X(_ref, 40),
                SourceDocLine = _lineNo,
                ApprovalRef = Cob.X(_ref, 40),
                SourceModule = "ITM",
            };
            var ca = CaHeader.Init(StkmoveFunctions.Adjust, Co, Site, User, 0);
            var stkmove = Ctx.Get<StkMoveProgram>();
            if (cursorOpen)
            {
                var prefix = Ctx.Get<Microsoft.Extensions.Options.IOptions<Sterp.Platform.Kernel.Data.SterpDatabaseOptions>>().Value.LegacyDriverMessagePrefix;
                stkmove.BeginFailure = new SqlioOutcome(-1, null, 0,
                    prefix + "New transaction is not allowed because there are other threads running in the session.");
            }
            await stkmove.RunAsync(ca, sm, Ct);
            _lastMovementId = sm.MovementId;
            if (ca.ReturnCode > 4)
            {
                Rc = 4;
                Display("STERP-W-4101 line " + Batch.Disp(_lineNo, 9) + " " + Batch.Field(ca.Message, 90));
                return false;
            }
            _posted++;
            await MarkLineAsync(adjust);
            return true;
        }

        /// <summary>4150-MARK-LINE: the movement id is the last STKMOVE reply's (outcome not checked).</summary>
        private async Task MarkLineAsync(decimal adjust)
        {
            _ = await Sql.ExecAsync(
                "UPDATE inv.StockCountLine SET AdjustmentQty = @p1, MovementId = NULLIF(@p2,0), ReasonCode = 'COUNT', Status = 'POSTED' " +
                "WHERE CountId = @p3 AND LineNum = @p4",
                [Cob.SqlNum(adjust), Cob.SqlInt(_lastMovementId), Cob.SqlInt(_countId), Cob.SqlInt(_lineNo)], Ct);
        }

        /// <summary>4200-CLOSE-COUNT: the header is POSTED unconditionally (outcome not checked).</summary>
        private async Task CloseCountAsync()
        {
            _ = await Sql.ExecAsync(
                "UPDATE inv.StockCount SET Status = 'POSTED', ModifiedBy = @p1, ModifiedAt = SYSDATETIME() WHERE CountId = @p2",
                [User, Cob.SqlInt(_countId)], Ct);
        }
    }
}
