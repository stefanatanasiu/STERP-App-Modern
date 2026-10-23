using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Xmd.Jobs;

/// <summary>
/// KPIOTIF (cobol/batch/xmd/KPIOTIF.cob; FR-STERP-XMD-023): on-time-in-full for customers (original promise) and suppliers
/// (original confirmation, up to XMD.OTIF.EARLY.DAYS working days early on the ENG calendar) with the published metric
/// definitions. Read only. The customer cohort's 9450-FORMAT-DATE calls overwrite XB-DATE-ED, which is then the lower bound of
/// the supplier cohort (BRF-KPIOTIF-01, reproduced). Listing FL-070 (RD PAGE LIMIT 60, FIRST DETAIL 8, LAST DETAIL 52, FOOTING 58).
/// </summary>
internal static class KpiotifProgram
{
    private static readonly int[] Widths = Enumerable.Repeat(120, 30).ToArray();

    private sealed class State
    {
        public string DateEd = "00000000";
        public string DateEd2 = "00000000";
        public bool Detail;
        public int EarlyDays = 5;
        public int Due, Otif, Late, Short, Early, Open;
    }

    public static async Task<int> RunAsync(JobRunContext ctx)
    {
        var b = XmdBatch.Start(ctx);
        var result = new JsonObject();
        try
        {
            return await RunAsync(b, result);
        }
        finally
        {
            ctx.Result = result;
        }
    }

    private static async Task<int> RunAsync(XmdBatch b, JsonObject result)
    {
        var st = new State();
        // 0100-READ-ARGS
        if (b.Args.To == 0) b.SetDates(to: b.AsAt);
        if (b.Args.From == 0) b.SetDates(from: (int)CobolNumeric.Store(b.Args.To - 10000m, 8, 0, signed: false));
        if (CobolText.Fit(b.GetArg("DETAIL"), 1) == "Y") st.Detail = true;
        st.EarlyDays = (int)CobolNumeric.Store(await b.GetParmNumAsync("XMD.OTIF.EARLY.DAYS", 5m), 4, 0);
        var sFrom = FormatDate(st, b.Args.From);
        var sTo = FormatDate(st, b.Args.To);
        st.DateEd = Disp.D8(b.Args.From);
        st.DateEd2 = Disp.D8(b.Args.To);
        const string basis = "Basis: original promise, full quantity, despatch";
        b.Display("STERP-I-0100 KPIOTIF " + CobolText.Trim(b.Co) + " " + Disp.D8(b.Args.From) + " to " + Disp.D8(b.Args.To));

        result["from"] = IsoDate(b.Args.From);
        result["to"] = IsoDate(b.Args.To);
        result["basis"] = basis;
        var definitions = new JsonArray();
        var sides = new JsonArray();
        var schedules = new JsonArray();
        result["definitions"] = definitions;
        result["sides"] = sides;
        result["schedules"] = schedules;

        if (!await b.OpenSpoolAsync("KPIOTIF", "On time in full - customer and supplier", "Shared measure definitions (XMD-010, D-19)") || b.Rc > 4)
        {
            return Finish(b, result);
        }
        var rw = b.Report(
        [
            RwLine.PlusN(2, () => new string('=', 132)),
            RwLine.Plus1(() => new RwText().Lit(3, "Measured on the original promise (customer) and")
                .Lit(50, "original confirmation (supplier) per decision D-19.").Text),
        ]);
        rw.Initiate();
        rw.Generate(
            RwLine.Plus1(() => new RwText().Lit(1, "Obligations due from     ").Put(27, sFrom, 10).Lit(39, "to ").Put(42, sTo, 10)
                .Put(56, basis, 60).Text),
            RwLine.Plus1(() => new RwText().Lit(1, "Cohort: eligible schedule obligations whose due")
                .Lit(48, "date falls in the window. Zero cohort = undefined.").Text));
        await PublishMeasuresAsync(b, rw, definitions);
        rw.Generate(
            RwLine.PlusN(2, () => new RwText().Lit(1, "Side").Lit(12, "Party").Lit(33, "Order").Lit(54, "Line").Lit(60, "Sch")
                .Lit(66, "Promised").Lit(78, "Actual  ").Lit(90, "Days").Lit(97, "Scheduled").Lit(110, "Delivered").Lit(122, "Result").Text),
            RwLine.Plus1(() => new string('-', 132)));

        // 1000-CUSTOMER
        await CohortAsync(b, st, rw, "CUSTOMER",
            "SELECT s.OrderNumber, CONVERT(varchar(12),s.LineNum), CONVERT(varchar(12),s.ScheduleNo), ISNULL(d.Party,''), " +
            "CONVERT(varchar(24),s.Quantity), CONVERT(varchar(24),ISNULL(s.DespatchedQty,0)), CONVERT(char(8),s.OriginalPromiseDate,112), " +
            "ISNULL(d.Actual,''), s.Status FROM so.DeliverySchedule s OUTER APPLY (SELECT MAX(CONVERT(char(8),h.DespatchDate,112)) AS Actual, " +
            "MAX(h.CustomerCode) AS Party FROM so.DespatchLine dl JOIN so.Despatch h ON h.DespatchId = dl.DespatchId WHERE h.CompanyCode " +
            "= s.CompanyCode AND dl.OrderNumber = s.OrderNumber AND dl.OrderLineNum = s.LineNum AND dl.ScheduleNo = s.ScheduleNo AND " +
            "h.Status = 'CONFIRMED') d WHERE s.CompanyCode = ? AND s.Status <> 'CANCELLED' AND CONVERT(char(8),s.OriginalPromiseDate,112) " +
            "BETWEEN ? AND ? ORDER BY s.OrderNumber, s.LineNum, s.ScheduleNo",
            "Customer OTIF (original promise, full qty)", sides, schedules);
        // 2000-SUPPLIER
        await CohortAsync(b, st, rw, "SUPPLIER",
            "SELECT s.OrderNumber, CONVERT(varchar(12),s.LineNum), CONVERT(varchar(12),s.ScheduleNo), ISNULL(r.Party,''), " +
            "CONVERT(varchar(24),s.Quantity), CONVERT(varchar(24),ISNULL(s.ReceivedQty,0)), CONVERT(char(8),s.OriginalConfirmedDate,112), " +
            "ISNULL(r.Actual,''), s.Status FROM po.PurchaseSchedule s OUTER APPLY (SELECT MAX(CONVERT(char(8),h.PhysicalDate,112)) AS Actual, " +
            "MAX(h.SupplierCode) AS Party FROM po.Receipt h WHERE h.CompanyCode = s.CompanyCode AND h.PurchaseOrderNumber = s.OrderNumber " +
            "AND h.Status = 'CONFIRMED') r WHERE s.CompanyCode = ? AND s.Status <> 'CANCELLED' AND CONVERT(char(8),s.OriginalConfirmedDate,112) " +
            "BETWEEN ? AND ? ORDER BY s.OrderNumber, s.LineNum, s.ScheduleNo",
            "Supplier OTIF (original confirmation, 5 day early)", sides, schedules);

        rw.Terminate();
        b.CloseSpool();
        return Finish(b, result);
    }

    private static int Finish(XmdBatch b, JsonObject result)
    {
        var rc = b.Finish();
        result["returnCode"] = rc;
        return rc;
    }

    /// <summary>0300-PUBLISH-MEASURES: the active OTIF definitions (an SQL error prints the 'unavailable' text and sets RC 4).</summary>
    private static async Task PublishMeasuresAsync(XmdBatch b, GnuReportWriter rw, JsonArray definitions)
    {
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await b.Sql.FetchAllAsync(
                "SELECT MetricCode, Name, LEFT(Definition + ' Zero denominator: ' + ISNULL(ZeroDenominatorRule,''),110) FROM " +
                "rpt.MetricDefinition WHERE MetricCode IN ('OTIF.CUSTOMER','OTIF.SUPPLIER') AND IsActive = 1 ORDER BY MetricCode",
                [], Widths[..3], b.Ct);
        }
        catch (SqlioException)
        {
            Measure(rw, definitions, "OTIF.CUSTOMER", "On time in full - customer", "Definition unavailable - rpt.MetricDefinition");
            b.Rc = 4;
            return;
        }
        foreach (var row in rows) Measure(rw, definitions, row.Raw(0), row.Raw(1), row.Raw(2));
    }

    private static void Measure(GnuReportWriter rw, JsonArray definitions, string code, string name, string text)
    {
        var mCode = CobolText.Fit(code, 30);
        var mName = CobolText.Fit(name, 40);
        var mText = CobolText.Fit(text, 110);
        rw.Generate(
            RwLine.Plus1(() => new RwText().Put(3, mCode, 30).Put(34, mName, 40).Text),
            RwLine.Plus1(() => new RwText().Put(6, mText, 110).Text));
        definitions.Add(new JsonObject
        {
            ["metricCode"] = mCode.TrimEnd(' '),
            ["name"] = mName.TrimEnd(' '),
            ["text"] = mText.TrimEnd(' '),
        });
    }

    /// <summary>1050-ZERO-COHORT, 1060-RUN-COHORT (+ 1100-ONE-SCHEDULE per row) and 3000-SUMMARISE of one side.</summary>
    private static async Task CohortAsync(XmdBatch b, State st, GnuReportWriter rw, string side, string statement, string label,
        JsonArray sides, JsonArray schedules)
    {
        st.Due = st.Otif = st.Late = st.Short = st.Early = st.Open = 0;
        IReadOnlyList<SqlioRow>? rows = null;
        try
        {
            rows = await b.Sql.FetchAllAsync(statement,
                [SqlioParm.Text(b.Co), SqlioParm.Text(st.DateEd), SqlioParm.Text(st.DateEd2)], Widths[..9], b.Ct);
        }
        catch (SqlioException ex)
        {
            b.Display("STERP-W-1061 " + CobolText.Fit(ex.DriverText, 90));
            b.Rc = 4;
        }
        if (rows is not null)
        {
            foreach (var row in rows) await OneScheduleAsync(b, st, rw, side, row, schedules);
        }
        // 3000-SUMMARISE
        var uLabel = CobolText.Fit(label, 40);
        string uPct;
        if (st.Due == 0)
        {
            uPct = "UNDEFINED";
        }
        else
        {
            uPct = CobolText.Fit(CobolText.Fit(Disp.Num(OsvsPercent(st.Otif, st.Due)), 200)[..7] + " pc", 12);
        }
        rw.Generate(RwLine.Plus1(() => new RwText().Put(3, uLabel, 40).Lit(45, "Eligible ").Num(54, st.Due, "ZZZ,ZZ9")
            .Lit(63, "OTIF  ").Num(69, st.Otif, "ZZZ,ZZ9").Lit(78, "Late ").Num(83, st.Late, "ZZZ,ZZ9")
            .Lit(92, "Short ").Num(98, st.Short, "ZZZ,ZZ9").Lit(107, "Open ").Num(112, st.Open, "ZZZ,ZZ9")
            .Put(121, uPct, 12).Text));
        b.Display("STERP-I-3001 " + CobolText.Trim(uLabel) + " eligible " + Disp.Signed(st.Due, 7) + " otif " + Disp.Signed(st.Otif, 7));
        sides.Add(new JsonObject
        {
            ["label"] = uLabel.TrimEnd(' '),
            ["eligible"] = st.Due,
            ["otif"] = st.Otif,
            ["late"] = st.Late,
            ["short"] = st.Short,
            ["early"] = st.Early,
            ["open"] = st.Open,
            ["percentage"] = uPct.TrimEnd(' '),
        });
    }

    /// <summary>
    /// COMPUTE XB-PCT ROUNDED = (K-OTIF * 100) / K-DUE as the deployed GnuCOBOL 3.2 build computes it (XMD is compiled with
    /// -std=mvs, i.e. arithmetic-osvs). Before the division the product is aligned to the result's 4 decimals by
    /// cob_decimal_align, which for an integer intermediate divides by 10^4 instead of scaling up: the product loses its
    /// four low-order digits (zero below 10000, hence "0.00000 pc" for fewer than 100 OTIF schedules). Characterized with
    /// the GnuCOBOL 3.2 compiler (the legacy toolchain GC32-BDB-SP1) and the legacy KPIOTIF.exe listing.
    /// </summary>
    internal static decimal OsvsPercent(long otif, long due)
    {
        var aligned = decimal.Truncate(otif * 100m / 10000m) * 10000m;
        return aligned == 0 ? 0m : CobolNumeric.Store(aligned / due, 5, 4, signed: true, rounded: true);
    }

    /// <summary>1100-ONE-SCHEDULE + 1200-JUDGE.</summary>
    private static async Task OneScheduleAsync(XmdBatch b, State st, GnuReportWriter rw, string side, SqlioRow row, JsonArray schedules)
    {
        var dOrder = CobolText.Fit(row.Raw(0), 20);
        var dLine = (int)CobolNumeric.Store(row.NumVal(1), 4, 0, signed: false);
        var dSched = (int)CobolNumeric.Store(row.NumVal(2), 4, 0, signed: false);
        var dParty = CobolText.Fit(row.Raw(3), 20);
        var dQty = CobolNumeric.Store(row.NumVal(4), 11, 3);
        var dDone = CobolNumeric.Store(row.NumVal(5), 11, 3);
        var promise = BatchSuite.IsoToNum(row.Raw(6));
        var dPromise = FormatDate(st, promise);
        var actual = BatchSuite.IsoToNum(row.Raw(7));
        var dDays = 0;
        string dActual;
        string dResult;
        st.Due++;
        if (actual == 0)
        {
            dActual = "";
            dResult = CobolText.Fit("NOT DELIVERED", 12);
            st.Open++;
            st.Late++;
        }
        else
        {
            dActual = FormatDate(st, actual);
            dDays = promise == 0
                ? 0
                : (int)CobolNumeric.Store(CobolDate.IntegerOfDate(actual) - (decimal)CobolDate.IntegerOfDate(promise), 5, 0);
            // 1200-JUDGE
            dResult = "OTIF";
            if (dDays > 0)
            {
                dResult = "LATE";
                st.Late++;
            }
            if (side == "SUPPLIER" && dDays < 0)
            {
                var earliest = await EarlyLimitAsync(b, st, promise);
                if (actual < earliest)
                {
                    dResult = "EARLY";
                    st.Early++;
                }
            }
            if (dDone < dQty)
            {
                dResult = dResult == "OTIF" ? "SHORT" : "LATE/SHORT";
                st.Short++;
            }
            if (dResult == "OTIF") st.Otif++;
        }
        b.RowsOut++;
        if (st.Detail || dResult.TrimEnd(' ') != "OTIF")
        {
            rw.Generate(RwLine.Plus1(() => new RwText().Put(1, side, 10).Put(12, dParty, 20).Put(33, dOrder, 20).Num(54, dLine, "ZZZ9")
                .Num(60, dSched, "ZZZ9").Put(66, dPromise, 10).Put(78, dActual, 10).Num(89, dDays, "-ZZZ9")
                .Num(96, dQty, "ZZZ,ZZ9.999").Num(109, dDone, "ZZZ,ZZ9.999").Put(122, dResult, 11).Text));
            schedules.Add(new JsonObject
            {
                ["side"] = side,
                ["party"] = dParty.TrimEnd(' '),
                ["order"] = dOrder.TrimEnd(' '),
                ["line"] = dLine,
                ["schedule"] = dSched,
                ["promiseDate"] = IsoDate(promise),
                ["actualDate"] = actual == 0 ? null : IsoDate(actual),
                ["quantity"] = CobolNumeric.WithScale(dQty, 3),
                ["delivered"] = CobolNumeric.WithScale(dDone, 3),
                ["daysLate"] = dDays,
                ["result"] = dResult.TrimEnd(' '),
                ["reason"] = "",
            });
        }
    }

    /// <summary>1250-EARLY-LIMIT: DATECALC ADDBUS promise less the early days on ENG (refused: promise - 7 as a number).</summary>
    private static async Task<int> EarlyLimitAsync(XmdBatch b, State st, int promise)
    {
        var r = await b.Ctx.Get<IBusinessCalendar>().AddBusinessDaysAsync(promise, 0 - st.EarlyDays, "ENG", b.Ct);
        return r.Header.ReturnCode < 8 ? r.ResultDate : (int)CobolNumeric.Store(promise - 7m, 8, 0, signed: false);
    }

    /// <summary>9450-FORMAT-DATE: a non-zero date also overwrites the shared XB-DATE-ED (BRF-XMDBATPR-04).</summary>
    private static string FormatDate(State st, int yyyymmdd)
    {
        if (yyyymmdd == 0) return "00/00/0000";
        st.DateEd = Disp.D8(yyyymmdd);
        return BatchSuite.FormatDate(yyyymmdd);
    }

    private static string? IsoDate(int yyyymmdd) =>
        CobolDate.FromYyyymmdd(yyyymmdd) is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
}
