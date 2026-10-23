using Sterp.Modules.Itm.Data;
using Sterp.Modules.Itm.Services;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;

namespace Sterp.Modules.Itm.Jobs;

/// <summary>
/// STKSTAT (cobol/batch/itm/STKSTAT.cob; FR-STERP-ITM-030): stock status report - every bucket of inv.usp_StockEnquiry with
/// owner, condition, lot or serial, eligibility and reason; MODE=ELIGIBLE hides non-eligible buckets from the print only.
/// </summary>
internal static class StkstatJob
{
    public static readonly JobDefinition Definition = new()
    {
        JobCode = "STKSTAT",
        Slice = "STERP-ITM",
        SubmitOperationId = "itm_submitStkstatRun",
        ParameterOrder = ["CO", "SITE", "USER", "MODE", "ASAT", "ITEM"],
        SelfRegistration = pre =>
        {
            var a = Arguments.From(pre.Args);
            return ItmJobSubmission.JoblogStart("STKSTAT", a.Co, a.User, "", pre.Args.Text);
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
            if (Cob.Spaces(user)) user = "WHSE";
            if (asat == 0) asat = Batch.Today();
            if (Cob.Spaces(mode)) mode = "ALL";
            return new Arguments(co, Cob.X(a.Site, 10), item, user, asat, mode);
        }
    }


    private static async Task<int> RunAsync(JobRunContext ctx)
    {
        var ct = ctx.Cancellation;
        var a = Arguments.From(ctx.Args);
        ctx.Display("STERP-I-0100 STKSTAT stock status for " + Cob.Trim(a.Co));
        var sql = ctx.Get<Sqlio>();
        var rc = 0;

        // 0300-OPEN-SPOOL: RUNPATH, 0350 headings, OPEN OUTPUT, INITIATE
        var built = Batch.OpenSpool(ctx, "STKSTAT", a.Co, out var opened, out var spoolName);
        if (built.ReturnCode > 4)
        {
            ctx.Display("STERP-E-0301 cannot build the spool path");
            return 12;
        }
        var heading = await Batch.HeadingAsync(sql, a.Co, "STKSTAT", "Stock status report",
            Cob.Spaces(a.Site) ? "All sites" : "Site " + Cob.Trim(a.Site), Batch.AsAtText(a.AsAt), a.User, ct);
        if (opened is null)
        {
            ctx.Display("STERP-E-0302 cannot open " + Batch.Field(spoolName, 200));
            return 12;
        }

        var d = new Detail();
        using (opened)
        {
            var rw = Report(opened, heading, d);
            rw.Initiate();
            // 1000-PRODUCE-REPORT
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
                var line = RwLine.Plus1(() => new RwText().Src(3, d.Site, 5).Src(9, d.Whs, 6).Src(16, d.Loc, 10).Src(27, d.Lot, 16)
                    .Src(44, d.Owner, 10).Src(55, d.Cond, 11).Ed(67, d.Qty, "Z(10)9.999").Ed(84, d.Resv, "Z(10)9.999")
                    .Ed(98, d.Free, "Z(7)9.999").Src(111, d.Elig, 3).Src(115, d.Why, 18).Text);
                foreach (var row in r.Rows!)
                {
                    // 1100-EMIT-LINE
                    d.Item = Cob.X(Col.Text(row, 5), 40);
                    d.Site = Cob.X(Col.Text(row, 1), 5);
                    d.Whs = Cob.X(Col.Text(row, 2), 6);
                    d.Loc = Cob.X(Col.Text(row, 3), 10);
                    d.Lot = Cob.X(Cob.Spaces(Col.Text(row, 8)) ? Col.Text(row, 7) : Col.Text(row, 8), 16);
                    d.Owner = Col.Text(row, 9) == "OWNED" ? "Owned" : Cob.Str(10, Cob.Trim(Col.Text(row, 9)), " ", Cob.Trim(Col.Text(row, 10)));
                    d.Cond = Cob.X(Col.Text(row, 11), 11);
                    d.Qty = CobolNumeric.Store(Col.Num(row, 12), 11, 3);
                    d.Resv = CobolNumeric.Store(Col.Num(row, 18), 11, 3);
                    d.Uom = Cob.X(Col.Text(row, 14), 5);
                    if (Col.Bit(row, 19))
                    {
                        d.Elig = "Yes";
                        d.Free = CobolNumeric.Store(d.Qty - d.Resv, 11, 3);
                        if (d.Free < 0) d.Free = 0;
                        d.TotEligible = CobolNumeric.Store(d.TotEligible + d.Qty, 13, 3);
                    }
                    else
                    {
                        d.Elig = "No";
                        d.Free = 0;
                    }
                    d.Why = Cob.X(Col.Text(row, 20), 24);
                    d.Desc = "";
                    if (Col.Text(row, 9) == "OWNED") d.TotPhysical = CobolNumeric.Store(d.TotPhysical + d.Qty, 13, 3);
                    else d.TotThirdParty = CobolNumeric.Store(d.TotThirdParty + d.Qty, 13, 3);
                    d.TotReserved = CobolNumeric.Store(d.TotReserved + d.Resv, 13, 3);
                    d.TotBuckets++;
                    d.RowsOut++;
                    if (a.Mode == "ELIGIBLE" && d.Elig == "No") continue;
                    // 1200-ITEM-DESC
                    var desc = await sql.SingleAsync("SELECT Description FROM inv.Item WHERE CompanyCode = @p1 AND ItemCode = @p2", [a.Co, d.Item], ct);
                    if (desc.SqlCode == 0) d.Desc = Cob.X(Col.Text(desc.Row, 0), 34);
                    rw.Generate(line);
                    // SUM D-QTY, D-RESV, D-FREE of CF-ITEM (sum_all_detail after the control break)
                    d.SumQty += d.Qty;
                    d.SumResv += d.Resv;
                    d.SumFree += d.Free;
                }
            }
            // 0400-CLOSE-SPOOL
            rw.Terminate();
        }
        ctx.Display("STERP-I-0401 " + Batch.Disp(d.RowsOut, 9) + " stock bucket(s)");
        ctx.AnnounceSpool(spoolName.Trim(' '));
        return rc;
    }

    /// <summary>R-STKSTAT: PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 56; CONTROLS FINAL, D-ITEM.</summary>
    private static GnuReportWriter Report(SpoolWriter spool, Sterp.Platform.Kernel.Services.ReportHeading heading, Detail d)
    {
        var item = new RwControl(() => d.Item, v => d.Item = v, 40)
        {
            Heading =
            [
                RwLine.PlusN(2, () => new RwText().Lit(1, "Item ").Src(6, d.Item, 40).Src(48, d.Desc, 34).Lit(84, "Unit ").Src(89, d.Uom, 5).Text),
                RwLine.Plus1(() => new RwText().Lit(3, "Site ").Lit(9, "Whse ").Lit(16, "Location").Lit(27, "Lot / serial ").Lit(44, "Owner")
                    .Lit(55, "Condition").Lit(70, "Quantity").Lit(87, "Reserved").Lit(104, "Free").Lit(110, "Elg")
                    .Lit(114, "Not eligible because").Text),
                RwLine.Plus1(() => new RwText().Lit(3, new string('-', 66)).Lit(69, new string('-', 64)).Text),
            ],
            Footing =
            [
                RwLine.Plus1(() =>
                {
                    var text = new RwText().Lit(27, "Item totals   ").Lit(55, "physical   ").Ed(67, d.SumQty, "Z(10)9.999")
                        .Ed(84, d.SumResv, "Z(10)9.999").Ed(98, d.SumFree, "Z(7)9.999").Text;
                    d.SumQty = d.SumResv = d.SumFree = 0;
                    return text;
                }),
            ],
        };
        return new GnuReportWriter(spool.WriteLine, heading, 60, 1, 9, 56, [item],
        [
            RwLine.PlusN(2, () => new RwText().Lit(3, "Report totals - owned:").Lit(26, "physical").Ed(36, d.TotPhysical, "Z(11)9.999")
                .Lit(53, "eligible").Ed(63, d.TotEligible, "Z(11)9.999").Lit(80, "reserved").Ed(90, d.TotReserved, "Z(11)9.999").Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Third party owned in custody:").Ed(36, d.TotThirdParty, "Z(11)9.999").Lit(55, "Buckets:")
                .Ed(64, d.TotBuckets, "Z(6)9").Text),
            RwLine.PlusN(2, () => new RwText().Lit(3, "Eligible stock is owned, accepted, unexpired").Text),
            RwLine.Plus1(() => new RwText().Lit(3, "and in an available location.").Text),
        ]);
    }

    /// <summary>WS-DET, WS-TOT and the CF-ITEM SUM counters.</summary>
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
        public decimal Resv;
        public decimal Free;
        public string Elig = "";
        public string Why = "";
        public string Uom = "";
        public decimal SumQty;
        public decimal SumResv;
        public decimal SumFree;
        public decimal TotPhysical;
        public decimal TotEligible;
        public decimal TotReserved;
        public decimal TotThirdParty;
        public int TotBuckets;
        public int RowsOut;
    }
}
