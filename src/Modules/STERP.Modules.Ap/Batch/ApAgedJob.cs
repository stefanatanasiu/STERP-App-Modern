using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ap.Batch;

/// <summary>
/// APAGED - aged creditors and cash requirement (cobol/batch/ap/APAGED.cob; FR-STERP-AP-040). Read-only report over
/// ap.vw_OpenItemAge: RD R-APAGED (CONTROLS FINAL D-SUPPLIER, PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 54,
/// FOOTING 56) on the GnuCOBOL Report Writer runtime; RC 0 or 12; JOBLOG END re-initialised (SME-009).
/// </summary>
internal sealed class ApAgedJob
{
    public static JobDefinition Definition => new()
    {
        JobCode = "APAGED",
        Slice = "STERP-AP",
        SubmitOperationId = "ap_submitApagedRun",
        ParameterOrder = ["CO", "ASAT", "SUPP", "MODE", "USER"],
        SelfRegistration = pre =>
        {
            var a = ApBatchArgs.From(pre.Args);
            return ApJobSubmission.JoblogStart("APAGED", a.Co, a.User, pre.Args.Text);
        },
        EndValues = ApJobSubmission.NeverEnded,
        Execute = ctx => new ApAgedJob().RunAsync(ctx),
    };

    // WS-DET
    private string _dSupplier = "";
    private string _dSuppName = "";
    private string _dDocRef = "";
    private string _dDocDate = "";
    private string _dDueDate = "";
    private string _dState = "";
    private decimal _dCurrent, _dB30, _dB60, _dB90, _dBOver, _dBalance;

    // WS-TOT
    private decimal _tCash7, _tCash14, _tCash30, _tBlocked;
    private long _tItems;

    // SUM counters of CF-SUPP and CF-FINAL
    private readonly decimal[] _sumSupp = new decimal[5];
    private readonly decimal[] _sumFinal = new decimal[5];

    public async Task<int> RunAsync(JobRunContext ctx)
    {
        var sql = ctx.Get<ISqlRunner>();
        var ct = ctx.Cancellation;
        var rc = 0;

        // 0100-READ-ARGS
        var a = ApBatchArgs.From(ctx.Args);
        var asAt = string.IsNullOrWhiteSpace(a.AsAtText) ? 0 : (int)CobolNumeric.Store(CobolNumeric.NumVal(a.AsAtText), 8, 0, signed: false);
        var supp = ctx.Args.Get("SUPP") is { } sv ? CobolText.Trimmed(CobolText.Upper(sv), 20) : "";
        var mode = a.Mode.Length > 0 ? a.Mode : "DUE";
        if (asAt == 0) asAt = CobolDate.ToYyyymmdd(DateTime.Now);
        var asAtInt = CobolDate.IntegerOfDate(asAt);
        ctx.Display("STERP-I-0100 APAGED aged creditors for " + a.Co.Trim());

        // 0300-OPEN-SPOOL / 0350-FILL-HEADINGS
        SpoolWriter? spool = null;
        var spoolName = "";
        var heading = new ReportHeading
        {
            CompanyCode = a.Co,
            ReportId = "APAGED",
            Title = "Aged creditors and cash requirement",
            User = a.User,
            RunAt = DateTime.Now,
            AsAtText = "As at " + ApBatchArgs.Ddmmyyyy(asAt),
            Subtitle = mode == "INVOICE" ? "Aged by document date" : "Aged by due date",
        };
        if (supp.Length > 0) heading.Subtitle = CobolText.Trimmed(heading.Subtitle.Trim() + " - supplier " + supp.Trim(), 60);
        try
        {
            spool = ctx.OpenSpool("APAGED", a.Co);
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

        var result = new JsonArray();
        if (rc < 8 && spool is not null)
        {
            await ApBatchArgs.FillCompanyAsync(sql, heading, a.Co, ct);
            var w = Writer(spool, heading);
            w.Initiate();

            // 1000-PRODUCE-REPORT / 1100-OPEN-ITEMS
            IReadOnlyList<SqlioRow>? rows = null;
            try
            {
                rows = await sql.FetchAllAsync(
                    "SELECT SupplierCode, SupplierName, DocumentNumber, ISNULL(CONVERT(varchar(10),DocumentDate,103),''), " +
                    "ISNULL(CONVERT(varchar(10),DueDate,103),''), CONVERT(varchar(30), ResidualAmountGBP), BlockState, " +
                    "CONVERT(varchar(10), DueDate, 112), CONVERT(varchar(10), DocumentDate, 112) FROM ap.vw_OpenItemAge " +
                    "WHERE CompanyCode = ? AND ResidualAmountGBP <> 0 AND DocumentDate <= ? AND (? = '' OR SupplierCode = ?) " +
                    "ORDER BY SupplierCode, DocumentNumber",
                    [SqlioParm.Text(a.Co), SqlioParm.Text(IsoDate(asAt)), SqlioParm.Text(supp), SqlioParm.Text(supp)],
                    Enumerable.Repeat(100, 24).ToArray(), ct);
            }
            catch (SqlioException ex)
            {
                ctx.Display("STERP-E-1101 " + ex.DriverText);
                rc = 12;
            }

            var detail = RwLine.Plus1(() => new RwText().Src(3, _dDocRef, 22).Src(25, _dDocDate, 10).Src(37, _dDueDate, 10)
                .Src(49, _dState, 10).Ed(60, _dCurrent, "Z(8)9.99-").Ed(74, _dB30, "Z(8)9.99-").Ed(88, _dB60, "Z(8)9.99-")
                .Ed(102, _dB90, "Z(8)9.99-").Ed(116, _dBOver, "Z(8)9.99-").Text);
            var generated = false;
            foreach (var r in rows ?? [])
            {
                // 1200-FETCH-ITEM
                var previous = _dSupplier;
                _dSupplier = CobolText.Trimmed(r.Raw(0), 20);
                _dSuppName = CobolText.Trimmed(r.Raw(1), 34);
                _dDocRef = CobolText.Trimmed(r.Raw(2), 22);
                _dDocDate = CobolText.Trimmed(r.Raw(3), 10);
                _dDueDate = CobolText.Trimmed(r.Raw(4), 10);
                _dBalance = CobolNumeric.Store(r.NumVal(5), 13, 2);
                _dState = CobolText.Trimmed(r.Raw(6), 10);
                var basis = (int)CobolNumeric.Store(r.NumVal(mode == "INVOICE" ? 8 : 7), 8, 0, signed: false);
                var dueInt = CobolDate.IntegerOfDate(basis);

                // 1300-AGE-ITEM: positive age means overdue
                _dCurrent = _dB30 = _dB60 = _dB90 = _dBOver = 0;
                var age = asAtInt - dueInt;
                if (age <= 0) _dCurrent = _dBalance;
                else if (age <= 30) _dB30 = _dBalance;
                else if (age <= 60) _dB60 = _dBalance;
                else if (age <= 90) _dB90 = _dBalance;
                else _dBOver = _dBalance;

                // 1400-CASH-REQUIREMENT: blocked and disputed items are not a cash commitment
                if (_dState is "BLOCKED" or "DISPUTED")
                {
                    _tBlocked = CobolNumeric.Store(_tBlocked + _dBalance, 15, 2);
                }
                else if (_dBalance > 0)
                {
                    var ahead = dueInt - asAtInt;
                    if (ahead <= 7) _tCash7 = CobolNumeric.Store(_tCash7 + _dBalance, 15, 2);
                    if (ahead <= 14) _tCash14 = CobolNumeric.Store(_tCash14 + _dBalance, 15, 2);
                    if (ahead <= 30) _tCash30 = CobolNumeric.Store(_tCash30 + _dBalance, 15, 2);
                }
                _tItems = (_tItems + 1) % 10_000_000;

                // GENERATE DE-LINE: a supplier break presents CF-SUPP with its sums, which are then zeroed
                var broke = generated && CobolText.Fit(previous, 20) != CobolText.Fit(_dSupplier, 20);
                w.Generate(detail);
                if (broke) Array.Clear(_sumSupp);
                Sum([_dCurrent, _dB30, _dB60, _dB90, _dBOver]);
                generated = true;
                result.Add(new JsonObject
                {
                    ["supplierCode"] = _dSupplier,
                    ["supplierName"] = _dSuppName,
                    ["documentNumber"] = _dDocRef,
                    ["documentDate"] = _dDocDate,
                    ["dueDate"] = _dDueDate,
                    ["state"] = _dState,
                    ["current"] = CobolNumeric.WithScale(_dCurrent, 2),
                    ["days1To30"] = CobolNumeric.WithScale(_dB30, 2),
                    ["days31To60"] = CobolNumeric.WithScale(_dB60, 2),
                    ["days61To90"] = CobolNumeric.WithScale(_dB90, 2),
                    ["over90"] = CobolNumeric.WithScale(_dBOver, 2),
                });
            }

            // 0400-CLOSE-SPOOL
            w.Terminate();
            spool.Dispose();
            ctx.Result = new JsonObject
            {
                ["items"] = result,
                ["cashWithin7Days"] = CobolNumeric.WithScale(_tCash7, 2),
                ["cashWithin14Days"] = CobolNumeric.WithScale(_tCash14, 2),
                ["cashWithin30Days"] = CobolNumeric.WithScale(_tCash30, 2),
                ["blockedOrDisputed"] = CobolNumeric.WithScale(_tBlocked, 2),
                ["itemCount"] = _tItems,
            };
        }

        // 0500-JOB-END (the JOBLOG END is refused: run id 0)
        ctx.RowsProcessed = (int)_tItems;
        ctx.Display("STERP-I-0500 APAGED aged " + CobolEdit.Field(_tItems, "Z(8)9") + " items to " + spoolName.Trim());
        ctx.Display("STERP-I-SPOOL " + spoolName.Trim());
        return rc;
    }

    /// <summary>R-APAGED: CH-SUPP, DE-LINE, CF-SUPP and CF-FINAL (APAGED.cob:82-157).</summary>
    private GnuReportWriter Writer(SpoolWriter spool, ReportHeading heading)
    {
        var supplier = new RwControl(() => _dSupplier, v => _dSupplier = v, 20)
        {
            Heading =
            [
                RwLine.PlusN(2, () => new RwText().Lit(1, "Supplier ").Src(11, _dSupplier, 20).Src(33, _dSuppName, 34).Text),
                RwLine.Plus1(() => new RwText().Lit(3, "Document            ").Lit(25, "Doc date  ").Lit(37, "Due date  ").Lit(49, "State  ")
                    .Lit(60, "Current").Lit(74, "1-30  ").Lit(88, "31-60 ").Lit(102, "61-90 ").Lit(116, "Over 90").Text),
                RwLine.Plus1(() => ApRw.Rule),
            ],
            Footing =
            [
                RwLine.Plus1(() => Totals(new RwText().Lit(37, "Supplier total      "), _sumSupp).Text),
            ],
        };
        RwLine[] final =
        [
            RwLine.PlusN(2, () => Totals(new RwText().Lit(37, "Creditors total     "), _sumFinal).Text),
            RwLine.PlusN(2, () => new RwText().Lit(3, "CASH REQUIREMENT (unblocked items)").Text),
            RwLine.Plus1(() => new RwText().Lit(5, "Due within 7 days     ").Ed(30, _tCash7, "Z(12)9.99-").Text),
            RwLine.Plus1(() => new RwText().Lit(5, "Due within 14 days    ").Ed(30, _tCash14, "Z(12)9.99-").Text),
            RwLine.Plus1(() => new RwText().Lit(5, "Due within 30 days    ").Ed(30, _tCash30, "Z(12)9.99-").Text),
            RwLine.Plus1(() => new RwText().Lit(5, "Blocked or disputed   ").Ed(30, _tBlocked, "Z(12)9.99-").Text),
            RwLine.Plus1(() => new RwText().Lit(5, "Open items aged       ").Ed(30, _tItems, "Z(6)9").Text),
            RwLine.PlusN(2, () => new RwText().Lit(3, "Blocked and disputed items are not a cash").Text),
            RwLine.Plus1(() => new RwText().Lit(3, "commitment until they are released.").Text),
        ];
        return ApRw.Writer(spool, heading, 60, 1, 9, 54, [supplier], final);
    }

    private static RwText Totals(RwText line, decimal[] s) =>
        line.Ed(60, s[0], "Z(8)9.99-").Ed(74, s[1], "Z(8)9.99-").Ed(88, s[2], "Z(8)9.99-").Ed(102, s[3], "Z(8)9.99-").Ed(116, s[4], "Z(8)9.99-");

    /// <summary>sum_all_detail: every SUM counter of CF-SUPP and CF-FINAL adds its source item after the control breaks.</summary>
    private void Sum(decimal[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            _sumSupp[i] = CobolNumeric.Store(_sumSupp[i] + values[i], 13, 2);
            _sumFinal[i] = CobolNumeric.Store(_sumFinal[i] + values[i], 13, 2);
        }
    }

    private static string IsoDate(int yyyymmdd)
    {
        var t = ((long)Math.Abs(yyyymmdd) % 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
        return t[..4] + "-" + t[4..6] + "-" + t[6..8];
    }
}
