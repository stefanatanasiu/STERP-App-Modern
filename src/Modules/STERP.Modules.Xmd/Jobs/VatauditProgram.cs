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
/// VATAUDIT (cobol/batch/xmd/VATAUDIT.cob; FR-STERP-XMD-028): VAT detail (MODE=DETAIL) or MTD audit trail (MODE=MTD) of one
/// VAT period, proved against the movement on the VAT control accounts. Read only. Legacy validates its arguments and loads
/// the VAT period before its own JOBLOG START: a missing period is refused by <see cref="PreCheckAsync"/> before anything is
/// registered; an unknown period ends the run RC 8/12 with no program row (BRF-VATAUDIT-06). A job-framework entry point
/// registers the run before it starts (contracts 8.3: the 202 jobRunId), so that row stands for the JOBLOG row and the
/// in-program registration below only runs where no entry point registered one.
/// Listing FL-073 (RD PAGE LIMIT 60, FIRST DETAIL 8, LAST DETAIL 52, FOOTING 58).
/// </summary>
internal static class VatauditProgram
{
    private static readonly int[] Widths = Enumerable.Repeat(120, 30).ToArray();

    private static readonly string[] BoxCaptions =
    [
        "VAT due on sales and other outputs",
        "VAT due on acquisitions from EU to N Ireland",
        "Total VAT due - boxes 1 and 2",
        "VAT reclaimed on purchases and other inputs",
        "Net VAT to pay to HMRC or to reclaim",
        "Total value of sales, excluding VAT",
        "Total value of purchases, excluding VAT",
        "Total value of dispatches to N Ireland from EU",
        "Total value of acquisitions from EU to N Ireland",
    ];

    private sealed class State
    {
        public string Key = "";
        public string Start8 = "";
        public string End8 = "";
        public decimal Tolerance;
        public decimal[] HeaderBox = new decimal[9];
        public int Cnt, Late, Adj, Blocked;
        public decimal Net, Vat;
        public bool ReturnFound;
        public string RId = "", RVersion = "", RStatus = "", RSubRef = "", RHmrc = "", RPrepared = "", RApproved = "", RSubmitted = "", RRegNo = "";
        public int RSubmittedNum;
        public decimal PfBoxes;
        public long JoblogId;
    }

    /// <summary>STERP-E-0103 of 0100-READ-ARGS (VATAUDIT.cob:509-513).</summary>
    public const string PeriodRequired = "STERP-E-0103 VATPERIOD= is required, for example VATPERIOD=26A3";

    /// <summary>
    /// The 0100-READ-ARGS refusal (VATAUDIT.cob:436-439,478-513): neither VATPERIOD nor PERIOD gives a period key (first 10
    /// characters) - RC 8 STERP-E-0103 and FINISH before 9100-JOB-START, so nothing is registered (STERP-XMD 4.3: the only
    /// reachable early check of the suite; FR-STERP-PLATFORM-057 AC2). The rest of 0100 (mode default, STERP-W-0102,
    /// tolerance) changes nothing on that path. An unknown period (0200-LOAD-PERIOD, STERP-E-0202) is decided inside the run.
    /// </summary>
    public static Task<LegacyResult?> PreCheckAsync(JobPreCheckContext pre)
    {
        var key = CobolText.Fit(Arg(pre, "VATPERIOD"), 200)[..10];
        if (CobolText.IsSpaces(key)) key = CobolText.Fit(Arg(pre, "PERIOD"), 200)[..10];
        return Task.FromResult(CobolText.IsSpaces(key) ? LegacyResult.Error("E-0103", PeriodRequired) : null);
    }

    /// <summary>9550-GET-ARG: the value of the key (first 12 characters) or "".</summary>
    private static string Arg(JobPreCheckContext pre, string key) => pre.Args.Get(CobolText.Fit(key, 12).TrimEnd(' ')) ?? "";

    public static async Task<int> RunAsync(JobRunContext ctx)
    {
        var b = XmdBatch.Start(ctx);
        var result = new JsonObject();
        var st = new State();
        var rc = 12;
        try
        {
            rc = await RunAsync(b, st, result);
            return rc;
        }
        finally
        {
            ctx.Result = result;
            // 9150-JOB-END: JOBLOG END of the program's own row (run id 0 when it never started: refused, nothing changes)
            if (st.JoblogId > 0)
            {
                await BatchSuite.JobEndAsync(ctx.Get<IJobRunRegistry>(), b.Args, "VATAUDIT", st.JoblogId, rc, b.RowsOut,
                    b.SpoolName.Length > 0 ? b.SpoolName : null, CancellationToken.None);
            }
        }
    }

    private static async Task<int> RunAsync(XmdBatch b, State st, JsonObject result)
    {
        // 0100-READ-ARGS
        var key = CobolText.Fit(b.GetArg("VATPERIOD"), 200)[..10];
        if (CobolText.IsSpaces(key)) key = CobolText.Fit(b.GetArg("PERIOD"), 200)[..10];
        st.Key = CobolText.Upper(key);
        var mode = b.Mode;
        if (mode == "PREVIEW" || mode.Length == 0) mode = "DETAIL";
        if (mode != "DETAIL" && mode != "MTD")
        {
            b.Display("STERP-W-0102 mode " + CobolText.Trim(mode) + " is not DETAIL or MTD - DETAIL assumed");
            mode = "DETAIL";
        }
        var sMode = CobolText.Fit(mode, 10);
        st.Tolerance = CobolNumeric.Store(await b.GetParmNumAsync("XMD.RECON.TOLERANCE", 0.01m), 9, 2);
        var tolText = b.GetArg("TOL");
        if (!CobolText.IsSpaces(tolText)) st.Tolerance = CobolNumeric.Store(CobolNumeric.NumVal(tolText), 9, 2);
        var sTol = CobolText.Fit(Disp.Mon(st.Tolerance), 12);
        if (CobolText.IsSpaces(st.Key))
        {
            b.Rc = 8;
            b.Display(PeriodRequired);
        }
        b.Display("STERP-I-0100 VATAUDIT company " + CobolText.Trim(b.Co) + " VAT period " + CobolText.Trim(st.Key) + " mode " +
                  CobolText.Trim(sMode));
        result["periodKey"] = st.Key.TrimEnd(' ');
        result["mode"] = sMode.TrimEnd(' ');
        result["tolerance"] = sTol.TrimEnd(' ');
        var transactions = new JsonArray();
        var boxes = new JsonArray();
        var corrections = new JsonArray();
        var piv = new JsonArray();
        result["transactions"] = transactions;
        result["boxes"] = boxes;
        result["corrections"] = corrections;
        result["postponedImportVat"] = piv;
        if (b.Rc > 4) return Finish(b, st, result, "");

        // 0200-LOAD-PERIOD
        SqlioRow row;
        try
        {
            row = await b.Sql.SingleAsync(
                "SELECT ISNULL(p.VatRegNo,''), CONVERT(char(8),p.StartDate,112), CONVERT(char(8),p.EndDate,112), ISNULL(CONVERT(char(8)," +
                "p.FilingDueDate,112),''), ISNULL(CONVERT(char(8),p.PaymentDueDate,112),''), ISNULL(p.Frequency,''), ISNULL(p.Status,''), " +
                "ISNULL(p.SubmissionRef,''), ISNULL(c.VatScheme,'INVOICE') FROM fin.VatPeriod p JOIN core.Company c ON c.CompanyCode = " +
                "p.CompanyCode WHERE p.CompanyCode = ? AND p.VatPeriodKey = ?",
                [SqlioParm.Text(b.Co), SqlioParm.Text(st.Key)], Widths[..9], b.Ct);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.Display("STERP-E-0201 " + CobolText.Fit(ex.DriverText, 100));
            return Finish(b, st, result, "");
        }
        if (!row.Found)
        {
            b.Rc = 8;
            b.Display("STERP-E-0202 no VAT period " + CobolText.Trim(st.Key) + " for company " + CobolText.Trim(b.Co));
            return Finish(b, st, result, "");
        }
        var sRegNo = CobolText.Fit(row.Raw(0), 20);
        st.Start8 = row.Raw(1)[..8];
        st.End8 = row.Raw(2)[..8];
        var sFreq = CobolText.Fit(row.Raw(5), 12);
        var sStatus = CobolText.Fit(row.Raw(6), 12);
        var sScheme = CobolText.Fit(row.Raw(8), 12);
        var startNum = BatchSuite.IsoToNum(st.Start8);
        var sStart = BatchSuite.FormatDate(startNum);
        var endNum = BatchSuite.IsoToNum(st.End8);
        var sEnd = BatchSuite.FormatDate(endNum);
        b.SetDates(asAt: endNum);
        var dueNum = BatchSuite.IsoToNum(row.Raw(3)[..8]);
        var sDue = dueNum == 0 ? "not set" : BatchSuite.FormatDate(dueNum);
        var payNum = BatchSuite.IsoToNum(row.Raw(4)[..8]);
        var sPayDue = payNum == 0 ? "not set" : BatchSuite.FormatDate(payNum);
        b.Heading.AsAtText = CobolText.Fit("VAT period " + CobolText.Trim(st.Key) + " to " + sEnd, 30).TrimEnd(' ');
        result["start"] = IsoDate(startNum);
        result["end"] = IsoDate(endNum);
        result["filingDue"] = IsoDate(dueNum);
        result["paymentDue"] = IsoDate(payNum);
        result["periodStatus"] = sStatus.TrimEnd(' ');
        result["frequency"] = sFreq.TrimEnd(' ');
        result["registration"] = sRegNo.TrimEnd(' ');
        result["scheme"] = sScheme.TrimEnd(' ');

        var mtd = sMode.TrimEnd(' ') == "MTD";
        // 9100-JOB-START (the program's own row, unless the entry point already registered one for it)
        if (b.Ctx.ProgramRunId is null)
        {
            st.JoblogId = await BatchSuite.JobStartAsync(b.Ctx.Get<IJobRunRegistry>(), b.Args, "VATAUDIT", b.Ctx.CommandLine, b.Ct);
        }
        if (!await b.OpenSpoolAsync("VATAUDIT", mtd ? "VAT return and MTD audit trail" : "VAT detail and return review",
                mtd ? "Boxes 1 to 9, corrections and the evidence" : "Every taxable entry under the box it feeds") || b.Rc > 4)
        {
            return Finish(b, st, result, "");
        }
        var statusText = "";
        var rw = b.Report(
        [
            RwLine.PlusN(2, () => new string('=', 132)),
            RwLine.Plus1(() => new RwText().Lit(1, "Lines listed").Num(24, st.Cnt, "ZZZ,ZZ9").Lit(34, "Late entries    ")
                .Num(50, st.Late, "ZZZ,ZZ9").Lit(60, "Adjustments   ").Num(74, st.Adj, "ZZZ,ZZ9")
                .Lit(84, "Blocked recovery    ").Num(104, st.Blocked, "ZZZ,ZZ9").Text),
            RwLine.Plus1(() => new RwText().Lit(1, "Period totals").Num(62, st.Net, BigPic).Num(81, st.Vat, BigPic).Put(104, statusText, 20).Text),
            RwLine.PlusN(2, () => new RwText().Lit(3, "Keep this listing with the return: it is the")
                .Lit(56, "digital link between ledger and figures filed.").Text),
        ]);
        rw.Initiate();
        rw.Generate(
            RwLine.Plus1(() => new RwText().Lit(1, "VAT period key    ").Put(20, st.Key, 10).Lit(32, "From  ").Put(38, sStart, 10)
                .Lit(50, "To  ").Put(54, sEnd, 10).Lit(66, "Status  ").Put(74, sStatus, 12).Lit(88, "Mode  ").Put(94, sMode, 10)
                .Lit(106, "Frequency  ").Put(117, sFreq, 12).Text),
            RwLine.Plus1(() => new RwText().Lit(1, "VAT registration  ").Put(20, sRegNo, 20).Lit(42, "Scheme  ").Put(50, sScheme, 12)
                .Lit(64, "Filing due   ").Put(77, sDue, 10).Lit(89, "Payment due   ").Put(103, sPayDue, 10).Lit(117, "Tol  ")
                .Put(122, sTol, 11).Text),
            RwLine.Plus1(() => new RwText().Lit(1, "Basis: tax point within the VAT period, sterling")
                .Lit(50, "carrying values, boxes proved to the VAT control.").Text));
        if (mtd) await MtdRunAsync(b, st, rw, boxes, corrections, piv, result);
        else await DetailRunAsync(b, st, rw, transactions);
        statusText = await ProveAsync(b, st, rw, mtd);
        rw.Terminate();
        b.CloseSpool();
        return Finish(b, st, result, statusText);
    }

    private const string BigPic = "Z,ZZZ,ZZZ,ZZ9.99-";

    private static int Finish(XmdBatch b, State st, JsonObject result, string statusText)
    {
        result["totalNetGbp"] = CobolNumeric.WithScale(st.Net, 2);
        result["totalVatGbp"] = CobolNumeric.WithScale(st.Vat, 2);
        result["lines"] = st.Cnt;
        result["lateLines"] = st.Late;
        result["adjustments"] = st.Adj;
        result["blockedRecovery"] = st.Blocked;
        result["proofStatus"] = statusText.Length > 0 ? statusText : null;
        var rc = b.Finish();
        result["returnCode"] = rc;
        return rc;
    }

    /// <summary>1000-DETAIL-RUN (+ 1100-PRINT-TXN, 1200-BOX-SUBTOTAL).</summary>
    private static async Task DetailRunAsync(XmdBatch b, State st, GnuReportWriter rw, JsonArray transactions)
    {
        rw.Generate(
            RwLine.PlusN(2, () => new string('-', 132)),
            RwLine.Plus1(() => new RwText().Lit(1, "Box").Lit(5, "VAT code").Lit(14, "Tax point ").Lit(25, "Mechanism")
                .Lit(35, "Document").Lit(54, "Party").Lit(64, "         Net GBP").Lit(83, "         VAT GBP")
                .Lit(101, "Recovery  ").Lit(112, "Late ").Lit(118, "Adjustment").Text),
            RwLine.Plus1(() => new string('-', 132)));
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await b.Sql.FetchAllAsync(
                "SELECT CONVERT(varchar(4),ISNULL(v.VatBox,0)), ISNULL(v.VatCode,''), CONVERT(char(8),v.TaxPointDate,112), ISNULL(v.Mechanism," +
                "'NORMAL'), ISNULL(v.Direction,''), ISNULL(v.DocumentType,'') + ' ' + ISNULL(v.DocumentRef,''), ISNULL(v.PartyCode,''), " +
                "CONVERT(varchar(24),ISNULL(v.NetAmountGBP,0)), CONVERT(varchar(24),ISNULL(v.VatAmountGBP,0)), CONVERT(varchar(24),ISNULL(" +
                "v.RecoverableVatGBP,0)), CONVERT(char(1),ISNULL(v.IsLateEntry,0)), ISNULL(v.AdjustmentType,''), CONVERT(char(1),ISNULL(" +
                "c.IsRecoverable,0)), CONVERT(varchar(4),ISNULL(v.NetBox,0)) FROM fin.VatTransaction v LEFT JOIN fin.VatCode c ON c.VatCode = " +
                "v.VatCode WHERE v.CompanyCode = ? AND v.VatPeriodKey = ? ORDER BY ISNULL(v.VatBox,0), v.TaxPointDate, v.DocumentRef, v.LineNum",
                [SqlioParm.Text(b.Co), SqlioParm.Text(st.Key)], Widths[..15], b.Ct);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.Display("STERP-E-1001 " + CobolText.Fit(ex.DriverText, 100));
            return;
        }
        var prevBox = -1;
        int bCnt = 0;
        decimal bNet = 0, bVat = 0;
        void Subtotal()
        {
            var label = prevBox == 0 ? "Outside the nine boxes" : "Subtotal box " + Disp.Unsigned(prevBox, 9).Substring(7, 2);
            rw.Generate(
                RwLine.Plus1(() => new RwText().Put(5, label, 22).Lit(29, "entries     ").Num(41, bCnt, "ZZZ,ZZ9")
                    .Num(62, bNet, BigPic).Num(81, bVat, BigPic).Text),
                RwLine.Plus1(() => new RwText().Put(62, new string('-', 18), 18).Put(81, new string('-', 18), 18).Text));
            bNet = 0;
            bVat = 0;
            bCnt = 0;
        }
        foreach (var r in rows)
        {
            var boxNo = (int)CobolNumeric.Store(r.NumVal(0), 4, 0);
            if (prevBox != boxNo && prevBox != -1) Subtotal();
            var dBox = boxNo == 0 ? " - " : Disp.Unsigned(boxNo, 9).Substring(7, 2) + " ";
            var dCode = CobolText.Fit(r.Raw(1), 8);
            var tp = BatchSuite.IsoToNum(r.Raw(2)[..8]);
            var dTax = BatchSuite.FormatDate(tp);
            var dMech = CobolText.Fit(r.Raw(3), 8);
            var dDoc = CobolText.Fit(r.Raw(5), 18);
            var dParty = CobolText.Fit(r.Raw(6), 8);
            var dNet = CobolNumeric.Store(r.NumVal(7), 13, 2);
            var dVat = CobolNumeric.Store(r.NumVal(8), 13, 2);
            var rec = CobolNumeric.Store(r.NumVal(9), 13, 2);
            string recFlag;
            if (r.Raw(4)[..6] == "OUTPUT")
            {
                recFlag = "OUTPUT";
            }
            else if (rec == 0 && dVat != 0)
            {
                recFlag = "BLOCKED";
                st.Blocked++;
            }
            else if (r.Raw(12)[0] == '1')
            {
                recFlag = "RECOVER";
            }
            else
            {
                recFlag = "PART REC";
            }
            var lateFlag = "";
            if (r.Raw(10)[0] == '1')
            {
                lateFlag = "LATE";
                st.Late++;
            }
            var adj = CobolText.Fit(r.Raw(11), 14);
            if (!CobolText.IsSpaces(adj)) st.Adj++;
            st.Cnt++;
            bCnt++;
            b.RowsOut++;
            bNet = CobolNumeric.Store(bNet + dNet, 13, 2);
            bVat = CobolNumeric.Store(bVat + dVat, 13, 2);
            st.Net = CobolNumeric.Store(st.Net + dNet, 13, 2);
            st.Vat = CobolNumeric.Store(st.Vat + dVat, 13, 2);
            prevBox = boxNo;
            rw.Generate(RwLine.Plus1(() => new RwText().Put(1, dBox, 3).Put(5, dCode, 8).Put(14, dTax, 10).Put(25, dMech, 8).Put(35, dDoc, 18)
                .Put(54, dParty, 8).Num(62, dNet, BigPic).Num(81, dVat, BigPic).Put(101, recFlag, 10).Put(112, lateFlag, 5).Put(118, adj, 14).Text));
            transactions.Add(new JsonObject
            {
                ["box"] = dBox.TrimEnd(' '),
                ["vatCode"] = dCode.TrimEnd(' '),
                ["taxPoint"] = IsoDate(tp),
                ["mechanism"] = dMech.TrimEnd(' '),
                ["document"] = dDoc.TrimEnd(' '),
                ["party"] = dParty.TrimEnd(' '),
                ["netGbp"] = CobolNumeric.WithScale(dNet, 2),
                ["vatGbp"] = CobolNumeric.WithScale(dVat, 2),
                ["recoveryFlag"] = recFlag,
                ["lateFlag"] = lateFlag,
                ["adjustment"] = adj.TrimEnd(' '),
            });
        }
        if (prevBox != -1) Subtotal();
        if (st.Cnt == 0)
        {
            None(rw, "No VAT transaction is held for this period");
            if (b.Rc < 4) b.Rc = 4;
        }
        b.Display("STERP-I-VATROWS " + Disp.Unsigned(st.Cnt, 9));
    }

    /// <summary>2000-MTD-RUN: the return, its nine boxes, corrections, postponed import VAT and the submission evidence.</summary>
    private static async Task MtdRunAsync(XmdBatch b, State st, GnuReportWriter rw, JsonArray boxes, JsonArray corrections, JsonArray piv, JsonObject result)
    {
        await LoadReturnAsync(b, st);
        rw.Generate(
            RwLine.PlusN(2, () => new string('-', 132)),
            RwLine.Plus1(() => new RwText().Lit(1, "Box").Lit(5, "Description of the box as filed").Lit(55, "       Value GBP")
                .Lit(75, "Contributing ").Lit(90, "Late entries ").Lit(105, "Source of value").Text),
            RwLine.Plus1(() => new string('-', 132)));
        await PrintBoxesAsync(b, st, rw, boxes);
        await CorrectionsAsync(b, st, rw, corrections);
        await PostponedAsync(b, st, rw, piv);
        // 2500-SUBMISSION
        if (st.ReturnFound)
        {
            rw.Generate(
                RwLine.PlusN(2, () => new RwText().Lit(1, "Submission evidence held for this return").Text),
                RwLine.Plus1(() => new RwText().Lit(3, "Return identity   ").Put(21, st.RId, 12).Lit(35, "Version ").Put(43, st.RVersion, 6)
                    .Lit(52, "Status ").Put(59, st.RStatus, 14).Lit(75, "Prepared  ").Put(85, st.RPrepared, 20).Lit(107, "Approved  ")
                    .Put(117, st.RApproved, 15).Text),
                RwLine.Plus1(() => new RwText().Lit(3, "Submission ref    ").Put(21, st.RSubRef, 40).Lit(63, "Submitted  ")
                    .Put(74, st.RSubmitted, 10).Lit(86, "VAT number    ").Put(100, st.RRegNo, 20).Text),
                RwLine.Plus1(() => new RwText().Lit(3, "HMRC response     ").Put(21, st.RHmrc, 60).Text));
            result["submission"] = new JsonObject
            {
                ["returnId"] = CobolText.Fit(st.RId, 12).TrimEnd(' '),
                ["version"] = CobolText.Fit(st.RVersion, 6).TrimEnd(' '),
                ["status"] = CobolText.Fit(st.RStatus, 14).TrimEnd(' '),
                ["submissionRef"] = CobolText.Fit(st.RSubRef, 40).TrimEnd(' '),
                ["hmrcResponse"] = CobolText.Fit(st.RHmrc, 60).TrimEnd(' '),
                ["preparedBy"] = CobolText.Fit(st.RPrepared, 20).TrimEnd(' '),
                ["approvedBy"] = CobolText.Fit(st.RApproved, 20).TrimEnd(' '),
                ["submitted"] = IsoDate(st.RSubmittedNum),
            };
        }
        else
        {
            result["submission"] = null;
        }
    }

    /// <summary>2100-LOAD-RETURN: the latest version of the return (none: RC 4, STERP-W-2102).</summary>
    private static async Task LoadReturnAsync(XmdBatch b, State st)
    {
        st.ReturnFound = false;
        SqlioRow row;
        try
        {
            row = await b.Sql.SingleAsync(
                "SELECT TOP 1 CONVERT(varchar(12),r.VatReturnId), CONVERT(varchar(6),r.Version), ISNULL(r.Status,''), ISNULL(r.SubmissionRef,''), " +
                "ISNULL(r.HmrcResponse,''), ISNULL(r.PreparedBy,''), ISNULL(r.ApprovedBy,''), ISNULL(CONVERT(char(8),r.SubmittedAt,112),''), " +
                "ISNULL(r.VatRegNo,''), CONVERT(varchar(24),ISNULL(r.Box1,0)), CONVERT(varchar(24),ISNULL(r.Box2,0)), CONVERT(varchar(24)," +
                "ISNULL(r.Box3,0)), CONVERT(varchar(24),ISNULL(r.Box4,0)), CONVERT(varchar(24),ISNULL(r.Box5,0)), CONVERT(varchar(24),ISNULL(" +
                "r.Box6,0)), CONVERT(varchar(24),ISNULL(r.Box7,0)), CONVERT(varchar(24),ISNULL(r.Box8,0)), CONVERT(varchar(24),ISNULL(r.Box9,0)) " +
                "FROM fin.VatReturn r WHERE r.CompanyCode = ? AND r.VatPeriodKey = ? ORDER BY r.Version DESC",
                [SqlioParm.Text(b.Co), SqlioParm.Text(st.Key)], Widths[..18], b.Ct);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.Display("STERP-E-2101 " + CobolText.Fit(ex.DriverText, 100));
            return;
        }
        if (!row.Found)
        {
            if (b.Rc < 4) b.Rc = 4;
            b.Display("STERP-W-2102 no VAT return is prepared for " + CobolText.Trim(st.Key));
            return;
        }
        st.ReturnFound = true;
        st.RId = CobolText.Fit(row.Raw(0), 12);
        st.RVersion = CobolText.Fit(row.Raw(1), 6);
        st.RStatus = CobolText.Fit(row.Raw(2), 14);
        st.RSubRef = CobolText.Fit(row.Raw(3), 40);
        st.RHmrc = CobolText.Fit(row.Raw(4), 60);
        st.RPrepared = CobolText.Fit(row.Raw(5), 20);
        st.RApproved = CobolText.Fit(row.Raw(6), 20);
        st.RRegNo = CobolText.Fit(row.Raw(8), 20);
        st.RSubmittedNum = BatchSuite.IsoToNum(row.Raw(7)[..8]);
        st.RSubmitted = st.RSubmittedNum == 0 ? "not yet" : BatchSuite.FormatDate(st.RSubmittedNum);
        for (var i = 0; i < 9; i++) st.HeaderBox[i] = CobolNumeric.Store(row.NumVal(9 + i), 13, 2);
        if (CobolText.IsSpaces(st.RSubRef)) st.RSubRef = "Not yet submitted to HMRC";
        if (CobolText.IsSpaces(st.RHmrc)) st.RHmrc = "No HMRC receipt is held against this version";
    }

    /// <summary>2200-PRINT-BOXES (+ 2250-ONE-BOX, 2260-TRACE-BOX, 2270-HEADER-BOXES).</summary>
    private static async Task PrintBoxesAsync(XmdBatch b, State st, GnuReportWriter rw, JsonArray boxes)
    {
        var seen = 0;
        if (!st.ReturnFound)
        {
            None(rw, "No return - boxes cannot be listed");
            return;
        }
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await b.Sql.FetchAllAsync(
                "SELECT CONVERT(varchar(4),b.BoxNo), CONVERT(varchar(24),ISNULL(b.BoxValue,0)), ISNULL(b.Caption,''), CONVERT(varchar(12)," +
                "ISNULL(b.TransactionCount,0)), CONVERT(varchar(12),(SELECT COUNT(*) FROM fin.VatReturnDetail d WHERE d.VatReturnId = " +
                "b.VatReturnId AND d.BoxNo = b.BoxNo)), CONVERT(varchar(12),(SELECT COUNT(*) FROM fin.VatReturnDetail d WHERE d.VatReturnId " +
                "= b.VatReturnId AND d.BoxNo = b.BoxNo AND d.IsLateEntry = 1)) FROM fin.VatReturnBox b WHERE b.VatReturnId = ? ORDER BY b.BoxNo",
                [SqlioParm.Text(st.RId)], Widths[..6], b.Ct);
        }
        catch (SqlioException ex)
        {
            b.Rc = 12;
            b.Display("STERP-E-2201 " + CobolText.Fit(ex.DriverText, 100));
            return;
        }
        foreach (var row in rows)
        {
            var boxNo = (int)CobolNumeric.Store(row.NumVal(0), 4, 0);
            if (boxNo < 1 || boxNo > 9) continue;
            var value = CobolNumeric.Store(row.NumVal(1), 13, 2);
            var caption = CobolText.IsSpaces(row.Raw(2)) ? BoxCaptions[boxNo - 1] : CobolText.Fit(row.Raw(2), 48);
            var cnt = (int)CobolNumeric.Store(row.NumVal(4), 7, 0);
            if (cnt == 0) cnt = (int)CobolNumeric.Store(row.NumVal(3), 7, 0);
            var late = (int)CobolNumeric.Store(row.NumVal(5), 7, 0);
            seen++;
            st.Cnt++;
            b.RowsOut++;
            st.Late += late;
            st.HeaderBox[boxNo - 1] = value;
            Box(b, rw, boxes, boxNo, caption, value, cnt, late, "fin.VatReturnBox");
        }
        if (seen == 0)
        {
            for (var i = 1; i <= 9; i++)
            {
                st.Cnt++;
                b.RowsOut++;
                Box(b, rw, boxes, i, BoxCaptions[i - 1], st.HeaderBox[i - 1], 0, 0, "fin.VatReturn header");
            }
            None(rw, "Box breakdown not stored - header values listed");
            if (b.Rc < 4) b.Rc = 4;
        }
        st.PfBoxes = CobolNumeric.Store(st.HeaderBox[2] - st.HeaderBox[3], 13, 2);
    }

    private static void Box(XmdBatch b, GnuReportWriter rw, JsonArray boxes, int boxNo, string caption, decimal value, int cnt, int late, string note)
    {
        var mBox = Disp.Unsigned(boxNo, 9).Substring(7, 2);
        rw.Generate(RwLine.Plus1(() => new RwText().Put(1, mBox, 3).Put(5, caption, 48).Num(54, value, BigPic).Num(77, cnt, "ZZZ,ZZ9")
            .Num(92, late, "ZZZ,ZZ9").Put(105, note, 26).Text));
        // 2260-TRACE-BOX
        b.Display("STERP-I-MTDBOX " + mBox + " " + Disp.Ed(value, "-(14)9.99"));
        boxes.Add(new JsonObject
        {
            ["box"] = mBox,
            ["caption"] = CobolText.Fit(caption, 48).TrimEnd(' '),
            ["valueGbp"] = CobolNumeric.WithScale(value, 2),
            ["contributing"] = cnt,
            ["late"] = late,
            ["source"] = "",
            ["note"] = note,
        });
    }

    /// <summary>2300-CORRECTIONS (+ 2350-ONE-ERROR).</summary>
    private static async Task CorrectionsAsync(XmdBatch b, State st, GnuReportWriter rw, JsonArray corrections)
    {
        rw.Generate(
            RwLine.PlusN(2, () => new RwText().Lit(1, "Error corrections touching this VAT period").Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Original period ").Lit(20, "Discovered").Lit(33, "   Net error GBP")
                .Lit(53, "   Box 6 turnov.").Lit(72, "Method  ").Lit(82, "Deliberate").Lit(94, "Status    ")
                .Lit(106, "Disclosure ref").Text));
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await b.Sql.FetchAllAsync(
                "SELECT ISNULL(e.OriginalVatPeriodKey,''), ISNULL(CONVERT(char(8),e.DiscoveredDate,112),''), CONVERT(varchar(24),ISNULL(" +
                "e.NetErrorAmount,0)), CONVERT(varchar(24),ISNULL(e.Box6Turnover,0)), ISNULL(e.Method,''), ISNULL(e.Status,''), CONVERT(char(1)," +
                "ISNULL(e.IsDeliberate,0)), ISNULL(e.DisclosureRef,''), ISNULL(e.Reason,'') FROM fin.VatErrorCorrection e WHERE e.CompanyCode = ? " +
                "AND (e.CorrectionVatPeriodKey = ? OR e.OriginalVatPeriodKey = ?) ORDER BY e.DiscoveredDate",
                [SqlioParm.Text(b.Co), SqlioParm.Text(st.Key), SqlioParm.Text(st.Key)], Widths[..9], b.Ct);
        }
        catch (SqlioException)
        {
            None(rw, "Corrections unavailable - input error");
            if (b.Rc < 4) b.Rc = 4;
            return;
        }
        foreach (var row in rows)
        {
            var orig = CobolText.Fit(row.Raw(0), 10);
            var foundNum = BatchSuite.IsoToNum(row.Raw(1)[..8]);
            var found = BatchSuite.FormatDate(foundNum);
            var net = CobolNumeric.Store(row.NumVal(2), 13, 2);
            var turnover = CobolNumeric.Store(row.NumVal(3), 13, 2);
            var method = CobolText.Fit(row.Raw(4), 12);
            var status = CobolText.Fit(row.Raw(5), 12);
            var delib = row.Raw(6)[0] == '1' ? "DELIBERATE" : "careless";
            var disc = CobolText.Fit(row.Raw(7), 20);
            var reason = CobolText.Fit(row.Raw(8), 54);
            b.RowsOut++;
            rw.Generate(
                RwLine.Plus1(() => new RwText().Put(3, orig, 10).Put(20, found, 10).Num(31, net, BigPic).Num(51, turnover, BigPic).Put(72, method, 8)
                    .Put(82, delib, 10).Put(94, status, 10).Put(106, disc, 20).Text),
                RwLine.Plus1(() => new RwText().Put(6, reason, 54).Text));
            corrections.Add(new JsonObject
            {
                ["originalPeriod"] = orig.TrimEnd(' '),
                ["discovered"] = IsoDate(foundNum),
                ["netErrorGbp"] = CobolNumeric.WithScale(net, 2),
                ["turnoverGbp"] = CobolNumeric.WithScale(turnover, 2),
                ["method"] = method.TrimEnd(' '),
                ["status"] = status.TrimEnd(' '),
                ["deliberate"] = delib,
                ["disclosure"] = disc.TrimEnd(' '),
                ["reason"] = reason.TrimEnd(' '),
            });
        }
        if (corrections.Count == 0) None(rw, "None disclosed against this period");
    }

    /// <summary>2400-POSTPONED (+ 2450-ONE-PIV): mechanism PIV transactions of the period.</summary>
    private static async Task PostponedAsync(XmdBatch b, State st, GnuReportWriter rw, JsonArray piv)
    {
        rw.Generate(
            RwLine.PlusN(2, () => new RwText().Lit(1, "Postponed import VAT accounted in this period").Text),
            RwLine.Plus1(() => new RwText().Lit(3, "VAT code").Lit(13, "Tax point ").Lit(25, "Document").Lit(45, "Supplier")
                .Lit(58, "         Net GBP").Lit(77, "         VAT GBP").Lit(96, " Recoverable GBP").Lit(115, "Evidence (C79)").Text));
        IReadOnlyList<SqlioRow> rows;
        try
        {
            rows = await b.Sql.FetchAllAsync(
                "SELECT ISNULL(v.VatCode,''), CONVERT(char(8),v.TaxPointDate,112), ISNULL(v.DocumentType,'') + ' ' + ISNULL(v.DocumentRef,''), " +
                "ISNULL(v.PartyCode,''), CONVERT(varchar(24),ISNULL(v.NetAmountGBP,0)), CONVERT(varchar(24),ISNULL(v.VatAmountGBP,0)), " +
                "CONVERT(varchar(24),ISNULL(v.RecoverableVatGBP,0)), ISNULL(v.EvidenceRef,'') FROM fin.VatTransaction v WHERE v.CompanyCode = ? " +
                "AND v.VatPeriodKey = ? AND v.Mechanism = 'PIV' ORDER BY v.TaxPointDate, v.DocumentRef",
                [SqlioParm.Text(b.Co), SqlioParm.Text(st.Key)], Widths[..8], b.Ct);
        }
        catch (SqlioException)
        {
            None(rw, "Postponed import VAT unavailable");
            if (b.Rc < 4) b.Rc = 4;
            return;
        }
        foreach (var row in rows)
        {
            var code = CobolText.Fit(row.Raw(0), 8);
            var tp = BatchSuite.IsoToNum(row.Raw(1)[..8]);
            var tax = BatchSuite.FormatDate(tp);
            var doc = CobolText.Fit(row.Raw(2), 18);
            var party = CobolText.Fit(row.Raw(3), 10);
            var net = CobolNumeric.Store(row.NumVal(4), 13, 2);
            var vat = CobolNumeric.Store(row.NumVal(5), 13, 2);
            var rec = CobolNumeric.Store(row.NumVal(6), 13, 2);
            var evidence = CobolText.Fit(row.Raw(7), 20);
            b.RowsOut++;
            rw.Generate(RwLine.Plus1(() => new RwText().Put(3, code, 8).Put(13, tax, 10).Put(25, doc, 18).Put(45, party, 10).Num(56, net, BigPic)
                .Num(75, vat, BigPic).Num(94, rec, BigPic).Put(115, evidence, 18).Text));
            piv.Add(new JsonObject
            {
                ["vatCode"] = code.TrimEnd(' '),
                ["taxPoint"] = IsoDate(tp),
                ["document"] = doc.TrimEnd(' '),
                ["party"] = party.TrimEnd(' '),
                ["netGbp"] = CobolNumeric.WithScale(net, 2),
                ["vatGbp"] = CobolNumeric.WithScale(vat, 2),
                ["recoverableGbp"] = CobolNumeric.WithScale(rec, 2),
                ["evidence"] = evidence.TrimEnd(' '),
            });
        }
        if (piv.Count == 0) None(rw, "No postponed import VAT in this period");
    }

    /// <summary>3000-PROVE (+ 3100-LEDGER-SIDE, 3200-GL-SIDE): the period's VAT against the VAT control movement.</summary>
    private static async Task<string> ProveAsync(XmdBatch b, State st, GnuReportWriter rw, bool mtd)
    {
        decimal ledger = 0, gl = 0;
        var txns = 0;
        try
        {
            var row = await b.Sql.SingleAsync(
                "SELECT CONVERT(varchar(24),ISNULL(SUM(CASE WHEN v.Direction = 'OUTPUT' THEN v.VatAmountGBP ELSE -ISNULL(v.RecoverableVatGBP," +
                "v.VatAmountGBP) END),0)), CONVERT(varchar(12),COUNT(*)) FROM fin.VatTransaction v WHERE v.CompanyCode = ? AND v.VatPeriodKey = ?",
                [SqlioParm.Text(b.Co), SqlioParm.Text(st.Key)], Widths[..2], b.Ct);
            if (row.Found)
            {
                ledger = CobolNumeric.Store(row.NumVal(0), 13, 2);
                txns = (int)CobolNumeric.Store(row.NumVal(1), 7, 0);
            }
        }
        catch (SqlioException)
        {
        }
        var accts = "";
        try
        {
            var row = await b.Sql.SingleAsync(
                "SELECT CONVERT(varchar(24),ISNULL(SUM(l.CreditAmountGBP - l.DebitAmountGBP),0)), MIN(a.AccountCode) + CASE WHEN COUNT(DISTINCT " +
                "a.AccountCode) > 1 THEN '..' + MAX(a.AccountCode) ELSE '' END FROM fin.JournalLine l JOIN fin.Journal j ON j.JournalId = " +
                "l.JournalId JOIN fin.Account a ON a.CompanyCode = l.CompanyCode AND a.AccountCode = l.AccountCode WHERE l.CompanyCode = ? AND " +
                "j.Status = 'POSTED' AND a.ControlType = 'VAT' AND CONVERT(char(8),j.PostingDate,112) BETWEEN ? AND ?",
                [SqlioParm.Text(b.Co), SqlioParm.Text(st.Start8), SqlioParm.Text(st.End8)], Widths[..2], b.Ct);
            if (row.Found)
            {
                gl = CobolNumeric.Store(row.NumVal(0), 13, 2);
                accts = CobolText.Fit(row.Raw(1), 16);
            }
        }
        catch (SqlioException)
        {
        }
        if (CobolText.IsSpaces(accts)) accts = "none posted";
        var diff = CobolNumeric.Store(ledger - gl, 13, 2);
        var status = diff > st.Tolerance || diff < 0 - st.Tolerance ? "DIFFERENCE" : "AGREED";
        if (status == "DIFFERENCE") b.Diffs++;
        rw.Generate(
            RwLine.PlusN(2, () => new string('=', 132)),
            RwLine.Plus1(() => new RwText().Lit(1, "Proof of the period against the VAT control").Lit(46, "Control accounts  ").Put(64, accts, 16).Text),
            RwLine.PlusN(2, () => new RwText().Lit(3, "Net VAT declared by the transactions of the period").Num(62, ledger, BigPic)
                .Lit(84, "entries     ").Num(96, txns, "ZZZ,ZZ9").Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Movement on the VAT control accounts in the period").Num(62, gl, BigPic).Text),
            RwLine.Plus1(() => new RwText().Lit(3, "Difference carried as unexplained").Num(62, diff, BigPic).Put(84, status, 14).Text));
        if (mtd)
        {
            var boxDiff = CobolNumeric.Store(st.PfBoxes - ledger, 13, 2);
            var boxStat = boxDiff > st.Tolerance || boxDiff < 0 - st.Tolerance ? "DIFFERENCE" : "AGREED";
            if (boxStat == "DIFFERENCE") b.Diffs++;
            rw.Generate(
                RwLine.Plus1(() => new RwText().Lit(3, "Boxes 3 less 4 as held on the return").Num(62, st.PfBoxes, BigPic).Text),
                RwLine.Plus1(() => new RwText().Lit(3, "Boxes against the transactions of the period").Num(62, boxDiff, BigPic).Put(84, boxStat, 14).Text));
        }
        string statusText;
        if (b.Diffs == 0)
        {
            statusText = "RECONCILED";
        }
        else
        {
            statusText = "UNRECONCILED";
            if (b.Rc < 4) b.Rc = 4;
        }
        b.Display("STERP-I-3001 VAT control difference " + Disp.Mon(diff) + " on " + Disp.Int(b.Diffs) + " comparison(s)");
        return statusText;
    }

    /// <summary>DE-NONE.</summary>
    private static void None(GnuReportWriter rw, string note) => rw.Generate(RwLine.Plus1(() => new RwText().Put(5, note, 60).Text));

    private static string? IsoDate(int yyyymmdd) =>
        CobolDate.FromYyyymmdd(yyyymmdd) is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
}
