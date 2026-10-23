using Sterp.Modules.Po.Programs;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Po.Jobs;

/// <summary>
/// REQGEN (cobol/batch/po/REQGEN.cob; FR-STERP-PO-040): requisition generation - active bought/packaging items below the
/// site re-order point with no open requisition line; proposal up to max (or back to the re-order point), minimum order,
/// purchase multiples; needed-by = today + lead time in business days (DATECALC ADDBUS); MODE=COMMIT raises requisitions
/// through POREQ CREATE/ADDLINE (one per site and demand type), anything else is PREVIEW. Spool RPT-001 (FL-055).
/// </summary>
internal static class ReqgenJob
{
    public static readonly JobDefinition Definition = new()
    {
        JobCode = "REQGEN",
        Slice = "STERP-PO",
        SubmitOperationId = "po_runReqgen",
        ParameterOrder = ["CO", "SITE", "ITEM", "MODE", "USER"],
        SelfRegistration = pre =>
        {
            var a = Arguments.From(pre.Args);
            return PoBatch.JoblogStart("REQGEN", a.Co, a.User, pre.Args.Text);
        },
        EndValues = (ctx, exit) => PoBatch.JoblogEnd(exit, ctx.RowsProcessed ?? 0, ctx.SpoolFile),
        Execute = RunAsync,
    };

    private sealed record Arguments(string Co, string Site, string Item, string User, string Mode)
    {
        public bool Commit => Mode == "COMMIT";

        public static Arguments From(Sterp.Platform.Kernel.Jobs.CommandLineArgs a)
        {
            var co = Cob.X(a.Co, 10);
            var user = Cob.X(a.User, 20);
            var item = a.Get("ITEM") is { } i ? Cob.X(i, 40) : "";
            var mode = a.Get("MODE") is { } m ? Cob.X(m, 8) : "PREVIEW";
            if (Cob.Spaces(co)) co = "STC";
            if (Cob.Spaces(user)) user = "BUYER1";
            if (mode != "COMMIT") mode = "PREVIEW";
            return new Arguments(co, Cob.X(a.Site, 10), item, user, mode);
        }
    }

    private sealed class Proposal
    {
        public string Site = "";
        public string Item = "";
        public string Desc = "";
        public decimal Free;
        public decimal OnOrder;
        public decimal Rop;
        public decimal Suggest;
        public int NeededBy;
        public decimal EstValue;
        public decimal Price;
        public string Supplier = "";
        public string Demand = "";
        public string Uom = "";
    }

    private static async Task<int> RunAsync(JobRunContext ctx)
    {
        var ct = ctx.Cancellation;
        var a = Arguments.From(ctx.Args);
        var now = PoBatch.Now();
        var today = PoBatch.Ymd(now);
        var rc = 0;
        ctx.Display("STERP-I-1750 REQGEN requisition generation " + Cob.Trim(a.Co) + " mode " + Cob.Trim(a.Mode));
        var sql = PoBatch.Sql(ctx);
        var spool = PoBatch.OpenSpool(ctx, "REQGEN", a.Co, out var pathFailed, out var spoolName);
        if (!pathFailed) ctx.SpoolFile = spoolName;
        if (spool is null)
        {
            ctx.Display(pathFailed ? "STERP-E-1751 cannot build the spool path" : "STERP-E-1752 cannot open " + Sterp.Platform.Kernel.Cobol.CobolText.Fit(spoolName, 200));
            ctx.RowsProcessed = 0;
            return 12;
        }
        var heading = await PoBatch.HeadingAsync(sql, a.Co, "RPT-001", "Requisition generation - replenishment and shortage",
            a.Commit ? "MODE=COMMIT - requisitions are created through POREQ" : "MODE=PREVIEW - proposal only, nothing is written",
            "As at " + now.ToString("dd'/'MM'/'yyyy", System.Globalization.CultureInfo.InvariantCulture), a.User, now, ct);
        var report = new Report(spool, heading);
        var proposals = new List<Proposal>();
        var rowsRead = 0;
        using (spool)
        {
            report.Initiate();
            var r = await sql.Cursor(
                "SELECT isi.SiteCode, isi.ItemCode, LEFT(it.Description,22), CAST(ISNULL((SELECT SUM(sb.QtyOnHand - sb.QtyReserved) " +
                "FROM inv.StockBalance sb WHERE sb.CompanyCode = isi.CompanyCode AND sb.SiteCode = isi.SiteCode AND sb.ItemCode = " +
                "isi.ItemCode AND sb.OwnerType = 'OWNED' AND sb.QualityStatus = 'ACCEPTED'),0) AS varchar(30)), CAST(ISNULL((SELECT " +
                "SUM(pl.OrderedQty - pl.CancelledQty - pl.ReceivedQty) FROM po.PurchaseOrderLine pl JOIN po.PurchaseOrder po2 ON " +
                "po2.PurchaseOrderId = pl.PurchaseOrderId WHERE pl.CompanyCode = isi.CompanyCode AND pl.ItemCode = isi.ItemCode AND " +
                "pl.Status = 'OPEN' AND po2.Status IN ('APPROVED','ISSUED','ACKNOWLEDGED','AMENDED')),0) AS varchar(30)), " +
                "CAST(ISNULL(isi.ReorderPointQty,0) AS varchar(30)), CAST(ISNULL(isi.MaxStockQty,0) AS varchar(30)), " +
                "CAST(ISNULL(isi.MinOrderQty,0) AS varchar(30)), CAST(ISNULL(isi.PurchaseMultipleQty,0) AS varchar(30)), " +
                "CAST(isi.LeadTimeDays AS varchar(10)), ISNULL((SELECT TOP 1 ss.SupplierCode FROM po.SupplierSource ss WHERE " +
                "ss.CompanyCode = isi.CompanyCode AND ss.ItemCode = isi.ItemCode AND ss.IsActive = 1 AND ss.ApprovalStatus IN " +
                "('APPROVED','CONDITION') ORDER BY ss.Priority),''), CAST(ISNULL((SELECT TOP 1 pp.UnitPrice / NULLIF(pp.PricePerQty,0) " +
                "FROM po.PurchasePrice pp WHERE pp.CompanyCode = isi.CompanyCode AND pp.ItemCode = isi.ItemCode AND pp.IsActive = 1 " +
                "ORDER BY pp.Priority),0) AS varchar(30)), it.StockUomCode, ISNULL(it.PurchaseUomCode,it.StockUomCode) FROM " +
                "inv.ItemSite isi JOIN inv.Item it ON it.CompanyCode = isi.CompanyCode AND it.ItemCode = isi.ItemCode WHERE " +
                "isi.CompanyCode = ? AND isi.IsActive = 1 AND it.ItemClass IN ('BOUGHT','PACKAGING') AND it.MakeBuy IN ('BUY','DUAL') " +
                "AND ISNULL(isi.ReorderPointQty,0) > 0 AND (? = '' OR isi.SiteCode = ?) AND (? = '' OR isi.ItemCode = ?) AND NOT " +
                "EXISTS (SELECT 1 FROM po.RequisitionLine rl JOIN po.Requisition rq ON rq.RequisitionId = rl.RequisitionId WHERE " +
                "rq.CompanyCode = isi.CompanyCode AND rq.SiteCode = isi.SiteCode AND rl.ItemCode = isi.ItemCode AND rl.Status = " +
                "'OPEN' AND rq.Status IN ('DRAFT','SUBMITTED','APPROVED','PARTCONV')) ORDER BY isi.SiteCode, isi.ItemCode",
                [PoSql.Tx(a.Co), PoSql.Tx(a.Site), PoSql.Tx(a.Site), PoSql.Tx(a.Item), PoSql.Tx(a.Item)],
                Enumerable.Repeat(100, 14).ToArray(), ct);
            if (r.Error)
            {
                rc = 12;
                ctx.Display("STERP-E-1753 " + Cob.Trim(r.Err));
            }
            else
            {
                foreach (var row in r.Rows)
                {
                    rowsRead++;
                    var p = await OneItemAsync(ctx, a, today, row, report, ct);
                    if (p is null) continue;
                    if (proposals.Count >= 2000)
                    {
                        ctx.Display("STERP-W-1760 proposal table full at 2000");
                        continue;
                    }
                    proposals.Add(p);
                }
                rc = await ProcessAsync(ctx, a, today, proposals, report, rc, ct);
            }
            report.Terminate();
        }
        ctx.Display("STERP-I-1756 " + PoBatch.Disp(report.Proposed, 7) + " proposal(s) from " + PoBatch.Disp(rowsRead, 9) + " planned item(s)");
        if (a.Commit)
        {
            ctx.Display("STERP-I-1757 " + PoBatch.Disp(report.Raised, 7) + " requisition line(s) in " + PoBatch.Disp(report.Reqs, 7) + " requisition(s)");
        }
        else
        {
            ctx.Display("STERP-I-1758 PREVIEW - no requisition was created");
        }
        ctx.Display("STERP-I-1759 no purchase order or commitment was written by this run");
        ctx.AnnounceSpool(spoolName);
        ctx.RowsProcessed = report.Proposed;
        return rc;
    }

    /// <summary>1100-ONE-ITEM: the proposal for one planned item (null when the projected position covers the re-order point).</summary>
    private static async Task<Proposal?> OneItemAsync(JobRunContext ctx, Arguments a, int today, Sterp.Platform.Kernel.Data.SqlioRow row,
        Report report, CancellationToken ct)
    {
        var free = Cob.T(row.NumVal(3), 13, 6);
        var onOrder = Cob.T(row.NumVal(4), 13, 6);
        var rop = Cob.T(row.NumVal(5), 13, 6);
        var maxq = Cob.T(row.NumVal(6), 13, 6);
        var minq = Cob.T(row.NumVal(7), 13, 6);
        var mult = Cob.T(row.NumVal(8), 13, 6);
        var projected = Cob.T(free + onOrder, 13, 6);
        if (projected > rop) return null;
        var target = maxq > rop ? maxq : rop;
        var suggest = Cob.T(target - projected, 11, 3);
        if (suggest < minq) suggest = Cob.T(minq, 11, 3);
        if (mult > 0)
        {
            var packs = Cob.T(suggest / mult, 13, 0);
            if (packs * mult < suggest) packs++;
            suggest = Cob.T(packs * mult, 11, 3);
        }
        if (suggest <= 0) return null;
        var price = Cob.T(row.NumVal(11), 11, 6);
        var p = new Proposal
        {
            Site = Cob.X(row.Text(0), 10),
            Item = Cob.X(row.Text(1), 20),
            Desc = Cob.X(row.Text(2), 22),
            Free = Cob.T(free, 11, 3),
            OnOrder = Cob.T(onOrder, 11, 3),
            Rop = Cob.T(rop, 11, 3),
            Suggest = suggest,
            Supplier = Cob.X(row.Text(10), 12),
            EstValue = Cob.R(suggest * row.NumVal(11), 11, 2),
            Price = price,
            Uom = Cob.X(row.Text(13), 10),
        };
        if (free < 0)
        {
            p.Demand = "SHORTAGE";
            report.Shortage++;
        }
        else
        {
            p.Demand = "REPLEN";
        }
        var lead = (int)Cob.T(row.NumVal(9), 4, 0);
        var dc = await ctx.Get<IBusinessCalendar>().CallAsync("ADDBUS", d =>
        {
            d.Header.Company = a.Co;
            d.Date1 = today;
            d.Number = lead;
        }, ct);
        p.NeededBy = dc.Header.ReturnCode > 4 ? today : dc.ResultDate;
        report.Proposed++;
        report.Value = Cob.T(report.Value + p.EstValue, 13, 2);
        return p;
    }

    /// <summary>1500-PROCESS: every proposal printed; in COMMIT raised through POREQ after the cursor is closed.</summary>
    private static async Task<int> ProcessAsync(JobRunContext ctx, Arguments a, int today, List<Proposal> proposals, Report report,
        int rc, CancellationToken ct)
    {
        var reqOpen = false;
        var prevGroup = "";
        var curReq = "";
        var curLine = 0;
        foreach (var p in proposals)
        {
            string outcome;
            if (a.Commit)
            {
                outcome = "";
                var group = Cob.Str(20, Sterp.Platform.Kernel.Cobol.CobolText.Fit(p.Site, 10), Sterp.Platform.Kernel.Cobol.CobolText.Fit(p.Demand, 8));
                if (reqOpen && group != prevGroup) reqOpen = false;
                var go = true;
                if (!reqOpen)
                {
                    var porq = ctx.Get<PoreqProgram>();
                    var hdr = CaHeader.Call("CREATE", a.Co, p.Site, a.User, today, "");
                    hdr.Program = "POREQ";
                    var rq = new PoreqArea
                    {
                        Requester = a.User,
                        ReqDate = today,
                        NeededBy = p.NeededBy,
                        DemandType = p.Demand,
                        Purpose = p.Demand == "SHORTAGE"
                            ? "Shortage cover - reservations exceed stock"
                            : "Replenishment - below the site re-order point",
                        DemandRef = p.Demand == "SHORTAGE" ? "REQGEN SHORTAGE SCAN" : "REQGEN REORDER POINT",
                    };
                    await porq.RunAsync(hdr, rq, ct);
                    if (hdr.Failed)
                    {
                        outcome = "REQ REFUSED";
                        curReq = "";
                        report.Refused++;
                        ctx.Display("STERP-W-1754 POREQ CREATE " + Sterp.Platform.Kernel.Cobol.CobolText.Fit(hdr.ReasonCode, 8) + " " + Cob.Trim(hdr.Message));
                        if (rc < 4) rc = 4;
                        go = false;
                    }
                    else
                    {
                        curReq = Cob.X(rq.ReqNumber, 30);
                        curLine = 0;
                        reqOpen = true;
                        report.Reqs++;
                    }
                }
                if (go)
                {
                    curLine++;
                    var hdr = CaHeader.Call("ADDLINE", a.Co, p.Site, a.User, today, "");
                    hdr.Program = "POREQ";
                    var rq = new PoreqArea
                    {
                        ReqNumber = curReq,
                        LineNum = curLine,
                        ItemCode = p.Item,
                        Description = p.Desc,
                        Quantity = p.Suggest,
                        UomCode = p.Uom,
                        EstUnitPrice = p.Price,
                        SuggestSupp = p.Supplier,
                        LineNeededBy = p.NeededBy,
                    };
                    await ctx.Get<PoreqProgram>().RunAsync(hdr, rq, ct);
                    if (hdr.Failed)
                    {
                        outcome = "LINE REFUSED";
                        report.Refused++;
                        curLine--;
                        ctx.Display("STERP-W-1755 POREQ ADDLINE " + Sterp.Platform.Kernel.Cobol.CobolText.Fit(hdr.ReasonCode, 8) + " " + Cob.Trim(hdr.Message));
                        if (rc < 4) rc = 4;
                    }
                    else
                    {
                        outcome = "RAISED";
                        report.Raised++;
                    }
                    prevGroup = group;
                }
            }
            else
            {
                outcome = "PREVIEW ONLY";
                curReq = "";
            }
            report.Generate(p, outcome, curReq);
        }
        return rc;
    }

    /// <summary>R-REQGEN: CONTROLS ARE FINAL D-SITE; CH-SITE, DE-LINE, CF-SITE, CF-FINAL.</summary>
    private sealed class Report
    {
        private readonly GnuReportWriter _rw;
        private readonly RwLine[] _detail;

        // WS-DET
        private string _site = "";
        private string _item = "";
        private string _desc = "";
        private decimal _free;
        private decimal _onOrder;
        private decimal _rop;
        private decimal _suggest;
        private string _neededBy = "";
        private decimal _estValue;
        private string _supplier = "";
        private string _demandTx = "";
        private string _outcome = "";
        private string _curReq = "";

        // CF-SITE SUM counters (SUM D-ONE, SUM D-EST-VALUE)
        private decimal _sumOne;
        private decimal _sumValue;

        // WS-TOT
        public int Proposed;
        public int Raised;
        public int Refused;
        public int Shortage;
        public int Reqs;
        public decimal Value;

        public Report(SpoolWriter spool, ReportHeading heading)
        {
            var site = new RwControl(() => _site, v => _site = v, 10)
            {
                Heading =
                [
                    RwLine.PlusN(2, () => PoBatch.Line().Lit(1, "Site: ").Put(7, _site, 10)
                        .Lit(22, "A requisition is a need, not a purchase.  Nothing").Lit(75, "here commits the company to any supplier.").Text),
                    RwLine.Plus1(() => PoBatch.Line().Lit(1, "Item").Lit(22, "Description").Lit(45, " Free stock").Lit(57, "   On order")
                        .Lit(69, " Reorder pt").Lit(81, "  Suggested").Lit(93, "Needed by ").Lit(104, "   Est value").Lit(117, "Supplier")
                        .Lit(126, "Demand").Text),
                    RwLine.Plus1(() => PoBatch.Line().Lit(1, PoBatch.Rule('-')).Text),
                ],
                Footing =
                [
                    RwLine.Plus1(() =>
                    {
                        var text = PoBatch.Line().Lit(22, "Site proposed lines : ").Num(44, _sumOne, "ZZZZ9").Lit(56, "Estimated value     : ")
                            .Num(78, _sumValue, "Z(11)9.99-").Text;
                        _sumOne = 0;
                        _sumValue = 0;
                        return text;
                    }),
                ],
            };
            RwLine[] final =
            [
                RwLine.PlusN(2, () => PoBatch.Line().Lit(1, PoBatch.Rule('=')).Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(5, "Lines proposed              : ").Num(35, Proposed, "ZZZZ9")
                    .Lit(48, "of which shortages          : ").Num(78, Shortage, "ZZZZ9").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(5, "Requisition lines raised    : ").Num(35, Raised, "ZZZZ9")
                    .Lit(48, "Requisitions created        : ").Num(78, Reqs, "ZZZZ9").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(5, "Refused by POREQ            : ").Num(35, Refused, "ZZZZ9")
                    .Lit(48, "Estimated value             : ").Num(78, Value, "Z(11)9.99-").Text),
                RwLine.PlusN(2, () => PoBatch.Line().Lit(5, "No purchase order, order line, schedule or")
                    .Lit(68, "commitment movement was written by this run.").Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(5, "Each proposal still needs sourcing (PO-002) and")
                    .Lit(68, "approval (PO-006) before a supplier is told.").Text),
            ];
            _rw = PoBatch.Report(spool, heading, [site], final);
            _detail =
            [
                RwLine.Plus1(() => PoBatch.Line().Put(1, _item, 20).Put(22, _desc, 22).Num(45, _free, "Z(6)9.999").Num(57, _onOrder, "Z(6)9.999")
                    .Num(69, _rop, "Z(6)9.999").Num(81, _suggest, "Z(6)9.999").Put(93, _neededBy, 10).Num(104, _estValue, "Z(8)9.99-")
                    .Put(117, _supplier, 8).Put(126, _demandTx, 6).Text),
                RwLine.Plus1(() => PoBatch.Line().Lit(22, "Outcome:  ").Put(32, _outcome, 14).Put(48, _curReq, 30).Text),
            ];
        }

        /// <summary>INITIATE R-REQGEN.</summary>
        public void Initiate() => _rw.Initiate();

        /// <summary>1500-PROCESS: the D- fields of one proposal and GENERATE DE-LINE.</summary>
        public void Generate(Proposal p, string outcome, string curReq)
        {
            _site = CobolText.Fit(p.Site, 10);
            _item = CobolText.Fit(p.Item, 20);
            _desc = CobolText.Fit(p.Desc, 22);
            _free = p.Free;
            _onOrder = p.OnOrder;
            _rop = p.Rop;
            _suggest = p.Suggest;
            _estValue = p.EstValue;
            _supplier = CobolText.Fit(p.Supplier, 12);
            _demandTx = p.Demand == "SHORTAGE" ? "SHORT" : "REPLEN";
            _neededBy = PoBatch.DateEd(p.NeededBy);
            _outcome = CobolText.Fit(outcome, 14);
            _curReq = CobolText.Fit(curReq, 30);
            _rw.Generate(_detail);
            _sumOne += 1;
            _sumValue = Cob.T(_sumValue + _estValue, 12, 2);
        }

        /// <summary>TERMINATE R-REQGEN (nothing at all when nothing was generated).</summary>
        public void Terminate() => _rw.Terminate();
    }
}
