using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Modules.Ap.Engine;
using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Reporting;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ap.Batch;

/// <summary>
/// APBACS - Bacs Standard 18 direct credit file and control listing for a payment run (cobol/batch/ap/APBACS.cob;
/// FR-STERP-AP-041, -042; spec 4.9, 8.6). Approval through APPAYAPR VERIFY (original cut, original first cut of a released
/// run, or reproduction with RC 4); classification CHAPS / REJECT / FPS / BACS with the modulus check whose weighted sum is
/// discarded (BR-APBACS-023, reproduced); the file written byte by byte with CR LF under run\bacs (never transmitted); in LIVE
/// mode an original cut with Bacs items calls APPAYRUN SUBMIT. JOBLOG END ends its own row (FAILED / COMPLETE / WARNING, rows =
/// Bacs items, log = the Bacs file). Bank details are written to the file and listing only.
/// </summary>
internal static class ApBacsJob
{
    private const decimal ChapsLimit = 250000m;

    public static JobDefinition Definition => new()
    {
        JobCode = "APBACS",
        Slice = "STERP-AP",
        SubmitOperationId = "ap_submitApbacsRun",
        ParameterOrder = ["CO", "RUN", "MODE", "USER"],
        SelfRegistration = pre =>
        {
            var a = ApBatchArgs.From(pre.Args);
            return ApJobSubmission.JoblogStart("APBACS", a.Co, a.User, pre.Args.Text);
        },
        EndValues = (ctx, exit) => new JobEnd(EndStatus(exit), exit, ctx.RowsProcessed ?? 0, null, ctx.SpoolFile),
        Execute = RunAsync,
    };

    /// <summary>0500-JOB-END: RC &gt; 4 FAILED, 0 COMPLETE (core.JobRun 'OK'), otherwise WARNING.</summary>
    private static string EndStatus(int rc) => rc > 4 ? "FAILED" : rc == 0 ? "OK" : "WARNING";

    private sealed class Run
    {
        public int Rc;
        public string Co = "";
        public string RunRef = "";
        public string User = "";
        public string Mode = "";
        public int Today;
        public string PropStatus = "";
        public int PayDate;
        public int ProcDate;
        public long OrigSort;
        public long OrigAccount;
        public string OrigName = "";
        public string Sun = "";
        public long PropId;
        public bool Reproduction;
        public string AprReason = "";
        public string MsgText = "";
        public string Auth1 = "";
        public string Auth2 = "";
        public long ReadyCount;
        public long CutCount;
        public string ContentHash = "";
        public string StoredHash = "";
        public string BacsName = "";
        public string SpoolName = "";
        public string SpoolFileName = "";
        public long Written;
    }

    public static async Task<int> RunAsync(JobRunContext ctx)
    {
        var sql = ctx.Get<ISqlRunner>();
        var ct = ctx.Cancellation;
        var svc = new ApSvc(ctx.Services);
        var r = new Run();

        // 0100-READ-ARGS
        var a = ApBatchArgs.From(ctx.Args);
        r.Co = a.Co;
        r.User = a.User;
        r.Mode = a.Mode.Length > 0 ? a.Mode : "LIVE";
        r.RunRef = CobolText.Trimmed(a.Run, 30);
        r.Today = CobolDate.ToYyyymmdd(DateTime.Now);
        if (r.RunRef.Length == 0)
        {
            r.Rc = 12;
            ctx.Display("STERP-E-0101 APBACS needs RUN=<payment run>");
        }
        ctx.Display("STERP-I-0100 APBACS Bacs Standard 18 for " + r.RunRef.Trim());

        // 0200-JOB-START is the program's self-registration (JOBLOG START) made by the job service.

        // 0250-CHECK-APPROVAL
        if (r.Rc < 8) await CheckApprovalAsync(ctx, sql, svc, r, ct);

        BacsListing? listing = null;
        if (r.Rc < 8) listing = await OpenOutputAsync(ctx, sql, r, ct);
        if (r.Rc < 8 && listing is not null)
        {
            await listing.ProduceAsync(ctx, sql, r, ct);
            // 0400-CLOSE-OUTPUT
            listing.Close();
            if (listing.Totals.BacsCount > 0 && r.Rc < 8) await StampRunAsync(ctx, sql, svc, r, listing.Totals, ct);
            ctx.Result = listing.Result();
        }
        else
        {
            listing?.Close();
        }

        // 0500-JOB-END
        r.Written = listing?.Totals.BacsCount ?? 0;
        ctx.RowsProcessed = (int)r.Written;
        if (r.SpoolName.Length > 0) ctx.SpoolFile = r.SpoolName;
        // JOBLOG END: JL-LOG-FILE is BL-LOG-FILE (the Bacs file, spaces before it exists -> NULL; "" keeps NULL, platform.md 3)
        ctx.EndOverride = new JobEnd(EndStatus(r.Rc), r.Rc, (int)r.Written, r.BacsName.Length > 0 ? r.BacsName : "",
            r.SpoolName.Length > 0 ? r.SpoolName : null);
        ctx.Display("STERP-I-0500 APBACS wrote " + CobolEdit.Field(listing?.Totals.BacsCount ?? 0, "Z(8)9") + " Bacs items to " + r.BacsName.Trim());
        ctx.Display("STERP-I-SPOOL " + r.SpoolName.Trim());
        return r.Rc;
    }

    // 0250-CHECK-APPROVAL
    private static async Task CheckApprovalAsync(JobRunContext ctx, ISqlRunner sql, ApSvc svc, Run r, CancellationToken ct)
    {
        await ReadRunHeaderAsync(ctx, sql, svc, r, ct);
        if (r.Rc >= 8) return;
        var ca = BatchHeader(r, "APPAYAPR", "VERIFY");
        var pa = new PaymentApprovalArea { ProposalRef = r.RunRef };
        await new ApPayApr(sql, svc).RunAsync(ca, pa, ct);
        r.ContentHash = pa.ContentHash;
        r.StoredHash = pa.StoredHash;
        if (ca.ReturnCode == 0)
        {
            return;
        }
        r.AprReason = CobolText.Trimmed(ca.ReasonCode, 8);
        r.MsgText = CobolText.Trimmed(ca.Message, 120);
        await AlreadyReleasedAsync(ctx, sql, r, ct);
    }

    // 0255-ALREADY-RELEASED
    private static async Task AlreadyReleasedAsync(JobRunContext ctx, ISqlRunner sql, Run r, CancellationToken ct)
    {
        if (r.PropStatus is "RELEASED" or "PAID" or "COMPLETE")
        {
            await CountInstructionsAsync(ctx, sql, r, ct);
            if (r.Rc >= 8) return;
            if (r.AprReason == "APA0020" && r.ReadyCount > 0 && r.CutCount == 0 && r.Auth1.Length > 0 && r.Auth2.Length > 0
                && !CobolText.IsSpaces(r.ContentHash) && CobolText.Fit(r.ContentHash, 64) == CobolText.Fit(r.StoredHash, 64))
            {
                r.Reproduction = false;
                ctx.Display("STERP-I-0253 run " + r.RunRef.Trim() + " is released and has never been cut");
                return;
            }
            r.Reproduction = true;
            r.Rc = 4;
            ctx.Display("STERP-W-0252 run " + r.RunRef.Trim() + " is " + r.PropStatus.Trim() + " - the file is a reproduction");
            return;
        }
        r.Rc = 8;
        ctx.Display("STERP-E-0251 approval not valid - " + r.MsgText.Trim());
    }

    // 0256-COUNT-INSTRUCTIONS
    private static async Task CountInstructionsAsync(JobRunContext ctx, ISqlRunner sql, Run r, CancellationToken ct)
    {
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT ISNULL(p.Approver1,''), ISNULL(p.Approver2,''), CAST(ISNULL(SUM(CASE WHEN i.Status = 'APPROVED' " +
                "AND i.BacsFileRef IS NULL THEN 1 ELSE 0 END),0) AS varchar(12)), CAST(ISNULL(SUM(CASE WHEN i.Status <> 'CANCELLED'" +
                " AND (i.BacsFileRef IS NOT NULL OR i.Status <> 'APPROVED') THEN 1 ELSE 0 END),0) AS varchar(12)) " +
                "FROM ap.PaymentProposal p LEFT JOIN ap.PaymentInstruction i ON i.ProposalId = p.ProposalId WHERE p.CompanyCode = ? AND " +
                "p.ProposalRef = ? GROUP BY p.Approver1, p.Approver2",
                [SqlioParm.Text(r.Co), SqlioParm.Text(r.RunRef)], Widths, ct);
        }
        catch (SqlioException ex)
        {
            r.Rc = 12;
            ctx.Display("STERP-E-0256 " + CobolText.Fit(ex.DriverText, 512));
            return;
        }
        if (!row.Found) return;
        r.Auth1 = CobolText.Trimmed(row.Raw(0), 20);
        r.Auth2 = CobolText.Trimmed(row.Raw(1), 20);
        r.ReadyCount = (long)CobolNumeric.Store(row.NumVal(2), 7, 0);
        r.CutCount = (long)CobolNumeric.Store(row.NumVal(3), 7, 0);
    }

    // 0260-READ-RUN-HEADER
    private static async Task ReadRunHeaderAsync(JobRunContext ctx, ISqlRunner sql, ApSvc svc, Run r, CancellationToken ct)
    {
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT ISNULL(r.Status,''), ISNULL(CONVERT(varchar(10),r.PayDate,23),''), ISNULL(b.SortCode,''), ISNULL(b.AccountNumber,''), " +
                "ISNULL(b.Name,''), ISNULL(b.BacsServiceUserNumber,''), CAST(r.ProposalId AS varchar(12)) FROM ap.PaymentProposal r LEFT JOIN " +
                "fin.BankAccount b ON b.BankAccountCode = r.BankAccountCode AND b.CompanyCode = r.CompanyCode WHERE r.CompanyCode = ? AND r.ProposalRef = ?",
                [SqlioParm.Text(r.Co), SqlioParm.Text(r.RunRef)], Widths, ct);
        }
        catch (SqlioException ex)
        {
            r.Rc = 12;
            ctx.Display("STERP-E-0262 " + CobolText.Fit(ex.DriverText, 512));
            return;
        }
        if (!row.Found)
        {
            r.Rc = 8;
            ctx.Display("STERP-E-0261 payment run not found");
            return;
        }
        r.PropStatus = CobolText.Trimmed(row.Raw(0), 14);
        var dc = CobolText.Fit(row.Raw(1), 10);
        if (!CobolText.IsSpaces(dc))
        {
            r.PayDate = Digits(dc[..4]) * 10000 + Digits(dc.Substring(5, 2)) * 100 + Digits(dc.Substring(8, 2));
        }
        else
        {
            r.Rc = 8;
            ctx.Display("STERP-E-0263 Bacs value date is missing");
            return;
        }
        var ca = BatchHeader(r, "APBACS", "");
        var s = ApSvcArea.For(ca, "ADDBUS");
        s.Date1 = r.PayDate;
        s.Int1 = -1;
        s.Text1 = "BACS";
        await svc.CallAsync(s, ct);
        if (s.Rc > 4 || s.DateOut == 0)
        {
            r.Rc = 8;
            ctx.Display("STERP-E-0264 No Bacs processing date");
            return;
        }
        r.ProcDate = s.DateOut;
        r.OrigSort = (long)CobolNumeric.Store(row.NumVal(2), 6, 0, signed: false);
        r.OrigAccount = (long)CobolNumeric.Store(row.NumVal(3), 8, 0, signed: false);
        r.OrigName = CobolText.Fit(row.Raw(4), 18);
        r.Sun = CobolText.Fit(row.Raw(5), 6);
        r.PropId = (long)CobolNumeric.Store(row.NumVal(6), 9, 0);
        if (CobolText.IsSpaces(r.OrigName)) r.OrigName = CobolText.Fit("STERP LIMITED", 18);
        if (CobolText.IsSpaces(r.Sun)) r.Sun = "931001";
    }

    // 0300-OPEN-OUTPUT
    private static async Task<BacsListing?> OpenOutputAsync(JobRunContext ctx, ISqlRunner sql, Run r, CancellationToken ct)
    {
        var built = ctx.Paths.Build("BACS", "APBACS", r.Co, "txt", null, true, out var bacsPath);
        if (built.ReturnCode > 4)
        {
            r.Rc = 12;
            ctx.Display("STERP-E-0301 cannot build the Bacs path");
            return null;
        }
        r.BacsName = bacsPath;

        // 0350-FILL-HEADINGS / 0360-READ-COMPANY
        var heading = new ReportHeading
        {
            CompanyCode = r.Co,
            ReportId = "APBACS",
            Title = "Bacs Standard 18 payment file",
            User = r.User,
            StatusText = r.Mode,
            RunAt = DateTime.Now,
            AsAtText = "Processing " + ApBatchArgs.Ddmmyyyy(r.ProcDate),
            Subtitle = CobolText.Trimmed("Payment run " + r.RunRef.Trim(), 60),
        };
        SpoolWriter spool;
        try
        {
            spool = ctx.OpenSpool("APBACS", r.Co);
        }
        catch (Exception ex) when (ex is LegacyOutcomeException or IOException or UnauthorizedAccessException)
        {
            r.Rc = 12;
            ctx.Display("STERP-E-0302 cannot open " + CobolText.Fit(r.SpoolName, 200));
            return null;
        }
        r.SpoolName = spool.Path;
        r.SpoolFileName = Path.GetFileName(spool.Path);
        await ApBatchArgs.FillCompanyAsync(sql, heading, r.Co, ct);
        var listing = new BacsListing(spool, heading);
        try
        {
            listing.OpenBacs(bacsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            r.Rc = 12;
            ctx.Display("STERP-E-0303 cannot open " + CobolText.Fit(r.BacsName, 200));
            return listing;
        }
        listing.WriteVolumeHeader(r);
        return listing;
    }

    // 0450-STAMP-RUN
    private static async Task StampRunAsync(JobRunContext ctx, ISqlRunner sql, ApSvc svc, Run r, BacsTotals t, CancellationToken ct)
    {
        if (r.Mode == "PREVIEW") return;
        if (r.Reproduction)
        {
            ctx.Display("STERP-I-0452 reproduction - run " + r.RunRef.Trim() + " keeps its status");
            return;
        }
        var ca = BatchHeader(r, "APPAYRUN", "SUBMIT");
        var pr = new PaymentRunArea
        {
            ProposalRef = r.RunRef,
            ProposalId = (int)r.PropId,
            Method = "BACS",
            BacsFileRef = CobolText.Trimmed(r.SpoolFileName, 60),
            CreatedCount = (int)CobolNumeric.Store(t.BacsCount, 6, 0, signed: false),
            Status = "SUBMITTED",
        };
        await new ApPayRun(sql, svc).RunAsync(ca, pr, ct);
        if (ca.ReturnCode > 4)
        {
            r.Rc = 4;
            ctx.Display("STERP-W-0451 " + CobolText.Fit(ca.Message, 200));
        }
    }

    /// <summary>INITIALIZE of a called program's COMMAREA in the batch: company, user and the system date only.</summary>
    private static CaHeader BatchHeader(Run r, string program, string function) => new()
    {
        Program = program,
        Function = function,
        Company = CobolText.Trimmed(r.Co, 10),
        User = CobolText.Trimmed(r.User, 20),
        BusinessDate = r.Today,
    };

    private static readonly int[] Widths = Enumerable.Repeat(100, 24).ToArray();

    /// <summary>A PIC 9(n) DISPLAY field read from text by position (non-digits count as their low nibble).</summary>
    private static int Digits(string text)
    {
        var v = 0;
        foreach (var c in text) v = v * 10 + (c & 0x0F) % 10;
        return v;
    }

    private sealed class BacsTotals
    {
        public decimal BacsValue;
        public long BacsCount;
        public decimal ChapsValue;
        public long ChapsCount;
        public decimal FpsValue;
        public long FpsCount;
        public decimal RejectValue;
        public long RejectCount;
        public long TotalPence;
        public long ItemCount;
    }

    /// <summary>
    /// The RPT-FILE (R-APBACS: CONTROLS FINAL D-METHOD, PAGE LIMIT 60, HEADING 1, FIRST DETAIL 9, LAST DETAIL 56, FOOTING 58) and
    /// BACS-FILE outputs of one run.
    /// </summary>
    private sealed class BacsListing
    {
        private readonly SpoolWriter _spool;
        private readonly GnuReportWriter _rw;
        private FileStream? _bacs;
        private string _hdr1 = "";
        private string _hdr2 = "";
        private bool _generated;
        private readonly JsonArray _lines = [];

        // WS-DET as the report groups read it
        private string _dSupplier = "";
        private string _dName = "";
        private string _dMethod = "";
        private string _dSort = "";
        private string _dAccount = "";
        private string _dRef = "";
        private string _dCurrency = "";
        private decimal _dAmount;
        private string _dStatus = "";

        // SUM counter of CF-METHOD
        private decimal _sumMethod;

        public BacsListing(SpoolWriter spool, ReportHeading heading)
        {
            _spool = spool;
            var method = new RwControl(() => _dMethod, v => _dMethod = v, 8)
            {
                Heading =
                [
                    RwLine.PlusN(2, () => new RwText().Lit(1, "Payment method  ").Src(17, _dMethod, 8).Text),
                    RwLine.Plus1(() => new RwText().Lit(3, "Supplier").Lit(13, "Account name").Lit(49, "Sort code").Lit(60, "Account ")
                        .Lit(72, "Reference").Lit(92, "Ccy").Lit(102, "Amount").Lit(118, "Status / why  ").Text),
                    RwLine.Plus1(() => ApRw.Rule),
                ],
                Footing =
                [
                    RwLine.Plus1(() => new RwText().Lit(60, "Method total    ").Ed(96, _sumMethod, "Z(10)9.99").Text),
                ],
            };
            var t = Totals;
            RwLine[] final =
            [
                RwLine.PlusN(2, () => Final("Bacs written to the file", t.BacsCount, t.BacsValue)),
                RwLine.Plus1(() => Final("CHAPS for manual release", t.ChapsCount, t.ChapsValue)),
                RwLine.Plus1(() => Final("Faster Payments to release", t.FpsCount, t.FpsValue)),
                RwLine.Plus1(() => Final("Rejected - not paid       ", t.RejectCount, t.RejectValue)),
                RwLine.PlusN(2, () => new RwText().Lit(3, "A payment over GBP 250,000.00 is routed to CHAPS").Text),
                RwLine.Plus1(() => new RwText().Lit(3, "and is not in the Bacs file - docs\\modules\\ap.md").Text),
            ];
            _rw = ApRw.Writer(spool, heading, 60, 1, 9, 56, [method], final);
            // INITIATE R-APBACS
            _rw.Initiate();
        }

        public BacsTotals Totals { get; } = new();

        public void OpenBacs(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _bacs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        }

        // 0380-WRITE-VOL-HEADER
        public void WriteVolumeHeader(Run r)
        {
            var julian = ProcessingJulian(r.ProcDate);
            var creation = LabelJulian(r.Today);
            var expiry = LabelJulian(r.PayDate);
            var fileNumber = r.PropId % 999 + 1;

            var rec = new BacsRecord(80);
            rec.Put(1, "VOL1").Put(5, r.Sun).Put(42, r.Sun).Put(80, "1");
            Write(rec);

            rec = new BacsRecord(80);
            rec.Put(1, "HDR1A").Put(6, r.Sun).Put(12, "S").Put(16, r.Sun).Put(22, r.Sun).Put(28, "00010001000100")
                .Put(43, Nine(creation, 5)).Put(49, Nine(expiry, 5)).Put(55, "000000");
            _hdr1 = rec.Text;
            Write(rec);

            rec = new BacsRecord(80);
            rec.Put(1, "HDR2F0200000100").Put(51, "00");
            _hdr2 = rec.Text;
            Write(rec);

            rec = new BacsRecord(80);
            rec.Put(1, "UHL1").Put(6, Nine(julian, 5)).Put(11, "999999    ").Put(21, "00000000").Put(29, "1 DAILY  ")
                .Put(38, Nine(Math.Abs(fileNumber), 3));
            Write(rec);
        }

        // 1000-PRODUCE-FILE
        public async Task ProduceAsync(JobRunContext ctx, ISqlRunner sql, Run r, CancellationToken ct)
        {
            IReadOnlyList<SqlioRow> rows;
            try
            {
                // 1100-OPEN-INSTRUCTIONS / 1150-REPRODUCE-STMT
                rows = await sql.FetchAllAsync(
                    "SELECT SupplierCode, SupplierName, PaymentMethod, SortCode, AccountNumber, BeneficiaryName, PaymentReference, CurrencyCode, " +
                    "CONVERT(varchar(30), AmountGBP), Status, CopResult FROM " + (r.Reproduction ? "ap.vw_BacsInstructionAll" : "ap.vw_BacsInstruction") +
                    " WHERE CompanyCode = ? AND ProposalRef = ? ORDER BY PaymentMethod, SupplierCode",
                    [SqlioParm.Text(r.Co), SqlioParm.Text(r.RunRef)], Widths, ct);
            }
            catch (SqlioException ex)
            {
                ctx.Display("STERP-E-1101 " + CobolText.Fit(ex.DriverText, 512));
                r.Rc = 12;
                return;
            }
            foreach (var row in rows) Process(row, r);
            WriteContra(ctx, r);
        }

        // 1200-FETCH-INSTRUCTION / 1300-PROCESS-INSTRUCTION
        private void Process(SqlioRow row, Run r)
        {
            var supplier = CobolText.Fit(row.Raw(0), 20);
            var name = CobolText.Fit(row.Raw(1), 34);
            var method = CobolText.Trimmed(row.Raw(2), 8);
            var sort = CobolText.Fit(row.Raw(3), 8);
            var account = CobolText.Fit(row.Raw(4), 10);
            var reference = CobolText.Fit(row.Raw(6), 18);
            var currency = CobolText.Fit(row.Raw(7), 3);
            var amount = CobolNumeric.Store(row.NumVal(8), 13, 2);
            var status = CobolText.Trimmed(row.Raw(9), 12);
            var beneficiary = CobolText.Fit(row.Raw(5), 18);
            if (CobolText.IsSpaces(beneficiary)) beneficiary = CobolText.Fit(name, 18);
            var cop = CobolText.Trimmed(row.Raw(10), 40);

            var reason = "";
            if (amount > ChapsLimit) method = "CHAPS";
            if (cop != "MATCH" && cop != "VERIFIED")
            {
                method = "REJECT";
                reason = "destination not verified";
            }
            else if (amount <= 0)
            {
                method = "REJECT";
                reason = "zero or credit amount";
            }
            else if (method == "CHAPS")
            {
                reason = "manual release - over 250,000";
            }
            else if (method is "FPS" or "FASTER")
            {
                method = "FPS";
                reason = "manual release - faster payment";
            }
            else
            {
                method = "BACS";
            }
            long bxSort = 0, bxAccount = 0;
            if (method == "BACS" && !ModulusCheck(sort, account, out bxSort, out bxAccount))
            {
                method = "REJECT";
                reason = "failed the modulus check";
            }
            var shown = CobolText.Fit((status.Trim() + " " + reason.Trim()).TrimEnd(), 12);

            var t = Totals;
            switch (method)
            {
                case "BACS":
                    WriteCredit(r, amount, bxSort, bxAccount, reference, supplier, beneficiary);
                    t.BacsCount++;
                    t.BacsValue += amount;
                    break;
                case "CHAPS":
                    t.ChapsCount++;
                    t.ChapsValue += amount;
                    break;
                case "FPS":
                    t.FpsCount++;
                    t.FpsValue += amount;
                    break;
                default:
                    t.RejectCount++;
                    t.RejectValue += amount;
                    break;
            }

            // GENERATE DE-LINE (CONTROLS ARE FINAL D-METHOD): a method break presents CF-METHOD with its sum, then zeroes it
            var broke = _generated && CobolText.Fit(_dMethod, 8) != CobolText.Fit(method, 8);
            _dSupplier = supplier;
            _dName = name;
            _dMethod = method;
            _dSort = sort;
            _dAccount = account;
            _dRef = reference;
            _dCurrency = currency;
            _dAmount = amount;
            _dStatus = shown;
            _rw.Generate(Detail);
            if (broke) _sumMethod = 0;
            _sumMethod = CobolNumeric.Store(_sumMethod + amount, 13, 2);
            _generated = true;
            _lines.Add(new JsonObject
            {
                ["method"] = method,
                ["supplierCode"] = supplier.TrimEnd(),
                ["reference"] = reference.TrimEnd(),
                ["currency"] = currency.TrimEnd(),
                ["amountGbp"] = CobolNumeric.WithScale(amount, 2),
                ["status"] = shown.TrimEnd(),
            });
        }

        /// <summary>
        /// 1400-MODULUS-CHECK: only a zero sort code or account fails; the weighted sum and its MOD 11 / MOD 10 tests change
        /// nothing (BR-APBACS-023, G09-ADV-18, reproduced).
        /// </summary>
        private static bool ModulusCheck(string sort, string account, out long bxSort, out long bxAccount)
        {
            bxSort = (long)CobolNumeric.Store(CobolNumeric.NumVal(sort), 6, 0, signed: false);
            bxAccount = (long)CobolNumeric.Store(CobolNumeric.NumVal(account), 8, 0, signed: false);
            if (bxSort == 0 || bxAccount == 0) return false;
            var text = Nine(bxSort, 6) + Nine(bxAccount, 8);
            var weighted = 0;
            for (var i = 1; i <= 14; i++) weighted += (text[i - 1] - '0') * (15 - i);
            _ = weighted % 11 != 0 && weighted % 10 != 0;
            return true;
        }

        // 1500-WRITE-BACS-RECORD
        private void WriteCredit(Run r, decimal amount, long bxSort, long bxAccount, string reference, string supplier, string beneficiary)
        {
            var pence = (long)CobolNumeric.Store(amount * 100, 11, 0, signed: false, rounded: true);
            var bxRef = CobolText.IsSpaces(reference) ? CobolText.Fit(supplier, 18) : CobolText.Fit(reference, 18);
            var rec = new BacsRecord(100);
            rec.Put(1, Nine(bxSort, 6) + Nine(bxAccount, 8) + "0" + "99" + Nine(r.OrigSort, 6) + Nine(r.OrigAccount, 8) + "    " +
                Nine(pence, 11) + CobolText.Fit(r.OrigName, 18) + bxRef + CobolText.Fit(beneficiary, 18));
            Write(rec);
            Totals.TotalPence = (Totals.TotalPence + pence) % 10_000_000_000_000;
            Totals.ItemCount = (Totals.ItemCount + 1) % 10_000_000;
        }

        // 1950-WRITE-CONTRA
        private void WriteContra(JobRunContext ctx, Run r)
        {
            if (Totals.ItemCount == 0) return;
            if (Totals.TotalPence > 99_999_999_999)
            {
                r.Rc = 8;
                ctx.Display("STERP-E-1951 Bacs contra exceeds 11 digits");
                return;
            }
            var rec = new BacsRecord(100);
            rec.Put(1, Nine(r.OrigSort, 6) + Nine(r.OrigAccount, 8) + "0" + "17" + Nine(r.OrigSort, 6) + Nine(r.OrigAccount, 8) + "    " +
                Nine(Totals.TotalPence, 11) + CobolText.Fit(r.OrigName, 18) + "CONTRA            " + CobolText.Fit(r.OrigName, 18));
            Write(rec);
            rec = new BacsRecord(80);
            rec.Put(1, _hdr1).Put(1, "EOF1").Put(55, "      ");
            Write(rec);
            rec = new BacsRecord(80);
            rec.Put(1, _hdr2).Put(1, "EOF2");
            Write(rec);
            rec = new BacsRecord(80);
            rec.Put(1, "UTL1" + Nine(Totals.TotalPence, 13) + Nine(Totals.TotalPence, 13) + "0000001" + Nine(Totals.ItemCount, 7));
            Write(rec);
        }

        /// <summary>DE-LINE (APBACS.cob:141-150): the supplier column (3, X(20)) runs under the name column (13, X(34)).</summary>
        private RwLine Detail => RwLine.Plus1(() => new RwText().Src(3, _dSupplier, 20).Src(13, _dName, 34).Src(49, _dSort, 8)
            .Src(60, _dAccount, 10).Src(72, _dRef, 18).Src(92, _dCurrency, 3).Ed(96, _dAmount, "Z(10)9.99").Src(112, _dStatus, 20).Text);

        /// <summary>0400-CLOSE-OUTPUT: TERMINATE R-APBACS (nothing printed when no GENERATE ran), close both files.</summary>
        public void Close()
        {
            _rw.Terminate();
            _spool.Dispose();
            _bacs?.Dispose();
            _bacs = null;
        }

        public JsonObject Result() => new()
        {
            ["lines"] = _lines,
            ["bacsCount"] = Totals.BacsCount,
            ["bacsValueGbp"] = CobolNumeric.WithScale(Totals.BacsValue, 2),
            ["chapsCount"] = Totals.ChapsCount,
            ["chapsValueGbp"] = CobolNumeric.WithScale(Totals.ChapsValue, 2),
            ["fpsCount"] = Totals.FpsCount,
            ["fpsValueGbp"] = CobolNumeric.WithScale(Totals.FpsValue, 2),
            ["rejectedCount"] = Totals.RejectCount,
            ["rejectedValueGbp"] = CobolNumeric.WithScale(Totals.RejectValue, 2),
        };

        private static string Final(string label, long count, decimal value) =>
            new RwText().Lit(3, label).Ed(30, count, "Z(6)9").Ed(42, value, "Z(12)9.99").Text;

        // 0375-WRITE-RECORD: the record bytes one by one, then CR LF
        private void Write(BacsRecord rec)
        {
            if (_bacs is null) return;
            var bytes = CobolText.Ansi.GetBytes(rec.Text);
            _bacs.Write(bytes, 0, bytes.Length);
            _bacs.WriteByte(0x0D);
            _bacs.WriteByte(0x0A);
        }
    }

    /// <summary>
    /// 0390-JULIAN-DATE: the integer dates are held in PIC S9(4) work fields (high-order digits truncated), so the UHL1
    /// processing day is wrong when the year's day numbers cross a multiple of 10,000 (2039-01-24 to 2039-12-31; SME-385, reproduced).
    /// </summary>
    private static long ProcessingJulian(int procDate)
    {
        var yyyy = procDate / 10000 % 10000;
        var ix = CobolDate.IntegerOfDate(procDate) % 10000;
        var jx = CobolDate.IntegerOfDate(yyyy * 10000 + 101) % 10000;
        var kx = (ix - jx + 1) % 10000;
        return Math.Abs((long)(yyyy % 100) * 1000 + kx) % 100000;
    }

    /// <summary>0391-LABEL-DATE: yyddd computed in one expression (correct for every date).</summary>
    private static long LabelJulian(int date)
    {
        var yyyy = date / 10000 % 10000;
        var v = (long)(yyyy % 100) * 1000 + CobolDate.IntegerOfDate(date) - CobolDate.IntegerOfDate(yyyy * 10000 + 101) + 1;
        return Math.Abs(v) % 100000;
    }

    /// <summary>An unsigned PIC 9(n) DISPLAY field: the low-order n digits.</summary>
    private static string Nine(long value, int digits)
    {
        var limit = 1L;
        for (var i = 0; i < digits; i++) limit *= 10;
        return (Math.Abs(value) % limit).ToString("D" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    /// <summary>BACS-RECORD (PIC X(100)) written for the first 80 or 100 bytes.</summary>
    private sealed class BacsRecord(int length)
    {
        private readonly char[] _chars = Enumerable.Repeat(' ', 100).ToArray();

        public BacsRecord Put(int position, string text)
        {
            for (var i = 0; i < text.Length && position - 1 + i < 100; i++) _chars[position - 1 + i] = text[i];
            return this;
        }

        public string Text => new string(_chars, 0, length);
    }
}
