using System.Text;
using Sterp.Modules.Itm.Data;
using Sterp.Modules.Itm.Services;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Itm.Jobs;

/// <summary>
/// STKVALF (cobol/batch/itm/STKVALF.cob; FR-STERP-ITM-031/032): stock valuation report and CSV extract (FL-047) - owned
/// buckets valued at the unit cost of the last costed movement up to ASAT (LASTMOVE), else NO COST; exit 4 with W-0402 when
/// any bucket has no cost evidence.
/// </summary>
internal static class StkvalfJob
{
    public static readonly JobDefinition Definition = new()
    {
        JobCode = "STKVALF",
        Slice = "STERP-ITM",
        SubmitOperationId = "itm_submitStkvalfRun",
        ParameterOrder = ["CO", "SITE", "USER", "MODE", "ITEM", "ASAT"],
        SelfRegistration = pre =>
        {
            var a = Arguments.From(pre.Args);
            return ItmJobSubmission.JoblogStart("STKVALF", a.Co, a.User, "", pre.Args.Text);
        },
        EndValues = ItmJobSubmission.NeverEnded,
        Execute = RunAsync,
    };

    private sealed record Arguments(string Co, string Site, string Item, string User, int AsAt, string Mode)
    {
        public static Arguments From(Sterp.Platform.Kernel.Jobs.CommandLineArgs a)
        {
            var co = Cob.X(a.Co, 10);
            var user = Cob.X(a.User, 20);
            var mode = Cob.X(a.Mode, 10);
            var item = a.Get("ITEM") is { } i ? Cob.X(i, 40) : "";
            var asat = a.AsAt.Length > 0 ? Batch.Date8(a.AsAt) : 0;
            if (Cob.Spaces(co)) co = "STC";
            if (Cob.Spaces(user)) user = "STKACCT";
            if (Cob.Spaces(mode)) mode = "ALL";
            if (asat == 0) asat = Batch.Today();
            return new Arguments(co, Cob.X(a.Site, 10), item, user, asat, mode);
        }
    }


    private static async Task<int> RunAsync(JobRunContext ctx)
    {
        var ct = ctx.Cancellation;
        var a = Arguments.From(ctx.Args);
        ctx.Display("STERP-I-0100 STKVALF stock valuation for " + Cob.Trim(a.Co));
        var sql = ctx.Get<Sqlio>();
        var rc = 0;
        var s = new State();

        // 0300-OPEN-SPOOL: RUNPATH for the spool and the extract, 0350 headings, OPEN OUTPUT both, header record, INITIATE
        var built = ctx.Paths.Build("SPOOL", "STKVALF", a.Co, null, null, true, out var spoolName);
        if (built.ReturnCode > 4)
        {
            ctx.Display("STERP-E-0301 cannot build the spool path");
            return 12;
        }
        var ex = ctx.Paths.Build("EXTRACTS", "STKVALF", a.Co, "csv", null, true, out var extractName);
        if (!ex.IsOk) extractName = "";
        var heading = await Batch.HeadingAsync(sql, a.Co, "STKVALF", "Stock valuation report",
            Cob.Spaces(a.Site) ? "All sites - owned stock valued" : "Site " + Cob.Trim(a.Site) + " - owned stock valued",
            Batch.AsAtText(a.AsAt), a.User, ct);
        SpoolWriter spool;
        try
        {
            spool = new SpoolWriter(spoolName);
            ctx.SpoolFiles.Add(spoolName);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ctx.Display("STERP-E-0302 cannot open " + Batch.Field(spoolName, 200));
            return 12;
        }
        StreamWriter extract;
        try
        {
            extract = new StreamWriter(new FileStream(extractName, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), CobolText.Ansi)
            {
                NewLine = "\r\n",
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            spool.Dispose();
            ctx.Display("STERP-E-0303 cannot open " + Batch.Field(extractName, 200));
            return 12;
        }

        using (spool)
        using (extract)
        {
            extract.WriteLine("ITEM,SITE,WAREHOUSE,LOCATION,LOT,OWNER,CONDITION,QTY,UNITCOST,VALUE,COSTSOURCE");
            var rw = Report(spool, heading, s);
            rw.Initiate();
            var d = s.D;
            var line = RwLine.Plus1(() => new RwText().Src(3, d.Site, 5).Src(9, d.Whs, 6).Src(16, d.Loc, 10).Src(27, d.Lot, 18).Src(46, d.Owner, 10)
                .Src(57, d.Cond, 11).Ed(69, d.Qty, "Z(10)9.999").Ed(85, d.Cost, "Z(8)9.9999").Ed(100, d.Value, "Z(10)9.99").Src(122, d.Source, 9).Text);
            var wsText = new char[200];
            Array.Fill(wsText, ' ');
            var costItem = "";
            var costHeld = 0m;
            var costFound = false;
            var desc = "";
            var method = "";
            var r = await sql.QueryAsync(
                "EXEC inv.usp_StockEnquiry @p1, @p2, @p3, NULL, NULL, NULL, NULL, NULL, NULL, NULL, @p4, 0",
                [a.Co, Cob.Spaces(a.Item) ? null : a.Item, Cob.Spaces(a.Site) ? null : a.Site, Cob.D8(a.AsAt)], ct);
            if (r.IsError)
            {
                rc = 12;
                ctx.Display("STERP-E-1001 " + Batch.Field(r.ErrorText, 100));
            }
            else
            {
                foreach (var row in r.Rows!)
                {
                    // 1100-VALUE-BUCKET
                    d.Item = Cob.X(Col.Text(row, 5), 40);
                    d.Site = Cob.X(Col.Text(row, 1), 5);
                    d.Whs = Cob.X(Col.Text(row, 2), 6);
                    d.Loc = Cob.X(Col.Text(row, 3), 10);
                    d.Lot = Cob.X(Cob.Spaces(Col.Text(row, 8)) ? Col.Text(row, 7) : Col.Text(row, 8), 18);
                    d.Owner = Cob.X(Col.Text(row, 9), 10);
                    d.Cond = Cob.X(Col.Text(row, 11), 11);
                    d.Uom = Cob.X(Col.Text(row, 14), 5);
                    d.Qty = CobolNumeric.Store(Col.Num(row, 12), 11, 3);
                    // 1200-ITEM-COST (cached for the item being listed)
                    if (d.Item != costItem)
                    {
                        costItem = d.Item;
                        costHeld = 0;
                        costFound = false;
                        desc = "";
                        method = "";
                        var it = await sql.SingleAsync("SELECT Description, CostMethod FROM inv.Item WHERE CompanyCode = @p1 AND ItemCode = @p2", [a.Co, d.Item], ct);
                        if (it.SqlCode == 0)
                        {
                            desc = Cob.X(Col.Text(it.Row, 0), 34);
                            method = Cob.X(Col.Text(it.Row, 1), 4);
                        }
                        var c = await sql.SingleAsync(
                            "SELECT TOP 1 UnitCost FROM inv.StockMovement WHERE CompanyCode = @p1 AND ItemCode = @p2 AND UnitCost IS NOT NULL AND " +
                            "PostingDate <= @p3 ORDER BY MovementId DESC",
                            [a.Co, d.Item, Cob.D8(a.AsAt)], ct);
                        if (c.SqlCode == 0 && !Cob.Spaces(Col.Text(c.Row, 0)))
                        {
                            costHeld = CobolNumeric.Store(Col.Num(c.Row, 0), 11, 6);
                            costFound = true;
                        }
                    }
                    d.Desc = desc;
                    d.Method = method;
                    d.Cost = CobolNumeric.Store(costHeld, 9, 4);
                    if (costFound)
                    {
                        d.Source = "LASTMOVE";
                    }
                    else
                    {
                        d.Source = "NO COST";
                        s.NoCost++;
                    }
                    var owned = Col.Text(row, 9) == "OWNED";
                    d.Value = owned && costFound ? CobolNumeric.Store(d.Qty * costHeld, 11, 2, rounded: true) : 0;
                    if (owned)
                    {
                        s.OwnedQty = CobolNumeric.Store(s.OwnedQty + d.Qty, 13, 3);
                        s.OwnedVal = CobolNumeric.Store(s.OwnedVal + d.Value, 13, 2);
                    }
                    else
                    {
                        s.ThirdQty = CobolNumeric.Store(s.ThirdQty + d.Qty, 13, 3);
                    }
                    if (Col.Text(row, 4) == "TRANSIT") s.TransitQty = CobolNumeric.Store(s.TransitQty + d.Qty, 13, 3);
                    var eligible = Col.Bit(row, 19);
                    if (eligible) s.EligVal = CobolNumeric.Store(s.EligVal + d.Value, 13, 2);
                    s.Buckets++;
                    s.RowsOut++;
                    if (a.Mode == "ELIGIBLE" && !eligible) continue;
                    // 1300-WRITE-EXTRACT: WS-TEXT is never cleared, so a shorter line keeps the tail of a longer one (FR-STKVALF-013)
                    var head = Cob.Str(220, Cob.Trim(d.Item), ",", Cob.Trim(d.Site), ",", Cob.Trim(d.Whs), ",", Cob.Trim(d.Loc), ",",
                        Cob.Trim(d.Lot), ",", Cob.Trim(d.Owner), ",", Cob.Trim(d.Cond), ",");
                    var text = Cob.Trim(head) + CobolEdit.Format(d.Qty, "-(11)9.999") + "," + CobolEdit.Format(d.Cost, "-(9)9.9999") + "," +
                        CobolEdit.Format(d.Value, "-(11)9.99") + "," + Cob.Trim(d.Source);
                    var bytes = CobolText.Fit(text, Math.Min(text.Length, 200));
                    for (var i = 0; i < bytes.Length; i++) wsText[i] = bytes[i];
                    try
                    {
                        extract.WriteLine(new string(wsText).TrimEnd(' '));
                    }
                    catch (IOException)
                    {
                        rc = 8;
                    }
                    rw.Generate(line);
                    // SUM V-QTY, V-VALUE of CF-ITEM (sum_all_detail after the control break)
                    s.SumQty += d.Qty;
                    s.SumValue += d.Value;
                }
            }
            // 0400-CLOSE-SPOOL
            rw.Terminate();
        }
        if (s.NoCost > 0)
        {
            rc = 4;
            ctx.Display("STERP-W-0402 " + Batch.Disp(s.NoCost, 9) + " bucket(s) have no cost evidence");
        }
        ctx.Display("STERP-I-0403 " + Batch.Disp(s.RowsOut, 9) + " bucket(s) valued");
        ctx.Display("STERP-I-0404 " + Cob.Trim(extractName));
        ctx.AnnounceSpool(spoolName.Trim(' '));
        return rc;
    }

    /// <summary>R-STKVALF: PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 56; CONTROLS FINAL, V-ITEM.</summary>
    private static GnuReportWriter Report(SpoolWriter spool, ReportHeading heading, State s)
    {
        var d = s.D;
        var item = new RwControl(() => d.Item, v => d.Item = v, 40)
        {
            Heading =
            [
                RwLine.PlusN(2, () => new RwText().Lit(1, "Item ").Src(6, d.Item, 40).Src(48, d.Desc, 34).Lit(84, "Unit ").Src(89, d.Uom, 5)
                    .Lit(96, "Method ").Src(103, d.Method, 4).Text),
                RwLine.Plus1(() => new RwText().Lit(3, "Site ").Lit(9, "Whse ").Lit(16, "Location").Lit(27, "Lot / serial ").Lit(46, "Owner")
                    .Lit(57, "Condition").Lit(72, "Quantity").Lit(88, "Unit cost").Lit(104, "Value").Lit(122, "Cost source").Text),
                RwLine.Plus1(() => new RwText().Lit(3, new string('-', 66)).Lit(69, new string('-', 64)).Text),
            ],
            Footing =
            [
                RwLine.Plus1(() =>
                {
                    var text = new RwText().Lit(27, "Item totals ").Ed(69, s.SumQty, "Z(10)9.999").Ed(100, s.SumValue, "Z(10)9.99").Text;
                    s.SumQty = s.SumValue = 0;
                    return text;
                }),
            ],
        };
        return new GnuReportWriter(spool.WriteLine, heading, 60, 1, 9, 56, [item],
        [
            RwLine.PlusN(2, () => new RwText().Lit(3, "Owned stock quantity:").Ed(26, s.OwnedQty, "Z(12)9.999").Lit(48, "Owned value:")
                .Ed(62, s.OwnedVal, "Z(12)9.99").Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Eligible stock value:").Ed(26, s.EligVal, "Z(12)9.99").Lit(48, "Buckets:")
                .Ed(62, s.Buckets, "Z(6)9").Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Third party quantity:").Ed(26, s.ThirdQty, "Z(12)9.999").Lit(48, "In transit:")
                .Ed(62, s.TransitQty, "Z(12)9.999").Text),
            RwLine.PlusN(2, () => new RwText().Lit(3, "Third party owned stock is held in custody").Text),
            RwLine.Plus1(() => new RwText().Lit(3, "and carries no value in this company.").Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Buckets marked NO COST have no cost").Text),
            RwLine.Plus1(() => new RwText().Lit(3, "evidence and are not valued at zero.").Text),
        ]);
    }

    /// <summary>WS-VAL (the current bucket), the CF-ITEM SUM counters and WS-TOT.</summary>
    private sealed class State
    {
        public readonly Detail D = new();
        public decimal SumQty;
        public decimal SumValue;
        public decimal OwnedVal;
        public decimal OwnedQty;
        public decimal EligVal;
        public decimal ThirdQty;
        public decimal TransitQty;
        public int Buckets;
        public int NoCost;
        public int RowsOut;
    }

    private sealed class Detail
    {
        public string Item = "";
        public string Desc = "";
        public string Site = "";
        public string Whs = "";
        public string Loc = "";
        public string Lot = "";
        public string Owner = "";
        public string Cond = "";
        public decimal Qty;
        public decimal Cost;
        public decimal Value;
        public string Source = "";
        public string Uom = "";
        public string Method = "";
    }
}
