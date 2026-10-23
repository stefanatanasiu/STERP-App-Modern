using System.Text.Json.Nodes;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ap.Batch;

/// <summary>
/// APPPR - payment practices and performance, RPT-018 (cobol/batch/ap/APPPR.cob; FR-STERP-AP-044). Paid invoices of
/// ap.vw_PaymentPracticesBase in the period banded by days taken, the four headline figures (percentages of the invoice count,
/// ROUNDED), and in LIVE mode a new report version through ap.usp_PaymentPracticesData (failure RC 4). FROM and TO required
/// (RC 12 after registration). RD R-APPPR (CONTROLS FINAL D-BAND, PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 50,
/// FOOTING 52). JOBLOG END re-initialised (SME-009).
/// </summary>
internal sealed class ApPprJob
{
    public static JobDefinition Definition => new()
    {
        JobCode = "APPPR",
        Slice = "STERP-AP",
        SubmitOperationId = "ap_submitAppprRun",
        ParameterOrder = ["CO", "FROM", "TO", "MODE", "USER"],
        SelfRegistration = pre =>
        {
            var a = ApBatchArgs.From(pre.Args);
            return ApJobSubmission.JoblogStart("APPPR", a.Co, a.User, pre.Args.Text);
        },
        EndValues = ApJobSubmission.NeverEnded,
        Execute = ctx => new ApPprJob().RunAsync(ctx),
    };

    // WS-DET
    private string _dBand = "";
    private string _dSupplier = "";
    private string _dDocRef = "";
    private string _dReceived = "";
    private string _dPaidDate = "";
    private decimal _dTermDays, _dDaysTaken, _dAmount;
    private string _dInTerms = "";
    private string _dDisputed = "";

    // WS-TOT, WS-CALC
    private long _tCount, _tDays, _tIn30, _tIn60, _tOver60, _tLate, _tDisputed;
    private decimal _tValue;
    private decimal _cAvg, _cPct30, _cPct60, _cPctOver, _cPctLate;

    // SUM counter of CF-BAND
    private decimal _sumBand;

    public async Task<int> RunAsync(JobRunContext ctx)
    {
        var sql = ctx.Get<ISqlRunner>();
        var ct = ctx.Cancellation;
        var rc = 0;

        // 0100-READ-ARGS
        var a = ApBatchArgs.From(ctx.Args);
        var from = ctx.Args.Get("FROM") is { } f ? (int)CobolNumeric.Store(CobolNumeric.NumVal(f), 8, 0, signed: false) : 0;
        var to = ctx.Args.Get("TO") is { } t ? (int)CobolNumeric.Store(CobolNumeric.NumVal(t), 8, 0, signed: false) : 0;
        var mode = a.Mode.Length > 0 ? a.Mode : "LIVE";
        var periodRef = "";
        if (from == 0 || to == 0)
        {
            rc = 12;
            ctx.Display("STERP-E-0101 APPPR needs FROM= and TO=");
        }
        else
        {
            periodRef = CobolText.Trimmed("PPR-" + D8(from) + "-" + D8(to), 30);
            ctx.Display("STERP-I-0100 APPPR payment practices RPT-018");
        }

        var spoolName = "";
        if (rc < 8)
        {
            // 0300-OPEN-SPOOL / 0350-FILL-HEADINGS
            SpoolWriter? spool = null;
            var heading = new ReportHeading
            {
                CompanyCode = a.Co,
                ReportId = "APPPR",
                Title = "Payment practices and performance",
                User = a.User,
                StatusText = mode,
                RunAt = DateTime.Now,
                AsAtText = "To " + ApBatchArgs.Ddmmyyyy(to),
                Subtitle = "Reporting period from " + ApBatchArgs.Ddmmyyyy(from),
            };
            try
            {
                spool = ctx.OpenSpool("APPPR", a.Co);
                spoolName = spool.Path;
            }
            catch (LegacyOutcomeException)
            {
                rc = 12;
                ctx.Display("STERP-E-0301 cannot build the spool path");
            }
            catch (IOException)
            {
                rc = 12;
                ctx.Display("STERP-E-0302 cannot open the spool file");
            }

            if (spool is not null)
            {
                await ApBatchArgs.FillCompanyAsync(sql, heading, a.Co, ct);
                var w = Writer(spool, heading);
                w.Initiate();

                // 1000-PRODUCE-REPORT / 1100-OPEN-PAID
                IReadOnlyList<SqlioRow>? rows = null;
                try
                {
                    rows = await sql.FetchAllAsync(
                        "SELECT CASE WHEN DATEDIFF(day, d.StartDate, d.PaidDate) <= 30 THEN '1 up to 30 days' WHEN DATEDIFF(day, d.StartDate, " +
                        "d.PaidDate) <= 60 THEN '2 31 to 60 days' ELSE '3 over 60 days' END, d.SupplierCode, d.DocumentNumber, " +
                        "CONVERT(varchar(10), d.StartDate, 103), CONVERT(varchar(10), d.PaidDate, 103), CONVERT(varchar(10), ISNULL(d.TermDays,0)), " +
                        "CONVERT(varchar(10), DATEDIFF(day, d.StartDate, d.PaidDate)), CONVERT(varchar(30), ISNULL(d.AmountBase,0)), " +
                        "CASE WHEN d.PaidDate <= d.DueDate THEN 'Yes' ELSE 'No' END, CASE WHEN ISNULL(d.WasDisputed,0) = 1 THEN 'Yes' ELSE 'No' END " +
                        "FROM ap.vw_PaymentPracticesBase d WHERE d.CompanyCode = ? AND d.PaidDate >= ? AND d.PaidDate <= ? ORDER BY 1, d.SupplierCode",
                        [SqlioParm.Text(a.Co), SqlioParm.Text(IsoDate(from)), SqlioParm.Text(IsoDate(to))], Enumerable.Repeat(100, 24).ToArray(), ct);
                }
                catch (SqlioException ex)
                {
                    ctx.Display("STERP-E-1101 " + CobolText.Fit(ex.DriverText, 512));
                    rc = 12;
                }

                var detail = RwLine.Plus1(() => new RwText().Src(3, _dSupplier, 20).Src(25, _dDocRef, 24).Src(51, _dReceived, 10)
                    .Src(63, _dPaidDate, 10).Ed(76, _dTermDays, "Z(3)9").Ed(84, _dDaysTaken, "Z(4)9").Ed(92, _dAmount, "Z(9)9.99-")
                    .Src(118, _dInTerms, 3).Src(127, _dDisputed, 3).Text);
                var result = new JsonArray();
                var generated = false;
                foreach (var r in rows ?? [])
                {
                    // 1200-FETCH-PAID
                    var previous = _dBand;
                    _dBand = CobolText.Trimmed(r.Raw(0), 18);
                    _dSupplier = CobolText.Trimmed(r.Raw(1), 20);
                    _dDocRef = CobolText.Trimmed(r.Raw(2), 24);
                    _dReceived = CobolText.Trimmed(r.Raw(3), 10);
                    _dPaidDate = CobolText.Trimmed(r.Raw(4), 10);
                    _dTermDays = CobolNumeric.Store(r.NumVal(5), 5, 0);
                    _dDaysTaken = CobolNumeric.Store(r.NumVal(6), 5, 0);
                    _dAmount = CobolNumeric.Store(r.NumVal(7), 13, 2);
                    _dInTerms = CobolText.Trimmed(r.Raw(8), 3);
                    _dDisputed = CobolText.Trimmed(r.Raw(9), 3);

                    // 1300-CLASSIFY
                    _tCount = (_tCount + 1) % 1_000_000_000;
                    _tDays = (_tDays + (long)_dDaysTaken) % 100_000_000_000;
                    _tValue = CobolNumeric.Store(_tValue + _dAmount, 15, 2);
                    if (_dDaysTaken <= 30) _tIn30++;
                    else if (_dDaysTaken <= 60) _tIn60++;
                    else _tOver60++;
                    if (_dInTerms != "Yes") _tLate++;
                    if (_dDisputed == "Yes") _tDisputed++;

                    // GENERATE DE-LINE: a band break presents CF-BAND with its sum, which is then zeroed
                    var broke = generated && CobolText.Fit(previous, 18) != CobolText.Fit(_dBand, 18);
                    w.Generate(detail);
                    if (broke) _sumBand = 0;
                    _sumBand = CobolNumeric.Store(_sumBand + _dAmount, 13, 2);
                    generated = true;
                    result.Add(new JsonObject
                    {
                        ["band"] = _dBand,
                        ["supplierCode"] = _dSupplier,
                        ["documentNumber"] = _dDocRef,
                        ["daysTaken"] = (int)_dDaysTaken,
                        ["amount"] = CobolNumeric.WithScale(_dAmount, 2),
                        ["inTerms"] = _dInTerms,
                        ["disputed"] = _dDisputed,
                    });
                }

                // 2000-CALCULATE-HEADLINES (percentages of the invoice count)
                if (_tCount != 0)
                {
                    _cAvg = CobolNumeric.Store((decimal)_tDays / _tCount, 7, 2, rounded: true);
                    _cPct30 = CobolNumeric.Store(_tIn30 * 100m / _tCount, 5, 2, rounded: true);
                    _cPct60 = CobolNumeric.Store(_tIn60 * 100m / _tCount, 5, 2, rounded: true);
                    _cPctOver = CobolNumeric.Store(_tOver60 * 100m / _tCount, 5, 2, rounded: true);
                    _cPctLate = CobolNumeric.Store(_tLate * 100m / _tCount, 5, 2, rounded: true);
                }

                // 0400-CLOSE-SPOOL
                w.Terminate();
                spool.Dispose();
                ctx.Result = new JsonObject
                {
                    ["lines"] = result,
                    ["averageDays"] = CobolNumeric.WithScale(_cAvg, 2),
                    ["pctWithin30"] = CobolNumeric.WithScale(_cPct30, 2),
                    ["pct31To60"] = CobolNumeric.WithScale(_cPct60, 2),
                    ["pctOver60"] = CobolNumeric.WithScale(_cPctOver, 2),
                    ["pctNotInTerms"] = CobolNumeric.WithScale(_cPctLate, 2),
                    ["invoiceCount"] = _tCount,
                    ["totalValue"] = CobolNumeric.WithScale(_tValue, 2),
                };

                // 2100-STORE-RESULT (ap.usp_PaymentPracticesData opens its own transaction)
                if (mode != "PREVIEW" && _tCount != 0)
                {
                    try
                    {
                        await sql.ExecAsync(
                            "EXEC ap.usp_PaymentPracticesData @CompanyCode = ?, @PeriodRef = ?, @AverageDays = ?, @PctWithin30 = ?, @Pct31To60 = ?, " +
                            "@PctOver60 = ?, @PctNotInTerms = ?, @InvoiceCount = ?, @TotalValue = ?, @CreatedBy = ?",
                            [SqlioParm.Text(a.Co), SqlioParm.Text(periodRef), NumEdit(_cAvg), NumEdit(_cPct30), NumEdit(_cPct60), NumEdit(_cPctOver),
                                NumEdit(_cPctLate), SqlioParm.Text(CobolEdit.Field(_tCount, "-(17)9")), NumEdit(_tValue), SqlioParm.Text(a.User)], ct);
                    }
                    catch (SqlioException ex)
                    {
                        rc = 4;
                        ctx.Display("STERP-W-2101 " + CobolText.Fit(ex.DriverText, 512));
                    }
                }
            }
        }

        // 0500-JOB-END (the JOBLOG END is refused: run id 0)
        ctx.RowsProcessed = (int)_tCount;
        ctx.Display("STERP-I-0500 APPPR counted " + CobolEdit.Field(_tCount, "Z(8)9") + " invoices for RPT-018");
        ctx.Display("STERP-I-SPOOL " + spoolName.Trim());
        return rc;
    }

    /// <summary>R-APPPR: CH-BAND, DE-LINE, CF-BAND and CF-FINAL (APPPR.cob:87-167).</summary>
    private GnuReportWriter Writer(SpoolWriter spool, ReportHeading heading)
    {
        var band = new RwControl(() => _dBand, v => _dBand = v, 18)
        {
            Heading =
            [
                RwLine.PlusN(2, () => new RwText().Lit(1, "Payment band  ").Src(16, _dBand, 18).Text),
                RwLine.Plus1(() => new RwText().Lit(3, "Supplier            ").Lit(25, "Document").Lit(51, "Received  ").Lit(63, "Paid      ")
                    .Lit(76, "Terms ").Lit(85, "Taken").Lit(97, "Amount").Lit(116, "InTerm").Lit(124, "Disputed").Text),
                RwLine.Plus1(() => ApRw.Rule),
            ],
            Footing =
            [
                RwLine.Plus1(() => new RwText().Lit(63, "Band total        ").Ed(92, _sumBand, "Z(9)9.99-").Text),
            ],
        };
        RwLine[] final =
        [
            RwLine.PlusN(2, () => new RwText().Lit(3, "PAYMENT PRACTICES AND PERFORMANCE - RPT-018").Text),
            RwLine.PlusN(2, () => new RwText().Lit(5, "Average days taken to pay an invoice").Ed(48, _cAvg, "Z(5)9.99").Text),
            RwLine.Plus1(() => new RwText().Lit(5, "Invoices paid within 30 days        %").Ed(48, _cPct30, "Z(3)9.99").Text),
            RwLine.Plus1(() => new RwText().Lit(5, "Invoices paid in 31 to 60 days      %").Ed(48, _cPct60, "Z(3)9.99").Text),
            RwLine.Plus1(() => new RwText().Lit(5, "Invoices paid in over 60 days       %").Ed(48, _cPctOver, "Z(3)9.99").Text),
            RwLine.Plus1(() => new RwText().Lit(5, "Invoices NOT paid within agreed terms %").Ed(48, _cPctLate, "Z(3)9.99").Text),
            RwLine.PlusN(2, () => new RwText().Lit(5, "Invoices counted").Ed(48, _tCount, "Z(8)9").Text),
            RwLine.Plus1(() => new RwText().Lit(5, "Of which disputed in the period").Ed(48, _tDisputed, "Z(8)9").Text),
            RwLine.Plus1(() => new RwText().Lit(5, "Total value paid").Ed(45, _tValue, "Z(12)9.99-").Text),
            RwLine.PlusN(2, () => new RwText().Lit(3, "Days taken run from the later of invoice receipt").Text),
            RwLine.Plus1(() => new RwText().Lit(3, "and goods receipt, to the date of payment.").Text),
        ];
        return ApRw.Writer(spool, heading, 60, 1, 9, 50, [band], final);
    }

    /// <summary>MOVE number TO SQLIO-NUM-EDIT (-(12)9.9(6)), moved untrimmed to the parameter (leading spaces kept).</summary>
    private static SqlioParm NumEdit(decimal v) => SqlioParm.Text(CobolEdit.Field(v, "-(12)9.9(6)"));

    private static string D8(int yyyymmdd) => ((long)Math.Abs(yyyymmdd) % 100_000_000).ToString("D8", System.Globalization.CultureInfo.InvariantCulture);

    private static string IsoDate(int yyyymmdd)
    {
        var t = D8(yyyymmdd);
        return t[..4] + "-" + t[4..6] + "-" + t[6..8];
    }
}
