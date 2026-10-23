using Sterp.Modules.Itm.Contracts;
using Sterp.Modules.Itm.Data;

namespace Sterp.Modules.Itm.Services;

/// <summary>
/// UOMCONV (cobol/online/itm/UOMCONV.cob) functions TOSTOCK and FACTOR (FR-STERP-ITM-023). Read-only. FROMSTK, CONVERT,
/// CHECKQTY, UNITCOST and ITEMUOM are deferred (D-ITM-04).
/// </summary>
internal sealed class UomConvProgram(Sqlio sql)
{
    private const string Prog = "UOMCONV";

    private bool _error;
    private bool _itemRead;
    private string _usage = "";
    // 0400-READ-UOM outputs (WS-TEXT, WS-FACTOR, WS-IX, WS-TEXT2)
    private string _uomDim = "";
    private decimal _uomFactor;
    private int _uomDp;

    public async Task RunAsync(CaHeader ca, UomconvCommarea uc, CancellationToken ct)
    {
        Initialise(ca, uc);
        Validate(ca, uc);
        if (_error) return;
        switch (ca.Function)
        {
            case "TOSTOCK":
                await ToStockAsync(ca, uc, ct);
                break;
            case "FACTOR":
                await ResolveFactorAsync(ca, uc, ct);
                break;
            case "FROMSTK" or "CONVERT" or "CHECKQTY" or "UNITCOST" or "ITEMUOM":
                throw new NotSupportedException($"UOMCONV {ca.Function} is a test-harness-only function deferred by D-ITM-04.");
            default:
                ca.ReasonCode = "UOM0008";
                ca.SetMessage("UOMCONV does not support function " + Cob.Trim(ca.Function));
                Reject(ca);
                break;
        }
    }

    private void Initialise(CaHeader ca, UomconvCommarea uc)
    {
        _error = false;
        _itemRead = false;
        ca.Program = Prog;
        ca.ReturnCode = 0;
        ca.MsgCount = 0;
        ca.ReasonCode = "";
        ca.Message = "";
        ca.ReqRef = "ITM-005";
        uc.ResultQty = 0;
        uc.Factor = 0;
        if (ca.Function != "FROMSTK") uc.StockQty = 0;
        uc.InverseFactor = 0;
        uc.RoundedQty = 0;
        uc.Decimals = 0;
        uc.Quantum = 0;
        uc.EffFrom = null;
        uc.EffTo = null;
        uc.Lines.Clear();
        uc.UnitCostGbp = 0;
        uc.StockUom = "";
        uc.FromDim = "";
        uc.ToDim = "";
        uc.PackDesc = "";
        uc.Source = "";
        uc.Traceability = "";
        uc.Adjusted = "N";
        uc.Indivisible = "N";
        uc.FromUom = Cob.Upper(uc.FromUom, 10);
        uc.ToUom = Cob.Upper(uc.ToUom, 10);
        uc.UsageType = Cob.Upper(uc.UsageType, 10);
        uc.ItemCode = Cob.Upper(uc.ItemCode, 40);
        uc.EnteredQty = Cob.Q(uc.EnteredQty);
        _usage = Cob.Spaces(uc.UsageType) ? "PURCHASE" : uc.UsageType;
        var eff = Cob.Date8(uc.EffDate);
        if (eff == 0) eff = ca.BusinessDate;
        if (eff == 0) eff = 20260401;
        uc.EffDate = Cob.Date(eff);
        _effDate = eff;
    }

    private int _effDate;

    private void Validate(CaHeader ca, UomconvCommarea uc)
    {
        if (Cob.Spaces(ca.Company))
        {
            ca.ReasonCode = "UOM0001";
            ca.SetMessage("A company must be supplied to convert units");
            Reject(ca);
            return;
        }
        if (ca.Function == "CONVERT") return;
        if (Cob.Spaces(uc.ItemCode))
        {
            ca.ReasonCode = "UOM0001";
            ca.SetMessage("An item code is required for this conversion");
            Reject(ca);
        }
    }

    /// <summary>0300-READ-ITEM.</summary>
    private async Task ReadItemAsync(CaHeader ca, UomconvCommarea uc, CancellationToken ct)
    {
        if (_itemRead) return;
        var r = await sql.SingleAsync(
            "SELECT StockUomCode, QuantumQty, IsIndivisible, TraceabilityType, LifecycleStatus FROM inv.Item WHERE CompanyCode = @p1 AND ItemCode = @p2",
            [ca.Company, uc.ItemCode], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        if (r.NotFound)
        {
            ca.ReasonCode = "UOM0001";
            ca.SetMessage("Item " + Cob.Trim(uc.ItemCode) + " is not held in company " + Cob.Trim(ca.Company));
            ca.ReturnCode = 16;
            _error = true;
            ca.AddMessage();
            return;
        }
        var row = r.Row;
        uc.StockUom = Cob.X(Col.Text(row, 0), 10);
        uc.Quantum = Cob.Q(Col.Num(row, 1));
        uc.Indivisible = Col.Bit(row, 2) ? "Y" : "N";
        uc.Traceability = Cob.X(Col.Text(row, 3), 8);
        if (uc.Traceability == "SERIAL")
        {
            uc.Indivisible = "Y";
            if (uc.Quantum != 1) uc.Quantum = 1;
        }
        _itemRead = true;
    }

    /// <summary>0400-READ-UOM.</summary>
    private async Task ReadUomAsync(CaHeader ca, string code, CancellationToken ct)
    {
        var r = await sql.SingleAsync(
            "SELECT UomDimension, FactorToBase, DecimalPlaces, ISNULL(BaseUomCode,'') FROM inv.Uom WHERE UomCode = @p1 AND IsActive = 1",
            [code], ct);
        _uomDim = "";
        _uomFactor = 0;
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        if (r.NotFound)
        {
            ca.ReasonCode = "UOM0002";
            ca.SetMessage("Unit of measure " + Cob.Trim(code) + " is not known");
            Reject(ca);
            return;
        }
        var row = r.Row;
        _uomDim = Cob.X(Col.Text(row, 0), 200);
        _uomFactor = Cob.F(Col.Num(row, 1));
        _uomDp = (int)Math.Truncate(Col.Num(row, 2));
    }

    /// <summary>1000-TO-STOCK: stock quantity = entered quantity x factor ROUNDED 6 dp, then the quantum check.</summary>
    private async Task ToStockAsync(CaHeader ca, UomconvCommarea uc, CancellationToken ct)
    {
        await ResolveFactorAsync(ca, uc, ct);
        if (ca.Failed) return;
        uc.StockQty = Cob.QR(uc.EnteredQty * uc.Factor);
        uc.ResultQty = uc.StockQty;
        uc.ToUom = uc.StockUom;
        await CheckQuantumAsync(ca, uc, ct);
        if (ca.Failed) return;
        var first = Cob.Str(200, Cob.QtyEd(uc.StockQty), " ", Cob.Trim(uc.StockUom), " at ");
        ca.SetMessage(Cob.Str(200, Cob.Trim(first), Cob.NumEd(uc.Factor), " per ", Cob.Trim(uc.FromUom)));
    }

    /// <summary>3000-RESOLVE-FACTOR.</summary>
    private async Task ResolveFactorAsync(CaHeader ca, UomconvCommarea uc, CancellationToken ct)
    {
        await ReadItemAsync(ca, uc, ct);
        if (ca.Failed) return;
        if (Cob.Spaces(uc.FromUom)) uc.FromUom = uc.StockUom;
        await ItemFactorAsync(ca, uc, ct);
        if (ca.Failed) return;
        if (uc.Factor == 0)
        {
            if (uc.FromUom == uc.StockUom)
            {
                uc.Factor = 1;
                uc.Source = "IDENTITY";
            }
            else
            {
                await DimensionalFactorAsync(ca, uc, ct);
                if (ca.Failed) return;
                if (uc.Factor == 0)
                {
                    ca.ReasonCode = "UOM0003";
                    ca.SetMessage("No effective conversion from " + Cob.Trim(uc.FromUom) + " to " + Cob.Trim(uc.StockUom) +
                        " for item " + Cob.Trim(uc.ItemCode));
                    Reject(ca);
                    return;
                }
                GuardPack(ca, uc);
            }
        }
        if (ca.Failed) return;
        if (uc.Factor <= 0)
        {
            ca.ReasonCode = "UOM0004";
            ca.SetMessage("A conversion factor must be greater than zero");
            Reject(ca);
            return;
        }
        uc.InverseFactor = Cob.FR(1m / uc.Factor);
    }

    /// <summary>3100-ITEM-FACTOR: the stated usage first, then any usage.</summary>
    private async Task ItemFactorAsync(CaHeader ca, UomconvCommarea uc, CancellationToken ct)
    {
        uc.Factor = 0;
        var done = false;
        while (!done)
        {
            await ItemFactorReadAsync(ca, uc, ct);
            if (ca.Failed) done = true;
            else if (uc.Factor != 0 || _usage == "ANY") done = true;
            else _usage = "ANY";
        }
    }

    /// <summary>3150-ITEM-FACTOR-READ.</summary>
    private async Task ItemFactorReadAsync(CaHeader ca, UomconvCommarea uc, CancellationToken ct)
    {
        var d = Cob.D8(_effDate);
        var r = await sql.SingleAsync(
            "SELECT TOP 1 StockUnitsPerUom, ISNULL(PackDescription,''), CONVERT(char(8), EffectiveFrom, 112), " +
            "ISNULL(CONVERT(char(8), EffectiveTo, 112),'') FROM inv.ItemUom WHERE CompanyCode = @p1 AND ItemCode = @p2 AND UomCode = @p3 " +
            "AND (UsageType = @p4 OR @p5 = 'ANY') AND EffectiveFrom <= CONVERT(date, @p6, 112) AND (EffectiveTo IS NULL OR " +
            "EffectiveTo >= CONVERT(date, @p7, 112)) ORDER BY EffectiveFrom DESC",
            [ca.Company, uc.ItemCode, uc.FromUom, _usage, _usage, d, d], ct);
        if (r.IsError)
        {
            SqlFailed(ca, r);
            return;
        }
        if (r.NotFound) return;
        var row = r.Row;
        uc.Factor = Cob.F(Col.Num(row, 0));
        uc.PackDesc = Cob.X(Col.Text(row, 1), 40);
        uc.EffFrom = Cob.Date((int)Col.Num(row, 2));
        uc.EffTo = Cob.Spaces(Col.Text(row, 3)) ? null : Cob.Date((int)Col.Num(row, 3));
        uc.Source = "ITEMUOM";
    }

    /// <summary>3200-DIMENSIONAL-FACTOR.</summary>
    private async Task DimensionalFactorAsync(CaHeader ca, UomconvCommarea uc, CancellationToken ct)
    {
        await ReadUomAsync(ca, uc.FromUom, ct);
        if (ca.Failed) return;
        var fromDim = Cob.X(_uomDim, 10);
        var fromFactor = _uomFactor;
        await ReadUomAsync(ca, uc.StockUom, ct);
        if (ca.Failed) return;
        var toDim = Cob.X(_uomDim, 10);
        var toFactor = _uomFactor;
        uc.FromDim = fromDim;
        uc.ToDim = toDim;
        uc.Decimals = _uomDp % 100;
        if (fromDim != toDim)
        {
            ca.ReasonCode = "UOM0005";
            ca.SetMessage("Units " + Cob.Trim(uc.FromUom) + " (" + Cob.Trim(fromDim) + ") and " + Cob.Trim(uc.StockUom) + " (" +
                Cob.Trim(toDim) + ") measure different things");
            Reject(ca);
            return;
        }
        if (toFactor == 0)
        {
            ca.ReasonCode = "UOM0004";
            ca.SetMessage("The stock unit has no factor to its base unit");
            Reject(ca);
            return;
        }
        uc.Factor = Cob.FR(fromFactor / toFactor);
        uc.Source = "UOM";
    }

    /// <summary>3400-GUARD-PACK: a dimensional factor of exactly one between two different EACH units is refused.</summary>
    private void GuardPack(CaHeader ca, UomconvCommarea uc)
    {
        if (uc.Source != "UOM" || uc.FromDim != "EACH" || uc.Factor != 1 || uc.FromUom == uc.StockUom) return;
        uc.Factor = 0;
        ca.ReasonCode = "UOM0003";
        ca.SetMessage("Unit " + Cob.Trim(uc.FromUom) + " is a pack unit: approve how many " + Cob.Trim(uc.StockUom) + " of item " +
            Cob.Trim(uc.ItemCode) + " it holds");
        Reject(ca);
    }

    /// <summary>5000-CHECK-QUANTUM.</summary>
    private async Task CheckQuantumAsync(CaHeader ca, UomconvCommarea uc, CancellationToken ct)
    {
        await ReadItemAsync(ca, uc, ct);
        if (ca.Failed) return;
        if (uc.StockQty == 0) uc.StockQty = uc.EnteredQty;
        uc.RoundedQty = uc.StockQty;
        uc.Adjusted = "N";
        if (uc.Quantum <= 0) uc.Quantum = 1;
        if (uc.Indivisible != "Y") return;
        var multiples = Sterp.Platform.Kernel.Cobol.CobolNumeric.Store(uc.StockQty / uc.Quantum, 15, 6);
        var whole = Math.Floor(multiples);
        var remain = Cob.F(multiples - whole);
        if (remain < 0.0000005m && remain > -0.0000005m) return;
        uc.RoundedQty = Cob.Q((whole + 1) * uc.Quantum);
        uc.Adjusted = "Y";
        ca.ReasonCode = "UOM0006";
        var q = Cob.QtyEd(uc.Quantum);
        ca.SetMessage(uc.Traceability == "SERIAL"
            ? "Item " + Cob.Trim(uc.ItemCode) + " is serialised and moves in whole units of " + q
            : "Item " + Cob.Trim(uc.ItemCode) + " moves in multiples of " + q + " " + Cob.Trim(uc.StockUom));
        Reject(ca);
    }

    private void Reject(CaHeader ca)
    {
        _error = true;
        ca.ReturnCode = 8;
        ca.AddMessage();
    }

    private void SqlFailed(CaHeader ca, SqlioOutcome r)
    {
        _error = true;
        ca.SetMessage(r.Message200(Prog));
        ca.ReasonCode = "SQL0001";
        ca.ReturnCode = 12;
        ca.AddMessage();
    }
}
