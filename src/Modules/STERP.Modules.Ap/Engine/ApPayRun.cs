using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Ap.Engine;

/// <summary>The APPAYRUNCA fields CREATE and SUBMIT use (cobol/copy/APPAYRUNCA.cpy).</summary>
public sealed class PaymentRunArea
{
    public string ProposalRef { get; set; } = "";
    public int ProposalId { get; set; }
    public int Version { get; set; }
    public string InstructionRef { get; set; } = "";
    public string Method { get; set; } = "";
    public string BankAccount { get; set; } = "";
    public string Currency { get; set; } = "";
    public int ValueDate { get; set; }
    public string SchemeRef { get; set; } = "";
    public string BacsFileRef { get; set; } = "";
    public string Status { get; set; } = "";
    public string Authoriser1 { get; set; } = "";
    public string Authoriser2 { get; set; } = "";
    public string ReservedHeld { get; set; } = "";
    public string SettlePosted { get; set; } = "";
    public int CreatedCount { get; set; }
    public int UpdatedCount { get; set; }
    public string Note { get; set; } = "";

    /// <summary>The ApInstructionCreateResult response object.</summary>
    public object ToData() => new { ProposalRef, ProposalId, Version, CreatedCount, Note };
}

/// <summary>
/// APPAYRUN (cobol/online/ap/APPAYRUN.cob; FR-STERP-AP-037, spec 4.8) CREATE and SUBMIT (APBACS). Every statement autocommits.
/// OUTCOME/RETURN/SETTLE have no production route (deferred).
/// </summary>
public sealed class ApPayRun(ISqlRunner sql, ApSvc svc)
{
    private bool _fatal;
    private string _pStatus = "";

    public async Task RunAsync(CaHeader ca, PaymentRunArea pr, CancellationToken ct)
    {
        // 1000-INIT
        _fatal = false;
        ca.Program = "APPAYRUN";
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "AP-013";
        pr.Note = "";
        pr.CreatedCount = 0;
        pr.UpdatedCount = 0;
        pr.SettlePosted = "N";
        ca.Company = CobolText.Upper(ca.Company);
        if (CobolText.IsSpaces(ca.Company))
        {
            ca.ReasonCode = "APN0001";
            ca.SetMessage("A company is required");
            Reject(ca);
            return;
        }
        switch (ca.Function)
        {
            case "CREATE": await CreateAsync(ca, pr, ct); break;
            case "SUBMIT": await SubmitAsync(ca, pr, ct); break;
            default:
                ca.ReasonCode = "APN0099";
                ca.SetMessage("APPAYRUN does not support function " + ca.Function.Trim());
                Reject(ca);
                break;
        }
    }

    // 2000-CREATE
    private async Task CreateAsync(CaHeader ca, PaymentRunArea pr, CancellationToken ct)
    {
        var perm = await svc.PermAsync(ca, "AP.PAYMENT.RELEASE", ct);
        if (perm.Flag1 != "Y")
        {
            ca.ReturnCode = 6;
            ca.ReasonCode = "APN0002";
            ca.SetMessage(perm.Message);
            ca.MsgCount++;
            _fatal = true;
            return;
        }
        await ReadProposalAsync(ca, pr, ct);
        if (_fatal) return;
        if (_pStatus != "APPROVED")
        {
            ca.ReturnCode = 6;
            ca.ReasonCode = "APN0010";
            ca.SetMessage("The proposal is " + _pStatus.Trim(' ') + "; only an approved version may be released for payment");
            ca.MsgCount++;
            _fatal = true;
            return;
        }
        // 2100-INSERT-INSTRUCTIONS
        var rows = await ExecUpdateAsync(ca,
            "INSERT ap.PaymentInstruction (CompanyCode, InstructionRef, ProposalId, ProposalVersion, SupplierCode, PaymentMethod, " +
            "BankAccountCode, CurrencyCode, Amount, AmountGBP, FxRate, BeneficiaryName, BeneficiarySortCode, BeneficiaryAccountNumber, " +
            "BeneficiaryIban, BeneficiaryBic, SupplierBankId, CopResultSnapshot, ValueDate, Status, Authoriser1, Authoriser2, ApprovalId, " +
            "CorrelationId, CreatedBy) SELECT p.CompanyCode, p.ProposalRef + '-' + RIGHT('000' + CAST(ROW_NUMBER() OVER (ORDER BY " +
            "l.SupplierCode) AS varchar(4)), 3), p.ProposalId, p.Version, l.SupplierCode, p.PaymentMethod, p.BankAccountCode, p.CurrencyCode, " +
            "SUM(l.NetPayGBP), SUM(l.NetPayGBP), 1, MAX(b.BeneficiaryName), MAX(b.SortCode), MAX(b.AccountNumber), MAX(b.Iban), MAX(b.Bic), " +
            "MAX(b.SupplierBankId), MAX(b.CopResult), p.PayDate, 'APPROVED', p.Approver1, p.Approver2, p.ApprovalId, @p1, @p2 " +
            "FROM ap.PaymentProposalLine l JOIN ap.PaymentProposal p ON p.ProposalId = l.ProposalId JOIN ap.SupplierBank b ON " +
            "b.SupplierBankId = l.SupplierBankId WHERE l.ProposalId = @p3 AND l.IsExcluded = 0 AND NOT EXISTS (SELECT 1 FROM " +
            "ap.PaymentInstruction x WHERE x.ProposalId = l.ProposalId AND x.SupplierCode = l.SupplierCode AND x.Status <> 'CANCELLED') " +
            "GROUP BY p.CompanyCode, p.ProposalRef, p.ProposalId, p.Version, l.SupplierCode, p.PaymentMethod, p.BankAccountCode, " +
            "p.CurrencyCode, p.PayDate, p.Approver1, p.Approver2, p.ApprovalId HAVING SUM(l.NetPayGBP) > 0",
            new { p1 = ca.CorrelationId, p2 = ca.User, p3 = ApBankVr.IntEdit(pr.ProposalId) }, ct);
        pr.CreatedCount = rows < 0 ? 0 : (int)CobolNumeric.Store(rows, 6, 0, signed: false);
        if (_fatal) return;
        await ExecUpdateAsync(ca, "UPDATE ap.PaymentProposal SET Status = 'RELEASED', ModifiedBy = @p1, ModifiedAt = SYSDATETIME() WHERE ProposalId = @p2",
            new { p1 = ca.User, p2 = ApBankVr.IntEdit(pr.ProposalId) }, ct);
        ca.ReturnCode = 4;
        ca.ReasonCode = "APN0011";
        ca.SetMessage(ApPayPrp.CountEdit(pr.CreatedCount) + " instruction(s) created and approved. Producing an instruction is not payment");
        ca.MsgCount++;
    }

    // 3000-SUBMIT
    private async Task SubmitAsync(CaHeader ca, PaymentRunArea pr, CancellationToken ct)
    {
        var perm = await svc.PermAsync(ca, "AP.PAYMENT.RELEASE", ct);
        if (perm.Flag1 != "Y")
        {
            ca.ReturnCode = 6;
            ca.ReasonCode = "APN0003";
            ca.SetMessage(perm.Message);
            ca.MsgCount++;
            _fatal = true;
            return;
        }
        var id = ApBankVr.IntEdit(pr.ProposalId);
        var rows = await ExecUpdateAsync(ca,
            "UPDATE ap.PaymentInstruction SET Status = 'SUBMITTED', SubmittedAt = SYSDATETIME(), SchemeReference = COALESCE(NULLIF(@p1,''), " +
            "SchemeReference), BacsFileRef = COALESCE(NULLIF(@p2,''), BacsFileRef), ModifiedBy = @p3, ModifiedAt = SYSDATETIME() WHERE " +
            "CompanyCode = @p4 AND Status = 'APPROVED' AND ((@p5 <> 0 AND ProposalId = @p6) OR (@p7 <> '' AND InstructionRef = @p8))",
            new
            {
                p1 = pr.SchemeRef,
                p2 = pr.BacsFileRef,
                p3 = ca.User,
                p4 = ca.Company,
                p5 = id,
                p6 = id,
                p7 = pr.InstructionRef,
                p8 = pr.InstructionRef,
            }, ct);
        if (_fatal) return;
        pr.UpdatedCount = (int)CobolNumeric.Store(rows, 6, 0, signed: false);
        pr.Status = "SUBMITTED";
        pr.ReservedHeld = "Y";
        ca.ReturnCode = 4;
        ca.ReasonCode = "APN0012";
        ca.SetMessage(ApPayPrp.CountEdit(pr.UpdatedCount) + " instruction(s) submitted. Submission reserves the amount; it is not proof of settlement");
        ca.MsgCount++;
    }

    // 7000-READ-PROPOSAL
    private async Task ReadProposalAsync(CaHeader ca, PaymentRunArea pr, CancellationToken ct)
    {
        var id = ApBankVr.IntEdit(pr.ProposalId);
        string[]? row;
        try
        {
            row = await ApSqlio.SingleAsync(sql,
                "SELECT TOP (1) p.ProposalRef, p.Status, CAST(p.Version AS varchar(8)), p.PaymentMethod, p.BankAccountCode, p.CurrencyCode, " +
                "CONVERT(char(8), p.PayDate, 112), ISNULL(p.Approver1,''), ISNULL(p.Approver2,''), CAST(ISNULL(p.ApprovalId,0) AS varchar(24)) " +
                "FROM ap.PaymentProposal p WHERE p.CompanyCode = @p1 AND ((@p2 <> 0 AND p.ProposalId = @p3) OR (@p4 <> '' AND " +
                "p.ProposalRef = @p5)) ORDER BY p.Version DESC",
                new { p1 = ca.Company, p2 = id, p3 = id, p4 = pr.ProposalRef, p5 = pr.ProposalRef }, [30, 12, 8, 10, 20, 3, 8, 20, 20, 24], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        if (row is null)
        {
            ca.ReturnCode = 16;
            ca.ReasonCode = "APN0006";
            ca.SetMessage("That payment proposal was not found");
            ca.MsgCount++;
            _fatal = true;
            return;
        }
        _pStatus = ApSqlio.T(row[1]);
        pr.ProposalRef = ApSqlio.T(row[0]);
        pr.Method = ApSqlio.T(row[3]);
        pr.BankAccount = ApSqlio.T(row[4]);
        pr.Currency = ApSqlio.T(row[5]);
        pr.Authoriser1 = ApSqlio.T(row[7]);
        pr.Authoriser2 = ApSqlio.T(row[8]);
        pr.Version = (int)CobolNumeric.Store(ApSqlio.NumVal(row[2]), 4, 0, signed: false);
        pr.ValueDate = (int)CobolNumeric.Store(ApSqlio.NumVal(row[6]), 8, 0, signed: false);
    }

    // 8500-EXEC-UPDATE: returns SQLIO-ROWCOUNT (0 after a failure)
    private async Task<int> ExecUpdateAsync(CaHeader ca, string statement, object param, CancellationToken ct)
    {
        try
        {
            return await ApSqlio.ExecAsync(sql, statement, param, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return 0;
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
        ca.Message = text.Length == 0 ? "SQL failure in APPAYRUN" : text;
        ca.MsgCount++;
    }
}
