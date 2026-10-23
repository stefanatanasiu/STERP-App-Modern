using Sterp.Modules.Ar.Contracts;
using Sterp.Modules.Ar.Domain;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Application;

/// <summary>
/// ARDISPUT (cobol/online/ar/ARDISPUT.cob) - disputes and collection. DISPUTE (own unit) and PROMISE (autocommit per
/// statement) are the functions with a production invoker; ACTION, RESOLVE and REVIEW have none and are not delivered in
/// this wave (STERP-AR.md section 1.5) - they answer as an unsupported function.
/// </summary>
public sealed class DisputeEngine(
    ISqlRunner sql,
    IPermissionService permchk,
    IDocumentNumberService numgen,
    IBusinessCalendar datecalc,
    IAuditWriter auditlog)
{
    private const string Program = "ARDISPUT";

    private sealed class Ws
    {
        public required ArCtl Ctl;
        public required ArdisputArea Ds;
        public bool TranOpen;
        public bool ItemFound;
        public bool CaseFound;
        public decimal Resid;
        public int Seq;
    }

    public async Task<LegacyResult> CallAsync(ArdisputArea ds, CancellationToken ct = default)
    {
        var w = new Ws { Ctl = ArCtl.Start(ds.Header, Program, "AR-015"), Ds = ds };
        var c = w.Ctl;
        Initialise(w);
        Validate(w);
        if (!c.InputBad)
        {
            await ReadCustomerAsync(w, ct);
            if (!c.RcBad) await ReadItemAsync(w, ct);
            if (!c.RcBad) await FindCaseAsync(w, ct);
            if (!c.RcBad)
            {
                switch (c.Func)
                {
                    case "DISPUTE":
                        await DisputeAsync(w, ct);
                        break;
                    case "PROMISE":
                        await PromiseAsync(w, ct);
                        break;
                    default:
                        c.Rc = 8;
                        c.Reason = "DSP0009";
                        c.Msg = "ARDISPUT does not support function [" + CobolText.Trim(c.Func) + "]";
                        c.MsgCnt++;
                        break;
                }
            }
        }
        c.SetResult(ds.Header);
        return ds.Header.Outcome();
    }

    private static void Initialise(Ws w)
    {
        var ds = w.Ds;
        var c = w.Ctl;
        ds.CaseType = CobolText.Upper(ds.CaseType);
        ds.ActionType = CobolText.Upper(ds.ActionType);
        if (CobolText.IsSpaces(ds.CaseType)) ds.CaseType = "DISPUTE";
        if (ds.ActionDate == 0) ds.ActionDate = c.BusDate;
        if (CobolText.IsSpaces(ds.OwnerUser)) ds.OwnerUser = c.UserId;
        ds.BrokenCount = 0;
        ds.SuppressReason = "";
    }

    private static void Validate(Ws w)
    {
        var c = w.Ctl;
        var ds = w.Ds;
        if (CobolText.IsSpaces(c.Co))
        {
            c.Reject("DSP0001", "A company code is required");
            return;
        }
        if (CobolText.IsSpaces(ds.Customer) && CobolText.IsSpaces(ds.CaseRef) && ds.CaseId == 0)
        {
            c.Reject("DSP0002", "A customer account or an existing case reference is required");
            return;
        }
        if (ds.CaseType is not ("DISPUTE" or "COLLECTION" or "INSOLVENCY" or "LEGAL"))
        {
            c.Reject("DSP0003", "Case type [" + CobolText.Trim(ds.CaseType) + "] is not DISPUTE, COLLECTION, INSOLVENCY or LEGAL");
            return;
        }
        if (c.Func == "DISPUTE" && (CobolText.IsSpaces(ds.ReasonCode) || CobolText.IsSpaces(ds.Reason)))
        {
            c.Reject("DSP0004", "A dispute needs a reason code and the evidence the customer has given");
            return;
        }
        if (c.Func == "DISPUTE" && ds.DisputedAmt <= 0)
        {
            c.Reject("DSP0005", "The amount in dispute must be greater than nil");
            return;
        }
        if (c.Func == "PROMISE" && (ds.PromiseAmt <= 0 || ds.PromiseDate == 0))
        {
            c.Reject("DSP0006", "A promise to pay records an amount, a date and an owner");
            return;
        }
        if (c.Func == "ACTION" && CobolText.IsSpaces(ds.ActionType))
        {
            c.Reject("DSP0007", "An action type is required");
            return;
        }
        if (c.Func == "RESOLVE" && CobolText.IsSpaces(ds.Resolution))
        {
            c.Reject("DSP0008", "A case is closed with a written resolution, not silently");
        }
    }

    /// <summary>0300: AR-016 - the approved communication rules live on the customer.</summary>
    private async Task ReadCustomerAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ds = w.Ds;
        if (CobolText.IsSpaces(ds.Customer)) return;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT c.LegalName, c.Status, ISNULL(c.ContactRestriction,'') FROM ar.Customer c WHERE c.CompanyCode = ? AND c.CustomerCode = ?",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ds.Customer)], [120, 12, 100], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (!row.Found)
        {
            c.Rc = 16;
            c.Reason = "DSP0010";
            c.Msg = "Customer " + CobolText.Trim(ds.Customer) + " is not on the sales ledger";
            c.MsgCnt++;
            return;
        }
        if (CobolText.IsSpaces(ds.ContactRestrict)) ds.ContactRestrict = row.Text(2);
    }

    private async Task ReadItemAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ds = w.Ds;
        if (CobolText.IsSpaces(ds.DocumentNo) && ds.OpenItemId == 0) return;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT CONVERT(varchar(24), o.OpenItemId), o.DocumentNumber, CONVERT(varchar(24), o.ResidualAmountGBP), " +
                "CONVERT(varchar(24), o.DisputedAmountGBP), CONVERT(varchar(4), o.IsDisputed), o.CollectionStatus, o.SettlementStatus " +
                "FROM ar.OpenItem o WHERE o.CompanyCode = ? AND (o.OpenItemId = NULLIF(?,0) OR (o.DocumentNumber = NULLIF(?,'') " +
                "AND o.CustomerCode = ?))",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ArEdit.Int(ds.OpenItemId)), SqlioParm.Text(ds.DocumentNo), SqlioParm.Text(ds.Customer)],
                [24, 30, 24, 24, 4, 12, 12], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (!row.Found)
        {
            c.Rc = 16;
            c.Reason = "DSP0011";
            c.Msg = "Open item " + CobolText.Trim(ds.DocumentNo) + " does not exist for that customer";
            c.MsgCnt++;
            return;
        }
        w.ItemFound = true;
        ds.OpenItemId = (int)CobolNumeric.Truncated(row.NumVal(0), 0);
        ds.DocumentNo = row.Text(1);
        w.Resid = CobolNumeric.Store(row.NumVal(2), 11, 2);
        ds.Residual = w.Resid;
        // AR-015 - a dispute may be partial; the undisputed balance stays collectable under policy
        ds.UndisputedAmt = CobolNumeric.Store(w.Resid - ds.DisputedAmt, 11, 2);
        if (ds.UndisputedAmt < 0) ds.UndisputedAmt = 0m;
        if (c.Func == "DISPUTE" && ds.DisputedAmt > w.Resid + 0.004m)
        {
            c.Rc = 8;
            c.Reason = "DSP0012";
            c.Msg = "The amount in dispute exceeds the residual of GBP " + ArEdit.MoneyUk(w.Resid) + " on that item";
            c.MsgCnt++;
        }
    }

    /// <summary>0500: find the open case for this customer, item and type.</summary>
    private async Task FindCaseAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ds = w.Ds;
        var item = ArEdit.Int(ds.OpenItemId);
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "SELECT CONVERT(varchar(24), k.CaseId), k.CaseRef, k.CaseType, k.Status, CONVERT(varchar(4), k.CollectionStep), " +
                "CONVERT(varchar(24), k.DisputedAmountGBP), ISNULL(k.OwnerUser,'') FROM ar.CollectionCase k WHERE k.CompanyCode = ? " +
                "AND (k.CaseId = NULLIF(?,0) OR k.CaseRef = NULLIF(?,'') OR (k.CustomerCode = ? AND k.CaseType = ? " +
                "AND k.Status IN ('OPEN','ESCALATED') AND (k.OpenItemId = NULLIF(?,0) OR (k.OpenItemId IS NULL AND ? = 0))))",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ArEdit.Int(ds.CaseId)), SqlioParm.Text(ds.CaseRef), SqlioParm.Text(ds.Customer),
                 SqlioParm.Text(ds.CaseType), SqlioParm.Text(item), SqlioParm.Text(item)],
                [24, 30, 12, 12, 4, 24, 20], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        if (!row.Found) return;
        w.CaseFound = true;
        ds.CaseId = (int)CobolNumeric.Truncated(row.NumVal(0), 0);
        ds.CaseRef = row.Text(1);
        ds.CaseType = row.Text(2);
        ds.Status = row.Text(3);
        ds.CollectionStep = (int)CobolNumeric.Store(row.NumVal(4), 2, 0, signed: false);
        if (CobolText.EqualsPadded(ds.OwnerUser, c.UserId) && !CobolText.IsSpaces(row.Raw(6))) ds.OwnerUser = row.Text(6);
    }

    // ------------------------------------------------------------------ 1000 DISPUTE

    private async Task DisputeAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ds = w.Ds;
        await PermissionAsync(w, "AR.DISPUTE.RAISE", ct);
        if (c.RcBad || c.RcHold) return;
        if (!w.CaseFound)
        {
            await CaseRefAsync(w, ct);
            if (c.RcBad) return;
        }
        try
        {
            await sql.UnitOfWork.BeginAsync(ct);
        }
        catch (SqlioException)
        {
            // SQLIO BEGN result not inspected
        }
        w.TranOpen = true;
        if (w.CaseFound) await AmendCaseAsync(w, ct);
        else await OpenCaseAsync(w, ct);
        if (c.RcBad)
        {
            await RollbackAsync(w, ct);
            return;
        }
        if (w.ItemFound)
        {
            await ExecAsync(w,
                "UPDATE ar.OpenItem SET IsDisputed = 1, DisputedAmountGBP = ?, CollectionStatus = 'DISPUTED', ModifiedBy = ?, " +
                "ModifiedAt = SYSDATETIME() WHERE OpenItemId = ? AND CompanyCode = ?",
                [SqlioParm.Text(ArEdit.Money(ds.DisputedAmt)), SqlioParm.Text(c.UserId), SqlioParm.Text(ArEdit.Int(ds.OpenItemId)),
                 SqlioParm.Text(c.Co)], ct);
            if (c.RcBad)
            {
                await RollbackAsync(w, ct);
                return;
            }
        }
        ds.ActionType = "DISPUTE";
        ds.Narrative = CobolText.Trimmed(ds.Reason, 400);
        await WriteActionAsync(w, ct);
        if (c.RcBad)
        {
            await RollbackAsync(w, ct);
            return;
        }
        await CommitAsync(w, ct);
        await AuditCaseAsync(w, ct);
        ds.Status = "OPEN";
        c.Msg = "Dispute " + CobolText.Trim(ds.CaseRef) + " records GBP " + ArEdit.MoneyUk(ds.DisputedAmt) +
                " in dispute; the debt remains payable and stays in credit exposure";
    }

    private Task AmendCaseAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ds = w.Ds;
        return ExecAsync(w,
            "UPDATE ar.CollectionCase SET DisputedAmountGBP = ?, ReasonCode = ?, Reason = ?, OwnerUser = ?, " +
            "NextActionDate = NULLIF(?,'19000101'), NextAction = NULLIF(?,''), Status = 'OPEN', ModifiedBy = ?, " +
            "ModifiedAt = SYSDATETIME() WHERE CaseId = ? AND CompanyCode = ?",
            [SqlioParm.Text(ArEdit.Money(ds.DisputedAmt)), SqlioParm.Text(ds.ReasonCode), SqlioParm.Text(ds.Reason),
             SqlioParm.Text(ds.OwnerUser), ds.NextActionDt == 0 ? SqlioParm.Text("19000101") : SqlioParm.Digits(ds.NextActionDt, 8),
             SqlioParm.Text(ds.NextAction), SqlioParm.Text(c.UserId), SqlioParm.Text(ArEdit.Int(ds.CaseId)), SqlioParm.Text(c.Co)], ct);
    }

    private async Task OpenCaseAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ds = w.Ds;
        SqlioRow row;
        try
        {
            row = await sql.SingleAsync(
                "INSERT ar.CollectionCase (CompanyCode, CustomerCode, CaseRef, CaseType, OpenItemId, DocumentNumber, ReasonCode, Reason, " +
                "DisputedAmountGBP, OwnerUser, CollectionStep, NextActionDate, NextAction, Status, CreatedBy) " +
                "OUTPUT CONVERT(varchar(24), INSERTED.CaseId) VALUES (?, ?, ?, ?, NULLIF(?,0), NULLIF(?,''), ?, ?, ?, ?, ?, " +
                "NULLIF(?,'19000101'), NULLIF(?,''), 'OPEN', ?)",
                [SqlioParm.Text(c.Co), SqlioParm.Text(ds.Customer), SqlioParm.Text(ds.CaseRef), SqlioParm.Text(ds.CaseType),
                 SqlioParm.Text(ArEdit.Int(ds.OpenItemId)), SqlioParm.Text(ds.DocumentNo), SqlioParm.Text(ds.ReasonCode),
                 SqlioParm.Text(ds.Reason), SqlioParm.Text(ArEdit.Money(ds.DisputedAmt)), SqlioParm.Text(ds.OwnerUser),
                 SqlioParm.Text(ArEdit.Int(ds.CollectionStep)),
                 ds.NextActionDt == 0 ? SqlioParm.Text("19000101") : SqlioParm.Digits(ds.NextActionDt, 8),
                 SqlioParm.Text(ds.NextAction), SqlioParm.Text(c.UserId)],
                [24], ct);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
            return;
        }
        ds.CaseId = (int)CobolNumeric.Truncated(row.NumVal(0), 0);
    }

    // ------------------------------------------------------------------ 2000 PROMISE

    private async Task PromiseAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ds = w.Ds;
        await PermissionAsync(w, "AR.COLLECTION.PROMISE", ct);
        if (c.RcBad || c.RcHold) return;
        if (ds.PromiseDate < c.BusDate)
        {
            c.Rc = 8;
            c.Reason = "DSP0020";
            c.Msg = "A promise to pay cannot be dated before today";
            c.MsgCnt++;
            return;
        }
        if (w.ItemFound && ds.PromiseAmt > w.Resid + 0.004m)
        {
            c.Rc = 4;
            c.Reason = "DSP0021";
            c.Msg = "The promise exceeds the residual of GBP " + ArEdit.MoneyUk(w.Resid) + " on that item";
            c.MsgCnt++;
        }
        if (!w.CaseFound)
        {
            ds.CaseType = "COLLECTION";
            await CaseRefAsync(w, ct);
            if (c.RcBad) return;
            await OpenCaseAsync(w, ct);
            if (c.RcBad) return;
        }
        ds.PromiseStatus = "OPEN";
        ds.ActionType = "PROMISE";
        await FollowUpAsync(w, ct);
        await WriteActionAsync(w, ct);
        if (c.RcBad) return;
        // 2200-SET-NEXT-ACTION
        await ExecAsync(w,
            "UPDATE ar.CollectionCase SET NextActionDate = ?, NextAction = ?, OwnerUser = ?, ModifiedBy = ?, " +
            "ModifiedAt = SYSDATETIME() WHERE CaseId = ? AND CompanyCode = ?",
            [SqlioParm.Digits(ds.NextActionDt, 8), SqlioParm.Text(ds.NextAction), SqlioParm.Text(ds.OwnerUser), SqlioParm.Text(c.UserId),
             SqlioParm.Text(ArEdit.Int(ds.CaseId)), SqlioParm.Text(c.Co)], ct);
        c.Msg = "Promise of GBP " + ArEdit.MoneyUk(ds.PromiseAmt) + " on " + ArEdit.Dmy(ds.PromiseDate) + " recorded for " +
                CobolText.Trim(ds.OwnerUser);
    }

    /// <summary>2100: the follow up falls one working day after the promise.</summary>
    private async Task FollowUpAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ds = w.Ds;
        var dc = new DatecalcArea { Date1 = ds.PromiseDate, Number = 1, Calendar = "ENG" };
        dc.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, c.BusDate, ""), "ADDBUS");
        await datecalc.ExecuteAsync(dc, ct);
        ds.NextActionDt = dc.Header.ReturnCode > 4 ? ds.PromiseDate : dc.ResultDate;
        ds.NextAction = "Check the promised payment has been received";
    }

    // ------------------------------------------------------------------ shared sections

    private async Task WriteActionAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ds = w.Ds;
        // 3150-NEXT-SEQ: the read-add-one of the old source, kept on purpose
        try
        {
            var seq = await sql.SingleAsync(
                "SELECT CONVERT(varchar(24), ISNULL(MAX(a.ActionSeq),0) + 1) FROM ar.CollectionAction a WHERE a.CaseId = ?",
                [SqlioParm.Text(ArEdit.Int(ds.CaseId))], [24], ct);
            w.Seq = (int)CobolNumeric.Store(seq.NumVal(0), 9, 0);
        }
        catch (SqlioException ex)
        {
            c.SqlFailed(ex.DriverText);
        }
        await ExecAsync(w,
            "INSERT ar.CollectionAction (CaseId, ActionSeq, ActionType, ActionDate, PerformedBy, ContactName, Narrative, " +
            "PromiseAmountGBP, PromiseDate, PromiseStatus, SuppressedReason) VALUES (?, ?, ?, ?, ?, NULLIF(?,''), NULLIF(?,''), " +
            "NULLIF(CAST(? AS decimal(18,2)), 0), NULLIF(?,'19000101'), NULLIF(?,''), NULLIF(?,''))",
            [SqlioParm.Text(ArEdit.Int(ds.CaseId)), SqlioParm.Text(ArEdit.Int(w.Seq)),
             SqlioParm.Text(ds.ActionType == "DISPUTE" ? "NOTE" : ds.ActionType), SqlioParm.Digits(ds.ActionDate, 8),
             SqlioParm.Text(c.UserId), SqlioParm.Text(ds.ContactName), SqlioParm.Text(ds.Narrative),
             SqlioParm.Text(ArEdit.Money(ds.PromiseAmt)), ds.PromiseDate == 0 ? SqlioParm.Text("19000101") : SqlioParm.Digits(ds.PromiseDate, 8),
             SqlioParm.Text(ds.PromiseStatus), SqlioParm.Text(ds.SuppressReason)], ct);
        ds.ActionSeq = w.Seq;
    }

    private async Task PermissionAsync(Ws w, string permission, CancellationToken ct)
    {
        var c = w.Ctl;
        var pk = new PermchkArea { Permission = permission };
        pk.Header.Prepare(new KernelCaller(c.Co, c.Site, c.UserId, c.BusDate, ""), "CHECK");
        await permchk.ExecuteAsync(pk, ct);
        if (pk.Header.ReturnCode > 4)
        {
            c.Rc = 6;
            c.Reason = "DSP0060";
            c.Msg = pk.Header.Message;
            c.MsgCnt++;
        }
    }

    private async Task CaseRefAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ds = w.Ds;
        if (!CobolText.IsSpaces(ds.CaseRef)) return;
        var ng = new NumgenArea { SeriesCode = "ARCASE" };
        ng.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, c.BusDate, c.Correl), "NEXT");
        await numgen.ExecuteAsync(ng, ct);
        if (ng.Header.ReturnCode > 4)
        {
            c.Rc = ng.Header.ReturnCode;
            c.Reason = "DSP0061";
            c.Msg = ng.Header.Message;
            c.MsgCnt++;
            return;
        }
        ds.CaseRef = CobolText.Trimmed(ng.DocumentNumber, 30);
    }

    private async Task AuditCaseAsync(Ws w, CancellationToken ct)
    {
        var c = w.Ctl;
        var ds = w.Ds;
        var au = new AuditlogArea
        {
            EntityType = "ar.CollectionCase",
            EntityKey = ds.CaseRef,
            Action = c.Func,
            FieldName = "DisputedAmountGBP",
            NewValue = ArEdit.MoneyUkField(ds.DisputedAmt),
            Reason = CobolText.Trimmed(ds.Reason, 200),
            AuthorityRef = ds.OwnerUser,
            Source = "COBOL",
        };
        au.Header.Prepare(new KernelCaller(c.Co, "", c.UserId, c.BusDate, ""), "WRITE");
        await auditlog.ExecuteAsync(au, ct);
    }

    /// <summary>8500-EXEC: EXEC then CLOS; a failure is 9300-SQL-FAILED.</summary>
    private async Task ExecAsync(Ws w, string statement, IReadOnlyList<SqlioParm> parms, CancellationToken ct)
    {
        try
        {
            await sql.ExecAsync(statement, parms, ct);
        }
        catch (SqlioException ex)
        {
            w.Ctl.SqlFailed(ex.DriverText);
        }
    }

    private async Task CommitAsync(Ws w, CancellationToken ct)
    {
        if (!w.TranOpen) return;
        try
        {
            await sql.UnitOfWork.CommitAsync(ct);
        }
        catch (SqlioException)
        {
            // SQLIO COMT result not inspected
        }
        w.TranOpen = false;
    }

    private async Task RollbackAsync(Ws w, CancellationToken ct)
    {
        if (!w.TranOpen) return;
        try
        {
            await sql.UnitOfWork.RollbackAsync(ct);
        }
        catch (SqlioException)
        {
            // SQLIO ROLB result not inspected
        }
        w.TranOpen = false;
    }
}
