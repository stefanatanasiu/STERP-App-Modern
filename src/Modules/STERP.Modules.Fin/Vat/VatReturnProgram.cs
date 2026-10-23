using System.Globalization;
using System.Text.Json.Nodes;
using Sterp.Modules.Fin.Common;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Vat;

/// <summary>One VR-DRILL entry.</summary>
public sealed record VatDrillRecord(string DocType, string DocRef, int TaxPoint, string VatCode, decimal Net, decimal Vat, decimal Amount, string Late);

/// <summary>VATRET9CA body (cobol/copy/VATRET9CA.cpy).</summary>
public sealed class VatRet9Area
{
    public string VatPeriodKey { get; set; } = "";
    public string Basis { get; set; } = "";
    public int Version { get; set; }
    public int BoxNo { get; set; }
    public string SubmissionRef { get; set; } = "";
    public string HmrcResponse { get; set; } = "";

    public long ReturnId { get; set; }
    public string VatRegNo { get; set; } = "";
    public int StartDate { get; set; }
    public int EndDate { get; set; }
    public int FilingDue { get; set; }
    public int PaymentDue { get; set; }
    public decimal Box1 { get; set; }
    public decimal Box2 { get; set; }
    public decimal Box3 { get; set; }
    public decimal Box4 { get; set; }
    public decimal Box5 { get; set; }
    public decimal Box6 { get; set; }
    public decimal Box7 { get; set; }
    public decimal Box8 { get; set; }
    public decimal Box9 { get; set; }
    public string PeriodStatus { get; set; } = "";
    public string ReturnStatus { get; set; } = "";
    public int RecordCnt { get; set; }
    public int LateCnt { get; set; }
    public string IsRepayment { get; set; } = "N";
    public List<VatDrillRecord> Drill { get; } = [];

    public decimal Box(int n) => n switch
    {
        1 => Box1, 2 => Box2, 3 => Box3, 4 => Box4, 5 => Box5, 6 => Box6, 7 => Box7, 8 => Box8, 9 => Box9, _ => 0m,
    };

    /// <summary>FinVatReturnAnswer.</summary>
    public JsonObject AnswerJson() => new()
    {
        ["vatPeriodKey"] = VatPeriodKey,
        ["basis"] = Basis,
        ["version"] = Version,
        ["returnId"] = ReturnId,
        ["vatRegNo"] = VatRegNo,
        ["startDate"] = Bank.BankRecArea.CobolDateText(StartDate),
        ["endDate"] = Bank.BankRecArea.CobolDateText(EndDate),
        ["filingDue"] = Bank.BankRecArea.CobolDateText(FilingDue),
        ["paymentDue"] = Bank.BankRecArea.CobolDateText(PaymentDue),
        ["box1"] = CobolNumeric.WithScale(Box1, 2),
        ["box2"] = CobolNumeric.WithScale(Box2, 2),
        ["box3"] = CobolNumeric.WithScale(Box3, 2),
        ["box4"] = CobolNumeric.WithScale(Box4, 2),
        ["box5"] = CobolNumeric.WithScale(Box5, 2),
        ["box6"] = (long)Box6,
        ["box7"] = (long)Box7,
        ["box8"] = (long)Box8,
        ["box9"] = (long)Box9,
        ["periodStatus"] = PeriodStatus,
        ["returnStatus"] = ReturnStatus,
        ["recordCnt"] = RecordCnt,
        ["lateCnt"] = LateCnt,
        ["isRepayment"] = IsRepayment,
    };

    public JsonArray DrillJson()
    {
        var a = new JsonArray();
        foreach (var d in Drill)
        {
            a.Add(new JsonObject
            {
                ["docType"] = d.DocType,
                ["docRef"] = d.DocRef,
                ["taxPoint"] = Bank.BankRecArea.CobolDateText(d.TaxPoint),
                ["vatCode"] = d.VatCode,
                ["net"] = CobolNumeric.WithScale(d.Net, 2),
                ["vat"] = CobolNumeric.WithScale(d.Vat, 2),
                ["amount"] = CobolNumeric.WithScale(d.Amount, 2),
                ["late"] = d.Late,
            });
        }
        return a;
    }
}

/// <summary>
/// VATRET9 (cobol/online/fin/VATRET9.cob) functions PREPARE, DRILL, FINALISE and SUBMIT of the nine-box VAT return.
/// </summary>
public sealed class VatReturnProgram(ISqlRunner sql, IFinKernel kernel)
{
    private const string Program = "VATRET9";

    private sealed class State
    {
        public bool Rejected;
        public bool Internal;
    }

    public async Task<CaHeader> RunAsync(CaRequest req, VatRet9Area vr, CancellationToken ct = default)
    {
        var ca = new CaHeader("FIN-025");
        var st = new State();
        Initialise(vr);
        await EditAsync(req, ca, vr, st);
        if (st.Rejected) return ca;
        switch (req.Function)
        {
            case "PREPARE":
                await PrepareAsync(req, ca, vr, st, ct);
                break;
            case "DRILL":
                await DrillAsync(req, ca, vr, st);
                break;
            case "FINALISE":
                await FinaliseAsync(req, ca, vr, st, ct);
                break;
            case "SUBMIT":
                await SubmitAsync(req, ca, vr, st, ct);
                break;
            default:
                Reject(ca, st, "VTR0009", "VATRET9 does not support function " + req.Function.Trim());
                break;
        }
        return ca;
    }

    // 0100-INITIALISE
    private static void Initialise(VatRet9Area vr)
    {
        vr.Box1 = vr.Box2 = vr.Box3 = vr.Box4 = vr.Box5 = vr.Box6 = vr.Box7 = vr.Box8 = vr.Box9 = 0;
        vr.RecordCnt = vr.LateCnt = 0;
        vr.Drill.Clear();
        vr.IsRepayment = "N";
        vr.ReturnStatus = vr.PeriodStatus = "";
        vr.VatPeriodKey = CobolText.Upper(vr.VatPeriodKey);
    }

    private static void Reject(CaHeader ca, State st, string reason, string message)
    {
        st.Rejected = true;
        ca.Reject(reason, message);
    }

    // 0200-EDIT-REQUEST + 0300-READ-PERIOD
    private async Task EditAsync(CaRequest req, CaHeader ca, VatRet9Area vr, State st)
    {
        if (CobolText.IsSpaces(req.Company))
        {
            Reject(ca, st, "VTR0001", "A company is required to prepare a VAT return");
            return;
        }
        if (CobolText.IsSpaces(vr.VatPeriodKey))
        {
            Reject(ca, st, "VTR0002", "A VAT return period key is required");
            return;
        }
        object?[]? row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT ISNULL(p.VatRegNo, c.VatRegNo), CONVERT(char(8), p.StartDate, 112), CONVERT(char(8), p.EndDate, 112), " +
                "CONVERT(char(8), p.FilingDueDate, 112), CONVERT(char(8), p.PaymentDueDate, 112), p.Status, " +
                "ISNULL(CAST(r.VatReturnId AS varchar(18)),'0'), ISNULL(r.Status,''), ISNULL(CAST(r.Version AS varchar(8)),'0') " +
                "FROM fin.VatPeriod p JOIN core.Company c ON c.CompanyCode = p.CompanyCode OUTER APPLY (SELECT TOP (1) x.VatReturnId, " +
                "x.Status, x.Version FROM fin.VatReturn x WHERE x.CompanyCode = p.CompanyCode AND x.VatPeriodKey = p.VatPeriodKey " +
                "ORDER BY x.Version DESC) r WHERE p.CompanyCode = @p1 AND p.VatPeriodKey = @p2",
                req.Company, vr.VatPeriodKey);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, st, ex);
            return;
        }
        if (row is null)
        {
            ca.Set(16, "VTR0003", "VAT period " + vr.VatPeriodKey.Trim() + " is not held for " + req.Company.Trim());
            st.Rejected = true;
            return;
        }
        vr.VatRegNo = Slot.Text(row[0], 15);
        vr.StartDate = Date8(row[1]);
        vr.EndDate = Date8(row[2]);
        vr.FilingDue = Date8(row[3]);
        vr.PaymentDue = Date8(row[4]);
        vr.PeriodStatus = Slot.Text(row[5], 14);
        vr.ReturnStatus = Slot.Text(row[7], 14);
        vr.ReturnId = (long)CobolNumeric.Store(Slot.NumVal(Slot.Text(row[6], 18)), 9, 0);
        if (vr.Version == 0) vr.Version = (int)(Math.Abs(Slot.NumVal(Slot.Text(row[8], 8))) % 10000);
        if (vr.Version == 0) vr.Version = 1;
    }

    private static int Date8(object? v) => (int)(Math.Abs(Slot.NumVal(Slot.Text(v, 8))) % 100_000_000);

    // 1000-PREPARE
    private async Task PrepareAsync(CaRequest req, CaHeader ca, VatRet9Area vr, State st, CancellationToken ct)
    {
        if (!await AuthorityAsync(req, ca, st, "FIN.VAT.PREPARE", ct)) return;
        await CallBoxesAsync(req, ca, vr, st);
        if (ca.Failed) return;
        if (vr.Box5 < 0) vr.IsRepayment = "Y";
        if (vr.ReturnStatus is "FINALISED" or "SUBMITTED")
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "VTR0010";
            ca.Message = "That return is already finalised. The figures sho";
            ca.MsgCount++;
            await ReadFrozenAsync(req, vr);
            return;
        }
        await SaveDraftAsync(req, ca, vr, st);
        if (ca.ReturnCode < 8)
        {
            ca.Message = "Return prepared. Box 5 is " + FinEdit.Money(vr.Box5) +
                         (vr.IsRepayment == "Y" ? " GBP, repayable by HMRC" : " GBP, payable to HMRC");
        }
    }

    // 1100-CALL-BOXES
    private async Task CallBoxesAsync(CaRequest req, CaHeader ca, VatRet9Area vr, State st)
    {
        object?[]? row;
        try
        {
            row = await sql.SingleAsync("EXEC fin.usp_VatBoxes @p1, @p2, @p3",
                req.Company, vr.VatPeriodKey, CobolText.IsSpaces(vr.Basis) ? null : vr.Basis);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, st, ex);
            return;
        }
        if (row is null)
        {
            ca.Set(12, "VTR0004", "The box aggregation returned no row");
            return;
        }
        vr.Basis = Slot.Text(row[2], 10);
        vr.Box1 = M2(row[3]);
        vr.Box2 = M2(row[4]);
        vr.Box3 = M2(row[5]);
        vr.Box4 = M2(row[6]);
        vr.Box5 = M2(row[7]);
        vr.Box6 = M0(row[8]);
        vr.Box7 = M0(row[9]);
        vr.Box8 = M0(row[10]);
        vr.Box9 = M0(row[11]);
        vr.RecordCnt = (int)(Math.Abs(Slot.Int(row[12])) % 1_000_000);
        vr.LateCnt = (int)(Math.Abs(Slot.Int(row[13])) % 1_000_000);
        var amt = CobolNumeric.Store(vr.Box1 + vr.Box2, 13, 2);
        if (amt != vr.Box3) vr.Box3 = amt;
        amt = CobolNumeric.Store(vr.Box3 - vr.Box4, 13, 2);
        if (amt != vr.Box5) vr.Box5 = amt;
    }

    private static decimal M2(object? v) => CobolNumeric.Store(Slot.Num(v), 13, 2);

    private static decimal M0(object? v) => CobolNumeric.Store(Slot.Num(v), 13, 0);

    // 1200-SAVE-DRAFT
    private async Task SaveDraftAsync(CaRequest req, CaHeader ca, VatRet9Area vr, State st)
    {
        int rows;
        try
        {
            rows = await sql.ExecAsync(
                "UPDATE fin.VatReturn SET Box1 = @p1, Box2 = @p2, Box3 = @p3, Box4 = @p4, Box5 = @p5, Box6 = @p6, Box7 = @p7, " +
                "Box8 = @p8, Box9 = @p9, PreparedBy = @p10, PreparedAt = SYSDATETIME(), VatRegNo = @p11 WHERE CompanyCode = @p12 " +
                "AND VatPeriodKey = @p13 AND Status = 'DRAFT'",
                vr.Box1, vr.Box2, vr.Box3, vr.Box4, vr.Box5, Int(vr.Box6), Int(vr.Box7), "0", "0", req.User, vr.VatRegNo,
                req.Company, vr.VatPeriodKey);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, st, ex);
            return;
        }
        if (rows > 0)
        {
            vr.ReturnStatus = "DRAFT";
            return;
        }
        try
        {
            await sql.ExecAsync(
                "INSERT fin.VatReturn (CompanyCode, VatPeriodKey, Version, VatRegNo, Box1, Box2, Box3, Box4, Box5, Box6, Box7, Box8, " +
                "Box9, Status, PreparedBy, PreparedAt) SELECT @p1, @p2, 1, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, " +
                "'DRAFT', @p13, SYSDATETIME() WHERE NOT EXISTS (SELECT 1 FROM fin.VatReturn WHERE CompanyCode = @p14 AND VatPeriodKey = @p15)",
                req.Company, vr.VatPeriodKey, vr.VatRegNo, vr.Box1, vr.Box2, vr.Box3, vr.Box4, vr.Box5, Int(vr.Box6), Int(vr.Box7),
                "0", "0", req.User, req.Company, vr.VatPeriodKey);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, st, ex);
            return;
        }
        vr.ReturnStatus = "DRAFT";
    }

    /// <summary>SQLIO-INT-EDIT of a whole-pound box.</summary>
    private static string Int(decimal v) => decimal.Truncate(v).ToString(CultureInfo.InvariantCulture);

    // 1300-READ-FROZEN (failures ignored)
    private async Task ReadFrozenAsync(CaRequest req, VatRet9Area vr)
    {
        object?[]? row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT r.CompanyCode, r.VatPeriodKey, 'FROZEN', r.Box1, r.Box2, r.Box3, r.Box4, r.Box5, r.Box6, r.Box7, r.Box8, " +
                "r.Box9, '0', '0' FROM fin.VatReturn r WHERE r.CompanyCode = @p1 AND r.VatPeriodKey = @p2 AND r.VatReturnId = @p3",
                req.Company, vr.VatPeriodKey, vr.ReturnId.ToString(CultureInfo.InvariantCulture));
        }
        catch (SqlioException)
        {
            return;
        }
        if (row is null) return;
        vr.Box1 = M2(row[3]);
        vr.Box2 = M2(row[4]);
        vr.Box3 = M2(row[5]);
        vr.Box4 = M2(row[6]);
        vr.Box5 = M2(row[7]);
        vr.Box6 = M0(row[8]);
        vr.Box7 = M0(row[9]);
        vr.Box8 = M0(row[10]);
        vr.Box9 = M0(row[11]);
    }

    // 2000-DRILL
    private async Task DrillAsync(CaRequest req, CaHeader ca, VatRet9Area vr, State st)
    {
        if (vr.BoxNo < 1 || vr.BoxNo > 9)
        {
            Reject(ca, st, "VTR0020", "A box number between 1 and 9 is required");
            return;
        }
        vr.Drill.Clear();
        List<object?[]> rows;
        try
        {
            rows = await sql.RowsAsync("EXEC fin.usp_VatBoxDrill @p1, @p2, @p3", 20,
                req.Company, vr.VatPeriodKey, vr.BoxNo.ToString(CultureInfo.InvariantCulture));
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, st, ex);
            return;
        }
        // columns: BoxNo, DocumentType, DocumentRef, PartyCode, TaxPointDate, PostingDate, VatCode, Mechanism, Direction,
        // NetAmountGBP, VatAmountGBP, ContributionGBP, IsLateEntry
        foreach (var d in rows)
        {
            vr.Drill.Add(new VatDrillRecord(
                Slot.Text(d[1], 20),
                Slot.Text(d[2], 40),
                (int)(Math.Abs(Slot.NumValOfDate(d[4], 8)) % 100_000_000),
                Slot.Text(d[6], 4),
                CobolNumeric.Store(Slot.Num(d[9]), 11, 2),
                CobolNumeric.Store(Slot.Num(d[10]), 11, 2),
                CobolNumeric.Store(Slot.Num(d[11]), 11, 2),
                d[12] is true || Convert.ToString(d[12], CultureInfo.InvariantCulture) == "1" ? "Y" : "N"));
        }
        ca.Message = "Box " + vr.BoxNo.ToString(CultureInfo.InvariantCulture) + " drills down to the listed source documents";
    }

    // 3000-FINALISE
    private async Task FinaliseAsync(CaRequest req, CaHeader ca, VatRet9Area vr, State st, CancellationToken ct)
    {
        if (!await AuthorityAsync(req, ca, st, "FIN.VAT.APPROVE", ct)) return;
        if (vr.ReturnStatus is "FINALISED" or "SUBMITTED")
        {
            Reject(ca, st, "VTR0030", "That return has already been finalised. A correct");
            return;
        }
        st.Internal = true;
        await PrepareAsync(req, ca, vr, st, ct);
        st.Internal = false;
        if (ca.ReturnCode > 4) return;
        var unit = new OwnedUnit(sql);
        await unit.BeginAsync(ct);
        await StepAsync(ca, st, () => sql.ExecAsync(
            "UPDATE fin.VatReturn SET Status = 'FINALISED', ApprovedBy = @p1, ApprovedAt = SYSDATETIME() WHERE CompanyCode = @p2 " +
            "AND VatPeriodKey = @p3 AND Status = 'DRAFT'", req.User, req.Company, vr.VatPeriodKey));
        await StepAsync(ca, st, () => sql.ExecAsync(
            "INSERT fin.VatReturnBox (VatReturnId, CompanyCode, BoxNo, BoxValue, TransactionCount, IsWholePounds, Caption) SELECT " +
            "r.VatReturnId, r.CompanyCode, b.BoxNo, b.Val, 0, CASE WHEN b.BoxNo >= 6 THEN 1 ELSE 0 END, b.Cap FROM fin.VatReturn r " +
            "CROSS APPLY (VALUES (1,r.Box1,'VAT due on sales'),(2,r.Box2,'VAT due on EU acquisitions'),(3,r.Box3,'Total VAT due')," +
            "(4,r.Box4,'VAT reclaimed on purchases'),(5,r.Box5,'Net VAT to pay or reclaim'),(6,r.Box6,'Total sales excluding VAT')," +
            "(7,r.Box7,'Total purchases excluding VAT'),(8,r.Box8,'Goods supplied to the EU'),(9,r.Box9,'Goods acquired from the EU')) " +
            "b (BoxNo, Val, Cap) WHERE r.CompanyCode = @p1 AND r.VatPeriodKey = @p2 AND NOT EXISTS (SELECT 1 FROM fin.VatReturnBox x " +
            "WHERE x.VatReturnId = r.VatReturnId)", req.Company, vr.VatPeriodKey));
        await StepAsync(ca, st, () => sql.ExecAsync(
            "INSERT fin.VatReturnDetail (VatReturnId, VatTxnId, BoxNo, ContributionGBP, IsLateEntry) SELECT r.VatReturnId, d.VatTxnId, " +
            "d.BoxNo, d.Contribution, d.IsLateEntry FROM fin.VatReturn r JOIN fin.vw_VatBoxDetail d ON d.CompanyCode = r.CompanyCode " +
            "AND d.VatPeriodKey = r.VatPeriodKey WHERE r.CompanyCode = @p1 AND r.VatPeriodKey = @p2 AND NOT EXISTS (SELECT 1 FROM " +
            "fin.VatReturnDetail x WHERE x.VatReturnId = r.VatReturnId)", req.Company, vr.VatPeriodKey));
        await StepAsync(ca, st, () => sql.ExecAsync(
            "UPDATE fin.VatPeriod SET Status = 'FINALISED', FinalisedBy = @p1, FinalisedAt = SYSDATETIME() WHERE CompanyCode = @p2 " +
            "AND VatPeriodKey = @p3", req.User, req.Company, vr.VatPeriodKey));
        if (ca.ReturnCode > 8)
        {
            await unit.RollbackAsync(ct);
            return;
        }
        await unit.CommitAsync(ct);
        vr.ReturnStatus = "FINALISED";
        vr.PeriodStatus = "FINALISED";
        await AuditAsync(req, vr, ct);
        ca.Message = "VAT return " + vr.VatPeriodKey.Trim() + " finalised. The figures and their supporting records are now frozen";
    }

    private static async Task StepAsync(CaHeader ca, State st, Func<Task<int>> statement)
    {
        try
        {
            await statement();
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, st, ex);
        }
    }

    // 4000-SUBMIT
    private async Task SubmitAsync(CaRequest req, CaHeader ca, VatRet9Area vr, State st, CancellationToken ct)
    {
        if (!await AuthorityAsync(req, ca, st, "FIN.VAT.SUBMIT", ct)) return;
        if (vr.ReturnStatus != "FINALISED")
        {
            Reject(ca, st, "VTR0040", "Only a finalised return may be submitted to HMRC");
            return;
        }
        if (CobolText.IsSpaces(vr.SubmissionRef))
        {
            Reject(ca, st, "VTR0041", "The submission receipt reference must be retained");
            return;
        }
        try
        {
            await sql.ExecAsync(
                "UPDATE fin.VatReturn SET Status = 'SUBMITTED', SubmittedAt = SYSDATETIME(), SubmissionRef = @p1, HmrcResponse = @p2 " +
                "WHERE CompanyCode = @p3 AND VatPeriodKey = @p4 AND Status = 'FINALISED'",
                vr.SubmissionRef, vr.HmrcResponse, req.Company, vr.VatPeriodKey);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, st, ex);
            return;
        }
        try
        {
            await sql.ExecAsync("UPDATE fin.VatPeriod SET Status = 'SUBMITTED', SubmissionRef = @p1 WHERE CompanyCode = @p2 AND VatPeriodKey = @p3",
                vr.SubmissionRef, req.Company, vr.VatPeriodKey);
        }
        catch (SqlioException)
        {
            // not tested by the legacy paragraph
        }
        vr.ReturnStatus = "SUBMITTED";
        await AuditAsync(req, vr, ct);
        ca.Message = "Return submitted. Receipt " + vr.SubmissionRef.Trim() + " retained";
    }

    // 8100-AUTHORITY (skipped for the internal PREPARE of FINALISE)
    private async Task<bool> AuthorityAsync(CaRequest req, CaHeader ca, State st, string permission, CancellationToken ct)
    {
        if (st.Internal) return true;
        var kh = await kernel.PermCheckAsync(req.Callee("CHECK", site: true, correlation: false), permission, ct);
        if (kh.ReturnCode <= 4) return true;
        ca.ReasonCode = kh.ReasonCode;
        ca.Message = kh.Message;
        ca.ReturnCode = kh.ReturnCode;
        st.Rejected = true;
        ca.MsgCount++;
        return false;
    }

    // 8200-AUDIT
    private Task AuditAsync(CaRequest req, VatRet9Area vr, CancellationToken ct) =>
        kernel.AuditAsync(req.Callee("WRITE", site: false, correlation: false), new AuditEntry(
            EntityType: "fin.VatReturn",
            EntityKey: vr.VatPeriodKey,
            Action: req.Function,
            FieldName: "Box 5 net VAT",
            NewValue: FinEdit.MoneyField(vr.Box5),
            Source: "COBOL"), ct);

    // 9300-SQL-FAILED (STERP-E- classification)
    private static void SqlFailed(CaHeader ca, State st, SqlioException ex)
    {
        var text = CobolText.Trimmed(ex.DriverText, 200);
        ca.Message = text.Length == 0 ? "SQL failure in " + Program : text;
        var msg = CobolText.Fit(ca.Message, 200);
        if (msg.StartsWith("STERP-E-", StringComparison.Ordinal))
        {
            ca.ReasonCode = msg.Substring(6, 6).TrimEnd();
            ca.ReturnCode = 8;
        }
        else
        {
            ca.ReasonCode = "SQL0001";
            ca.ReturnCode = 12;
        }
        st.Rejected = true;
        ca.MsgCount++;
    }
}
