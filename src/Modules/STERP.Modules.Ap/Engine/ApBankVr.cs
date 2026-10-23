using System.Globalization;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Ap.Engine;

/// <summary>
/// APBANKVR (cobol/online/ap/APBANKVR.cob; FR-STERP-AP-014..017, spec 4.2): PREPARE, COP, CALLBACK and APPROVE of a supplier
/// payment destination. No unit of work: every statement autocommits on the engine connection, AUDITLOG commits after it.
/// CHECK is deferred (no production route).
/// </summary>
public sealed class ApBankVr(ISqlRunner sql, ApSvc svc)
{
    private const string ReadBankSql =
        "SELECT TOP (1) CAST(SupplierBankId AS varchar(12)), BeneficiaryName, CountryCode, CurrencyCode, ISNULL(SortCode,''), " +
        "ISNULL(AccountNumber,''), ISNULL(Iban,''), ISNULL(Bic,''), ISNULL(BankName,''), PaymentMethod, ISNULL(CopResult,''), " +
        "ISNULL(CopDecision,''), ISNULL(CONVERT(char(10), CallBackVerifiedAt, 126),''), ISNULL(CallBackVerifiedBy,''), " +
        "ISNULL(CallBackContactSource,''), Status, ISNULL(PreparedBy,''), ISNULL(ApprovedBy,''), " +
        "CAST(ISNULL(SupersedesBankId,0) AS varchar(12)), ISNULL(ChangeRequestSource,'') FROM ap.SupplierBank " +
        "WHERE CompanyCode = @p1 AND SupplierCode = @p2 AND (@p3 = 0 OR SupplierBankId = @p4) AND (@p5 = '' OR BankRef = @p6) " +
        "ORDER BY CASE Status WHEN 'PENDING' THEN 1 WHEN 'VERIFIED' THEN 2 WHEN 'APPROVED' THEN 3 ELSE 4 END, SupplierBankId DESC";

    private static readonly int[] ReadBankWidths = [12, 120, 2, 3, 6, 8, 34, 11, 60, 10, 12, 200, 10, 20, 120, 12, 20, 20, 12, 30];

    private bool _fatal;
    private bool _found;

    // WS-BANK-ROW values the program tests after 7000-READ-BANK
    private string _kCop = "";
    private string _kStatus = "";
    private string _kPrepared = "";

    public async Task RunAsync(CaHeader ca, BankVerificationArea bv, CancellationToken ct)
    {
        // 1000-INIT
        _fatal = false;
        _found = false;
        ca.Program = "APBANKVR";
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "AP-027";
        bv.Note = "";
        bv.Usable = "N";
        bv.CopDone = "N";
        bv.CallbackDone = "N";
        bv.InvalidatedCount = 0;
        bv.UnsubmittedCount = 0;
        bv.Supplier = CobolText.Upper(bv.Supplier);
        bv.BankRef = CobolText.Upper(bv.BankRef);
        ca.Company = CobolText.Upper(ca.Company);
        if (CobolText.IsSpaces(bv.BankRef)) bv.BankRef = "MAIN";

        // 1500-VALIDATE
        if (CobolText.IsSpaces(ca.Company))
        {
            ca.ReasonCode = "APB0001";
            ca.SetMessage("A company is required");
            Reject(ca, bv);
            return;
        }
        if (CobolText.IsSpaces(bv.Supplier))
        {
            ca.ReasonCode = "APB0002";
            ca.SetMessage("A supplier account code is required");
            Reject(ca, bv);
            return;
        }

        switch (ca.Function)
        {
            case "PREPARE": await PrepareAsync(ca, bv, ct); break;
            case "COP": await CopAsync(ca, bv, ct); break;
            case "CALLBACK": await CallbackAsync(ca, bv, ct); break;
            case "APPROVE": await ApproveAsync(ca, bv, ct); break;
            default:
                ca.ReasonCode = "APB0099";
                ca.SetMessage("APBANKVR does not support function " + ca.Function.Trim());
                Reject(ca, bv);
                break;
        }
    }

    // ------------------------------------------------------------------ 2000-PREPARE
    private async Task PrepareAsync(CaHeader ca, BankVerificationArea bv, CancellationToken ct)
    {
        await CheckPermAsync(ca, "AP.BANK.MAINT", ct);
        if (_fatal) return;
        EditDetails(ca, bv);
        if (_fatal) return;
        await FindCurrentAsync(ca, bv, ct);
        if (_fatal) return;
        await InsertRowAsync(ca, bv, ct);
        if (_fatal) return;
        // 2400-AUDIT-PREPARE
        var a = ApSvcArea.For(ca, "AUDIT");
        a.AudEntity = "ap.SupplierBank";
        a.AudKey = CobolText.Trimmed(bv.Supplier.Trim() + "/" + bv.BankRef.Trim(), 60);
        a.AudAction = "PREPARE";
        a.AudField = "BankDetails";
        a.AudReason = bv.RequestSource;
        a.AudSens = "Y";
        await svc.CallAsync(a, ct);
        bv.Status = "PENDING";
        bv.Usable = "N";
        ca.ReturnCode = 4;
        ca.ReasonCode = "APB0010";
        ca.SetMessage("Destination recorded as pending. Confirmation of Payee, an independent call-back and approval are required before it may be paid");
        ca.MsgCount++;
    }

    // 2100-EDIT-DETAILS
    private void EditDetails(CaHeader ca, BankVerificationArea bv)
    {
        if (CobolText.IsSpaces(bv.Beneficiary))
        {
            ca.ReasonCode = "APB0003";
            ca.SetMessage("The beneficiary name exactly as held by the bank is required");
            Reject(ca, bv);
            return;
        }
        if (CobolText.IsSpaces(bv.Country)) bv.Country = "GB";
        if (CobolText.IsSpaces(bv.Currency)) bv.Currency = "GBP";
        if (bv.Country == "GB")
        {
            // 2110-EDIT-UK
            if (CountDigits(bv.SortCode, 6) != 6)
            {
                ca.ReasonCode = "APB0004";
                ca.SetMessage("A United Kingdom sort code must be six digits");
                Reject(ca, bv);
                return;
            }
            if (CountDigits(bv.AccountNo, 8) != 8)
            {
                ca.ReasonCode = "APB0005";
                ca.SetMessage("A United Kingdom account number must be eight digits");
                Reject(ca, bv);
            }
        }
        else
        {
            // 2120-EDIT-INTL
            if (CobolText.IsSpaces(bv.Iban))
            {
                ca.ReasonCode = "APB0006";
                ca.SetMessage("An IBAN is required for an international payment destination");
                Reject(ca, bv);
                return;
            }
            if (CobolText.IsSpaces(bv.Bic))
            {
                ca.ReasonCode = "APB0007";
                ca.SetMessage("A BIC is required for an international payment destination");
                Reject(ca, bv);
            }
        }
    }

    // 2130-COUNT-DIGITS over the first n positions of the field
    private static int CountDigits(string value, int n)
    {
        var text = CobolText.Fit(value, 300);
        var digits = 0;
        for (var i = 0; i < n; i++)
        {
            if (text[i] is >= '0' and <= '9') digits++;
        }
        return digits;
    }

    // 2200-FIND-CURRENT
    private async Task FindCurrentAsync(CaHeader ca, BankVerificationArea bv, CancellationToken ct)
    {
        string[]? row;
        try
        {
            row = await ApSqlio.SingleAsync(sql,
                "SELECT TOP (1) CAST(SupplierBankId AS varchar(12)) FROM ap.SupplierBank WHERE CompanyCode = @p1 AND SupplierCode = @p2 " +
                "AND Status = 'APPROVED' AND (ValidTo IS NULL OR ValidTo >= CAST(SYSDATETIME() AS date)) ORDER BY SupplierBankId DESC",
                new { p1 = ca.Company, p2 = bv.Supplier }, [12], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        bv.SupersedesId = row is null ? 0 : ToInt(ApSqlio.NumVal(row[0]));
    }

    // 2300-INSERT-ROW
    private async Task InsertRowAsync(CaHeader ca, BankVerificationArea bv, CancellationToken ct)
    {
        if (bv.ValidFrom == 0) bv.ValidFrom = ca.BusinessDate;
        try
        {
            await ApSqlio.ExecAsync(sql, 
                "INSERT ap.SupplierBank (CompanyCode, SupplierCode, BankRef, BeneficiaryName, CountryCode, CurrencyCode, SortCode, " +
                "AccountNumber, RollNumber, Iban, Bic, BankName, PaymentMethod, ValidFrom, Status, PreparedBy, SupersedesBankId, " +
                "ChangeRequestSource, IsSensitive, CreatedBy) SELECT @p1, @p2, @p3, @p4, @p5, @p6, NULLIF(@p7,''), NULLIF(@p8,''), " +
                "NULLIF(@p9,''), NULLIF(@p10,''), NULLIF(@p11,''), NULLIF(@p12,''), @p13, CAST(@p14 AS date), 'PENDING', @p15, " +
                "NULLIF(@p16,0), @p17, 1, @p18",
                new
                {
                    p1 = ca.Company,
                    p2 = bv.Supplier,
                    p3 = bv.BankRef,
                    p4 = bv.Beneficiary,
                    p5 = bv.Country,
                    p6 = bv.Currency,
                    p7 = bv.SortCode,
                    p8 = bv.AccountNo,
                    p9 = bv.RollNumber,
                    p10 = bv.Iban,
                    p11 = bv.Bic,
                    p12 = bv.BankName,
                    p13 = CobolText.IsSpaces(bv.PayMethod) ? "BACS" : bv.PayMethod,
                    p14 = IntEdit(bv.ValidFrom),
                    p15 = ca.User,
                    p16 = IntEdit(bv.SupersedesId),
                    p17 = bv.RequestSource,
                    p18 = ca.User,
                }, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        await ReadBankAsync(ca, bv, ct);
    }

    // ------------------------------------------------------------------ 3000-COP
    private async Task CopAsync(CaHeader ca, BankVerificationArea bv, CancellationToken ct)
    {
        await CheckPermAsync(ca, "AP.BANK.VERIFY", ct);
        if (_fatal) return;
        var inResult = bv.CopResult;
        var inDecision = bv.CopDecision;
        await ReadBankAsync(ca, bv, ct);
        bv.CopResult = inResult;
        bv.CopDecision = inDecision;
        if (_fatal || !_found) return;
        if (bv.CopResult is not ("MATCH" or "CLOSEMATCH" or "NOMATCH" or "UNAVAILABLE"))
        {
            ca.ReasonCode = "APB0011";
            ca.SetMessage("The Confirmation of Payee result must be MATCH, CLOSEMATCH, NOMATCH or UNAVAILABLE");
            Reject(ca, bv);
            return;
        }
        if (bv.CopResult != "MATCH" && CobolText.IsSpaces(bv.CopDecision))
        {
            ca.ReturnCode = 6;
            ca.ReasonCode = "APB0012";
            ca.SetMessage("A " + bv.CopResult.Trim() + " Confirmation of Payee result needs an authorised decision before the destination may be used");
            ca.MsgCount++;
            _fatal = true;
            return;
        }
        await ExecUpdateAsync(ca,
            "UPDATE ap.SupplierBank SET CopResult = @p1, CopCheckedAt = SYSDATETIME(), CopReference = @p2, CopDecision = NULLIF(@p3,''), " +
            "ModifiedBy = @p4, ModifiedAt = SYSDATETIME() WHERE SupplierBankId = @p5",
            new { p1 = bv.CopResult, p2 = bv.CopReference, p3 = bv.CopDecision, p4 = ca.User, p5 = IntEdit(bv.BankId) }, ct);
        if (_fatal) return;
        bv.CopDone = "Y";
        ca.ReturnCode = 0;
        ca.SetMessage("Confirmation of Payee result " + bv.CopResult.Trim() + " recorded");
        var a = ApSvcArea.For(ca, "AUDIT");
        a.AudEntity = "ap.SupplierBank";
        a.AudKey = IntEdit(bv.BankId);
        a.AudAction = "COPCHECK";
        a.AudField = "CopResult";
        a.AudNew = bv.CopResult;
        a.AudReason = bv.CopDecision;
        a.AudAuth = CobolText.Trimmed(bv.CopReference, 40);
        a.AudSens = "Y";
        await svc.CallAsync(a, ct);
    }

    // ------------------------------------------------------------------ 4000-CALLBACK
    private async Task CallbackAsync(CaHeader ca, BankVerificationArea bv, CancellationToken ct)
    {
        await CheckPermAsync(ca, "AP.BANK.VERIFY", ct);
        if (_fatal) return;
        var inSource = bv.CallbackSource;
        await ReadBankAsync(ca, bv, ct);
        bv.CallbackSource = inSource;
        if (_fatal || !_found) return;
        if (CobolText.IsSpaces(bv.CallbackSource))
        {
            ca.ReasonCode = "APB0013";
            ca.SetMessage("The call-back must say which contact already on record was telephoned");
            Reject(ca, bv);
            return;
        }
        if (bv.CallbackOnFile != "Y")
        {
            ca.ReasonCode = "APB0014";
            ca.SetMessage("The call-back was not made to a contact already held on record, so it cannot verify this change");
            Reject(ca, bv);
            return;
        }
        if (CobolText.IsSpaces(_kCop))
        {
            ca.ReturnCode = 6;
            ca.ReasonCode = "APB0015";
            ca.SetMessage("Record the Confirmation of Payee result before the call-back verification");
            ca.MsgCount++;
            _fatal = true;
            return;
        }
        await ExecUpdateAsync(ca,
            "UPDATE ap.SupplierBank SET CallBackVerifiedAt = SYSDATETIME(), CallBackVerifiedBy = @p1, CallBackContactSource = @p2, " +
            "CallBackNote = NULLIF(@p3,''), Status = 'VERIFIED', ModifiedBy = @p4, ModifiedAt = SYSDATETIME() WHERE SupplierBankId = @p5 " +
            "AND Status = 'PENDING'",
            new { p1 = ca.User, p2 = bv.CallbackSource, p3 = bv.CallbackNote, p4 = ca.User, p5 = IntEdit(bv.BankId) }, ct);
        if (_fatal) return;
        bv.CallbackDone = "Y";
        bv.Status = "VERIFIED";
        ca.ReturnCode = 0;
        ca.SetMessage("Independent call-back recorded; the change now needs approval by someone other than the preparer");
        var a = ApSvcArea.For(ca, "AUDIT");
        a.AudEntity = "ap.SupplierBank";
        a.AudKey = IntEdit(bv.BankId);
        a.AudAction = "CALLBACK";
        a.AudField = "CallBackVerifiedBy";
        a.AudNew = ca.User;
        a.AudReason = bv.CallbackSource;
        a.AudSens = "Y";
        await svc.CallAsync(a, ct);
    }

    // ------------------------------------------------------------------ 5000-APPROVE
    private async Task ApproveAsync(CaHeader ca, BankVerificationArea bv, CancellationToken ct)
    {
        await CheckPermAsync(ca, "AP.BANK.APPROVE", ct);
        if (_fatal) return;
        await ReadBankAsync(ca, bv, ct);
        if (_fatal || !_found) return;
        if (_kStatus != "VERIFIED")
        {
            ca.ReturnCode = 6;
            ca.ReasonCode = "APB0016";
            ca.SetMessage("The destination has not been independently verified, so it cannot be approved");
            ca.MsgCount++;
            return;
        }
        if (_kPrepared.Trim(' ') == ca.User.Trim(' '))
        {
            ca.ReasonCode = "APB0017";
            ca.SetMessage("The preparer of a bank detail change cannot approve it; an independent approver is required");
            Reject(ca, bv);
            return;
        }
        await RequestApprovalAsync(ca, bv, ct);
        if (_fatal) return;
        await DecideAsync(ca, bv, ct);
        if (_fatal) return;
        if (bv.Decision == "REJECTED")
        {
            await MarkRejectedAsync(ca, bv, ct);
            return;
        }
        await MarkApprovedAsync(ca, bv, ct);
        if (_fatal) return;
        await InvalidateAsync(ca, bv, ct);
        bv.Status = "APPROVED";
        bv.Usable = "Y";
        if (bv.InvalidatedCount > 0)
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "APB0018";
            ca.SetMessage("Destination approved. " + bv.InvalidatedCount.ToString(CultureInfo.InvariantCulture) +
                " payment approval(s) relying on the old details were invalidated and must be re-approved");
            ca.MsgCount++;
        }
        else
        {
            ca.ReturnCode = 0;
            ca.SetMessage("Destination approved and available for payment");
        }
    }

    // 5100-REQUEST-APPROVAL
    private async Task RequestApprovalAsync(CaHeader ca, BankVerificationArea bv, CancellationToken ct)
    {
        // 7100-BUILD-HASH
        var hashText = bv.Supplier.Trim(' ') + "|" + bv.BankRef.Trim(' ') + "|" + bv.Beneficiary.Trim(' ') + "|" + bv.SortCode.Trim(' ') + "|" +
                       bv.AccountNo.Trim(' ') + "|" + bv.Iban.Trim(' ') + "|" + bv.Currency.Trim(' ');
        bv.ContentHash = ApHash.Compute(CobolText.Fit(hashText, 300));
        string[]? row;
        try
        {
            row = await ApSqlio.SingleAsync(sql,
                "EXEC core.usp_RequestApproval @p1, 'SUPPLIERBANK', @p2, 'PAYEEBANK', @p3, @p4, NULL, 'GBP', NULL, NULL, @p5",
                new
                {
                    p1 = ca.Company,
                    p2 = CobolText.Trimmed(bv.Supplier.Trim(' ') + "/" + IntEdit(bv.BankId), 256),
                    p3 = bv.ContentHash,
                    p4 = _kPrepared,
                    p5 = CobolText.IsSpaces(ca.CorrelationId) ? null : ca.CorrelationId,
                }, [64, 64], ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        if (row is not null) bv.ApprovalId = ToInt(ApSqlio.NumVal(row[0]));
    }

    // 5200-DECIDE
    private async Task DecideAsync(CaHeader ca, BankVerificationArea bv, CancellationToken ct)
    {
        if (CobolText.IsSpaces(bv.Decision)) bv.Decision = "APPROVED";
        try
        {
            await ApSqlio.ExecAsync(sql, "EXEC core.usp_DecideApproval @p1, @p2, @p3, @p4",
                new { p1 = IntEdit(bv.ApprovalId), p2 = ca.User, p3 = bv.Decision, p4 = bv.CopDecision }, ct);
        }
        catch (SqlioException ex)
        {
            _fatal = true;
            ca.ReturnCode = 6;
            ca.ReasonCode = "APB0019";
            ca.Message = CobolText.Trimmed(ex.DriverText, 200);
            ca.MsgCount++;
        }
    }

    // 5300-MARK-REJECTED (an update failure is overwritten by the RC 0 answer)
    private async Task MarkRejectedAsync(CaHeader ca, BankVerificationArea bv, CancellationToken ct)
    {
        await ExecUpdateAsync(ca,
            "UPDATE ap.SupplierBank SET Status = 'REJECTED', ApprovalId = NULLIF(@p1,0), ModifiedBy = @p2, ModifiedAt = SYSDATETIME() " +
            "WHERE SupplierBankId = @p3",
            new { p1 = IntEdit(bv.ApprovalId), p2 = ca.User, p3 = IntEdit(bv.BankId) }, ct);
        bv.Status = "REJECTED";
        bv.Usable = "N";
        ca.ReturnCode = 0;
        ca.SetMessage("The bank detail change was rejected; the previously approved destination remains in force");
    }

    // 5400-MARK-APPROVED
    private async Task MarkApprovedAsync(CaHeader ca, BankVerificationArea bv, CancellationToken ct)
    {
        await ExecUpdateAsync(ca,
            "UPDATE ap.SupplierBank SET Status = 'APPROVED', ApprovedBy = @p1, ApprovedAt = SYSDATETIME(), ApprovalId = NULLIF(@p2,0), " +
            "ModifiedBy = @p3, ModifiedAt = SYSDATETIME() WHERE SupplierBankId = @p4",
            new { p1 = ca.User, p2 = IntEdit(bv.ApprovalId), p3 = ca.User, p4 = IntEdit(bv.BankId) }, ct);
        if (_fatal) return;
        if (bv.SupersedesId > 0)
        {
            await ExecUpdateAsync(ca,
                "UPDATE ap.SupplierBank SET Status = 'SUPERSEDED', ValidTo = CAST(@p1 AS date), ModifiedBy = @p2, ModifiedAt = SYSDATETIME() " +
                "WHERE SupplierBankId = @p3",
                new { p1 = IntEdit(ca.BusinessDate), p2 = ca.User, p3 = IntEdit(bv.SupersedesId) }, ct);
        }
        var a = ApSvcArea.For(ca, "AUDIT");
        a.AudEntity = "ap.SupplierBank";
        a.AudKey = IntEdit(bv.BankId);
        a.AudAction = "APPROVE";
        a.AudField = "Status";
        a.AudOld = "VERIFIED";
        a.AudNew = "APPROVED";
        a.AudReason = _kCop;
        a.AudAuth = CobolText.Trimmed(bv.ContentHash, 40);
        a.AudSens = "Y";
        await svc.CallAsync(a, ct);
    }

    // 5500-INVALIDATE (failure ignored)
    private async Task InvalidateAsync(CaHeader ca, BankVerificationArea bv, CancellationToken ct)
    {
        string[]? row;
        try
        {
            row = await ApSqlio.SingleAsync(sql, "EXEC ap.usp_InvalidatePaymentApprovals @p1, @p2, @p3, @p4",
                new { p1 = ca.Company, p2 = bv.Supplier, p3 = ca.User, p4 = "Payee bank details changed and approved" }, [64, 64], ct);
        }
        catch (SqlioException)
        {
            bv.InvalidatedCount = 0;
            return;
        }
        if (row is null) return;
        bv.InvalidatedCount = (int)CobolNumeric.Store(ApSqlio.NumVal(row[0]), 5, 0, signed: false);
        bv.UnsubmittedCount = (int)CobolNumeric.Store(ApSqlio.NumVal(row[1]), 5, 0, signed: false);
    }

    // ------------------------------------------------------------------ 7000-READ-BANK
    private async Task ReadBankAsync(CaHeader ca, BankVerificationArea bv, CancellationToken ct)
    {
        _found = false;
        var id = IntEdit(bv.BankId);
        var byRef = bv.BankId > 0 ? "" : bv.BankRef;
        string[]? row;
        try
        {
            row = await ApSqlio.SingleAsync(sql, ReadBankSql,
                new { p1 = ca.Company, p2 = bv.Supplier, p3 = id, p4 = id, p5 = byRef, p6 = byRef }, ReadBankWidths, ct);
        }
        catch (SqlioException ex)
        {
            SqlFailed(ca, ex);
            return;
        }
        if (row is null) return;
        _found = true;
        bv.BankId = ToInt(ApSqlio.NumVal(row[0]));
        bv.Beneficiary = ApSqlio.T(row[1]);
        bv.Country = ApSqlio.T(row[2]);
        bv.Currency = ApSqlio.T(row[3]);
        bv.SortCode = ApSqlio.T(row[4]);
        bv.AccountNo = ApSqlio.T(row[5]);
        bv.Iban = ApSqlio.T(row[6]);
        bv.Bic = ApSqlio.T(row[7]);
        bv.BankName = ApSqlio.T(row[8]);
        bv.PayMethod = ApSqlio.T(row[9]);
        bv.CopResult = ApSqlio.T(row[10]);
        bv.CopDecision = ApSqlio.T(row[11]);
        bv.CallbackBy = ApSqlio.T(row[13]);
        bv.CallbackSource = ApSqlio.T(row[14]);
        bv.Status = ApSqlio.T(row[15]);
        bv.PreparedBy = ApSqlio.T(row[16]);
        bv.Approver = ApSqlio.T(row[17]);
        bv.RequestSource = ApSqlio.T(row[19]);
        bv.SupersedesId = ToInt(ApSqlio.NumVal(row[18]));
        _kCop = ApSqlio.T(row[10]);
        _kStatus = ApSqlio.T(row[15]);
        _kPrepared = ApSqlio.T(row[16]);
        if (!CobolText.IsSpaces(_kCop)) bv.CopDone = "Y";
        if (!CobolText.IsSpaces(ApSqlio.T(row[13]))) bv.CallbackDone = "Y";
        if (_kStatus == "APPROVED") bv.Usable = "Y";
    }

    // 8000-CHECK-PERM
    private async Task CheckPermAsync(CaHeader ca, string permission, CancellationToken ct)
    {
        var a = await svc.PermAsync(ca, permission, ct);
        if (a.Flag1 != "Y")
        {
            _fatal = true;
            ca.ReturnCode = 6;
            ca.ReasonCode = "APB0022";
            ca.SetMessage(a.Message);
            ca.MsgCount++;
        }
    }

    // 8500-EXEC-UPDATE
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

    // 9000-REJECT
    private void Reject(CaHeader ca, BankVerificationArea bv)
    {
        _fatal = true;
        ca.ReturnCode = 8;
        bv.Usable = "N";
        ca.MsgCount++;
    }

    // 9300-SQL-FAILED
    private void SqlFailed(CaHeader ca, SqlioException ex)
    {
        _fatal = true;
        ca.ReturnCode = 12;
        ca.ReasonCode = "SQL0001";
        var text = CobolText.Trimmed(ex.DriverText, 200);
        ca.Message = text.Length == 0 ? "SQL failure in APBANKVR" : text;
        ca.MsgCount++;
    }

    /// <summary>SQLIO-INT-EDIT -(17)9 trimmed.</summary>
    internal static string IntEdit(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static int ToInt(decimal value) => (int)CobolNumeric.Store(value, 9, 0);
}
