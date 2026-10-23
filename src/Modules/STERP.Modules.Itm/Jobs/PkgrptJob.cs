using System.Globalization;
using Sterp.Modules.Itm.Data;
using Sterp.Modules.Itm.Services;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Itm.Jobs;

/// <summary>
/// PKGRPT (cobol/batch/itm/PKGRPT.cob; FR-STERP-ITM-035, catalogue RPT-019): compliance and packaging data report - per item
/// ITMCOMPL VATCODE, CUSTOMS, COMPLY and PACKAGE for the quantity despatched in FROM..TO (FR-STERP-ITM-027). Read-only.
/// </summary>
internal static class PkgrptJob
{
    public static readonly JobDefinition Definition = new()
    {
        JobCode = "PKGRPT",
        Slice = "STERP-ITM",
        SubmitOperationId = "itm_submitPkgrptRun",
        ParameterOrder = ["CO", "USER", "MODE", "ITEM", "FROM", "TO"],
        PreCheck = pre =>
        {
            var a = Arguments.Read(pre.Args, DateTime.Now);
            // 0100-READ-ARGS refuses before the program's JOBLOG START (its JOBLOG END has no run id)
            return Task.FromResult(a.From > a.To
                ? LegacyResult.Error("E-0101", "STERP-E-0101 FROM= is later than TO=")
                : (LegacyResult?)null);
        },
        SelfRegistration = pre =>
        {
            var a = Arguments.Read(pre.Args, DateTime.Now);
            return ItmJobSubmission.JoblogStart("PKGRPT", a.Co, a.User, "", pre.Args.Text);
        },
        EndValues = ItmJobSubmission.NeverEnded,
        Execute = RunAsync,
    };

    private sealed record Arguments(string Co, string Item, string User, int From, int To, string Mode)
    {
        public static Arguments Read(Sterp.Platform.Kernel.Jobs.CommandLineArgs a, DateTime now)
        {
            var co = Cob.X(a.Co, 10);
            var user = Cob.X(a.User, 20);
            var mode = Cob.X(a.Mode, 10);
            var item = a.Get("ITEM") is { } i ? Cob.X(i, 40) : "";
            var from = Cob.Spaces(a.From) ? 0 : Batch.Date8(a.From);
            var to = Cob.Spaces(a.To) ? 0 : Batch.Date8(a.To);
            if (Cob.Spaces(co)) co = "STC";
            if (Cob.Spaces(user)) user = "COMPLY1";
            if (Cob.Spaces(mode)) mode = "ALL";
            if (to == 0) to = CobolDate.ToYyyymmdd(now);
            if (from == 0) from = to / 10000 * 10000 + 101;
            return new Arguments(co, item, user, from, to, mode);
        }
    }

    private static string Ddmmyyyy(int yyyymmdd)
    {
        var d = yyyymmdd.ToString("D8", CultureInfo.InvariantCulture);
        return d[6..8] + "/" + d[4..6] + "/" + d[..4];
    }

    private static async Task<int> RunAsync(JobRunContext ctx)
    {
        var ct = ctx.Cancellation;
        var now = DateTime.Now;
        var a = Arguments.Read(ctx.Args, now);
        ctx.Display("STERP-I-0100 PKGRPT RPT-019 compliance for " + Cob.Trim(a.Co));
        var sql = ctx.Get<Sqlio>();
        var rc = 0;

        // 0300-OPEN-SPOOL
        var built = Batch.OpenSpool(ctx, "RPT-019", a.Co, out var opened, out var spoolName);
        if (built.ReturnCode > 4)
        {
            ctx.Display("STERP-E-0301 cannot build the spool path");
            return 12;
        }
        if (opened is null)
        {
            ctx.Display("STERP-E-0302 cannot open " + Batch.Field(spoolName, 200));
            return 12;
        }
        var heading = await Batch.HeadingAsync(sql, a.Co, "RPT-019", "Compliance and packaging data report",
            Cob.Str(60, Cob.Trim("Period " + Ddmmyyyy(a.From) + " to "), " ", Ddmmyyyy(a.To)), "Tax point " + Ddmmyyyy(a.To), a.User, ct);
        heading.RunAt = now;
        var report = new Report(opened, heading);

        using (opened)
        {
            // 1000-SELECT-ITEMS: the item list is read first (OCCURS 500)
            var items = new List<string>();
            var r = Cob.Spaces(a.Item)
                ? await sql.QueryAsync("SELECT ItemCode FROM inv.Item WHERE CompanyCode = @p1 AND LifecycleStatus <> 'RETIRED' ORDER BY ItemCode",
                    [a.Co], ct)
                : await sql.QueryAsync("SELECT ItemCode FROM inv.Item WHERE CompanyCode = @p1 AND ItemCode = @p2 ORDER BY ItemCode",
                    [a.Co, a.Item], ct);
            if (r.IsError)
            {
                rc = 12;
                ctx.Display("STERP-E-1001 " + Batch.Field(r.ErrorText, 100));
            }
            else
            {
                foreach (var row in r.Rows!)
                {
                    if (items.Count < 500)
                    {
                        items.Add(Col.Text(row, 0, 40));
                        continue;
                    }
                    rc = 4;
                    ctx.Display("STERP-W-1002 item table full at 500");
                    break;
                }
            }

            // 1100-REPORT-ITEMS
            var compl = ctx.Get<ItmComplProgram>();
            foreach (var item in items)
            {
                var d = await OneItemAsync(compl, sql, a, item, report, ct);
                if (a.Mode == "DG" && Cob.Spaces(d.DgUn)) continue;
                if (a.Mode == "FAULTS" && Cob.Spaces(d.Note)) continue;
                report.Generate(d);
            }
            report.Terminate();
        }

        // 0400-CLOSE-SPOOL
        if (report.Faults > 0)
        {
            if (rc < 4) rc = 4;
            ctx.Display("STERP-W-0402 " + Batch.Disp(report.Faults, 7) + " item(s) have compliance faults");
        }
        ctx.Display("STERP-I-0403 " + Batch.Disp(report.RowsOut, 9) + " item(s) reported");
        ctx.AnnounceSpool(spoolName.Trim(' '));
        ctx.RowsProcessed = report.RowsOut;
        return rc;
    }

    /// <summary>1200-ONE-ITEM (up to the MODE filter).</summary>
    private static async Task<Detail> OneItemAsync(ItmComplProgram compl, Sqlio sql, Arguments a, string item, Report report,
        CancellationToken ct)
    {
        var d = new Detail { Item = item };

        // 1300-VAT-CLASS
        var (ca, co) = Reset(a, item, "VATCODE");
        co.Direction = "SALE";
        await compl.RunAsync(ca, co, ct);
        if (ca.ReturnCode == 0)
        {
            d.Vat = Cob.X(co.VatCode, 4);
            d.Drc = co.DrcGoods == "Y" ? "Yes" : "No";
        }
        else
        {
            d.Vat = "----";
            d.Note = "No VAT classification at that tax point";
            report.Faults++;
        }

        // 1400-CUSTOMS
        (ca, co) = Reset(a, item, "CUSTOMS");
        co.Direction = "SALE";
        await compl.RunAsync(ca, co, ct);
        if (ca.ReturnCode == 0)
        {
            d.Comm = Cob.X(co.CommodityCode, 10);
            d.Origin = Cob.X(co.OriginCountry, 2);
            d.NetKg = CobolNumeric.Store(co.NetWeightKg, 9, 6);
            d.Pref = co.Preferential == "Y" ? "Yes" : "No";
            d.ExpCtl = co.ExportControl == "Y" ? "Yes" : "No";
        }
        else
        {
            d.Comm = "none held";
            if (Cob.Spaces(d.Note))
            {
                d.Note = "No customs data - export would be blocked";
                report.Faults++;
            }
        }

        // 1500-COMPLIANCE
        (ca, co) = Reset(a, item, "COMPLY");
        await compl.RunAsync(ca, co, ct);
        if (ca.ReturnCode < 8)
        {
            d.Ukca = co.UkcaMarked == "Y" ? "Yes" : "No";
            d.DgUn = Cob.X(co.DgUnNumber, 8);
            d.DgClass = Cob.X(co.DgClass, 8);
            d.DgPg = Cob.X(co.DgPackGroup, 4);
            d.Reach = Cob.X(co.ReachStatus, 18);
            d.Weee = Cob.X(co.WeeeCategory, 14);
            d.Battery = Cob.X(co.BatteryCategory, 14);
        }
        if (co.FaultCnt > 0 && Cob.Spaces(d.Note))
        {
            d.Note = Cob.X(ca.Message, 60);
            report.Faults++;
        }

        // 1600-DESPATCHED: the magnitude of the negative movements posted in the period
        var r = await sql.SingleAsync(
            "SELECT ISNULL(SUM(CASE WHEN Quantity < 0 THEN -Quantity ELSE 0 END),0) FROM inv.StockMovement WHERE CompanyCode = @p1 AND " +
            "ItemCode = @p2 AND PostingDate >= @p3 AND PostingDate <= @p4",
            [a.Co, item, Cob.D8(a.From), Cob.D8(a.To)], ct);
        if (r.SqlCode == 0) d.DespQty = CobolNumeric.Store(Col.Num(r.Row, 0), 11, 3);

        // 1700-PACKAGING
        (ca, co) = Reset(a, item, "PACKAGE");
        co.DespatchQty = d.DespQty;
        await compl.RunAsync(ca, co, ct);
        if (ca.ReturnCode <= 4)
        {
            d.PkgTot = CobolNumeric.Store(co.PkgTotalG, 11, 3);
            d.PkgPlas = CobolNumeric.Store(co.PkgPlasticG, 11, 3);
            d.PkgRecy = CobolNumeric.Store(co.PkgRecycledG, 11, 3);
            d.Ppt = co.PptLiable == "Y" ? "Yes" : "No";
            var text = "";
            for (var mx = 0; mx < co.LineCnt && mx < 4; mx++)
            {
                // STRING TRIM(WS-TEXT) TRIM(material) " ": the separator is trimmed away by the next pass
                text = Cob.Str(200, Cob.Trim(text), Cob.Trim(co.Lines[mx].Material), " ");
            }
            d.Materials = co.LineCnt == 0 ? "no packaging recorded" : Cob.X(text, 44);
        }
        return d;
    }

    /// <summary>1250-RESET-CA: company, user, business date and tax point = TO, the item.</summary>
    private static (CaHeader, ItmcomplArea) Reset(Arguments a, string item, string function) =>
        (CaHeader.Init(function, a.Co, "", a.User, a.To), new ItmcomplArea { ItemCode = item, TaxPoint = a.To });

    private sealed class Detail
    {
        public string Item = "";
        public string Vat = "";
        public string Drc = "";
        public string Comm = "";
        public string Origin = "";
        public string Pref = "";
        public decimal NetKg;
        public string ExpCtl = "";
        public string Ukca = "";
        public string DgUn = "";
        public string DgClass = "";
        public string DgPg = "";
        public string Reach = "";
        public string Weee = "";
        public string Battery = "";
        public decimal DespQty;
        public decimal PkgTot;
        public decimal PkgPlas;
        public decimal PkgRecy;
        public string Ppt = "";
        public string Materials = "";
        public string Note = "";
    }

    /// <summary>
    /// R-PKGRPT: PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 52, FOOTING 56, CONTROLS ARE FINAL; RH-COLHEAD (REPORT
    /// HEADING, LINE 8), DE-LINE (four lines, the first LINE PLUS 2) and CF-FINAL.
    /// </summary>
    private sealed class Report
    {
        private readonly GnuReportWriter _rw;
        private readonly RwLine[] _line;
        private Detail _d = new();
        public int Faults;
        public int RowsOut;
        private int _items;
        private int _dg;
        private decimal _totG;
        private decimal _plasG;
        private decimal _recyG;
        private decimal _plasKg;
        private decimal _recyKg;

        public Report(SpoolWriter spool, ReportHeading heading)
        {
            _line =
            [
                RwLine.PlusN(2, () => new RwText().Src(1, _d.Item, 40).Src(43, "", 40).Lit(85, "VAT ").Src(89, _d.Vat, 4).Lit(95, "DRC ")
                    .Src(99, _d.Drc, 3).Lit(104, "Commodity ").Src(114, _d.Comm, 10).Lit(126, "Org: ").Src(131, _d.Origin, 2).Text),
                RwLine.Plus1(() => new RwText().Lit(3, "Customs: ").Lit(12, "net  ").Ed(17, _d.NetKg, "Z(5)9.999").Lit(29, "kg ").Lit(33, "pref ")
                    .Src(38, _d.Pref, 3).Lit(42, "expctl ").Src(49, _d.ExpCtl, 3).Lit(54, "UKCA ").Src(59, _d.Ukca, 3).Lit(64, "DG ")
                    .Src(67, _d.DgUn, 8).Src(76, _d.DgClass, 8).Src(85, _d.DgPg, 4).Src(90, _d.Reach, 18).Src(109, _d.Weee, 14)
                    .Src(124, _d.Battery, 9).Text),
                RwLine.Plus1(() => new RwText().Lit(3, "Packaging: ").Lit(14, "desp ").Ed(19, _d.DespQty, "Z(8)9.999").Lit(33, "total ")
                    .Ed(39, _d.PkgTot, "Z(8)9.999").Lit(53, "plastic ").Ed(61, _d.PkgPlas, "Z(8)9.999").Lit(75, "recycled ")
                    .Ed(84, _d.PkgRecy, "Z(8)9.999").Lit(98, "PPT ").Src(102, _d.Ppt, 3).Src(106, _d.Materials, 27).Text),
                RwLine.Plus1(() => new RwText().Src(3, _d.Note, 60).Text),
            ];
            var dashes = new string('-', 66);
            _rw = new GnuReportWriter(spool.WriteLine, heading, 60, 1, 9, 52, controls: null,
                finalFooting:
                [
                    RwLine.PlusN(2, () => new RwText().Lit(1, dashes).Lit(67, dashes).Text),
                    RwLine.Plus1(() => new RwText().Lit(1, "Items listed:").Ed(16, _items, "Z(6)9").Lit(28, "Dangerous goods:").Ed(46, _dg, "Z(6)9")
                        .Lit(58, "Compliance faults:").Ed(78, Faults, "Z(6)9").Text),
                    RwLine.Plus1(() => new RwText().Lit(1, "Packaging grammes:").Ed(21, _totG, "Z(12)9.999").Lit(39, "plastic ")
                        .Ed(48, _plasG, "Z(12)9.999").Lit(66, "recycled").Ed(75, _recyG, "Z(12)9.999").Text),
                    RwLine.Plus1(() => new RwText().Lit(1, "Plastic kilogrammes:").Ed(21, _plasKg, "Z(10)9.999").Lit(39, "recycled")
                        .Ed(48, _recyKg, "Z(10)9.999").Text),
                    RwLine.PlusN(2, () => new RwText().Lit(1, "Packaging weight is the despatched quantity times").Text),
                    RwLine.Plus1(() => new RwText().Lit(1, "the recorded composition per unit (RPT-019).").Text),
                ],
                reportHeading:
                [
                    RwLine.At(8, () => new RwText().Lit(1, "Item, VAT liability, customs data, compliance")
                        .Lit(47, "flags and packaging composition for the period").Text),
                ]);
            _rw.Initiate();
        }

        /// <summary>1200-ONE-ITEM: the totals of a printed item, then GENERATE DE-LINE.</summary>
        public void Generate(Detail d)
        {
            _items++;
            RowsOut++;
            if (!Cob.Spaces(d.DgUn)) _dg++;
            _totG = CobolNumeric.Store(_totG + d.PkgTot, 13, 3);
            _plasG = CobolNumeric.Store(_plasG + d.PkgPlas, 13, 3);
            _recyG = CobolNumeric.Store(_recyG + d.PkgRecy, 13, 3);
            _d = d;
            _rw.Generate(_line);
        }

        /// <summary>1100 totals in kilogrammes (ROUNDED), then TERMINATE R-PKGRPT.</summary>
        public void Terminate()
        {
            _plasKg = CobolNumeric.Store(_plasG / 1000m, 11, 3, rounded: true);
            _recyKg = CobolNumeric.Store(_recyG / 1000m, 11, 3, rounded: true);
            _rw.Terminate();
        }
    }
}
