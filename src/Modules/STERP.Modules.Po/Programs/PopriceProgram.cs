using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Po.Programs;

/// <summary>
/// POPRICE (cobol/online/po/POPRICE.cob): purchase price determination - PRICE (price book in priority order, quantity
/// break, value), BREAKS (PRICE then the break table) and HISTORY (last confirmed receipt price). Reads only.
/// </summary>
internal sealed class PopriceProgram(ISqlRunner runner)
{
    private readonly PoSql _sql = new(runner);

    private sealed class Work
    {
        public bool Fatal;
        public string EffFrom = "";
        public decimal BestPrice;
        public decimal BestDisc;
    }

    public async Task RunAsync(CaHeader ca, PopriceArea pr, CancellationToken ct)
    {
        var w = new Work();
        Init(ca, pr, w);
        if (w.Fatal) return;
        switch (ca.Function)
        {
            case "PRICE":
                await PriceAsync(ca, pr, w, ct);
                break;
            case "BREAKS":
                await PriceAsync(ca, pr, w, ct);
                if (!w.Fatal) await BreakTableAsync(ca, pr, w, ct);
                break;
            case "HISTORY":
                await HistoryAsync(ca, pr, w, ct);
                break;
            default:
                ca.ReasonCode = "POP1004";
                ca.Message = Cob.Msg("Function ", Cob.Trim(ca.Function), " is not supported by POPRICE");
                ca.ReturnCode = 8;
                break;
        }
    }

    private static void Init(CaHeader ca, PopriceArea pr, Work w)
    {
        ca.Program = "POPRICE";
        ca.ReturnCode = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        pr.UnitPrice = 0; pr.EffectivePrice = 0;
        pr.DiscountPct = 0; pr.SettDiscPct = 0;
        pr.NetValue = 0; pr.GrossValue = 0;
        pr.PriceId = 0; pr.BreakCnt = 0;
        pr.EffectiveFrom = 0; pr.EffectiveTo = 0;
        pr.BreakQtyUsed = 0;
        pr.LastPaidPrice = 0; pr.LastPaidDate = 0;
        pr.PricePer = 1;
        pr.PriceBasis = ""; pr.AgreementRef = "";
        pr.SourceText = "";
        if (Cob.Spaces(pr.Supplier) || Cob.Spaces(pr.ItemCode))
        {
            ca.ReasonCode = "POP1001";
            ca.Message = Cob.Msg("A supplier and an item are needed to price a purchase line");
            Reject(ca, w);
            return;
        }
        if (ca.Function != "HISTORY" && pr.Quantity <= 0)
        {
            ca.ReasonCode = "POP1003";
            ca.Message = Cob.Msg("A purchase quantity must be greater than zero");
            Reject(ca, w);
            return;
        }
        if (pr.PricingDate == 0) pr.PricingDate = ca.BusinessDate;
        if (Cob.Spaces(pr.Currency)) pr.Currency = "GBP";
    }

    /// <summary>2000-PRICE: item agreement before group, CONTRACT before QUOTE before LIST, priority, latest from date.</summary>
    private async Task PriceAsync(CaHeader ca, PopriceArea pr, Work w, CancellationToken ct)
    {
        var date = Cob.Int(pr.PricingDate);
        var r = await _sql.Sngl(
            "SELECT TOP 1 CAST(p.PurchasePriceId AS varchar(16)), p.PriceBasis, ISNULL(p.AgreementRef,''), p.CurrencyCode, " +
            "CAST(p.UnitPrice AS varchar(24)), CAST(p.PricePerQty AS varchar(24)), CAST(p.DiscountPct AS varchar(16)), " +
            "CAST(p.SettlementDiscountPct AS varchar(16)), CONVERT(char(8), p.EffectiveFrom, 112), " +
            "ISNULL(CONVERT(char(8), p.EffectiveTo, 112),'') , ISNULL(p.UomCode,'') FROM po.PurchasePrice p WHERE " +
            "p.CompanyCode = ? AND p.SupplierCode = ? AND p.IsActive = 1 AND (p.ItemCode = ? OR (p.ItemCode IS NULL AND " +
            "p.ItemGroupCode = ?)) AND p.EffectiveFrom <= CAST(? AS date) AND (p.EffectiveTo IS NULL OR p.EffectiveTo >= " +
            "CAST(? AS date)) ORDER BY CASE WHEN p.ItemCode IS NULL THEN 1 ELSE 0 END, CASE p.PriceBasis WHEN 'CONTRACT' " +
            "THEN 1 WHEN 'QUOTE' THEN 2 WHEN 'LIST' THEN 3 ELSE 4 END, p.Priority, p.EffectiveFrom DESC",
            [PoSql.Tx(ca.Company), PoSql.Tx(pr.Supplier), PoSql.Tx(pr.ItemCode), PoSql.Tx(pr.ItemGroup), PoSql.Tx(date), PoSql.Tx(date)],
            [16, 10, 40, 3, 24, 24, 16, 16, 8, 8, 10], ct);
        if (r.Error) { SqlError(ca, w, r); return; }
        if (r.NotFound)
        {
            ca.ReasonCode = "POP1002";
            pr.PriceBasis = "NONE";
            ca.Message = Cob.Msg("No effective purchase price for supplier ", Cob.Trim(pr.Supplier), " and item ",
                Cob.Trim(pr.ItemCode), " on the pricing date");
            pr.SourceText = Cob.X(ca.Message, 120);
            ca.ReturnCode = 16;
            w.Fatal = true;
            return;
        }
        var row = r.Row;
        pr.PriceId = (int)Cob.T(row.NumVal(0), 15, 0);
        pr.PriceBasis = row.Text(1);
        if (Cob.Spaces(pr.PriceBasis)) pr.PriceBasis = "MANUAL";
        pr.AgreementRef = row.Text(2);
        pr.UnitPrice = Cob.T(row.NumVal(4), 11, 6);
        pr.PricePer = Cob.T(row.NumVal(5), 11, 6);
        pr.DiscountPct = Cob.T(row.NumVal(6), 5, 4);
        pr.SettDiscPct = Cob.T(row.NumVal(7), 5, 4);
        w.EffFrom = row.Raw(8);
        pr.EffectiveFrom = (int)Cob.T(row.NumVal(8), 8, 0, signed: false);
        if (!Cob.Spaces(row.Raw(9))) pr.EffectiveTo = (int)Cob.T(row.NumVal(9), 8, 0, signed: false);
        if (pr.PricePer <= 0) pr.PricePer = 1;
        var currency = row.Text(3);
        if (Cob.X(currency, 3) != Cob.X(pr.Currency, 3))
        {
            ca.ReasonCode = "POP1005";
            ca.Message = Cob.Msg("The agreed price is held in ", Cob.Trim(currency), " and the order is in ", Cob.Trim(pr.Currency));
            pr.Currency = currency;
            ca.ReturnCode = 4;
        }
        await ApplyBreakAsync(ca, pr, w, ct);
        Value(pr, w);
        pr.SourceText = Cob.Str(120, Cob.Trim(pr.PriceBasis), " ", Cob.Trim(pr.AgreementRef), " effective from ",
            w.EffFrom[6..8], "/", w.EffFrom[4..6], "/", w.EffFrom[..4]);
        if (ca.ReturnCode == 0)
        {
            ca.Message = Cob.Msg("Price ", Cob.Ed(pr.EffectivePrice, "-(11)9.999999"), " from ", Cob.Trim(pr.SourceText));
        }
    }

    /// <summary>2200-APPLY-BREAK: the highest break the order quantity reaches.</summary>
    private async Task ApplyBreakAsync(CaHeader ca, PopriceArea pr, Work w, CancellationToken ct)
    {
        w.BestPrice = pr.UnitPrice;
        w.BestDisc = pr.DiscountPct;
        var r = await _sql.Sngl(
            "SELECT TOP 1 CAST(b.FromQty AS varchar(24)), CAST(b.UnitPrice AS varchar(24)), CAST(b.DiscountPct AS varchar(16)) " +
            "FROM po.PurchasePriceBreak b WHERE b.PurchasePriceId = ? AND b.FromQty <= CAST(? AS decimal(19,6)) ORDER BY " +
            "b.FromQty DESC",
            [PoSql.Int(pr.PriceId), PoSql.Tx(Cob.Ed(pr.Quantity, "-(13)9.999999"))], [24, 24, 16], ct);
        if (r.Error) { SqlError(ca, w, r); return; }
        if (r.NotFound) return;
        var bestQty = Cob.T(r.Row.NumVal(0), 13, 6);
        w.BestPrice = Cob.T(r.Row.NumVal(1), 11, 6);
        w.BestDisc = Cob.T(r.Row.NumVal(2), 5, 4);
        pr.BreakQtyUsed = bestQty;
        pr.DiscountPct = w.BestDisc;
    }

    /// <summary>2400-VALUE: six-decimal unit value, the line rounded once to two decimals (D-09, COM-011).</summary>
    private static void Value(PopriceArea pr, Work w)
    {
        pr.EffectivePrice = Cob.R(w.BestPrice / pr.PricePer, 11, 6);
        var gross = Cob.R(pr.Quantity * w.BestPrice / pr.PricePer, 13, 2);
        pr.GrossValue = gross;
        var disc = Cob.R(gross * pr.DiscountPct / 100m, 13, 2);
        pr.NetValue = Cob.T(gross - disc, 13, 2);
        pr.UnitPrice = w.BestPrice;
    }

    /// <summary>3000-BREAK-TABLE: up to 10 breaks by from-quantity.</summary>
    private async Task BreakTableAsync(CaHeader ca, PopriceArea pr, Work w, CancellationToken ct)
    {
        pr.BreakCnt = 0;
        var r = await _sql.Fetch(
            "SELECT CAST(b.FromQty AS varchar(24)), CAST(b.UnitPrice AS varchar(24)), CAST(b.DiscountPct AS varchar(16)) " +
            "FROM po.PurchasePriceBreak b WHERE b.PurchasePriceId = ? ORDER BY b.FromQty",
            [PoSql.Int(pr.PriceId)], [24, 24, 16], ct);
        if (r.Error) { SqlError(ca, w, r); return; }
        foreach (var row in r.Rows)
        {
            if (pr.BreakCnt >= 10) break;
            var b = pr.Breaks[pr.BreakCnt];
            pr.BreakCnt++;
            b.BFromQty = Cob.T(row.NumVal(0), 13, 6);
            b.BUnitPrice = Cob.T(row.NumVal(1), 11, 6);
            b.BDiscPct = Cob.T(row.NumVal(2), 5, 4);
        }
    }

    /// <summary>4000-HISTORY: the last price received on a confirmed receipt.</summary>
    private async Task HistoryAsync(CaHeader ca, PopriceArea pr, Work w, CancellationToken ct)
    {
        var r = await _sql.Sngl(
            "SELECT TOP 1 CAST(l.UnitPrice AS varchar(24)), CONVERT(char(8), r.PhysicalDate, 112) FROM po.ReceiptLine l JOIN " +
            "po.Receipt r ON r.ReceiptId = l.ReceiptId WHERE r.CompanyCode = ? AND r.SupplierCode = ? AND l.ItemCode = ? AND " +
            "r.Status = 'CONFIRMED' ORDER BY r.PhysicalDate DESC, l.ReceiptLineId DESC",
            [PoSql.Tx(ca.Company), PoSql.Tx(pr.Supplier), PoSql.Tx(pr.ItemCode)], [24, 8], ct);
        if (r.Error) { SqlError(ca, w, r); return; }
        if (r.NotFound)
        {
            pr.LastPaidPrice = 0;
            pr.LastPaidDate = 0;
            ca.ReturnCode = 4;
            ca.ReasonCode = "POP1006";
            pr.PriceBasis = "NONE";
            pr.SourceText = Cob.Str(120, "No purchase history for supplier ", Cob.Trim(pr.Supplier), " and item ", Cob.Trim(pr.ItemCode));
            ca.Message = Cob.Msg("No purchase history for this supplier and item");
            return;
        }
        pr.LastPaidPrice = Cob.T(r.Row.NumVal(0), 11, 6);
        pr.LastPaidDate = (int)Cob.T(r.Row.NumVal(1), 8, 0, signed: false);
        pr.PriceBasis = "LASTPAID";
        var d = r.Row.Raw(1);
        pr.SourceText = Cob.Str(120, "LASTPAID last price received on ", d[6..8], "/", d[4..6], "/", d[..4]);
        ca.Message = Cob.Msg("Last price received ", Cob.Ed(pr.LastPaidPrice, "-(11)9.999999"), " on ", d[6..8], "/", d[4..6], "/", d[..4]);
    }

    private static void Reject(CaHeader ca, Work w)
    {
        ca.ReturnCode = 8;
        w.Fatal = true;
    }

    private static void SqlError(CaHeader ca, Work w, Sq r)
    {
        ca.ReturnCode = 12;
        ca.ReasonCode = "SQL0001";
        ca.Message = r.Err200;
        w.Fatal = true;
    }
}
