using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Po.Programs;

/// <summary>
/// SOURCSEL (cobol/online/po/SOURCSEL.cob) CHECK: is the supplier an approved, effective source for the item
/// (po.SupplierSource), with the last paid price from POPRICE HISTORY. OPEN, QUOTE, RANK and SELECT have no production
/// caller (spec 12.2, SME-015) and are not delivered. Reads only.
/// </summary>
internal sealed class SourcselProgram(ISqlRunner runner, PopriceProgram poprice)
{
    private readonly PoSql _sql = new(runner);

    public async Task RunAsync(CaHeader ca, SourcselArea ss, CancellationToken ct)
    {
        var fatal = Init(ca, ss);
        if (fatal) return;
        switch (ca.Function)
        {
            case "CHECK":
                await CheckAsync(ca, ss, ct);
                break;
            case "OPEN" or "QUOTE" or "RANK" or "SELECT":
                throw new NotSupportedException($"SOURCSEL {ca.Function} has no production caller and is not delivered (STERP-PO 12.2).");
            default:
                ca.ReasonCode = "SRC0008";
                ca.Message = Cob.Msg("Function ", Cob.Trim(ca.Function), " is not supported by SOURCSEL");
                ca.ReturnCode = 8;
                break;
        }
    }

    private static bool Init(CaHeader ca, SourcselArea ss)
    {
        ca.Program = "SOURCSEL";
        ca.ReturnCode = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ss.EventId = 0; ss.QuoteId = 0; ss.SourceId = 0;
        ss.LeadTimeDays = 0; ss.MinOrderQty = 0; ss.PackQty = 0;
        ss.LineValueGbp = 0; ss.CheapestGbp = 0;
        ss.SelectedGbp = 0; ss.PremiumGbp = 0; ss.Rank = 0;
        ss.QuoteCnt = 0; ss.ApprovalId = 0;
        ss.LastPaidPrice = 0; ss.LastPaidDate = 0;
        ss.ApprovalStatus = ""; ss.SupplierPart = "";
        ss.ContractRef = ""; ss.SrcCurrency = "";
        ss.SrcRestriction = ""; ss.CheapestSupp = "";
        ss.Status = ""; ss.SourceText = "";
        ss.NeedsAuth = "N";
        ss.OnPrice = "N";
        if (ss.EventDate == 0) ss.EventDate = ca.BusinessDate;
        if (Cob.Spaces(ss.Currency)) ss.Currency = "GBP";
        if (ss.PricePer <= 0) ss.PricePer = 1;
        if (Cob.Spaces(ca.Company))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "SRC0001";
            ca.Message = Cob.Msg("A company code is needed for source selection");
            return true;
        }
        return false;
    }

    /// <summary>2000-CHECK.</summary>
    private async Task CheckAsync(CaHeader ca, SourcselArea ss, CancellationToken ct)
    {
        if (Cob.Spaces(ss.ItemCode) || Cob.Spaces(ss.Supplier))
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "SRC0001";
            ca.Message = Cob.Msg("An item code and a supplier code are needed to check the approved source register");
            return;
        }
        var date = Cob.D8(ss.EventDate);
        var r = await _sql.Sngl(
            "SELECT TOP 1 CAST(s.SupplierSourceId AS varchar(12)), s.ApprovalStatus, ISNULL(s.SupplierPartNumber, ''), " +
            "ISNULL(s.ContractRef, ''), CAST(s.LeadTimeDays AS varchar(12)), CAST(s.MinOrderQty AS varchar(24)), " +
            "CAST(s.PackQty AS varchar(24)), s.CurrencyCode, ISNULL(s.CommercialRestriction, ''), CASE WHEN s.EffectiveFrom " +
            "<= TRY_CONVERT(date, ?) AND (s.EffectiveTo IS NULL OR s.EffectiveTo >= TRY_CONVERT(date, ?)) THEN 'Y' ELSE 'N' " +
            "END FROM po.SupplierSource s WHERE s.CompanyCode = ? AND s.ItemCode = ? AND s.SupplierCode = ? AND " +
            "s.IsActive = 1 ORDER BY s.EffectiveFrom DESC",
            [PoSql.Tx(date), PoSql.Tx(date), PoSql.Tx(ca.Company), PoSql.Tx(ss.ItemCode), PoSql.Tx(ss.Supplier)],
            [12, 12, 40, 40, 12, 24, 24, 3, 200, 1], ct);
        if (r.Error)
        {
            ca.ReturnCode = 12;
            ca.ReasonCode = "SQL0001";
            ca.Message = r.Err200;
            return;
        }
        if (r.NotFound)
        {
            ss.ApprovalStatus = "UNAPPROVED";
            ss.NeedsAuth = "Y";
            ca.ReturnCode = 16;
            ca.ReasonCode = "SRC0002";
            ca.Message = Cob.Msg("Supplier ", Cob.Trim(ss.Supplier), " is not an approved source for item ", Cob.Trim(ss.ItemCode));
            Authority(ca, ss);
            return;
        }
        var row = r.Row;
        ss.SourceId = (int)row.NumVal(0);
        ss.ApprovalStatus = row.Text(1);
        ss.SupplierPart = row.Text(2);
        ss.ContractRef = row.Text(3);
        ss.LeadTimeDays = (int)Cob.T(row.NumVal(4), 5, 0);
        ss.MinOrderQty = Cob.T(row.NumVal(5), 13, 6);
        ss.PackQty = Cob.T(row.NumVal(6), 13, 6);
        ss.SrcCurrency = row.Text(7);
        ss.SrcRestriction = row.Text(8);
        var effective = row.Raw(9);
        await PriceHistoryAsync(ca, ss, ct);
        if (ss.ApprovalStatus == "SUSPENDED")
        {
            ca.ReturnCode = 8;
            ca.ReasonCode = "SRC0003";
            ca.Message = Cob.Msg("Supplier ", Cob.Trim(ss.Supplier), " is suspended as a source for item ", Cob.Trim(ss.ItemCode));
            return;
        }
        if (effective != "Y")
        {
            ca.ReturnCode = 6;
            ca.ReasonCode = "SRC0004";
            ss.NeedsAuth = "Y";
            ca.Message = Cob.Msg("The approval of supplier ", Cob.Trim(ss.Supplier), " for item ", Cob.Trim(ss.ItemCode),
                " is not effective on this date");
            Authority(ca, ss);
            return;
        }
        if (ss.ApprovalStatus == "UNAPPROVED")
        {
            ca.ReturnCode = 6;
            ca.ReasonCode = "SRC0005";
            ss.NeedsAuth = "Y";
            ca.Message = Cob.Msg("Supplier ", Cob.Trim(ss.Supplier), " is an unapproved source and needs explicit authorisation before use");
            Authority(ca, ss);
            return;
        }
        if (ss.ApprovalStatus == "CONDITION")
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "SRC0010";
            ca.Message = Cob.Msg("Supplier ", Cob.Trim(ss.Supplier), " is a conditional source: ", Cob.Trim(ss.SrcRestriction));
        }
        else
        {
            ca.ReturnCode = 0;
        }
        if (ss.Quantity > 0 && ss.MinOrderQty > 0 && ss.Quantity < ss.MinOrderQty)
        {
            ca.ReturnCode = 4;
            ca.ReasonCode = "SRC0011";
            ca.Message = Cob.Msg("Supplier ", Cob.Trim(ss.Supplier), " has a minimum order quantity of ", Cob.Qty(ss.MinOrderQty));
        }
        ss.SourceText = Cob.Str(200, "Approved source ", Cob.Trim(ss.Supplier), ", part ", Cob.Trim(ss.SupplierPart), ", contract ",
            Cob.Trim(ss.ContractRef));
    }

    /// <summary>2500-AUTHORITY: a named authoriser with a reason turns the hold into a warning.</summary>
    private static void Authority(CaHeader ca, SourcselArea ss)
    {
        if (!Cob.Spaces(ss.AuthorisedBy) && !Cob.Spaces(ss.SelectReason))
        {
            ss.NeedsAuth = "N";
            ca.ReturnCode = 4;
            ca.Message = Cob.Msg("Use of unapproved source ", Cob.Trim(ss.Supplier), " authorised by ", Cob.Trim(ss.AuthorisedBy), ": ",
                Cob.Trim(ss.SelectReason));
        }
    }

    /// <summary>2600-PRICE-HISTORY: POPRICE HISTORY on an INITIALIZEd area.</summary>
    private async Task PriceHistoryAsync(CaHeader ca, SourcselArea ss, CancellationToken ct)
    {
        var pc = CaHeader.Call("HISTORY", ca.Company, ca.Site, ca.User, ss.EventDate, ca.CorrelationId);
        var pr = new PopriceArea
        {
            Supplier = ss.Supplier,
            ItemCode = ss.ItemCode,
            PricingDate = ss.EventDate,
            Quantity = ss.Quantity,
        };
        await poprice.RunAsync(pc, pr, ct);
        if (pc.ReturnCode < 8)
        {
            ss.LastPaidPrice = pr.LastPaidPrice;
            ss.LastPaidDate = pr.LastPaidDate;
        }
    }
}
