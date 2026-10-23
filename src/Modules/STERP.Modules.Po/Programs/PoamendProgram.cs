using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Po.Programs;

/// <summary>
/// POAMEND (cobol/online/po/POAMEND.cob) AMEND, called by POORDER with the same COMMAREA: an amendment row
/// (po.OrderAmendment); a material field (SupplierCode, OrderedQty, UnitPrice, TermsCode, CurrencyCode - case-sensitive)
/// invalidates the approvals (POAPPRV INVALID, RC not inspected) and marks the order AMENDED (spec 4.6, G09-ADV-15).
/// ACKNOWL, CALLOFF and COPY have no production caller (spec 12.2) and are not delivered. Autocommit throughout.
/// </summary>
internal sealed class PoamendProgram(ISqlRunner runner, PoapprvProgram poapprv)
{
    private readonly PoSql _sql = new(runner);

    private sealed class Work
    {
        public bool Fatal;
        public bool Material;
    }

    public async Task RunAsync(CaHeader ca, PoorderArea or, CancellationToken ct)
    {
        ca.Program = "POAMEND";
        var w = new Work();
        switch (ca.Function)
        {
            case "AMEND":
                await AmendAsync(ca, or, w, ct);
                break;
            case "ACKNOWL" or "CALLOFF" or "COPY":
                throw new NotSupportedException($"POAMEND {ca.Function} has no production caller and is not delivered (STERP-PO 12.2).");
            default:
                ca.ReasonCode = "ORD0008";
                ca.ReturnCode = 8;
                break;
        }
    }

    /// <summary>3000-AMEND.</summary>
    private async Task AmendAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        if (Cob.Spaces(or.AmendReason))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "ORD0016";
            ca.Message = Cob.Msg("An amendment to order ", Cob.Trim(or.OrderNumber), " needs a reason");
            return;
        }
        if (Cob.Spaces(or.AmendField))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "ORD0016";
            ca.Message = Cob.Msg("An amendment must name the field that changed");
            return;
        }
        await NextAmendAsync(ca, or, w, ct);
        if (w.Fatal) return;
        w.Material = or.AmendField is "SupplierCode" or "OrderedQty" or "UnitPrice" or "TermsCode" or "CurrencyCode";
        await WriteAmendAsync(ca, or, w, ct);
        if (w.Fatal) return;
        or.Outcome = "AMENDED";
        if (w.Material)
        {
            or.MaterialChange = "Y";
            or.NeedsReappr = "Y";
            await InvalidateAsync(ca, or, ct);
            await MarkAmendedAsync(ca, or, w, ct);
            ca.ReturnCode = 6;
            ca.ReasonCode = "ORD0012";
            ca.Message = Cob.Msg("Order ", Cob.Trim(or.OrderNumber), " amended materially (", Cob.Trim(or.AmendField),
                ") and must be approved again before reissue");
        }
        else
        {
            ca.ReturnCode = 0;
            ca.Message = Cob.Msg("Order ", Cob.Trim(or.OrderNumber), " amendment recorded: ", Cob.Trim(or.AmendReason));
        }
    }

    /// <summary>3500-INVALIDATE: POAPPRV INVALID on an INITIALIZEd area; only the hash is taken back.</summary>
    private async Task InvalidateAsync(CaHeader ca, PoorderArea or, CancellationToken ct)
    {
        var ap = CaHeader.Call("INVALID", ca.Company, ca.Site, ca.User, ca.BusinessDate, ca.CorrelationId);
        var av = new PoapprvArea
        {
            OrderNumber = or.OrderNumber,
            ChangeReason = Cob.Spaces(or.AmendReason)
                ? "Supplier acknowledgement differs from the approved order"
                : Cob.X(or.AmendReason, 300),
        };
        await poapprv.RunAsync(ap, av, ct);
        or.ContentHash = av.ContentHash;
    }

    /// <summary>3600-MARK-AMENDED: status AMENDED unless CLOSED/CANCELLED (row count unchecked).</summary>
    private async Task MarkAmendedAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        var r = await _sql.Exec(
            "UPDATE po.PurchaseOrder SET Status = 'AMENDED', ModifiedBy = ?, ModifiedAt = SYSDATETIME() WHERE CompanyCode = ? " +
            "AND OrderNumber = ? AND Status NOT IN ('CLOSED', 'CANCELLED')",
            [PoSql.Tx(ca.User), PoSql.Tx(ca.Company), PoSql.Tx(or.OrderNumber)], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        or.Status = "AMENDED";
    }

    /// <summary>8100-NEXT-AMEND.</summary>
    private async Task NextAmendAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        var r = await _sql.Sngl(
            "SELECT CAST(ISNULL(MAX(AmendmentNo), 0) + 1 AS varchar(24)) FROM po.OrderAmendment WHERE CompanyCode = ? AND " +
            "OrderNumber = ?",
            [PoSql.Tx(ca.Company), PoSql.Tx(or.OrderNumber)], [24], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        or.AmendNo = (int)r.Row.NumVal(0);
    }

    /// <summary>8200-WRITE-AMEND.</summary>
    private async Task WriteAmendAsync(CaHeader ca, PoorderArea or, Work w, CancellationToken ct)
    {
        var flag = w.Material ? "1" : "0";
        var r = await _sql.Exec(
            "INSERT po.OrderAmendment (CompanyCode, OrderNumber, LineNum, ScheduleNo, AmendmentNo, Source, FieldName, " +
            "OriginalValue, RevisedValue, IsMaterial, RequiresReapproval, AffectedSalesOrder, OwnerUser, Reason, AmendedBy, " +
            "Status) VALUES (?, ?, NULLIF(?, 0), NULLIF(?, 0), ?, ?, ?, NULLIF(?, ''), NULLIF(?, ''), ?, ?, NULLIF(?, ''), " +
            "NULLIF(?, ''), NULLIF(?, ''), ?, 'OPEN')",
            [PoSql.Tx(ca.Company), PoSql.Tx(or.OrderNumber), PoSql.Int(or.LineNum), PoSql.Int(or.ScheduleNo), PoSql.Int(or.AmendNo),
             PoSql.Tx(ca.Function == "ACKNOWL" ? "ACK" : "BUYER"), PoSql.Tx(Cob.X(or.AmendField, 40)), PoSql.Tx(Cob.X(or.AmendOld, 100)),
             PoSql.Tx(Cob.X(or.AmendNew, 100)), PoSql.Tx(flag), PoSql.Tx(flag), PoSql.Tx(or.LinkedSo), PoSql.Tx(or.Buyer),
             PoSql.Tx(Cob.Spaces(or.AmendReason) ? or.AckNote : or.AmendReason), PoSql.Tx(ca.User)], ct);
        if (r.Error) { SqlFail(ca, w, r); return; }
        or.AmendCnt = (or.AmendCnt + 1) % 10000;
        or.AmendNo++;
    }

    private static void SqlFail(CaHeader ca, Work w, Sq r)
    {
        ca.ReturnCode = 12;
        ca.ReasonCode = "SQL0001";
        ca.Message = r.Err200;
        w.Fatal = true;
    }
}
