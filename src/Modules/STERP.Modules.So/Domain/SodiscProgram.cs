using Sterp.Modules.Itm.Contracts;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.So.Domain;

/// <summary>
/// SODISC (cobol/online/so/SODISC.cob, FR-SODISC-001..019): the sales calculation sequence - quantity to price unit, extend,
/// sequential discounts, line and document allowances (RNDALLOC), charges, net, VAT, margin, document totals and the
/// control-total reconciliation (FR-STERP-SO-012). No writes; UOMCONV reads run on the request's engine connection.
/// </summary>
public sealed class SodiscProgram(IRoundingAllocator rnd, IUnitConversionService uomconv)
{
    private const string Program = "SODISC";

    private sealed class State
    {
        public bool InputBad;
        public bool LineBad;
        public int Lines;
        public int Places = 2;
        public string AllocMode = "WEIGHT";
        public decimal WeightTot;
        public decimal Chg;
        public readonly decimal[] Discounted = new decimal[200];
        public readonly decimal[] AfterLine = new decimal[200];
        public readonly decimal[] Weight = new decimal[200];
    }

    public async Task RunAsync(SodiscArea sd, CancellationToken ct = default)
    {
        var s = new State();
        Initialise(sd, s);
        Validate(sd, s);
        if (s.InputBad) return;
        switch (sd.Header.Function)
        {
            case "LINE":
                s.Lines = 1;
                await DocumentAsync(sd, s, ct);
                break;
            case "DOCUMENT":
            case "RECALC":
                await DocumentAsync(sd, s, ct);
                break;
            default:
                sd.Header.ReasonCode = "DSC0008";
                sd.Header.Message = Cob.Str(200, "Function ", Cob.Trim(sd.Header.Function), " is not one of LINE, DOCUMENT or RECALC");
                Reject(sd, s);
                break;
        }
    }

    /// <summary>0100-INITIALISE.</summary>
    private static void Initialise(SodiscArea sd, State s)
    {
        var h = sd.Header;
        h.Program = Program;
        h.ReturnCode = 0;
        h.MessageCount = 0;
        h.ReasonCode = "";
        h.Message = "";
        h.RequestRef = "SO-005";
        h.Function = Cob.Upper(h.Function, 8);
        if (sd.RoundPlaces < 0 || sd.RoundPlaces > 6) sd.RoundPlaces = 2;
        s.Places = sd.RoundPlaces;
        if (Cob.Spaces(sd.Currency)) sd.Currency = "GBP";
        if (Cob.Spaces(sd.ChargeVatBasis)) sd.ChargeVatBasis = "GOODS";
        if (sd.PriceDate == 0) sd.PriceDate = h.BusinessDate;
        sd.AlreadyApplied = "N";
        sd.Reconciled = "N";
        sd.Explanation = "";
        sd.TotGross = 0; sd.TotDiscount = 0; sd.TotAllowance = 0;
        sd.TotCharge = 0; sd.TotNet = 0; sd.TotVat = 0;
        sd.TotGrossTotal = 0; sd.TotCostGbp = 0;
        sd.DocMarginPct = 0; sd.AllocResidual = 0;
        sd.AllocBumped = 0;
    }

    /// <summary>0200-VALIDATE.</summary>
    private static void Validate(SodiscArea sd, State s)
    {
        if (Cob.Spaces(sd.Header.Company))
        {
            sd.Header.ReasonCode = "DSC0001";
            sd.Header.Message = "A company is required";
            Reject(sd, s);
            return;
        }
        if (sd.Header.Function == "LINE") sd.LineCnt = 1;
        if (sd.LineCnt < 1)
        {
            sd.Header.ReasonCode = "DSC0002";
            sd.Header.Message = "No lines were supplied to calculate";
            Reject(sd, s);
            return;
        }
        if (sd.LineCnt > 200)
        {
            sd.Header.ReasonCode = "DSC0003";
            sd.Header.Message = "A document is limited to 200 lines";
            Reject(sd, s);
            return;
        }
        s.Lines = sd.LineCnt;
    }

    /// <summary>2000-DOCUMENT: the seven steps in their fixed order.</summary>
    private async Task DocumentAsync(SodiscArea sd, State s, CancellationToken ct)
    {
        Signature(sd, s);
        for (var ix = 1; ix <= s.Lines; ix++) await LineStepsAsync(sd, s, ix, ct);
        if (s.InputBad) return;
        DocAllowance(sd, s);
        if (s.InputBad) return;
        DocCharge(sd, s);
        if (s.InputBad) return;
        for (var ix = 1; ix <= s.Lines; ix++)
        {
            LineNet(sd, s, ix);
            LineVat(sd, s, ix);
            LineMargin(sd, ix);
        }
        if (s.InputBad) return;
        Totals(sd, s);
        Reconcile(sd, s);
        Explain(sd);
    }

    /// <summary>2100-SIGNATURE: the input signature behind the result (SD-APPLIED-TAG, SD-ALREADY-APPLIED).</summary>
    private static void Signature(SodiscArea sd, State s)
    {
        var sig = Cob.St(s.Lines + sd.DocAllowance * 41 + sd.DocCharge * 43 + sd.RoundPlaces * 47, 15, 4);
        for (var ix = 1; ix <= s.Lines; ix++)
        {
            var l = sd.Line[ix - 1];
            sig = Cob.St(sig
                + l.LEntQty * 3
                + l.LFactor * 5
                + l.LUnitPrice * 7
                + l.LPerQty * 11
                + l.LDiscPct1 * 13
                + l.LDiscPct2 * 17
                + l.LDiscPct3 * 19
                + l.LLineAllow * 23
                + l.LCharge * 29
                + l.LVatRate * 31
                + l.LCostGbp * 37
                + ix, 15, 4);
        }
        var ed = Cob.EdField(sig, "-(15)9.9(4)");
        var tag = CobolText.Fit(sd.AppliedTag, 32);
        if (!CobolText.IsSpaces(tag) && tag[..2] == "S:" && sd.Header.Function != "RECALC" && tag.Substring(2, 21) == ed)
        {
            sd.AlreadyApplied = "Y";
        }
        sd.AppliedTag = Cob.Str(32, "S:", ed);
    }

    /// <summary>3000-LINE-STEPS: one line, steps one to four.</summary>
    private async Task LineStepsAsync(SodiscArea sd, State s, int ix, CancellationToken ct)
    {
        var l = sd.Line[ix - 1];
        s.LineBad = false;
        l.LWhy = "";
        l.LStatus = "OK";
        l.LPriceQty = 0; l.LGrossAmt = 0;
        l.LDiscAmt = 0; l.LAllowTot = 0;
        l.LGoodsNet = 0; l.LNetAmt = 0;
        l.LVatAmt = 0; l.LGrossTot = 0;
        l.LMarginPct = 0;
        await ConvertQtyAsync(sd, s, ix, ct);
        if (s.LineBad) return;
        Extend(sd, s, ix);
        if (s.LineBad) return;
        Discounts(sd, s, ix);
        if (s.LineBad) return;
        LineAllowance(sd, s, ix);
    }

    /// <summary>3100-CONVERT-QTY: step one - a missing conversion is an error, a factor of one is never assumed.</summary>
    private async Task ConvertQtyAsync(SodiscArea sd, State s, int ix, CancellationToken ct)
    {
        var l = sd.Line[ix - 1];
        if (l.LEntQty <= 0)
        {
            sd.Header.ReasonCode = "DSC0004";
            sd.Header.Message = "Quantity must be greater than zero";
            LineReject(sd, s, ix);
            return;
        }
        if (l.LFactor > 0)
        {
            l.LWhy = "captured factor";
            l.LPriceQty = Cob.Rd(l.LEntQty * l.LFactor, 13, 6);
            return;
        }
        if (Cob.Spaces(l.LItemCode))
        {
            sd.Header.ReasonCode = "DSC0007";
            sd.Header.Message = Cob.Str(200, "No unit conversion was supplied and a ", "factor of one is never assumed");
            LineReject(sd, s, ix);
            return;
        }
        await AskUomconvAsync(sd, s, ix, ct);
    }

    /// <summary>3200-EXTEND: step two - unit price = price / per (6 dp), gross = price quantity x unit price.</summary>
    private void Extend(SodiscArea sd, State s, int ix)
    {
        var l = sd.Line[ix - 1];
        if (l.LPerQty <= 0)
        {
            sd.Header.ReasonCode = "DSC0005";
            sd.Header.Message = "The price-per quantity must be positive";
            LineReject(sd, s, ix);
            return;
        }
        var unit = rnd.UnitPrice(l.LUnitPrice, l.LPerQty, 6);
        if (unit.Header.ReturnCode != 0)
        {
            sd.Header.ReasonCode = "DSC0005";
            sd.Header.Message = Cob.X(unit.Header.Message, 200);
            LineReject(sd, s, ix);
            return;
        }
        var unitEff = Cob.St(unit.ResultValue, 13, 12);
        var gross = rnd.Extend(l.LPriceQty, unitEff, s.Places);
        l.LGrossAmt = Cob.St(gross.ResultValue, 13, 2);
    }

    /// <summary>3300-DISCOUNTS: step three - sequential, not additive, each step rounded.</summary>
    private void Discounts(SodiscArea sd, State s, int ix)
    {
        var l = sd.Line[ix - 1];
        var running = Cob.St(l.LGrossAmt, 13, 6);
        for (var jx = 1; jx <= 3; jx++)
        {
            var pct = jx switch { 1 => l.LDiscPct1, 2 => l.LDiscPct2, _ => l.LDiscPct3 };
            if (pct == 0) continue;
            if (pct > 100 || pct < -100)
            {
                sd.Header.ReasonCode = "DSC0006";
                sd.Header.Message = "A discount percentage is out of range";
                LineReject(sd, s, ix);
                return;
            }
            var factor = Cob.St((100 - pct) / 100, 5, 12);
            var r = rnd.Extend(running, factor, s.Places);
            running = Cob.St(r.ResultValue, 13, 6);
        }
        l.LDiscAmt = Cob.St(l.LGrossAmt - running, 13, 2);
        s.Discounted[ix - 1] = Cob.St(running, 13, 2);
    }

    /// <summary>3400-LINE-ALLOWANCE.</summary>
    private static void LineAllowance(SodiscArea sd, State s, int ix)
    {
        var l = sd.Line[ix - 1];
        s.AfterLine[ix - 1] = Cob.St(s.Discounted[ix - 1] - l.LLineAllow, 13, 2);
        if (l.LLineAllow > s.Discounted[ix - 1])
        {
            l.LStatus = "REVIEW";
            l.LWhy = "allowance exceeds line";
            if (sd.Header.ReturnCode < 4)
            {
                sd.Header.ReturnCode = 4;
                sd.Header.ReasonCode = "DSC0009";
                sd.Header.Message = "A line allowance exceeds the line value";
                sd.Header.MessageCount++;
            }
        }
    }

    /// <summary>2300-DOC-ALLOWANCE: step four - RNDALLOC spreads the document allowance by discounted line value.</summary>
    private void DocAllowance(SodiscArea sd, State s)
    {
        for (var ix = 1; ix <= s.Lines; ix++) sd.Line[ix - 1].LDocAllow = 0;
        if (sd.DocAllowance == 0) return;
        s.WeightTot = 0;
        for (var ix = 1; ix <= s.Lines; ix++)
        {
            s.Weight[ix - 1] = s.AfterLine[ix - 1] > 0 ? s.AfterLine[ix - 1] : 0;
            s.WeightTot = Cob.St(s.WeightTot + s.Weight[ix - 1], 15, 6);
        }
        s.AllocMode = s.WeightTot == 0 ? "EQUAL" : "WEIGHT";
        if (s.WeightTot > 0 && sd.DocAllowance > s.WeightTot)
        {
            sd.Header.ReasonCode = "DSC0009";
            sd.Header.Message = Cob.Str(200, "Document allowance of ", Cob.Ed(sd.DocAllowance, "-(13)9.99"), " exceeds the discounted line value");
            Reject(sd, s);
            return;
        }
        var alloc = Allocate(sd, s, sd.DocAllowance);
        if (s.InputBad || alloc is null) return;
        for (var ix = 1; ix <= s.Lines; ix++) sd.Line[ix - 1].LDocAllow = Cob.St(Part(alloc, ix), 13, 2);
        sd.AllocResidual = Cob.St(alloc.Residual, 13, 2);
        sd.AllocBumped = alloc.Bumped;
    }

    /// <summary>2400-DOC-CHARGE: step five - the document charge is spread so it can follow the goods' VAT.</summary>
    private void DocCharge(SodiscArea sd, State s)
    {
        for (var ix = 1; ix <= s.Lines; ix++) sd.Line[ix - 1].LDocCharge = 0;
        if (sd.DocCharge == 0) return;
        s.WeightTot = 0;
        for (var ix = 1; ix <= s.Lines; ix++)
        {
            var amt2 = Cob.St(s.AfterLine[ix - 1] - sd.Line[ix - 1].LDocAllow, 13, 6);
            s.Weight[ix - 1] = amt2 > 0 ? Cob.St(amt2, 13, 2) : 0;
            s.WeightTot = Cob.St(s.WeightTot + s.Weight[ix - 1], 15, 6);
        }
        s.AllocMode = s.WeightTot == 0 ? "EQUAL" : "WEIGHT";
        var alloc = Allocate(sd, s, sd.DocCharge);
        if (s.InputBad || alloc is null) return;
        for (var ix = 1; ix <= s.Lines; ix++) sd.Line[ix - 1].LDocCharge = Cob.St(Part(alloc, ix), 13, 2);
    }

    /// <summary>3500-LINE-NET.</summary>
    private static void LineNet(SodiscArea sd, State s, int ix)
    {
        var l = sd.Line[ix - 1];
        l.LAllowTot = Cob.St(l.LLineAllow + l.LDocAllow, 13, 2);
        l.LGoodsNet = Cob.St(s.Discounted[ix - 1] - l.LAllowTot, 13, 2);
        s.Chg = Cob.St(l.LCharge + l.LDocCharge, 13, 2);
        l.LNetAmt = Cob.St(l.LGoodsNet + s.Chg, 13, 2);
    }

    /// <summary>3600-LINE-VAT: step six - charges follow the goods unless a separate charge code is fixed.</summary>
    private void LineVat(SodiscArea sd, State s, int ix)
    {
        var l = sd.Line[ix - 1];
        decimal vatGoods = 0, vatCharge = 0;
        if (l.LVatCode is "S" or "R" && l.LVatRate == 0)
        {
            sd.Header.ReasonCode = "DSC0011";
            sd.Header.Message = Cob.Str(200, "A standard or reduced rate line needs a ", "VAT rate - zero is not assumed");
            LineReject(sd, s, ix);
            return;
        }
        if (sd.ChargeVatBasis == "FIXED" && s.Chg != 0 && !Cob.Spaces(sd.DocChargeVat))
        {
            l.LChargeVat = Cob.X(sd.DocChargeVat, 4);
            vatGoods = Cob.St(Round(Cob.St(l.LGoodsNet * l.LVatRate / 100, 13, 6), s), 13, 2);
            vatCharge = Cob.St(Round(Cob.St(s.Chg * sd.ChargeVatRate / 100, 13, 6), s), 13, 2);
        }
        else
        {
            l.LChargeVat = Cob.X(l.LVatCode, 4);
            vatGoods = Cob.St(Round(Cob.St(l.LNetAmt * l.LVatRate / 100, 13, 6), s), 13, 2);
        }
        l.LVatAmt = Cob.St(vatGoods + vatCharge, 13, 2);
        l.LGrossTot = Cob.St(l.LNetAmt + l.LVatAmt, 13, 2);
    }

    /// <summary>3700-LINE-MARGIN: the display margin on net against the captured cost.</summary>
    private static void LineMargin(SodiscArea sd, int ix)
    {
        var l = sd.Line[ix - 1];
        if (l.LCostGbp != 0 && l.LNetAmt != 0)
        {
            l.LMarginPct = Cob.Rd((l.LNetAmt - l.LCostGbp) / l.LNetAmt * 100, 5, 4);
        }
        else
        {
            l.LMarginPct = 0;
            if (Cob.Spaces(l.LWhy)) l.LWhy = "no cost - ask SOMARGN";
        }
    }

    /// <summary>2600-TOTALS.</summary>
    private static void Totals(SodiscArea sd, State s)
    {
        sd.TotGross = 0; sd.TotDiscount = 0; sd.TotAllowance = 0;
        sd.TotCharge = 0; sd.TotNet = 0; sd.TotVat = 0;
        sd.TotGrossTotal = 0; sd.TotCostGbp = 0;
        for (var ix = 1; ix <= s.Lines; ix++)
        {
            var l = sd.Line[ix - 1];
            sd.TotGross = Cob.St(sd.TotGross + l.LGrossAmt, 13, 2);
            sd.TotDiscount = Cob.St(sd.TotDiscount + l.LDiscAmt, 13, 2);
            sd.TotAllowance = Cob.St(sd.TotAllowance + l.LAllowTot, 13, 2);
            sd.TotCharge = Cob.St(sd.TotCharge + l.LCharge, 13, 2);
            sd.TotCharge = Cob.St(sd.TotCharge + l.LDocCharge, 13, 2);
            sd.TotNet = Cob.St(sd.TotNet + l.LNetAmt, 13, 2);
            sd.TotVat = Cob.St(sd.TotVat + l.LVatAmt, 13, 2);
            sd.TotGrossTotal = Cob.St(sd.TotGrossTotal + l.LGrossTot, 13, 2);
            sd.TotCostGbp = Cob.St(sd.TotCostGbp + l.LCostGbp, 13, 2);
        }
        sd.DocMarginPct = sd.TotNet != 0 && sd.TotCostGbp != 0
            ? Cob.Rd((sd.TotNet - sd.TotCostGbp) / sd.TotNet * 100, 5, 4)
            : 0;
    }

    /// <summary>2700-RECONCILE: the control-total proof on every line and on the document (RC 12 DSC0010).</summary>
    private static void Reconcile(SodiscArea sd, State s)
    {
        sd.Reconciled = "Y";
        var checkNet = Cob.St(sd.TotGross - sd.TotDiscount - sd.TotAllowance + sd.TotCharge, 15, 2);
        if (checkNet != sd.TotNet) sd.Reconciled = "N";
        var checkGross = Cob.St(sd.TotNet + sd.TotVat, 15, 2);
        if (checkGross != sd.TotGrossTotal) sd.Reconciled = "N";
        for (var ix = 1; ix <= s.Lines; ix++)
        {
            var l = sd.Line[ix - 1];
            checkGross = Cob.St(l.LNetAmt + l.LVatAmt, 15, 2);
            if (checkGross != l.LGrossTot) sd.Reconciled = "N";
        }
        if (sd.Reconciled != "Y")
        {
            sd.Header.ReasonCode = "DSC0010";
            sd.Header.Message = "Line and document totals do not reconcile";
            sd.Header.ReturnCode = 12;
            s.InputBad = true;
            sd.Header.MessageCount++;
        }
    }

    /// <summary>2800-EXPLAIN: the totals sentence (STRING with FUNCTION TRIM of the target).</summary>
    private static void Explain(SodiscArea sd)
    {
        const string ed = "-(13)9.99";
        var e = Cob.Str(200, "Gross ", Cob.Ed(sd.TotGross, ed));
        e = Cob.Overlay(e, 200, Cob.Trim(e), ", sequential discount ", Cob.Ed(sd.TotDiscount, ed));
        e = Cob.Overlay(e, 200, Cob.Trim(e), ", allowance ", Cob.Ed(sd.TotAllowance, ed));
        e = Cob.Overlay(e, 200, Cob.Trim(e), ", charges ", Cob.Ed(sd.TotCharge, ed));
        e = Cob.Overlay(e, 200, Cob.Trim(e), ", net ", Cob.Ed(sd.TotNet, ed));
        e = Cob.Overlay(e, 200, Cob.Trim(e), ", VAT ", Cob.Ed(sd.TotVat, ed));
        e = Cob.Overlay(e, 200, Cob.Trim(e), ", gross total ", Cob.Ed(sd.TotGrossTotal, ed));
        if (sd.AlreadyApplied == "Y") e = Cob.Overlay(e, 200, Cob.Trim(e), " (repeat request, figures unchanged)");
        sd.Explanation = e;
        if (sd.Header.ReturnCode == 0 && Cob.Spaces(sd.Header.Message)) sd.Header.Message = Cob.X(sd.Explanation, 200);
    }

    /// <summary>4000-ALLOCATE: RNDALLOC ALLOC of the amount over the line weights; a refusal is RC 12 DSC0010.</summary>
    private RndallocArea? Allocate(SodiscArea sd, State s, decimal amount)
    {
        var weights = new decimal[s.Lines];
        for (var jx = 0; jx < s.Lines; jx++) weights[jx] = s.Weight[jx];
        var a = rnd.Allocate(Cob.St(amount, 13, 6), s.Places, s.AllocMode, weights);
        if (a.Header.ReturnCode != 0)
        {
            sd.Header.ReasonCode = "DSC0010";
            sd.Header.Message = Cob.X(a.Header.Message, 200);
            sd.Header.ReturnCode = 12;
            s.InputBad = true;
            sd.Header.MessageCount++;
            return null;
        }
        return a;
    }

    private static decimal Part(RndallocArea a, int ix) => ix - 1 < a.Lines.Count ? a.Lines[ix - 1].Part : 0m;

    /// <summary>4100-ROUND-AMT1: RNDALLOC ROUND to SD-ROUND-PLACES, held in WS-AMT1 S9(13)V9(6).</summary>
    private decimal Round(decimal amt1, State s) => Cob.St(rnd.Round(amt1, s.Places).ResultValue, 13, 6);

    /// <summary>5000-ASK-UOMCONV: TOSTOCK on the sales usage, then FACTOR to a non-stock price unit.</summary>
    private async Task AskUomconvAsync(SodiscArea sd, State s, int ix, CancellationToken ct)
    {
        var l = sd.Line[ix - 1];
        var h = sd.Header;
        var biz = CobolDate.FromYyyymmdd(h.BusinessDate);
        var uc = new UomconvCommarea
        {
            ItemCode = l.LItemCode,
            FromUom = l.LEntUom,
            UsageType = "SALES",
            EffDate = CobolDate.FromYyyymmdd(sd.PriceDate),
            EnteredQty = l.LEntQty,
        };
        var r = await uomconv.ExecuteAsync(UomconvFunctions.ToStock, uc, new ItmCall(h.Company, h.Site, h.User, biz), ct);
        if (r.ReturnCode != 0)
        {
            h.ReasonCode = "DSC0007";
            h.Message = Cob.X(r.Message, 200);
            LineReject(sd, s, ix);
            return;
        }
        var amt3 = Cob.St(uc.StockQty, 13, 6);
        if (Cob.Spaces(l.LPriceUom) || CobolText.EqualsPadded(l.LPriceUom, uc.StockUom))
        {
            l.LPriceQty = Cob.St(amt3, 13, 6);
            l.LWhy = "UOMCONV stock unit";
            return;
        }
        var fc = new UomconvCommarea
        {
            ItemCode = l.LItemCode,
            FromUom = l.LPriceUom,
            UsageType = "PRICE",
            EffDate = CobolDate.FromYyyymmdd(sd.PriceDate),
        };
        var f = await uomconv.ExecuteAsync(UomconvFunctions.Factor, fc, new ItmCall(h.Company, "", "", biz), ct);
        if (f.ReturnCode != 0 || fc.Factor == 0)
        {
            h.ReasonCode = "DSC0007";
            h.Message = Cob.Str(200, "No effective conversion to price unit ", Cob.Trim(l.LPriceUom));
            LineReject(sd, s, ix);
            return;
        }
        var q = rnd.UnitPrice(amt3, fc.Factor, 6);
        l.LPriceQty = Cob.St(q.ResultValue, 13, 6);
        l.LWhy = "UOMCONV price unit";
    }

    private static void Reject(SodiscArea sd, State s)
    {
        sd.Header.ReturnCode = 8;
        s.InputBad = true;
        sd.Header.MessageCount++;
    }

    /// <summary>9200-LINE-REJECT: line ERROR with the reason, " (line n)" in CA-MESSAGE(181:20).</summary>
    private static void LineReject(SodiscArea sd, State s, int ix)
    {
        var l = sd.Line[ix - 1];
        s.LineBad = true;
        l.LStatus = "ERROR";
        l.LWhy = Cob.X(sd.Header.ReasonCode, 30);
        var msg = CobolText.Fit(sd.Header.Message, 200);
        sd.Header.Message = (msg[..180] + CobolText.Fit(" (line " + Cob.Ed(ix, "-(8)9") + ")", 20)).TrimEnd(' ');
        Reject(sd, s);
    }
}
