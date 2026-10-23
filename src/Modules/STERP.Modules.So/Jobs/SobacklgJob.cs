using System.Text.Json.Nodes;
using Sterp.Modules.So.Domain;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Jobs;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.So.Jobs;

/// <summary>
/// SOBACKLG (cobol/batch/so/SOBACKLG.cob; FR-STERP-SO-033, RPT-008): sales backlog and fulfilment report - every OPEN or
/// PARTIAL delivery schedule of an OPEN line on an ACCEPTED or AMENDED order, bucketed by current promise date against
/// ASAT, ASAT+7 and ASAT+14 (DATECALC ADDDAYS), lateness measured from the ORIGINAL promise; W-1605 / RC 4 when any
/// schedule is late. Read only; JOBLOG START/END with the saved run id.
/// </summary>
internal static class SobacklgJob
{
    public const string Code = "SOBACKLG";

    public static JobDefinition Definition => new()
    {
        JobCode = Code,
        Slice = SoBatch.Slice,
        SubmitOperationId = "so_submitSobacklgRun",
        ParameterOrder = ["CO", "SITE", "FROM", "TO", "ASAT", "CUST", "USER"],
        SelfRegistration = pre =>
        {
            var a = Arguments.Read(pre.Args, DateTime.Now);
            return SoBatch.JoblogStart(Code, a.Co, a.User, "", pre.CommandLine);
        },
        EndValues = SoBatch.End,
        Execute = RunAsync,
    };

    /// <summary>0100-READ-ARGS: fixed fields CO, SITE, USER, FROM, TO, ASAT; CUST by key; defaults STC, SALESMGR, machine date.</summary>
    private sealed record Arguments(string Co, string Site, string Cust, string User, string From, string To, string AsAt)
    {
        public static Arguments Read(CommandLineArgs a, DateTime now)
        {
            var co = Cob.X(a.Co, 10);
            var user = Cob.X(a.User, 20);
            var asat = Cob.X(a.AsAt, 8);
            if (Cob.Spaces(co)) co = "STC";
            if (Cob.Spaces(user)) user = "SALESMGR";
            if (Cob.Spaces(asat)) asat = Cob.D8(CobolDate.ToYyyymmdd(now));
            return new Arguments(co, Cob.X(a.Site, 10), Cob.X(a.Get("CUST"), 20), user, Cob.X(a.From, 8), Cob.X(a.To, 8), asat);
        }
    }

    /// <summary>WS-DET: the detail values (kept between rows, as the WORKING-STORAGE group is never reset).</summary>
    private sealed class Det
    {
        public string Bucket = "";
        public string Order = "";
        public int Line;
        public int Sched;
        public string Cust = "";
        public string Item = "";
        public decimal Accepted, Cancelled, Despatched, Billed, Remaining, Value;
        public string Release = "";
        public string Requested = "";
        public string OrigProm = "";
        public string CurrProm = "";
        public int DaysLate;
        public int Changes;
        public string Hold = "";
    }

    private const string SelectText =
        "SELECT CASE WHEN CONVERT(char(8),s.CurrentPromiseDate,112) < ? THEN '1 Overdue' WHEN " +
        "CONVERT(char(8),s.CurrentPromiseDate,112) <= ? THEN '2 This week' WHEN " +
        "CONVERT(char(8),s.CurrentPromiseDate,112) <= ? THEN '3 Next week' ELSE '4 Later' END, " +
        "l.OrderNumber, l.LineNum, s.ScheduleNo, o.CustomerCode, ISNULL(l.ItemCode,''), " +
        "l.AcceptedQty, l.CancelledQty, l.DespatchedQty, l.BilledQty, s.Quantity, s.DespatchedQty, " +
        "l.UnitPrice, l.PricePerQty, l.DiscountPct1, l.DiscountPct2, " +
        "CONVERT(char(8),s.RequestedDate,112), CONVERT(char(8),s.OriginalPromiseDate,112), " +
        "CONVERT(char(8),s.CurrentPromiseDate,112), DATEDIFF(day, s.OriginalPromiseDate, CONVERT(date, ?)), " +
        "s.ChangeCount, o.ReleaseStatus, ISNULL(o.HoldReason,''), s.Status, l.Status, ISNULL(l.Description,''), o.CurrencyCode " +
        "FROM so.DeliverySchedule s JOIN so.SalesOrderLine l ON l.SalesOrderLineId = s.SalesOrderLineId " +
        "JOIN so.SalesOrder o ON o.SalesOrderId = l.SalesOrderId WHERE o.CompanyCode = ? AND l.Status = 'OPEN' AND " +
        "s.Status IN ('OPEN','PARTIAL') AND o.CommercialStatus IN ('ACCEPTED','AMENDED') AND " +
        "(? = '' OR ISNULL(s.SiteCode, o.SiteCode) = ?) AND (? = '' OR o.CustomerCode = ?) AND " +
        "(? = '' OR CONVERT(char(8),o.OrderDate,112) >= ?) AND (? = '' OR CONVERT(char(8),o.OrderDate,112) <= ?) " +
        "ORDER BY 1, o.CustomerCode, l.OrderNumber, l.LineNum, s.ScheduleNo";

    private static async Task<int> RunAsync(JobRunContext ctx)
    {
        var ct = ctx.Cancellation;
        var now = DateTime.Now;
        var a = Arguments.Read(ctx.Args, now);
        var rc = 0;
        ctx.Display("STERP-I-1600 SOBACKLG sales backlog for " + Cob.Trim(a.Co));

        // 0250-WEEK-BOUNDS
        var cal = ctx.Get<IBusinessCalendar>();
        var work = SoBatch.Date8(a.AsAt);
        var w1 = await cal.AddDaysAsync(work, 7, ct);
        var week1 = w1.Header.ReturnCode > 4 ? a.AsAt : Cob.D8(w1.ResultDate);
        var w2 = await cal.AddDaysAsync(work, 14, ct);
        var week2 = w2.Header.ReturnCode > 4 ? week1 : Cob.D8(w2.ResultDate);

        // 0300-OPEN-SPOOL / 0350-FILL-HEADINGS
        var sql = ctx.Get<ISqlRunner>();
        var heading = SoBatch.Heading(a.Co, "RPT-008", "Sales backlog and fulfilment (RPT-008)", a.User, now);
        heading.Subtitle = Cob.Spaces(a.Site) ? "All sites" : Cob.X("Site " + Cob.Trim(a.Site), 60);
        var asat8 = CobolText.Fit(a.AsAt, 8);
        heading.AsAtText = "As at " + asat8[6..8] + "/" + asat8[4..6] + "/" + asat8[..4];
        await SoBatch.ReadCompanyAsync(sql, heading, a.Co, ct);
        if (!SoBatch.TryOpenSpool(ctx, Code, a.Co, "STERP-E-1601 cannot build the spool path", "STERP-E-1602 cannot open ", out var spool))
        {
            return 12;
        }

        var d = new Det();
        int tLines = 0, tLate = 0, tHeld = 0, rowsOut = 0;
        decimal tRemaining = 0, tValue = 0, tLateValue = 0;
        decimal sumRemaining = 0, sumValue = 0;
        var items = new JsonArray();

        using (spool)
        {
            // RD R-SOBACKL: CONTROLS ARE FINAL D-BUCKET, PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 54
            var bucket = new RwControl(() => d.Bucket, v => d.Bucket = v, 12)
            {
                Heading =
                [
                    RwLine.PlusN(2, () => new RwText().Lit(1, "Promise date bucket: ").Put(22, d.Bucket, 12)
                        .Lit(40, "Lateness is measured against the ORIGINAL promise").Text),
                    RwLine.Plus1(() => new RwText().Lit(1, "Order").Lit(16, "Line").Lit(21, "Sch").Lit(25, "Customer").Lit(38, "Item")
                        .Lit(57, " Accepted").Lit(67, "Cancelled").Lit(77, "Despatchd").Lit(87, "   Billed").Lit(97, "Remaining")
                        .Lit(107, " Value outstg").Lit(121, "Release").Text),
                    RwLine.Plus1(() => new RwText().Lit(5, "Requested").Lit(17, "Original promis").Lit(33, "Current promis")
                        .Lit(48, "Days late").Lit(58, "Changes").Lit(66, "Hold reason").Text),
                    RwLine.Plus1(() => new string('-', 132)),
                ],
                Footing =
                [
                    RwLine.Plus1(() => new RwText().Lit(25, "Bucket totals").Num(97, sumRemaining, Qty).Num(107, sumValue, Val).Text),
                    RwLine.Plus1(() => new string('-', 132)),
                ],
            };
            var w = new GnuReportWriter(spool.WriteLine, heading, 60, 1, 9, 54, [bucket],
            [
                RwLine.PlusN(2, () => new RwText().Lit(1, "Open schedules").Num(28, tLines, "Z(6)9").Lit(40, "quantity remaining")
                    .Num(61, tRemaining, "Z(11)9.999").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "Value outstanding").Num(28, tValue, "Z(11)9.99-").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "Late on ORIGINAL promise").Num(28, tLate, "Z(6)9").Lit(40, "value at risk")
                    .Num(61, tLateValue, "Z(11)9.99-").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "Held, not released").Num(28, tHeld, "Z(6)9").Text),
                RwLine.PlusN(2, () => new RwText().Lit(1, "Remaining = accepted less cancelled less despatched").Text),
                RwLine.Plus1(() => new RwText().Lit(1, "promise does not change the original service measure.").Text),
            ]);
            var detail = new[]
            {
                RwLine.Plus1(() => new RwText().Put(1, d.Order, 14).Num(16, d.Line, "ZZZ9").Num(21, d.Sched, "ZZ9").Put(25, d.Cust, 12)
                    .Put(38, d.Item, 18).Num(57, d.Accepted, Qty).Num(67, d.Cancelled, Qty).Num(77, d.Despatched, Qty)
                    .Num(87, d.Billed, Qty).Num(97, d.Remaining, Qty).Num(107, d.Value, Val).Put(121, d.Release, 12).Text),
                RwLine.Plus1(() => new RwText().Put(5, d.Requested, 10).Put(17, d.OrigProm, 10).Put(33, d.CurrProm, 10)
                    .Num(48, d.DaysLate, "ZZZZ9-").Num(59, d.Changes, "ZZ9").Put(66, d.Hold, 54).Text),
            };
            w.Initiate();

            // 1000-PRODUCE
            IReadOnlyList<SqlioRow> rows = [];
            try
            {
                rows = await sql.FetchAllAsync(SelectText,
                [
                    SqlioParm.Text(a.AsAt), SqlioParm.Text(week1), SqlioParm.Text(week2), SqlioParm.Text(a.AsAt), SqlioParm.Text(a.Co),
                    SqlioParm.Text(a.Site), SqlioParm.Text(a.Site), SqlioParm.Text(a.Cust), SqlioParm.Text(a.Cust),
                    SqlioParm.Text(a.From), SqlioParm.Text(a.From), SqlioParm.Text(a.To), SqlioParm.Text(a.To),
                ], SoBatch.Widths(27, 100), ct);
            }
            catch (SqlioException ex)
            {
                rc = 12;
                ctx.Display("STERP-E-1603 " + CobolText.Fit(ex.DriverText, 100));
            }

            string? saved = null;
            foreach (var row in rows)
            {
                // 1100-EMIT
                rowsOut++;
                d.Bucket = CobolText.Fit(row.Raw(0), 100).Substring(2, 11).TrimEnd(' ');
                d.Order = Cob.X(row.Raw(1), 14);
                d.Line = (int)CobolNumeric.Store(row.NumVal(2), 4, 0, signed: false);
                d.Sched = (int)CobolNumeric.Store(row.NumVal(3), 3, 0, signed: false);
                d.Cust = Cob.X(row.Raw(4), 12);
                d.Item = Cob.X(Cob.Spaces(row.Raw(5)) ? row.Raw(25) : row.Raw(5), 18);
                d.Accepted = CobolNumeric.Store(row.NumVal(6), 11, 3);
                d.Cancelled = CobolNumeric.Store(row.NumVal(7), 11, 3);
                d.Despatched = CobolNumeric.Store(row.NumVal(8), 11, 3);
                d.Billed = CobolNumeric.Store(row.NumVal(9), 11, 3);
                // 1200-REMAINING
                d.Remaining = CobolNumeric.Store(row.NumVal(10) - row.NumVal(11), 11, 3);
                if (d.Remaining < 0) d.Remaining = 0;
                // 1300-VALUE: no zero-divisor guard - the size error leaves D-VALUE unchanged
                var per = row.NumVal(13);
                if (per != 0)
                {
                    d.Value = CobolNumeric.Store(d.Remaining * row.NumVal(12) / per * (1 - row.NumVal(14) / 100) * (1 - row.NumVal(15) / 100),
                        11, 2, rounded: true);
                }
                // 1400-DATES
                d.Requested = SoBatch.Dmy(SoBatch.MoveDigits8(CobolText.Fit(row.Raw(16), 8)));
                d.OrigProm = SoBatch.Dmy(SoBatch.MoveDigits8(CobolText.Fit(row.Raw(17), 8)));
                d.CurrProm = SoBatch.Dmy(SoBatch.MoveDigits8(CobolText.Fit(row.Raw(18), 8)));
                d.DaysLate = (int)CobolNumeric.Store(row.NumVal(19), 5, 0);
                d.Changes = (int)CobolNumeric.Store(row.NumVal(20), 3, 0, signed: false);
                d.Release = Cob.X(row.Raw(21), 12);
                d.Hold = Cob.X(row.Raw(22), 54);
                if (d.Release != "RELEASED")
                {
                    tHeld++;
                    if (Cob.Spaces(d.Hold)) d.Hold = Cob.X("Not released - " + Cob.Trim(d.Release), 54);
                }
                tLines++;
                tRemaining = CobolNumeric.Store(tRemaining + d.Remaining, 13, 3);
                tValue = CobolNumeric.Store(tValue + d.Value, 13, 2);
                if (d.DaysLate > 0)
                {
                    tLate++;
                    tLateValue = CobolNumeric.Store(tLateValue + d.Value, 13, 2);
                }

                // GENERATE DE-LINE: a D-BUCKET break presents CF-BUCKET with the sums so far, then the SUM counters restart
                var current = CobolText.Fit(d.Bucket, 12);
                var broke = saved is not null && saved != current;
                w.Generate(detail);
                if (broke)
                {
                    sumRemaining = 0;
                    sumValue = 0;
                }
                sumRemaining += d.Remaining;
                sumValue += d.Value;
                saved = current;
                items.Add(new JsonObject
                {
                    ["bucket"] = d.Bucket.TrimEnd(), ["orderNumber"] = d.Order, ["lineNum"] = d.Line, ["scheduleNo"] = d.Sched,
                    ["customerCode"] = d.Cust, ["item"] = d.Item, ["accepted"] = d.Accepted, ["cancelled"] = d.Cancelled,
                    ["despatched"] = d.Despatched, ["billed"] = d.Billed, ["remaining"] = d.Remaining, ["valueOutstanding"] = d.Value,
                    ["releaseStatus"] = d.Release, ["requested"] = d.Requested, ["originalPromise"] = d.OrigProm,
                    ["currentPromise"] = d.CurrProm, ["daysLate"] = d.DaysLate, ["changes"] = d.Changes, ["holdReason"] = d.Hold,
                });
            }

            // 0400-CLOSE-SPOOL: TERMINATE R-SOBACKL
            w.Terminate();
        }
        ctx.Display("STERP-I-1604 " + SoBatch.Num(rowsOut, 9) + " open schedule(s) reported");
        if (tLate > 0)
        {
            ctx.Display("STERP-W-1605 " + SoBatch.Num(tLate, 7) + " schedule(s) late on the original promise");
            if (rc < 4) rc = 4;
        }
        ctx.AnnounceSpool(spool.Path);
        ctx.RowsProcessed = tLines;
        ctx.Result = new JsonObject
        {
            ["jobName"] = Code, ["companyCode"] = a.Co, ["siteCode"] = a.Site, ["customerCode"] = a.Cust, ["asAt"] = a.AsAt,
            ["schedules"] = items, ["openSchedules"] = tLines, ["quantityRemaining"] = tRemaining, ["valueOutstanding"] = tValue,
            ["lateLines"] = tLate, ["valueAtRisk"] = tLateValue, ["heldLines"] = tHeld, ["returnCode"] = rc,
        };
        return rc;
    }

    private const string Qty = "Z(5)9.999";
    private const string Val = "Z(9)9.99-";
}
