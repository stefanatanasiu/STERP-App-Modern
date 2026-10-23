using System.Globalization;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Ap.Engine;

/// <summary>APPAYAPRCA body (cobol/copy/APPAYAPRCA.cpy).</summary>
public sealed class PaymentApprovalArea
{
    public string ProposalRef { get; set; } = "";
    public int ProposalId { get; set; }
    public int Version { get; set; }
    public string DocumentType { get; set; } = "";
    public string DocumentRef { get; set; } = "";
    public string ApprovalType { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public string StoredHash { get; set; } = "";
    public decimal AmountGbp { get; set; }
    public decimal SingleMaxGbp { get; set; }
    public string Currency { get; set; } = "";
    public string Approver { get; set; } = "";
    public string Decision { get; set; } = "";
    public string Comment { get; set; } = "";
    public string RequestedBy { get; set; } = "";
    public int ApprovalId { get; set; }
    public string Approver1 { get; set; } = "";
    public string Approver2 { get; set; } = "";
    public int RequiredCount { get; set; }
    public int HeldCount { get; set; }
    public string Status { get; set; } = "";
    public string Valid { get; set; } = "";
    public string NeedsFc { get; set; } = "";
    public string NeedsDirector { get; set; } = "";
    public int InvalidatedCount { get; set; }
    public string HashSource { get; set; } = "";
    public int HashInputCount { get; set; }
    public string Note { get; set; } = "";

    /// <summary>The ApPaymentApproval response object.</summary>
    public object ToData() => new
    {
        ProposalRef,
        ProposalId,
        Version,
        DocumentType,
        DocumentRef,
        ApprovalType,
        ContentHash,
        StoredHash,
        AmountGbp = CobolNumeric.WithScale(AmountGbp, 2),
        SingleMaxGbp = CobolNumeric.WithScale(SingleMaxGbp, 2),
        Currency,
        RequestedBy,
        ApprovalId,
        Approver1,
        Approver2,
        RequiredCount,
        HeldCount,
        Status,
        Valid,
        NeedsFinancialController = NeedsFc,
        NeedsDirector,
        Decision,
        Note,
    };
}

/// <summary>
/// APPAYAPR (cobol/online/ap/APPAYAPR.cob; FR-STERP-AP-035, -036, spec 4.7): REQUEST, DECIDE and VERIFY (APBACS). Statements
/// autocommit. HASH and INVALID have no production route (deferred). The second decision records Approver2 without reading
/// the decision value (G09-ADV-18, BR-APPAYAPR-021, reproduced).
/// </summary>
public sealed class ApPayApr(ISqlRunner sql, ApSvc svc)
{
    private bool _fatal;

    // WS-PROP-ROW
    private string _pRef = "";
    private string _pMethod = "";
    private string _pBank = "";
    private string _pCurrency = "";
    private string _pPayDate = "";
    private string _pOutflow = "";
    private string _pSelected = "";
    private string _pHash = "";
    private string _pStatus = "";
    private string _pPrepared = "";
    private string _pApprover1 = "";
    private string _pApprover2 = "";
    private decimal _fcLimit;
    private decimal _dirLimit;

    public async Task RunAsync(CaHeader ca, PaymentApprovalArea pa, CancellationToken ct)
    {
        // 1000-INIT
        _fatal = false;
        ca.Program = "APPAYAPR";
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "AP-012";
        pa.Note = "";
        pa.Valid = "N";
        pa.NeedsFc = "N";
        pa.NeedsDirector = "N";
        pa.InvalidatedCount = 0;
        ca.Company = CobolText.Upper(ca.Company);
        if (CobolText.IsSpaces(pa.DocumentType)) pa.DocumentType = "PAYMENTPROPOSAL";
        if (CobolText.IsSpaces(pa.ApprovalType)) pa.ApprovalType = "PAYMENTRUN";
        if (pa.RequiredCount == 0)
        {
            var a = await svc.ParmNumAsync(ca, "APR.PAYRUN.AUTHORISERS", 2, ct);
            pa.RequiredCount = (int)CobolNumeric.Store(a.NumOut, 2, 0, signed: false);
        }
        if (CobolText.IsSpaces(ca.Company))
        {
            ca.ReasonCode = "APA0001";
            ca.SetMessage("A company is required");
            Reject(ca);
        }
        if (_fatal) return;

        switch (ca.Function)
        {
            case "REQUEST": await RequestAsync(ca, pa, ct); break;
            case "DECIDE": await DecideAsync(ca, pa, ct); break;
            case "VERIFY": await VerifyAsync(ca, pa, ct); break;
            default:
                ca.ReasonCode = "APA0099";
                ca.SetMessage("APPAYAPR does not support function " + ca.Function.Trim());
                Reject(ca);
                break;
        }
    }

    // 3000-REQUEST
    private async Task RequestAsync(CaHeader ca, PaymentApprovalArea pa, CancellationToken ct)
    {
        await ReadProposalAsync(ca, pa, ct);
        if (_fatal) return;
        if (_pStatus is "APPROVED" or "RELEASED")
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "APA0011";
            ca.SetMessage("That proposal version is already approved");
            ca.MsgCount++;
            return;
        }
        pa.ContentHash = BuildHash(ca);
        await TiersAsync(ca, pa, ct);
        var amount = CobolNumeric.SqlioNumber(pa.AmountGbp);
        string[]? row;
        try
        {
            row = await ApSqlio.SingleAsync(sql, "EXEC core.usp_RequestApproval @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, NULL, @p10",
                new
                {
                    p1 = ca.Company,
                    p2 = pa.DocumentType,
                    p3 = pa.ProposalRef,
                    p4 = pa.ApprovalType,
                    p5 = pa.ContentHash,
                    p6 = _pPrepared,
                    p7 = amount,
                    p8 = "GBP",
                    p9 = amount,
                    p10 = CobolText.IsSpaces(ca.CorrelationId) ? null : ca.CorrelationId,
                }, [64, 64], ct);
        }
        catch (SqlioException ex)
        {
            _fatal = true;
            ca.ReturnCode = 8;
            ca.ReasonCode = "APA0012";
            ca.Message = CobolText.Trimmed(ex.DriverText, 200);
            ca.MsgCount++;
            return;
        }
        if (row is not null) pa.ApprovalId = ToInt(ApSqlio.NumVal(row[0]));
        await ExecUpdateAsync(ca,
            "UPDATE ap.PaymentProposal SET ContentHash = @p1, Status = 'PROPOSED', ApprovalId = NULLIF(@p2,0), ModifiedBy = @p3, " +
            "ModifiedAt = SYSDATETIME() WHERE ProposalId = @p4",
            new { p1 = pa.ContentHash, p2 = ApBankVr.IntEdit(pa.ApprovalId), p3 = ca.User, p4 = ApBankVr.IntEdit(pa.ProposalId) }, ct);
        pa.Status = "PENDING";
        ca.SetMessage("Approval requested for the exact proposal content; " + ApPayPrp.CountEdit(pa.RequiredCount) +
                      " independent authoriser(s) are required");
    }

    // 3100-TIERS
    private async Task TiersAsync(CaHeader ca, PaymentApprovalArea pa, CancellationToken ct)
    {
        _fcLimit = CobolNumeric.Store((await svc.ParmNumAsync(ca, "APR.PAYRUN.FC", 50000, ct)).NumOut, 13, 2);
        _dirLimit = CobolNumeric.Store((await svc.ParmNumAsync(ca, "APR.PAYMENT.DIRECTOR", 100000, ct)).NumOut, 13, 2);
        if (pa.AmountGbp > _fcLimit) pa.NeedsFc = "Y";
        if (pa.SingleMaxGbp > _dirLimit)
        {
            pa.NeedsDirector = "Y";
            pa.ApprovalType = "PAYMENTSINGLE";
        }
    }

    // 4000-DECIDE
    private async Task DecideAsync(CaHeader ca, PaymentApprovalArea pa, CancellationToken ct)
    {
        await ReadProposalAsync(ca, pa, ct);
        if (_fatal) return;
        var perm = await svc.PermAsync(ca, "AP.PAYMENT.APPROVE", ct);
        if (perm.Flag1 != "Y")
        {
            ca.ReturnCode = 6;
            ca.ReasonCode = "APA0013";
            ca.SetMessage(perm.Message);
            ca.MsgCount++;
            _fatal = true;
            return;
        }
        if (BuildHash(ca) != CobolText.Trimmed(_pHash, 64))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "APA0014";
            ca.SetMessage("The proposal has changed since approval was requested; a fresh version must be approved");
            ca.MsgCount++;
            _fatal = true;
            return;
        }
        if (ca.User.Trim(' ') == _pPrepared.Trim(' '))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "APA0015";
            ca.SetMessage("The preparer of a payment run cannot approve it");
            ca.MsgCount++;
            _fatal = true;
            return;
        }
        if (ca.User.Trim(' ') == _pApprover1.Trim(' '))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "APA0016";
            ca.SetMessage("The second authoriser must be a different person from the first");
            ca.MsgCount++;
            _fatal = true;
            return;
        }
        await TiersAsync(ca, pa, ct);
        if (CobolText.IsSpaces(_pApprover1)) await FirstAsync(ca, pa, ct);
        else await SecondAsync(ca, pa, ct);
    }

    // 4100-FIRST
    private async Task FirstAsync(CaHeader ca, PaymentApprovalArea pa, CancellationToken ct)
    {
        if (CobolText.IsSpaces(pa.Decision)) pa.Decision = "APPROVED";
        try
        {
            await ApSqlio.ExecAsync(sql, "EXEC core.usp_DecideApproval @p1, @p2, @p3, @p4",
                new { p1 = ApBankVr.IntEdit(pa.ApprovalId), p2 = ca.User, p3 = pa.Decision, p4 = pa.Comment }, ct);
        }
        catch (SqlioException ex)
        {
            _fatal = true;
            ca.ReturnCode = 6;
            ca.ReasonCode = "APA0017";
            ca.Message = CobolText.Trimmed(ex.DriverText, 200);
            ca.MsgCount++;
            return;
        }
        if (pa.Decision == "REJECTED")
        {
            await ExecUpdateAsync(ca, "UPDATE ap.PaymentProposal SET Status = 'CANCELLED', ModifiedBy = @p1, ModifiedAt = SYSDATETIME() WHERE ProposalId = @p2",
                new { p1 = ca.User, p2 = ApBankVr.IntEdit(pa.ProposalId) }, ct);
            pa.Status = "REJECTED";
            ca.ReturnCode = 0;
            ca.SetMessage("The payment run was rejected and cancelled");
            return;
        }
        pa.Approver1 = CobolText.Trimmed(ca.User, 20);
        pa.HeldCount = 1;
        await ExecUpdateAsync(ca,
            "UPDATE ap.PaymentProposal SET Approver1 = @p1, Approver1At = SYSDATETIME(), Status = CASE WHEN @p2 <= 1 THEN 'APPROVED' " +
            "ELSE 'PROPOSED' END, ModifiedBy = @p3, ModifiedAt = SYSDATETIME() WHERE ProposalId = @p4",
            new { p1 = ca.User, p2 = ApBankVr.IntEdit(pa.RequiredCount), p3 = ca.User, p4 = ApBankVr.IntEdit(pa.ProposalId) }, ct);
        if (pa.RequiredCount <= 1)
        {
            pa.Status = "APPROVED";
            ca.ReturnCode = 0;
            ca.SetMessage("Payment run approved");
        }
        else
        {
            pa.Status = "PENDING";
            ca.ReturnCode = 4;
            ca.ReasonCode = "APA0018";
            ca.SetMessage("First authorisation recorded; a second independent authoriser is still required");
            ca.MsgCount++;
        }
    }

    // 4200-SECOND (the decision value is not read: G09-ADV-18)
    private async Task SecondAsync(CaHeader ca, PaymentApprovalArea pa, CancellationToken ct)
    {
        if (pa.NeedsFc == "Y")
        {
            await CheckSeniorAsync(ca, pa, ct);
            if (_fatal) return;
        }
        pa.Approver2 = CobolText.Trimmed(ca.User, 20);
        pa.HeldCount = 2;
        await ExecUpdateAsync(ca,
            "UPDATE ap.PaymentProposal SET Approver2 = @p1, Approver2At = SYSDATETIME(), Status = 'APPROVED', ModifiedBy = @p2, " +
            "ModifiedAt = SYSDATETIME() WHERE ProposalId = @p3 AND Approver1 IS NOT NULL AND Approver1 <> @p4",
            new { p1 = ca.User, p2 = ca.User, p3 = ApBankVr.IntEdit(pa.ProposalId), p4 = ca.User }, ct);
        if (_fatal) return;
        pa.Status = "APPROVED";
        pa.Valid = "Y";
        ca.ReturnCode = 0;
        ca.SetMessage("Payment run approved by two independent authorisers for GBP " + ApPayPrp.MoneyEdit(pa.AmountGbp));
    }

    // 4300-CHECK-SENIOR
    private async Task CheckSeniorAsync(CaHeader ca, PaymentApprovalArea pa, CancellationToken ct)
    {
        var a = await svc.PermAsync(ca, pa.NeedsDirector == "Y" ? "SYS.APPROVE.T4" : "FIN.APPROVE.T3", ct);
        if (a.Flag1 != "Y")
        {
            _fatal = true;
            ca.ReturnCode = 6;
            ca.ReasonCode = "APA0019";
            ca.SetMessage(pa.NeedsDirector == "Y"
                ? "A single payment above the director limit requires a director to authorise it"
                : "A payment run above GBP " + ApPayPrp.MoneyEdit(_fcLimit) + " needs the financial controller or a director as one of its authorisers");
            ca.MsgCount++;
        }
    }

    // 5000-VERIFY
    private async Task VerifyAsync(CaHeader ca, PaymentApprovalArea pa, CancellationToken ct)
    {
        await ReadProposalAsync(ca, pa, ct);
        if (_fatal) return;
        var newHash = BuildHash(ca);
        pa.ContentHash = newHash;
        pa.StoredHash = CobolText.Trimmed(_pHash, 64);
        pa.Status = CobolText.Trimmed(_pStatus, 14);
        if (_pStatus != "APPROVED")
        {
            pa.Valid = "N";
            ca.ReturnCode = 6;
            ca.ReasonCode = "APA0020";
            ca.SetMessage("The proposal is " + _pStatus.Trim(' ') + " and has no current approval");
            ca.MsgCount++;
            return;
        }
        if (newHash != CobolText.Trimmed(_pHash, 64))
        {
            pa.Valid = "N";
            ca.ReturnCode = 8;
            ca.ReasonCode = "APA0021";
            ca.SetMessage("The approved content no longer matches the proposal; the approval is invalid");
            ca.MsgCount++;
            return;
        }
        if (CobolText.IsSpaces(_pApprover1) || (pa.RequiredCount > 1 && CobolText.IsSpaces(_pApprover2)))
        {
            pa.Valid = "N";
            ca.ReturnCode = 6;
            ca.ReasonCode = "APA0022";
            ca.SetMessage("The required number of independent authorisers has not signed this run");
            ca.MsgCount++;
            return;
        }
        pa.Valid = "Y";
        ca.ReturnCode = 0;
        ca.SetMessage("The approval covers this exact content");
    }

    // 7000-READ-PROPOSAL
    private async Task ReadProposalAsync(CaHeader ca, PaymentApprovalArea pa, CancellationToken ct)
    {
        var id = ApBankVr.IntEdit(pa.ProposalId);
        string[]? row;
        try
        {
            row = await ApSqlio.SingleAsync(sql,
                "SELECT TOP (1) CAST(p.ProposalId AS varchar(24)), p.ProposalRef, CAST(p.Version AS varchar(8)), p.PaymentMethod, " +
                "p.BankAccountCode, p.CurrencyCode, CONVERT(char(8), p.PayDate, 112), CAST(p.TotalOutflowGBP AS varchar(24)), " +
                "CAST((SELECT COUNT(*) FROM ap.PaymentProposalLine l WHERE l.ProposalId = p.ProposalId AND l.IsExcluded = 0) AS varchar(24)), " +
                "ISNULL(p.ContentHash,''), p.Status, ISNULL(p.PreparedBy,''), ISNULL(p.Approver1,''), ISNULL(p.Approver2,''), " +
                "CAST(ISNULL((SELECT MAX(l2.NetPayGBP) FROM ap.PaymentProposalLine l2 WHERE l2.ProposalId = p.ProposalId AND l2.IsExcluded = 0),0) " +
                "AS varchar(24)) FROM ap.PaymentProposal p WHERE p.CompanyCode = @p1 AND ((@p2 <> 0 AND p.ProposalId = @p3) OR " +
                "(@p4 <> '' AND p.ProposalRef = @p5)) ORDER BY p.Version DESC",
                new { p1 = ca.Company, p2 = id, p3 = id, p4 = pa.ProposalRef, p5 = pa.ProposalRef },
                [24, 30, 8, 10, 20, 3, 8, 24, 24, 64, 12, 20, 20, 20, 24], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        if (row is null)
        {
            ca.ReturnCode = 16;
            ca.ReasonCode = "APA0002";
            ca.SetMessage("That payment proposal was not found");
            ca.MsgCount++;
            _fatal = true;
            return;
        }
        _pRef = row[1];
        _pMethod = row[3];
        _pBank = row[4];
        _pCurrency = row[5];
        _pPayDate = row[6];
        _pOutflow = row[7];
        _pSelected = row[8];
        _pHash = row[9];
        _pStatus = ApSqlio.T(row[10]);
        _pPrepared = row[11];
        _pApprover1 = row[12];
        _pApprover2 = row[13];
        pa.ProposalId = ToInt(ApSqlio.NumVal(row[0]));
        pa.ProposalRef = ApSqlio.T(row[1]);
        pa.DocumentRef = CobolText.Trimmed(row[1], 40);
        pa.Version = (int)CobolNumeric.Store(ApSqlio.NumVal(row[2]), 4, 0, signed: false);
        pa.Currency = ApSqlio.T(row[5]);
        pa.AmountGbp = CobolNumeric.Store(ApSqlio.NumVal(row[7]), 13, 2);
        pa.SingleMaxGbp = CobolNumeric.Store(ApSqlio.NumVal(row[14]), 13, 2);
        pa.Status = CobolText.Trimmed(row[10], 14);
        pa.RequestedBy = ApSqlio.T(row[11]);
        pa.Approver1 = ApSqlio.T(row[12]);
        pa.Approver2 = ApSqlio.T(row[13]);
        pa.StoredHash = ApSqlio.T(row[9]);
        pa.HashInputCount = (int)CobolNumeric.Store(ApSqlio.NumVal(row[8]), 4, 0, signed: false);
    }

    // 7100-BUILD-HASH
    private string BuildHash(CaHeader ca)
    {
        var outflow = CobolNumeric.Store(ApSqlio.NumVal(_pOutflow), 15, 6);
        var selected = (int)CobolNumeric.Store(ApSqlio.NumVal(_pSelected), 15, 0);
        var payDate = (int)CobolNumeric.Store(ApSqlio.NumVal(_pPayDate), 8, 0, signed: false);
        return ApHash.Compute(ApPayPrp.HashText(_pRef, ca.Company, _pMethod, _pBank, _pCurrency, payDate, outflow, selected));
    }

    private async Task ExecUpdateAsync(CaHeader ca, string statement, object param, CancellationToken ct)
    {
        try
        {
            await ApSqlio.ExecAsync(sql, statement, param, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
        }
    }

    private void Reject(CaHeader ca)
    {
        _fatal = true;
        ca.ReturnCode = 8;
        ca.MsgCount++;
    }

    private void SqlFailed(CaHeader ca, SqlioException ex)
    {
        _fatal = true;
        ca.ReturnCode = 12;
        ca.ReasonCode = "SQL0001";
        var text = CobolText.Trimmed(ex.DriverText, 200);
        ca.Message = text.Length == 0 ? "SQL failure in APPAYAPR" : text;
        ca.MsgCount++;
    }

    private static int ToInt(decimal value) => (int)CobolNumeric.Store(value, 9, 0);

    internal static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
