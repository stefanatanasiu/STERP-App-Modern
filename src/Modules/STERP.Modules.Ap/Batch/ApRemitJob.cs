using System.Text.Json.Nodes;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ap.Batch;

/// <summary>
/// APREMIT - remittance advices (cobol/batch/ap/APREMIT.cob; FR-STERP-AP-043). One advice per supplier of
/// ap.vw_RemittanceLine for the RUN (no status check); RD R-APREMIT (CONTROLS FINAL D-SUPPLIER, PAGE LIMIT 60, HEADING 1,
/// FIRST DETAIL 9, LAST DETAIL 54, FOOTING 56) with CH-SUPP NEXT GROUP NEXT PAGE; RUN required (RC 12 after registration);
/// MODE ALL/REISSUE give identical output; JOBLOG END re-initialised (SME-009).
/// </summary>
internal sealed class ApRemitJob
{
    public static JobDefinition Definition => new()
    {
        JobCode = "APREMIT",
        Slice = "STERP-AP",
        SubmitOperationId = "ap_submitApremitRun",
        ParameterOrder = ["CO", "RUN", "MODE", "USER"],
        SelfRegistration = pre =>
        {
            var a = ApBatchArgs.From(pre.Args);
            return ApJobSubmission.JoblogStart("APREMIT", a.Co, a.User, pre.Args.Text);
        },
        EndValues = ApJobSubmission.NeverEnded,
        Execute = ctx => new ApRemitJob().RunAsync(ctx),
    };

    // WS-DET
    private string _dSupplier = "";
    private string _dSuppName = "";
    private string _dPayRef = "";
    private string _dPayMethod = "";
    private string _dDocType = "";
    private string _dDocRef = "";
    private string _dOurRef = "";
    private string _dDocDate = "";
    private string _dCurrency = "";
    private decimal _dGross, _dDiscount, _dSetoff, _dPaid;

    // WS-SUPPLIERS, WS-TOT
    private long _suppliers;
    private decimal _tPaid;
    private long _tDocs;

    // SUM counters of CF-SUPP
    private readonly decimal[] _sumSupp = new decimal[4];

    public async Task<int> RunAsync(JobRunContext ctx)
    {
        var sql = ctx.Get<ISqlRunner>();
        var ct = ctx.Cancellation;
        var rc = 0;

        // 0100-READ-ARGS
        var a = ApBatchArgs.From(ctx.Args);
        var runRef = CobolText.Trimmed(a.Run, 30);
        var mode = a.Mode.Length > 0 ? a.Mode : "ALL";
        var today = CobolDate.ToYyyymmdd(DateTime.Now);
        if (runRef.Length == 0)
        {
            rc = 12;
            ctx.Display("STERP-E-0101 APREMIT needs RUN=");
        }
        ctx.Display("STERP-I-0100 APREMIT remittances for " + runRef.Trim());

        SpoolWriter? spool = null;
        var spoolName = "";
        var result = new JsonArray();
        if (rc < 8)
        {
            // 0300-OPEN-SPOOL / 0350-FILL-HEADINGS
            var heading = new ReportHeading
            {
                CompanyCode = a.Co,
                ReportId = "APREMIT",
                Title = "Remittance advice",
                User = a.User,
                StatusText = mode,
                RunAt = DateTime.Now,
                AsAtText = "Printed " + ApBatchArgs.Ddmmyyyy(today),
                Subtitle = CobolText.Trimmed("Payment run " + runRef.Trim(), 60),
            };
            try
            {
                spool = ctx.OpenSpool("APREMIT", a.Co);
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

                // 1000-PRODUCE-REPORT / 1100-OPEN-ALLOCATIONS
                IReadOnlyList<SqlioRow>? rows = null;
                try
                {
                    rows = await sql.FetchAllAsync(
                        "SELECT SupplierCode, SupplierName, InstructionRef, PaymentMethod, DocumentType, SupplierDocRef, DocumentNumber, " +
                        "ISNULL(CONVERT(varchar(10),DocumentDate,103),''), CurrencyCode, CONVERT(varchar(30), GrossGBP), " +
                        "CONVERT(varchar(30), DiscountGBP), CONVERT(varchar(30), SetOffGBP), CONVERT(varchar(30), PaidGBP) " +
                        "FROM ap.vw_RemittanceLine WHERE CompanyCode = ? AND ProposalRef = ? ORDER BY SupplierCode, DocumentNumber",
                        [SqlioParm.Text(a.Co), SqlioParm.Text(runRef)], Enumerable.Repeat(100, 24).ToArray(), ct);
                }
                catch (SqlioException ex)
                {
                    ctx.Display("STERP-E-1101 " + CobolText.Fit(ex.DriverText, 512));
                    rc = 12;
                }

                var text = () => new RwText().Src(3, _dDocType, 12).Src(17, _dDocRef, 20).Src(39, _dOurRef, 16).Src(57, _dDocDate, 10)
                    .Src(69, _dCurrency, 3).Ed(73, _dGross, "Z(8)9.99-").Ed(89, _dDiscount, "Z(6)9.99-").Ed(103, _dSetoff, "Z(6)9.99-")
                    .Ed(117, _dPaid, "Z(8)9.99-").Text;
                var detail = RwLine.Plus1(text);
                // CH-SUPP NEXT GROUP NEXT PAGE (reportio.c set_next_info / report_line next_page): the supplier's first DE-LINE
                // starts with a page footing and page heading. After the first GENERATE's heading the line counter is 14 and the
                // runtime pads 46 lines to PAGE LIMIT, prints a heading that does not count the page and leaves FIRST DETAIL
                // pending (the next GENERATE prints a heading at once): LINE PLUS 42 reaches LAST DETAIL + 1 and throws the page
                // through the same overflow test. Later suppliers eject unconditionally and clear FIRST DETAIL, as an absolute LINE
                // before FIRST DETAIL does.
                var firstAfterHeading = RwLine.PlusN(42, text);
                var afterHeading = new RwLine(1, false, text);
                var code = "";
                var generated = false;
                foreach (var r in rows ?? [])
                {
                    // 1200-FETCH-LINE
                    var previous = _dSupplier;
                    _dSupplier = CobolText.Trimmed(r.Raw(0), 20);
                    _dSuppName = CobolText.Trimmed(r.Raw(1), 40);
                    _dPayRef = CobolText.Trimmed(r.Raw(2), 30);
                    _dPayMethod = CobolText.Trimmed(r.Raw(3), 10);
                    _dDocType = CobolText.Trimmed(r.Raw(4), 12);
                    _dDocRef = CobolText.Trimmed(r.Raw(5), 30);
                    _dOurRef = CobolText.Trimmed(r.Raw(6), 30);
                    _dDocDate = CobolText.Trimmed(r.Raw(7), 10);
                    _dCurrency = CobolText.Trimmed(r.Raw(8), 3);
                    _dGross = CobolNumeric.Store(r.NumVal(9), 13, 2);
                    _dDiscount = CobolNumeric.Store(r.NumVal(10), 13, 2);
                    _dSetoff = CobolNumeric.Store(r.NumVal(11), 13, 2);
                    _dPaid = CobolNumeric.Store(r.NumVal(12), 13, 2);
                    // a credit note reduces the remittance (never true: the view yields CREDIT, FR-STERP-AP-043)
                    if (_dDocType == "CREDITNOTE") _dPaid = CobolNumeric.Store(0 - _dPaid, 13, 2);

                    if (CobolText.Fit(_dSupplier, 40) != CobolText.Fit(code, 40))
                    {
                        _suppliers = (_suppliers + 1) % 10_000_000;
                        code = _dSupplier;
                    }
                    _tDocs = (_tDocs + 1) % 10_000_000;
                    _tPaid = CobolNumeric.Store(_tPaid + _dPaid, 15, 2);

                    // GENERATE DE-LINE
                    var newGroup = !generated || CobolText.Fit(previous, 20) != CobolText.Fit(_dSupplier, 20);
                    w.Generate(!generated ? firstAfterHeading : newGroup ? afterHeading : detail);
                    if (newGroup) Array.Clear(_sumSupp);
                    _sumSupp[0] = CobolNumeric.Store(_sumSupp[0] + _dGross, 13, 2);
                    _sumSupp[1] = CobolNumeric.Store(_sumSupp[1] + _dDiscount, 13, 2);
                    _sumSupp[2] = CobolNumeric.Store(_sumSupp[2] + _dSetoff, 13, 2);
                    _sumSupp[3] = CobolNumeric.Store(_sumSupp[3] + _dPaid, 13, 2);
                    generated = true;
                    result.Add(new JsonObject
                    {
                        ["supplierCode"] = _dSupplier,
                        ["instructionRef"] = _dPayRef,
                        ["documentType"] = _dDocType,
                        ["supplierDocRef"] = _dDocRef,
                        ["documentNumber"] = _dOurRef,
                        ["paidGbp"] = CobolNumeric.WithScale(_dPaid, 2),
                    });
                }

                // 0400-CLOSE-SPOOL
                w.Terminate();
                spool.Dispose();
                ctx.Result = new JsonObject
                {
                    ["lines"] = result,
                    ["advices"] = _suppliers,
                    ["documents"] = _tDocs,
                    ["totalPaid"] = CobolNumeric.WithScale(_tPaid, 2),
                };
            }
        }

        // 0500-JOB-END (the JOBLOG END is refused: run id 0)
        ctx.RowsProcessed = (int)_tDocs;
        ctx.Display("STERP-I-0500 APREMIT printed " + CobolEdit.Field(_suppliers, "Z(8)9") + " advices to " + spoolName.Trim());
        ctx.Display("STERP-I-SPOOL " + spoolName.Trim());
        return rc;
    }

    /// <summary>R-APREMIT: CH-SUPP (NEXT GROUP NEXT PAGE), DE-LINE, CF-SUPP and CF-FINAL (APREMIT.cob:74-145).</summary>
    private GnuReportWriter Writer(SpoolWriter spool, ReportHeading heading)
    {
        var supplier = new RwControl(() => _dSupplier, v => _dSupplier = v, 20)
        {
            Heading =
            [
                RwLine.PlusN(2, () => new RwText().Lit(1, "REMITTANCE ADVICE   ").Src(24, _dSupplier, 20).Src(46, _dSuppName, 40).Text),
                RwLine.Plus1(() => new RwText().Lit(1, "Our payment ref   ").Src(24, _dPayRef, 30).Lit(60, "Method  ").Src(69, _dPayMethod, 10).Text),
                RwLine.PlusN(2, () => new RwText().Lit(3, "Document    ").Lit(17, "Your reference      ").Lit(39, "Our reference   ")
                    .Lit(57, "Doc date  ").Lit(69, "Ccy").Lit(78, "Gross").Lit(95, "Discount").Lit(110, "Set-off").Lit(124, "Paid    ").Text),
                RwLine.Plus1(() => ApRw.Rule),
            ],
            Footing =
            [
                RwLine.Plus1(() => ApRw.Rule),
                RwLine.Plus1(() => new RwText().Lit(39, "Total paid to this supplier").Ed(73, _sumSupp[0], "Z(8)9.99-")
                    .Ed(89, _sumSupp[1], "Z(6)9.99-").Ed(103, _sumSupp[2], "Z(6)9.99-").Ed(117, _sumSupp[3], "Z(8)9.99-").Text),
                RwLine.PlusN(2, () => new RwText().Lit(3, "Settlement discount has been deducted as agreed.").Text),
                RwLine.Plus1(() => new RwText().Lit(3, "Queries to the purchase ledger, quoting our ref.").Text),
            ],
        };
        RwLine[] final =
        [
            RwLine.PlusN(2, () => new RwText().Lit(3, "Advices printed          ").Ed(30, _suppliers, "Z(6)9").Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Documents remitted       ").Ed(30, _tDocs, "Z(6)9").Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Total remitted           ").Ed(30, _tPaid, "Z(12)9.99-").Text),
        ];
        return ApRw.Writer(spool, heading, 60, 1, 9, 54, [supplier], final);
    }
}
