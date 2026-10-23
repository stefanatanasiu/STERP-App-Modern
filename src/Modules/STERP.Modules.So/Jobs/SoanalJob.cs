using System.Text.Json.Nodes;
using Sterp.Modules.So.Domain;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Jobs;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.So.Jobs;

/// <summary>
/// SOANAL (cobol/batch/so/SOANAL.cob; FR-STERP-SO-032): sales analysis and profitability - per group (CUSTOMER, ITEM,
/// SALESPERSON or MONTH) the quoted, ordered, despatched, invoiced, credited, discount and returns values in separate
/// columns, expected and realised margins, late cost adjustments and the FRS 102 s23 line. Six gather stages fill an
/// in-core table of at most 200 groups (W-1709), then every group is printed. PERIOD is never read (SME-016). Read only.
/// </summary>
internal static class SoanalJob
{
    public const string Code = "SOANAL";
    private const int MaxGroups = 200;

    public static JobDefinition Definition => new()
    {
        JobCode = Code,
        Slice = SoBatch.Slice,
        SubmitOperationId = "so_submitSoanalRun",
        ParameterOrder = ["CO", "SITE", "FROM", "TO", "PERIOD", "CUST", "GROUPBY", "USER"],
        SelfRegistration = pre =>
        {
            var a = Arguments.Read(pre.Args);
            return SoBatch.JoblogStart(Code, a.Co, a.User, "", pre.CommandLine);
        },
        EndValues = SoBatch.End,
        Execute = RunAsync,
    };

    /// <summary>0100-READ-ARGS: CO, SITE, USER, FROM, TO fixed fields; CUST and GROUPBY by key; defaults STC, SALESMGR, CUSTOMER.</summary>
    private sealed record Arguments(string Co, string Site, string Cust, string User, string From, string To, string GroupBy)
    {
        public static Arguments Read(CommandLineArgs a)
        {
            var co = Cob.X(a.Co, 10);
            var user = Cob.X(a.User, 20);
            if (Cob.Spaces(co)) co = "STC";
            if (Cob.Spaces(user)) user = "SALESMGR";
            var groupBy = Cob.X(a.Get("GROUPBY"), 12);
            if (groupBy is not ("ITEM" or "SALESPERSON" or "MONTH")) groupBy = "CUSTOMER";
            return new Arguments(co, Cob.X(a.Site, 10), Cob.X(a.Get("CUST"), 20), user, Cob.X(a.From, 8), Cob.X(a.To, 8), groupBy);
        }
    }

    /// <summary>WS-GRP OCCURS 200.</summary>
    private sealed class Group
    {
        public string Key = "";
        public decimal Quoted, Ordered, Despatched, Invoiced, Credited, Discount, Returns, ExpCost, ExpCostD, RealCost, Rebate, Ssp, Fx;
        public long PoCnt;
        public string Curr = "";
        public string Control = "";
        public string BillHold = "";
        public string CostSrc = "";
        public string Cutoff = "";
    }

    /// <summary>WS-DET.</summary>
    private sealed class Det
    {
        public string Key = "";
        public decimal Quoted, Ordered, Despatched, Invoiced, Credited, Discount, Returns, ExpCost, ExpMargin, RealCost, RealMargin, LateCost, Ssp, Rebate;
        public string FxBasis = "";
        public string ExpPct = "";
        public string RealPct = "";
        public string CostSrc = "";
        public long PoCnt;
        public string Control = "";
        public string BillHold = "";
    }

    private sealed class Run(JobRunContext ctx, ISqlRunner sql, Arguments a)
    {
        public readonly List<Group> Groups = [];
        public int Rc;

        /// <summary>COMPUTE GT-x = GT-x + FUNCTION NUMVAL(...) into S9(13)V99 (truncated).</summary>
        public static decimal Add(decimal total, decimal value) => CobolNumeric.Store(total + value, 13, 2);

        /// <summary>0800-FIND-GROUP: the slot of SO-COL(1)(1:24), a new zeroed slot, or null when the 200 slots are full (W-1709).</summary>
        public Group? Find(SqlioRow row)
        {
            var key = CobolText.Fit(row.Raw(0), 100)[..24];
            foreach (var g in Groups)
            {
                if (g.Key == key) return g;
            }
            if (Groups.Count >= MaxGroups)
            {
                if (Rc < 4) Rc = 4;
                ctx.Display("STERP-W-1709 more than 200 groups - report a narrower range");
                return null;
            }
            var n = new Group { Key = key };
            Groups.Add(n);
            return n;
        }

        /// <summary>0700-BIND-SCOPE: CO, SITE, SITE, CUST, CUST, FROM, FROM, TO, TO.</summary>
        public SqlioParm[] Scope() =>
        [
            SqlioParm.Text(a.Co), SqlioParm.Text(a.Site), SqlioParm.Text(a.Site), SqlioParm.Text(a.Cust), SqlioParm.Text(a.Cust),
            SqlioParm.Text(a.From), SqlioParm.Text(a.From), SqlioParm.Text(a.To), SqlioParm.Text(a.To),
        ];

        /// <summary>EXEC + FTCH loop: an EXEC failure is reported by the caller; a FTCH failure ends the loop silently.</summary>
        public async Task<bool> GatherAsync(string statement, IReadOnlyList<SqlioParm> parms, int cols, Action<SqlioRow> post,
            Action<SqlioException> failed, CancellationToken ct)
        {
            IReadOnlyList<SqlioRow> rows;
            try
            {
                rows = await sql.FetchAllAsync(statement, parms, SoBatch.Widths(cols, 100), ct);
            }
            catch (SqlioException ex)
            {
                failed(ex);
                return false;
            }
            foreach (var r in rows) post(r);
            return true;
        }
    }

    private static string OrdersKey(string groupBy) => groupBy switch
    {
        "ITEM" => "ISNULL(l.ItemCode,'(no item)')",
        "SALESPERSON" => "ISNULL(o.SalespersonCode,'(none)')",
        "MONTH" => "LEFT(CONVERT(char(8),o.OrderDate,112),6)",
        _ => "o.CustomerCode",
    };

    private static string QuotesKey(string groupBy) => groupBy switch
    {
        "ITEM" => "ISNULL(ql.ItemCode,'(no item)')",
        "SALESPERSON" => "ISNULL(q.SalespersonCode,'(none)')",
        "MONTH" => "LEFT(CONVERT(char(8),q.QuoteDate,112),6)",
        _ => "q.CustomerCode",
    };

    private static string InvoicesKey(string groupBy) => groupBy switch
    {
        "ITEM" => "ISNULL(il.ItemCode,'(no item)')",
        "SALESPERSON" => "ISNULL(i.SalespersonCode,'(none)')",
        "MONTH" => "LEFT(CONVERT(char(8),i.TaxPointDate,112),6)",
        _ => "i.CustomerCode",
    };

    private static string ReturnsKey(string groupBy) => groupBy switch
    {
        "ITEM" => "ISNULL(rl.ItemCode,'(no item)')",
        "SALESPERSON" => "ISNULL(o.SalespersonCode,'(none)')",
        "MONTH" => "LEFT(CONVERT(char(8),r.RequestedDate,112),6)",
        _ => "r.CustomerCode",
    };

    private static string RebatesKey(string groupBy) => groupBy switch
    {
        "ITEM" => "ISNULL(rg.ItemCode,'(all items)')",
        "SALESPERSON" or "MONTH" => "'(rebate)'",
        _ => "rg.CustomerCode",
    };

    private static async Task<int> RunAsync(JobRunContext ctx)
    {
        var ct = ctx.Cancellation;
        var now = DateTime.Now;
        var a = Arguments.Read(ctx.Args);
        var sql = ctx.Get<ISqlRunner>();
        var run = new Run(ctx, sql, a);
        ctx.Display("STERP-I-1700 SOANAL sales analysis for " + Cob.Trim(a.Co) + " by " + Cob.Trim(a.GroupBy));

        // 0300-OPEN-SPOOL / 0350-FILL-HEADINGS
        var heading = SoBatch.Heading(a.Co, Code, "Sales analysis and profitability", a.User, now);
        heading.Subtitle = Cob.X("Grouped by " + Cob.Trim(a.GroupBy) + " - stages shown separately", 60);
        heading.AsAtText = Cob.Spaces(a.From) && Cob.Spaces(a.To)
            ? "All dates"
            : Cob.X("From " + CobolText.Fit(a.From, 8) + " to " + CobolText.Fit(a.To, 8), 30);
        await SoBatch.ReadCompanyAsync(sql, heading, a.Co, ct);
        if (!SoBatch.TryOpenSpool(ctx, Code, a.Co, "STERP-E-1701 cannot build the spool path", "STERP-E-1702 cannot open ", out var spool))
        {
            return 12;
        }

        // 1000-GATHER-ORDERS
        var key = OrdersKey(a.GroupBy);
        await run.GatherAsync(
            "SELECT " + key + ", SUM(l.NetAmount), SUM(CASE WHEN l.PricePerQty > 0 THEN l.AcceptedQty * l.UnitPrice / l.PricePerQty " +
            "- l.NetAmount ELSE 0 END), SUM(ISNULL(l.CostSnapshotGBP,0) * l.AcceptedQty), SUM(CASE WHEN l.LineType IN " +
            "('KITCOMP','TEXT') THEN 0 ELSE 1 END), SUM(ISNULL(l.StandaloneSellingPrice,0) * l.AcceptedQty), MAX(o.CurrencyCode), " +
            "MAX(o.FxRate), MAX(CASE WHEN o.OrderType = 'DROPSHIP' THEN 'Supplier despatch' ELSE 'Despatch' END), MAX(CASE WHEN " +
            "UPPER(ISNULL(o.HoldReason,'')) LIKE '%BILL AND HOLD%' THEN 'BILL-AND-HOLD' WHEN UPPER(ISNULL(o.HoldReason,'')) LIKE " +
            "'%CONSIGN%' THEN 'Consignment' ELSE 'None' END) FROM so.SalesOrderLine l JOIN so.SalesOrder o ON o.SalesOrderId = " +
            "l.SalesOrderId WHERE o.CompanyCode = ? AND o.CommercialStatus <> 'DRAFT' AND (? = '' OR o.SiteCode = ?) AND " +
            "(? = '' OR o.CustomerCode = ?) AND (? = '' OR CONVERT(char(8),o.OrderDate,112) >= ?) AND " +
            "(? = '' OR CONVERT(char(8),o.OrderDate,112) <= ?) GROUP BY " + key,
            run.Scope(), 10, row =>
            {
                // 1050-POST-ORDER
                if (run.Find(row) is not { } g) return;
                g.Ordered = Run.Add(g.Ordered, row.NumVal(1));
                g.Discount = Run.Add(g.Discount, row.NumVal(2));
                g.ExpCost = Run.Add(g.ExpCost, row.NumVal(3));
                g.PoCnt = (long)CobolNumeric.Store(g.PoCnt + row.NumVal(4), 7, 0);
                g.Ssp = Run.Add(g.Ssp, row.NumVal(5));
                g.Curr = Cob.X(row.Raw(6), 3);
                g.Fx = CobolNumeric.Store(row.NumVal(7), 9, 8);
                g.Control = Cob.X(row.Raw(8), 18);
                g.BillHold = Cob.X(row.Raw(9), 12);
            },
            ex =>
            {
                run.Rc = 12;
                ctx.Display("STERP-E-1703 " + CobolText.Fit(ex.DriverText, 100));
            }, ct);

        // 1100-GATHER-QUOTES (the site pair is bound as "? = ?": no site filter)
        key = QuotesKey(a.GroupBy);
        await run.GatherAsync(
            "SELECT " + key + ", SUM(ql.NetAmount) FROM so.QuoteLine ql JOIN so.Quote q ON q.QuoteId = ql.QuoteId WHERE " +
            "q.CompanyCode = ? AND (? = ?) AND (? = '' OR q.CustomerCode = ?) AND (? = '' OR CONVERT(char(8),q.QuoteDate,112) >= ?) " +
            "AND (? = '' OR CONVERT(char(8),q.QuoteDate,112) <= ?) GROUP BY " + key,
            run.Scope(), 2, row =>
            {
                if (run.Find(row) is { } g) g.Quoted = Run.Add(g.Quoted, row.NumVal(1));
            },
            ex => Stage("STERP-W-1704 ", ex), ct);

        // 1200-GATHER-DESPATCH
        key = OrdersKey(a.GroupBy);
        await run.GatherAsync(
            "SELECT " + key + ", SUM(CASE WHEN l.PricePerQty > 0 THEN dl.DespatchedQty * l.UnitPrice / l.PricePerQty * " +
            "(1 - l.DiscountPct1 / 100) * (1 - l.DiscountPct2 / 100) ELSE 0 END), SUM(dl.CogsGBP), SUM(ISNULL(l.CostSnapshotGBP,0) * " +
            "dl.DespatchedQty), MAX(ISNULL(dl.CostMethod,'')), MAX(CONVERT(char(8),d.PostingDate,112)) FROM so.DespatchLine dl JOIN " +
            "so.Despatch d ON d.DespatchId = dl.DespatchId JOIN so.SalesOrderLine l ON l.SalesOrderLineId = dl.SalesOrderLineId JOIN " +
            "so.SalesOrder o ON o.SalesOrderId = l.SalesOrderId WHERE d.CompanyCode = ? AND d.Status = 'CONFIRMED' AND " +
            "(? = '' OR d.SiteCode = ?) AND (? = '' OR d.CustomerCode = ?) AND (? = '' OR CONVERT(char(8),d.DespatchDate,112) >= ?) " +
            "AND (? = '' OR CONVERT(char(8),d.DespatchDate,112) <= ?) GROUP BY " + key,
            run.Scope(), 6, row =>
            {
                // 1250-POST-DESPATCH
                if (run.Find(row) is not { } g) return;
                g.Despatched = Run.Add(g.Despatched, row.NumVal(1));
                g.RealCost = Run.Add(g.RealCost, row.NumVal(2));
                g.ExpCostD = Run.Add(g.ExpCostD, row.NumVal(3));
                g.CostSrc = Cob.X(row.Raw(4), 11);
                g.Cutoff = Cob.X(row.Raw(5), 8);
            },
            ex => Stage("STERP-W-1705 ", ex), ct);

        // 1300-GATHER-INVOICES
        key = InvoicesKey(a.GroupBy);
        await run.GatherAsync(
            "SELECT " + key + ", SUM(CASE WHEN i.DocumentType = 'INVOICE' THEN il.NetAmountGBP ELSE 0 END), SUM(CASE WHEN " +
            "i.DocumentType = 'CREDITNOTE' THEN il.NetAmountGBP ELSE 0 END) FROM ar.SalesInvoice i JOIN ar.SalesInvoiceLine il ON " +
            "il.InvoiceId = i.InvoiceId WHERE i.CompanyCode = ? AND i.Status = 'POSTED' AND (? = ?) AND (? = '' OR i.CustomerCode = ?) " +
            "AND (? = '' OR CONVERT(char(8),i.TaxPointDate,112) >= ?) AND (? = '' OR CONVERT(char(8),i.TaxPointDate,112) <= ?) " +
            "GROUP BY " + key,
            run.Scope(), 3, row =>
            {
                if (run.Find(row) is not { } g) return;
                g.Invoiced = Run.Add(g.Invoiced, row.NumVal(1));
                g.Credited = Run.Add(g.Credited, row.NumVal(2));
            },
            ex => Stage("STERP-W-1706 ", ex), ct);

        // 1400-GATHER-RETURNS
        key = ReturnsKey(a.GroupBy);
        await run.GatherAsync(
            "SELECT " + key + ", SUM(ISNULL(rl.ReturnValueGBP,0)) FROM so.ReturnLine rl JOIN so.ReturnAuthorisation r ON r.ReturnId = " +
            "rl.ReturnId LEFT JOIN so.SalesOrder o ON o.CompanyCode = r.CompanyCode AND o.OrderNumber = r.OriginalOrderNumber WHERE " +
            "r.CompanyCode = ? AND (? = '' OR r.SiteCode = ?) AND (? = '' OR r.CustomerCode = ?) AND (? = '' OR " +
            "CONVERT(char(8),r.RequestedDate,112) >= ?) AND (? = '' OR CONVERT(char(8),r.RequestedDate,112) <= ?) GROUP BY " + key,
            run.Scope(), 2, row =>
            {
                if (run.Find(row) is { } g) g.Returns = Run.Add(g.Returns, row.NumVal(1));
            },
            ex => Stage("STERP-W-1707 ", ex), ct);

        // 1500-GATHER-REBATES (SALESPERSON and MONTH group by a constant: SQL Server refuses it, W-1708)
        key = RebatesKey(a.GroupBy);
        await run.GatherAsync(
            "SELECT " + key + ", SUM(ra.AccruedGBP - ra.SettledGBP) FROM so.RebateAccrual ra JOIN so.RebateAgreement rg ON " +
            "rg.RebateId = ra.RebateId WHERE ra.CompanyCode = ? AND (? = '' OR rg.CustomerCode = ?) GROUP BY " + key,
            [SqlioParm.Text(a.Co), SqlioParm.Text(a.Cust), SqlioParm.Text(a.Cust)], 2, row =>
            {
                if (run.Find(row) is { } g) g.Rebate = Run.Add(g.Rebate, row.NumVal(1));
            },
            ex => Stage("STERP-W-1708 ", ex), ct);

        // later stage failures overwrite WS-RC with 4 (even an earlier 12)
        void Stage(string prefix, SqlioException ex)
        {
            run.Rc = 4;
            ctx.Display(prefix + CobolText.Fit(ex.DriverText, 90));
        }

        // 5000-PRINT-ALL
        var d = new Det();
        decimal tQuoted = 0, tOrdered = 0, tDespatched = 0, tInvoiced = 0, tCredited = 0, tDiscount = 0, tReturns = 0;
        decimal tExpMargin = 0, tRealMargin = 0, tLateCost = 0, tRebate = 0;
        long tGroups = 0, tBillHold = 0, undefined = 0, rowsOut = 0;
        var groups = new JsonArray();
        using (spool)
        {
            // PH-COLS: the program's own PAGE HEADING group below STRPTHDR
            RwLine[] phCols =
            [
                RwLine.At(7, () => new RwText().Lit(1, "Group").Lit(22, "      Quoted").Lit(35, "     Ordered").Lit(48, "  Despatched")
                    .Lit(61, "    Invoiced").Lit(74, "    Credited").Lit(87, "   Discounts").Lit(100, "     Returns").Lit(113, "FX basis").Text),
                RwLine.At(8, () => new RwText().Lit(3, "Margin basis").Lit(20, "        Cost").Lit(33, "      Margin").Lit(46, " Margin %")
                    .Lit(109, "Late cost ad").Lit(122, "Source").Text),
                RwLine.At(9, () => new string('-', 132)),
            ];
            // RD R-SOANAL: CONTROLS ARE FINAL, PAGE LIMIT 60, HEADING 1, FIRST DETAIL 10, LAST DETAIL 52
            var w = new GnuReportWriter(spool.WriteLine, heading, 60, 1, 10, 52, null,
            [
                RwLine.Plus1(() => new string('-', 132)),
                RwLine.Plus1(() => new RwText().Lit(1, "Report totals").Num(22, tQuoted, Amt).Num(35, tOrdered, Amt).Num(48, tDespatched, Amt)
                    .Num(61, tInvoiced, Amt).Num(74, tCredited, Amt).Num(87, tDiscount, Amt).Num(100, tReturns, Amt).Text),
                RwLine.Plus1(() => new RwText().Lit(3, "Expected margin").Num(20, tExpMargin, Big).Lit(36, "Realised margin")
                    .Num(53, tRealMargin, Big).Lit(70, "Late cost adjs").Num(87, tLateCost, Big).Text),
                RwLine.Plus1(() => new RwText().Lit(3, "Variable cons").Num(20, tRebate, Big).Lit(36, "Groups").Num(53, tGroups, "Z(6)9")
                    .Lit(70, "Bill and hold").Num(87, tBillHold, "Z(4)9").Text),
                RwLine.PlusN(2, () => new RwText().Lit(1, "Quoted, ordered, despatched, invoiced and credited are").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "stages of one demand and are never added together.").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "Bill and hold stock is NOT recognised as revenue here.").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "n/d means the margin has no denominator - review it.").Text),
            ], pageHeading: phCols);
            var detail = new[]
            {
                RwLine.Plus1(() => new RwText().Put(1, d.Key, 20).Num(22, d.Quoted, Amt).Num(35, d.Ordered, Amt).Num(48, d.Despatched, Amt)
                    .Num(61, d.Invoiced, Amt).Num(74, d.Credited, Amt).Num(87, d.Discount, Amt).Num(100, d.Returns, Amt)
                    .Put(113, d.FxBasis, 20).Text),
                RwLine.Plus1(() => new RwText().Lit(3, "Expected (orig)").Num(20, d.ExpCost, Amt).Num(33, d.ExpMargin, Amt).Put(46, d.ExpPct, 9)
                    .Lit(56, "Realised (actl)").Num(73, d.RealCost, Amt).Num(86, d.RealMargin, Amt).Put(99, d.RealPct, 9)
                    .Num(109, d.LateCost, Amt).Put(122, d.CostSrc, 11).Text),
                RwLine.Plus1(() => new RwText().Lit(3, "FRS 102 s23").Lit(16, "POs").Num(21, d.PoCnt, "Z(5)9").Lit(29, "SSP total")
                    .Num(40, d.Ssp, Amt).Lit(53, "Variable cons").Num(68, d.Rebate, Amt).Lit(81, "Control").Put(91, d.Control, 18)
                    .Lit(110, "Bill/hold").Put(121, d.BillHold, 12).Text),
            };
            w.Initiate();
            foreach (var g in run.Groups)
            {
                // 5100-ONE-GROUP
                rowsOut++;
                d.Key = Cob.X(g.Key, 20);
                d.Quoted = Det11(g.Quoted);
                d.Ordered = Det11(g.Ordered);
                d.Despatched = Det11(g.Despatched);
                d.Invoiced = Det11(g.Invoiced);
                d.Credited = Det11(g.Credited);
                d.Discount = Det11(g.Discount);
                d.Returns = Det11(g.Returns);
                d.FxBasis = Cob.Spaces(g.Curr) || g.Curr == "GBP"
                    ? "GBP at 1.00000000"
                    : Cob.X(CobolText.Fit(g.Curr, 3) + " at " + CobolEdit.Field(g.Fx, "-(4)9.9(6)").Trim(' '), 20);
                // 5200-MARGINS
                d.ExpCost = Det11(g.ExpCostD);
                d.RealCost = Det11(g.RealCost);
                d.ExpMargin = Det11(g.Despatched - g.ExpCostD);
                d.RealMargin = Det11(g.Despatched - g.RealCost);
                d.LateCost = Det11(g.RealCost - g.ExpCostD);
                var net = g.Despatched;
                if (net == 0)
                {
                    d.ExpPct = "      n/d";
                    d.RealPct = "      n/d";
                    undefined = (long)CobolNumeric.Store(undefined + 1, 5, 0);
                }
                else
                {
                    d.ExpPct = CobolEdit.Field(CobolNumeric.Store(d.ExpMargin / net * 100, 5, 4, rounded: true), "-(4)9.99");
                    d.RealPct = CobolEdit.Field(CobolNumeric.Store(d.RealMargin / net * 100, 5, 4, rounded: true), "-(4)9.99");
                }
                d.CostSrc = Cob.Spaces(g.CostSrc)
                    ? "No despatch"
                    : Cob.X(CobolText.Fit(g.CostSrc, 11)[..4] + " " + CobolText.Fit(g.Cutoff, 8)[6..8] + "/" + CobolText.Fit(g.Cutoff, 8)[4..6], 11);
                d.PoCnt = (long)CobolNumeric.Store(g.PoCnt, 6, 0);
                d.Ssp = Det11(g.Ssp);
                d.Rebate = Det11(g.Rebate);
                d.Control = g.Control;
                d.BillHold = g.BillHold;
                if (CobolText.Fit(g.BillHold, 12)[..4] == "BILL") tBillHold = (long)CobolNumeric.Store(tBillHold + 1, 5, 0);
                tQuoted = Tot(tQuoted + g.Quoted);
                tOrdered = Tot(tOrdered + g.Ordered);
                tDespatched = Tot(tDespatched + g.Despatched);
                tInvoiced = Tot(tInvoiced + g.Invoiced);
                tCredited = Tot(tCredited + g.Credited);
                tDiscount = Tot(tDiscount + g.Discount);
                tReturns = Tot(tReturns + g.Returns);
                tRebate = Tot(tRebate + g.Rebate);
                tExpMargin = Tot(tExpMargin + d.ExpMargin);
                tRealMargin = Tot(tRealMargin + d.RealMargin);
                tLateCost = Tot(tLateCost + d.LateCost);
                w.Generate(detail);
                groups.Add(new JsonObject
                {
                    ["group"] = d.Key, ["quoted"] = d.Quoted, ["ordered"] = d.Ordered, ["despatched"] = d.Despatched, ["invoiced"] = d.Invoiced,
                    ["credited"] = d.Credited, ["discounts"] = d.Discount, ["returns"] = d.Returns, ["fxBasis"] = d.FxBasis,
                    ["expectedCost"] = d.ExpCost, ["expectedMargin"] = d.ExpMargin, ["expectedMarginPct"] = d.ExpPct.Trim(),
                    ["realisedCost"] = d.RealCost, ["realisedMargin"] = d.RealMargin, ["realisedMarginPct"] = d.RealPct.Trim(),
                    ["lateCostAdjustment"] = d.LateCost, ["costSource"] = d.CostSrc, ["performanceObligations"] = d.PoCnt,
                    ["sspTotal"] = d.Ssp, ["variableConsideration"] = d.Rebate, ["controlPoint"] = d.Control, ["billAndHold"] = d.BillHold,
                });
            }
            tGroups = run.Groups.Count;

            // 0400-CLOSE-SPOOL
            w.Terminate();
        }
        ctx.Display("STERP-I-1710 " + SoBatch.Num(rowsOut, 9) + " analysis group(s)");
        if (undefined > 0)
        {
            ctx.Display("STERP-W-1711 " + SoBatch.Num(undefined, 5) + " group(s) with no margin denominator");
            if (run.Rc < 4) run.Rc = 4;
        }
        if (tBillHold > 0) ctx.Display("STERP-W-1712 " + SoBatch.Num(tBillHold, 5) + " bill and hold order(s) are NOT recognised");
        ctx.AnnounceSpool(spool.Path);
        ctx.RowsProcessed = run.Groups.Count;
        ctx.Result = new JsonObject
        {
            ["jobName"] = Code, ["companyCode"] = a.Co, ["siteCode"] = a.Site, ["customerCode"] = a.Cust, ["from"] = a.From, ["to"] = a.To,
            ["groupBy"] = a.GroupBy, ["groups"] = groups, ["quoted"] = tQuoted, ["ordered"] = tOrdered, ["despatched"] = tDespatched,
            ["invoiced"] = tInvoiced, ["credited"] = tCredited, ["discounts"] = tDiscount, ["returns"] = tReturns,
            ["expectedMargin"] = tExpMargin, ["realisedMargin"] = tRealMargin, ["lateCostAdjustments"] = tLateCost,
            ["variableConsideration"] = tRebate, ["billAndHold"] = tBillHold, ["returnCode"] = run.Rc,
        };
        return run.Rc;
    }

    /// <summary>MOVE/COMPUTE into a PIC S9(11)V99 detail field (high-order and decimal truncation).</summary>
    private static decimal Det11(decimal v) => CobolNumeric.Store(v, 11, 2);

    /// <summary>ADD into a PIC S9(13)V99 total.</summary>
    private static decimal Tot(decimal v) => CobolNumeric.Store(v, 13, 2);

    private const string Amt = "Z(7)9.99-";
    private const string Big = "Z(9)9.99-";
}
