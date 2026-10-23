using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Po.Programs;

/// <summary>
/// POAPPRV (cobol/online/po/POAPPRV.cob): approval of the exact order content - CHECK, REQUEST
/// (core.usp_RequestApproval), DECIDE (core.usp_DecideApproval; trigger core.trg_ApprovalPurchaseOrder), INVALID
/// (core.usp_InvalidateApprovals, positional binding: the change reason lands in @UserCode, D-PO-09) and HASH. The content
/// hash is 8000-HASH over the SQL Server renderings of 2100-READ-ORDER and 2200-READ-LINES (D-PO-06). Every statement
/// autocommits on the caller's engine connection.
/// </summary>
internal sealed class PoapprvProgram(ISqlRunner runner)
{
    private static readonly long[] Seeds = [17, 331, 5381, 52711, 648391, 9737333, 174440041, 93];
    private static readonly long[] Mults = [31, 33, 37, 39, 131, 137, 139, 16777];
    private const long Modulus = 4294967296L;

    private readonly PoSql _sql = new(runner);

    private sealed class Work
    {
        public bool Fatal;
        public SqlioRow Ord = new(100, []);
        public SqlioRow Lines = new(100, []);
        public SqlioRow Apr = new(100, []);
    }

    public async Task RunAsync(CaHeader ca, PoapprvArea av, CancellationToken ct)
    {
        var w = new Work();
        Init(ca, av, w);
        if (w.Fatal) return;
        switch (ca.Function)
        {
            case "HASH":
                await BuildHashAsync(ca, av, w, ct);
                if (!w.Fatal) await StoreHashAsync(ca, av, w, ct);
                break;
            case "REQUEST":
                await RequestAsync(ca, av, w, ct);
                break;
            case "DECIDE":
                await DecideAsync(ca, av, w, ct);
                break;
            case "CHECK":
                await CheckAsync(ca, av, w, ct);
                break;
            case "INVALID":
                await InvalidateAsync(ca, av, w, ct);
                break;
            default:
                ca.ReasonCode = "APV0008";
                ca.Message = Cob.Msg("Function ", Cob.Trim(ca.Function), " is not supported by POAPPRV");
                ca.ReturnCode = 8;
                break;
        }
    }

    private static void Init(CaHeader ca, PoapprvArea av, Work w)
    {
        ca.Program = "POAPPRV";
        ca.ReturnCode = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        av.OrderId = 0; av.ApprovalId = 0; av.LineCnt = 0;
        av.NetAmount = 0; av.GrossAmount = 0; av.AmountGbp = 0;
        av.ContentHash = ""; av.StoredHash = "";
        av.Supplier = ""; av.Currency = ""; av.TermsCode = "";
        av.OrderStatus = ""; av.ApprovalStatus = "";
        av.RequiredPerm = ""; av.RequiredRole = "";
        av.RequestedBy = "";
        av.WasInvalidated = "N";
        av.Outcome = "NOTAPPROVED";
        if (Cob.Spaces(av.ApprovalType)) av.ApprovalType = "PURCHASEORDER";
        FoldType(av);
        if (Cob.Spaces(ca.Company) || Cob.Spaces(av.OrderNumber))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "APV0001";
            ca.Message = Cob.Msg("A company code and an order number are needed before an order can be approved");
            w.Fatal = true;
        }
    }

    /// <summary>1100-FOLD-TYPE: upper-case, punctuation (space - _ .) folded out.</summary>
    private static void FoldType(PoapprvArea av)
    {
        var input = CobolText.Fit(Cob.Upper(av.ApprovalType), 30);
        var fold = new System.Text.StringBuilder();
        foreach (var c in input)
        {
            if (c is not (' ' or '-' or '_' or '.')) fold.Append(c);
        }
        av.ApprovalType = fold.Length > 0 ? Cob.X(fold.ToString(), 30) : "PURCHASEORDER";
    }

    /// <summary>2000-BUILD-HASH.</summary>
    private async Task BuildHashAsync(CaHeader ca, PoapprvArea av, Work w, CancellationToken ct)
    {
        await ReadOrderAsync(ca, av, w, ct);
        if (w.Fatal) return;
        await ReadLinesAsync(ca, av, w, ct);
        if (w.Fatal) return;
        if (av.LineCnt == 0)
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "APV0003";
            ca.Message = Cob.Msg("Order ", Cob.Trim(av.OrderNumber), " has no live lines, so there is nothing to approve");
            w.Fatal = true;
            return;
        }
        var o = w.Ord;
        var l = w.Lines;
        var text = Cob.Str(300, Cob.Trim(o.Raw(1)), "|", Cob.Trim(o.Raw(2)), "|", Cob.Trim(o.Raw(3)), "|", Cob.Trim(o.Raw(4)), "|",
            Cob.Trim(o.Raw(5)), "|", Cob.Trim(o.Raw(6)), "|", Cob.Trim(o.Raw(7)), "|", Cob.Trim(o.Raw(10)), "|",
            Cob.Trim(l.Raw(0)), "|", Cob.Trim(l.Raw(1)), "|", Cob.Trim(l.Raw(2)));
        av.ContentHash = Hash(text);
    }

    /// <summary>2100-READ-ORDER.</summary>
    private async Task ReadOrderAsync(CaHeader ca, PoapprvArea av, Work w, CancellationToken ct)
    {
        var r = await _sql.Sngl(
            "SELECT CAST(o.PurchaseOrderId AS varchar(20)), o.SupplierCode, o.CurrencyCode, ISNULL(o.TermsCode, ''), " +
            "ISNULL(o.IncotermsRule, ''), CAST(o.NetAmount AS varchar(24)), CAST(o.VatAmount AS varchar(24)), " +
            "CAST(o.GrossAmount AS varchar(24)), o.Status, ISNULL(o.ContentHash, ''), ISNULL(o.LinkedSalesOrderNumber, '') FROM " +
            "po.PurchaseOrder o WHERE o.CompanyCode = ? AND o.OrderNumber = ?",
            [PoSql.Tx(ca.Company), PoSql.Tx(av.OrderNumber)], [20, 20, 3, 12, 3, 24, 24, 24, 14, 64, 30], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        if (r.NotFound)
        {
            ca.ReturnCode = 16;
            ca.ReasonCode = "APV0002";
            ca.Message = Cob.Msg("Purchase order ", Cob.Trim(av.OrderNumber), " is not on file for company ", Cob.Trim(ca.Company));
            w.Fatal = true;
            return;
        }
        var o = r.Row;
        w.Ord = o;
        av.OrderId = (long)Cob.T(o.NumVal(0), 17, 0);
        av.Supplier = o.Text(1);
        av.Currency = o.Text(2);
        av.TermsCode = o.Text(3);
        av.OrderStatus = o.Text(8);
        av.StoredHash = o.Text(9);
        av.NetAmount = Cob.T(o.NumVal(5), 13, 2);
        av.GrossAmount = Cob.T(o.NumVal(7), 13, 2);
        av.AmountGbp = av.GrossAmount;
    }

    /// <summary>2200-READ-LINES: count, net and CHECKSUM_AGG of the live lines.</summary>
    private async Task ReadLinesAsync(CaHeader ca, PoapprvArea av, Work w, CancellationToken ct)
    {
        var r = await _sql.Sngl(
            "SELECT CAST(COUNT(*) AS varchar(12)), CAST(ISNULL(SUM(l.NetAmount), 0) AS varchar(24)), " +
            "CAST(ISNULL(CHECKSUM_AGG(CHECKSUM(l.LineNum, l.ItemCode, l.Description, l.OrderedQty, l.UnitPrice, " +
            "l.DiscountPct, l.DestinationSiteCode, l.AccountCode)), 0) AS varchar(16)) FROM po.PurchaseOrderLine l WHERE " +
            "l.CompanyCode = ? AND l.OrderNumber = ? AND l.Status <> 'CANCELLED'",
            [PoSql.Tx(ca.Company), PoSql.Tx(av.OrderNumber)], [12, 24, 16], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        if (r.NotFound)
        {
            av.LineCnt = 0;
            return;
        }
        w.Lines = r.Row;
        av.LineCnt = (int)Cob.T(r.Row.NumVal(0), 4, 0, signed: false);
    }

    /// <summary>2500-STORE-HASH.</summary>
    private async Task StoreHashAsync(CaHeader ca, PoapprvArea av, Work w, CancellationToken ct)
    {
        var r = await _sql.Exec(
            "UPDATE po.PurchaseOrder SET ContentHash = ?, ModifiedBy = ?, ModifiedAt = SYSDATETIME() WHERE CompanyCode = ? AND " +
            "OrderNumber = ?",
            [PoSql.Tx(av.ContentHash), PoSql.Tx(ca.User), PoSql.Tx(ca.Company), PoSql.Tx(av.OrderNumber)], ct);
        if (r.Error) SqlFail(ca, w, r);
    }

    /// <summary>3000-REQUEST.</summary>
    private async Task RequestAsync(CaHeader ca, PoapprvArea av, Work w, CancellationToken ct)
    {
        await BuildHashAsync(ca, av, w, ct);
        if (w.Fatal) return;
        var status = w.Ord.Text(8);
        if (status is "CLOSED" or "CANCELLED")
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "APV0004";
            ca.Message = Cob.Msg("Order ", Cob.Trim(av.OrderNumber), " is ", status, " and cannot be submitted for approval");
            return;
        }
        if (!Cob.Spaces(av.StoredHash) && av.StoredHash != av.ContentHash) av.WasInvalidated = "Y";
        await StoreHashAsync(ca, av, w, ct);
        if (w.Fatal) return;
        if (Cob.Spaces(av.RequestedBy)) av.RequestedBy = ca.User;
        var amount = PoSql.Num(av.GrossAmount);
        var r = await _sql.Sngl(
            "EXEC core.usp_RequestApproval ?, 'PURCHASEORDER', ?, ?, ?, ?, ?, ?, ?, NULL, ?",
            [PoSql.Tx(ca.Company), PoSql.Tx(av.OrderNumber), PoSql.Tx(av.ApprovalType), PoSql.Tx(av.ContentHash),
             PoSql.Tx(av.RequestedBy), amount, PoSql.Tx("GBP"), amount,
             Cob.Spaces(ca.CorrelationId) ? PoSql.Null : PoSql.Tx(ca.CorrelationId)],
            AprWidths, ct);
        if (r.Error)
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "APV0009";
            ca.Message = r.Err200;
            return;
        }
        if (r.NotFound)
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "APV0009";
            ca.Message = Cob.Msg("No approval rule covers a purchase order of this value");
            return;
        }
        w.Apr = r.Row;
        await RereadAsync(w, ct);
        TakeApproval(av, w);
        await SetOrderStatusAsync(ca, av, w, ct);
        if (av.ApprovalStatus == "APPROVED")
        {
            av.Outcome = "APPROVED";
            ca.ReturnCode = 0;
        }
        else
        {
            av.Outcome = "PENDING";
            ca.ReturnCode = 4;
            ca.ReasonCode = "APV0005";
            ca.Message = Cob.Msg("Order ", Cob.Trim(av.OrderNumber), " for ", Cob.Amt(av.GrossAmount), " awaits approval by ",
                Cob.Trim(av.RequiredRole));
        }
    }

    /// <summary>4000-DECIDE.</summary>
    private async Task DecideAsync(CaHeader ca, PoapprvArea av, Work w, CancellationToken ct)
    {
        if (Cob.Spaces(av.Approver) || Cob.Spaces(av.Decision))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "APV0010";
            ca.Message = Cob.Msg("An approval decision needs both an approver and a decision of APPROVE or REJECT");
            return;
        }
        if (av.ApprovalId == 0)
        {
            await CheckAsync(ca, av, w, ct);
            if (av.ApprovalId == 0)
            {
                ca.ReturnCode = 16;
                ca.ReasonCode = "APV0011";
                ca.Message = Cob.Msg("There is no approval request outstanding for order ", Cob.Trim(av.OrderNumber));
                return;
            }
        }
        var approve = av.Decision is "APPROVE" or "APPROVED";
        var decision = approve ? "APPROVED" : av.Decision is "REJECT" or "REJECTED" ? "REJECTED" : av.Decision;
        var r = await _sql.Exec(
            "EXEC core.usp_DecideApproval ?, ?, ?, ?",
            [PoSql.Int(av.ApprovalId), PoSql.Tx(av.Approver), PoSql.Tx(decision), PoSql.Tx(av.Comment)], ct);
        if (r.Error)
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "APV0007";
            ca.Message = r.Err200;
            return;
        }
        if (approve)
        {
            av.ApprovalStatus = "APPROVED";
            av.Outcome = "APPROVED";
            ca.ReturnCode = 0;
            ca.Message = Cob.Msg("Order ", Cob.Trim(av.OrderNumber), " approved by ", Cob.Trim(av.Approver),
                " for its exact value, supplier and terms");
        }
        else
        {
            av.ApprovalStatus = "REJECTED";
            av.Outcome = "REJECTED";
            ca.ReturnCode = 8;
            ca.Message = Cob.Msg("Order ", Cob.Trim(av.OrderNumber), " rejected by ", Cob.Trim(av.Approver));
        }
        await SetOrderStatusAsync(ca, av, w, ct);
    }

    /// <summary>5000-CHECK: approved only when an APPROVED row exists for the hash of the order as it stands now.</summary>
    private async Task CheckAsync(CaHeader ca, PoapprvArea av, Work w, CancellationToken ct)
    {
        await BuildHashAsync(ca, av, w, ct);
        if (w.Fatal) return;
        var r = await _sql.Sngl(
            "SELECT TOP 1 CAST(a.ApprovalId AS varchar(20)), a.Status, ISNULL(a.RequiredPermission, ''), " +
            "ISNULL(a.RequiredRole, ''), ISNULL(a.RequestedBy, ''), ISNULL(a.ContentHash, '') FROM core.Approval a WHERE " +
            "a.CompanyCode = ? AND a.DocumentType = 'PURCHASEORDER' AND a.DocumentRef = ? AND a.Status IN ('APPROVED', " +
            "'PENDING') ORDER BY CASE WHEN a.ContentHash = ? THEN 0 ELSE 1 END, CASE a.Status WHEN 'APPROVED' THEN 0 ELSE 1 " +
            "END, a.ApprovalId DESC",
            [PoSql.Tx(ca.Company), PoSql.Tx(av.OrderNumber), PoSql.Tx(av.ContentHash)], AprWidths, ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        if (r.NotFound)
        {
            av.Outcome = "NOTAPPROVED";
            ca.ReturnCode = 6;
            ca.ReasonCode = "APV0011";
            ca.Message = Cob.Msg("Order ", Cob.Trim(av.OrderNumber), " has not been approved, so it is a draft and not a supplier commitment");
            return;
        }
        w.Apr = r.Row;
        TakeApproval(av, w);
        if (Cob.X(w.Apr.Text(5), 64) != Cob.X(av.ContentHash, 64))
        {
            av.Outcome = "REAPPROVE";
            av.WasInvalidated = "Y";
            ca.ReturnCode = 6;
            ca.ReasonCode = "APV0006";
            ca.Message = Cob.Msg("Order ", Cob.Trim(av.OrderNumber), " has changed materially since it was approved and must be approved again");
        }
        else if (av.ApprovalStatus == "APPROVED")
        {
            av.Outcome = "APPROVED";
            ca.ReturnCode = 0;
        }
        else
        {
            av.Outcome = "PENDING";
            ca.ReturnCode = 6;
            ca.ReasonCode = "APV0005";
            ca.Message = Cob.Msg("Order ", Cob.Trim(av.OrderNumber), " is still awaiting an approval decision");
        }
    }

    /// <summary>6000-INVALIDATE: positional EXEC - the change reason is bound to @UserCode (D-PO-09, BC-PO-06 rejected).</summary>
    private async Task InvalidateAsync(CaHeader ca, PoapprvArea av, Work w, CancellationToken ct)
    {
        await BuildHashAsync(ca, av, w, ct);
        if (w.Fatal) return;
        if (Cob.Spaces(av.ChangeReason)) av.ChangeReason = "Material change to the approved order content";
        var r = await _sql.Exec(
            "EXEC core.usp_InvalidateApprovals ?, 'PURCHASEORDER', ?, ?, ?",
            [PoSql.Tx(ca.Company), PoSql.Tx(av.OrderNumber), PoSql.Tx(av.ContentHash), PoSql.Tx(av.ChangeReason)], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        await StoreHashAsync(ca, av, w, ct);
        av.WasInvalidated = "Y";
        av.Outcome = "REAPPROVE";
        ca.ReturnCode = 4;
        ca.ReasonCode = "APV0006";
        ca.Message = Cob.Msg("Approvals for order ", Cob.Trim(av.OrderNumber), " invalidated: ", Cob.Trim(av.ChangeReason));
    }

    private static readonly int[] AprWidths = [20, 14, 40, 20, 20, 64];

    /// <summary>7050-REREAD: the request re-read with the six check columns (result not inspected).</summary>
    private async Task RereadAsync(Work w, CancellationToken ct)
    {
        var r = await _sql.Sngl(
            "SELECT CAST(a.ApprovalId AS varchar(20)), a.Status, ISNULL(a.RequiredPermission, ''), ISNULL(a.RequiredRole, ''), " +
            "ISNULL(a.RequestedBy, ''), ISNULL(a.ContentHash, '') FROM core.Approval a WHERE a.ApprovalId = ?",
            [PoSql.Tx(w.Apr.Raw(0))], AprWidths, ct);
        if (r.Ok) w.Apr = r.Row;
        else if (r.NotFound) w.Apr = new SqlioRow(100, AprWidths.Select(x => new string(' ', x)).ToArray());
    }

    /// <summary>7100-TAKE-APPROVAL.</summary>
    private static void TakeApproval(PoapprvArea av, Work w)
    {
        av.ApprovalId = (long)Cob.T(w.Apr.NumVal(0), 17, 0);
        av.ApprovalStatus = w.Apr.Text(1);
        av.RequiredPerm = w.Apr.Text(2);
        av.RequiredRole = w.Apr.Text(3);
        av.RequestedBy = w.Apr.Text(4);
    }

    /// <summary>7200-SET-ORDER-STATUS: guarded UPDATE of po.PurchaseOrder (row count unchecked).</summary>
    private async Task SetOrderStatusAsync(CaHeader ca, PoapprvArea av, Work w, CancellationToken ct)
    {
        var stmt = av.ApprovalStatus switch
        {
            "APPROVED" => "UPDATE po.PurchaseOrder SET Status = 'APPROVED', ApprovalId = ?, ApprovedBy = ?, ApprovedAt = " +
                          "SYSDATETIME() WHERE CompanyCode = ? AND OrderNumber = ? AND Status IN ('DRAFT', 'AWAITAPPROVAL', 'AMENDED')",
            "REJECTED" => "UPDATE po.PurchaseOrder SET Status = 'DRAFT', ApprovalId = ?, ApprovedBy = ?, ApprovedAt = NULL WHERE " +
                          "CompanyCode = ? AND OrderNumber = ? AND Status IN ('AWAITAPPROVAL', 'AMENDED')",
            _ => "UPDATE po.PurchaseOrder SET Status = 'AWAITAPPROVAL', ApprovalId = ?, ModifiedBy = ?, ModifiedAt = SYSDATETIME() " +
                 "WHERE CompanyCode = ? AND OrderNumber = ? AND Status IN ('DRAFT', 'AMENDED', 'AWAITAPPROVAL')",
        };
        var r = await _sql.Exec(stmt,
            [PoSql.Int(av.ApprovalId), PoSql.Tx(Cob.Spaces(av.Approver) ? ca.User : av.Approver), PoSql.Tx(ca.Company),
             PoSql.Tx(av.OrderNumber)], ct);
        if (r.Error) SqlFail(ca, w, r);
    }

    private static void SqlFail(CaHeader ca, Work w, Sq r)
    {
        ca.ReturnCode = 12;
        ca.ReasonCode = "SQL0001";
        ca.Message = r.Err200;
        w.Fatal = true;
    }

    /// <summary>
    /// 8000-HASH: eight 32-bit accumulators over WS-HASH-TEXT X(300) up to its last non-space character, FUNCTION ORD of
    /// each byte (native collating sequence: byte value + 1), rendered as 64 lower-case hex digits.
    /// </summary>
    public static string Hash(string text)
    {
        var bytes = CobolText.Ansi.GetBytes(CobolText.Fit(text, 300));
        var len = 0;
        for (var i = bytes.Length; i >= 1; i--)
        {
            if (bytes[i - 1] != (byte)' ')
            {
                len = i;
                break;
            }
        }
        var acc = (long[])Seeds.Clone();
        for (var i = 1; i <= len; i++)
        {
            long c = bytes[i - 1] + 1;
            for (var k = 0; k < 8; k++) acc[k] = (acc[k] * Mults[k] + c + i) % Modulus;
        }
        var sb = new System.Text.StringBuilder(64);
        foreach (var a in acc) sb.Append(a.ToString("x8", System.Globalization.CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
