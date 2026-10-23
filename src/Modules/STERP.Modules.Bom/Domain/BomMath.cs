namespace Sterp.Modules.Bom.Domain;

/// <summary>BOMMATHCA (cobol/copy/BOMMATHCA.cpy): one BOMMATH call. A fresh area is the INITIALIZE of the caller.</summary>
internal sealed class MathArea
{
    public string Function { get; set; } = "";
    public int ReturnCode { get; set; }
    public string Reason { get; set; } = "";
    public string Message { get; set; } = "";
    public int MessageCount { get; set; }

    /// <summary>BM-GOOD-QTY S9(15)V9(6).</summary>
    public decimal GoodQty { get; set; }
    /// <summary>BM-YIELD S9(5)V9(6).</summary>
    public decimal Yield { get; set; }
    /// <summary>BM-QUANTUM S9(13)V9(6).</summary>
    public decimal Quantum { get; set; }
    /// <summary>BM-BATCH-SIZE S9(13)V9(6).</summary>
    public decimal BatchSize { get; set; }
    /// <summary>BM-NOMINAL-BASIS S9(13)V9(6).</summary>
    public decimal NominalBasis { get; set; }
    /// <summary>BM-USAGE-QTY S9(13)V9(6).</summary>
    public decimal UsageQty { get; set; }
    /// <summary>BM-CONV-FACT S9(9)V9(9).</summary>
    public decimal ConvFact { get; set; }
    /// <summary>BM-SCRAP-PCT S9(5)V9(6).</summary>
    public decimal ScrapPct { get; set; }
    public string UsageBasis { get; set; } = "";
    public int RunCount { get; set; }
    public int BatchCount { get; set; }
    /// <summary>BM-GROSS-RAW S9(15)V9(9).</summary>
    public decimal GrossRaw { get; set; }
    /// <summary>BM-GROSS-STARTS S9(15)V9(6).</summary>
    public decimal GrossStarts { get; set; }
    /// <summary>BM-UNROUNDED S9(15)V9(9).</summary>
    public decimal Unrounded { get; set; }
    public decimal YieldContrib { get; set; }
    public decimal ScrapContrib { get; set; }
    /// <summary>BM-REQ-QTY S9(15)V9(6).</summary>
    public decimal ReqQty { get; set; }
    /// <summary>BM-ROUND-INC S9(13)V9(6).</summary>
    public decimal RoundInc { get; set; }
    /// <summary>BM-PACK-MULT S9(13)V9(6).</summary>
    public decimal PackMult { get; set; }
    /// <summary>BM-MIN-ORDER S9(13)V9(6).</summary>
    public decimal MinOrder { get; set; }
    /// <summary>BM-FREE-STOCK S9(15)V9(6).</summary>
    public decimal FreeStock { get; set; }
    /// <summary>BM-PACK-QTY S9(15)V9(6).</summary>
    public decimal PackQty { get; set; }
    /// <summary>BM-EXCESS S9(15)V9(6).</summary>
    public decimal Excess { get; set; }
    /// <summary>BM-VALUE S9(15)V9(9).</summary>
    public decimal Value { get; set; }
    /// <summary>BM-RESULT S9(15)V9(6).</summary>
    public decimal Result { get; set; }
    public string Explain { get; set; } = "";
}

/// <summary>
/// BOMMATH (cobol/online/bom/BOMMATH.cob): the required-quantity mathematics (spec 4.5). Every intermediate receiving field
/// truncates (no ROUNDED) except the batch count.
/// </summary>
internal static class BomMath
{
    private static readonly decimal Eps = 0.000000001m;

    public static void Call(MathArea a)
    {
        // Initialise-Call
        var rejected = false;
        a.ReturnCode = 0;
        a.MessageCount = 0;
        a.Reason = "";
        a.Message = "";
        a.Explain = "";
        if (a.RunCount < 1) a.RunCount = 1;

        void Reject()
        {
            a.ReturnCode = 8;
            a.MessageCount += 1;
            rejected = true;
        }

        switch (a.Function)
        {
            case "PARENT":
                Parent(a, Reject, () => rejected);
                break;
            case "COMPON":
                Component(a, Reject, () => rejected);
                break;
            case "ROUNDUP":
                RoundUp(a, Reject);
                break;
            case "PACK":
                Pack(a);
                break;
            case "CEIL":
                if (a.Quantum <= 0)
                {
                    a.Reason = "BOM0103";
                    a.Message = "The step must be positive";
                    Reject();
                    break;
                }
                a.Result = Ceiling(a.Value, Pic.N(a.Quantum, 15, 6));
                break;
            default:
                a.Reason = "BOM0101";
                a.Message = Pic.Str(200, "BOMMATH does not support the function ", Pic.T(a.Function));
                a.ReturnCode = 8;
                break;
        }
    }

    private static void Parent(MathArea a, Action reject, Func<bool> rejected)
    {
        // Validate-Parent
        if (a.Yield <= 0 || a.Yield > 1)
        {
            a.Reason = "BOM0102";
            a.Message = "Yield must be greater than zero and not more than one";
            reject();
        }
        else if (a.Quantum <= 0)
        {
            a.Reason = "BOM0103";
            a.Message = "The production quantum must be positive";
            reject();
        }
        else if (a.GoodQty < 0)
        {
            a.Reason = "BOM0104";
            a.Message = "Required good output cannot be negative";
            reject();
        }
        else if (a.BatchSize < 0)
        {
            a.Reason = "BOM0105";
            a.Message = "The planned batch size cannot be negative";
            reject();
        }
        if (rejected()) return;

        a.GrossRaw = 0m;
        a.GrossStarts = 0m;
        a.BatchCount = 0;
        if (a.GoodQty == 0)
        {
            a.RunCount = 0;
            a.Explain = "Zero required output: no runs, batches or material";
            return;
        }
        a.GrossRaw = (Cd.F(a.GoodQty, 6) / Cd.F(a.Yield, 6)).Store(15, 9, previous: a.GrossRaw);
        a.Value = a.GrossRaw;
        var steps = Pic.N(a.Quantum, 15, 6);
        a.Result = Ceiling(a.Value, steps);
        a.GrossStarts = a.Result;
        if (a.BatchSize > 0)
        {
            a.Value = a.GrossStarts;
            steps = Pic.N(a.BatchSize, 15, 6);
            a.Result = Ceiling(a.Value, steps);
            a.BatchCount = (Cd.F(a.Result, 6) / Cd.F(a.BatchSize, 6)).StoreInt(9, rounded: true, previous: a.BatchCount);
        }
        a.Explain = Pic.Str(120, "Gross starts ", Pic.E(a.GrossStarts, 6), " from good output ");
        a.Explain = Pic.Str(120, Pic.T(a.Explain), Pic.E(a.GoodQty, 6));
    }

    private static void Component(MathArea a, Action reject, Func<bool> rejected)
    {
        // Validate-Component
        if (a.NominalBasis <= 0)
        {
            a.Reason = "BOM0107";
            a.Message = "The nominal output basis must be positive";
            reject();
        }
        else if (a.ConvFact <= 0)
        {
            a.Reason = "BOM0108";
            a.Message = "A positive unit conversion factor is required";
            reject();
        }
        else if (a.ScrapPct < 0)
        {
            a.Reason = "BOM0109";
            a.Message = "A scrap allowance cannot be negative";
            reject();
        }
        else if (a.UsageQty < 0)
        {
            a.Reason = "BOM0110";
            a.Message = "Negative usage is not a by-product mechanism";
            reject();
        }
        if (rejected()) return;

        a.Unrounded = 0m;
        a.YieldContrib = 0m;
        a.ScrapContrib = 0m;
        var scrapFactor = (Cd.F(1m, 6) + Cd.F(a.ScrapPct, 6)).Store(5, 6);
        var usage = Cd.F(a.UsageQty, 6);
        var conv = Cd.F(a.ConvFact, 9);
        var scrap = Cd.F(a.ScrapPct, 6);
        var factor = Cd.F(scrapFactor, 6);
        decimal baseInput;
        switch (a.UsageBasis)
        {
            case "PROPORTION":
                if (a.GrossStarts == 0)
                {
                    a.Explain = "No gross starts: no proportional requirement";
                    return;
                }
                var perBasis = (Cd.F(a.GrossStarts, 6) / Cd.F(a.NominalBasis, 6)).Store(15, 9);
                baseInput = (Cd.F(perBasis, 9) * usage * conv).Store(15, 9);
                a.Unrounded = (Cd.F(baseInput, 9) * factor).Store(15, 9);
                a.ScrapContrib = (Cd.F(baseInput, 9) * scrap).Store(15, 9);
                if (a.GoodQty > 0 && a.GrossStarts > a.GoodQty)
                {
                    var yieldStarts = (Cd.F(a.GrossStarts, 6) - Cd.F(a.GoodQty, 6)).Store(15, 9);
                    a.YieldContrib = (Cd.F(yieldStarts, 9) / Cd.F(a.NominalBasis, 6) * usage * conv * factor).Store(15, 9, previous: a.YieldContrib);
                }
                a.Explain = "Proportional to gross starts over nominal basis";
                break;
            case "FIXEDRUN":
                if (a.RunCount < 1 || a.GoodQty == 0)
                {
                    a.Explain = "No execution run: no fixed per run requirement";
                    return;
                }
                baseInput = (Cd.I(a.RunCount) * usage * conv).Store(15, 9);
                a.Unrounded = (Cd.F(baseInput, 9) * factor).Store(15, 9);
                a.ScrapContrib = (Cd.F(baseInput, 9) * scrap).Store(15, 9);
                a.Explain = "Fixed per recorded execution run";
                break;
            case "FIXEDBATCH":
                if (a.BatchCount < 1 || a.GoodQty == 0)
                {
                    a.Explain = "No batch: no fixed per batch requirement";
                    return;
                }
                baseInput = (Cd.I(a.BatchCount) * usage * conv).Store(15, 9);
                a.Unrounded = (Cd.F(baseInput, 9) * factor).Store(15, 9);
                a.ScrapContrib = (Cd.F(baseInput, 9) * scrap).Store(15, 9);
                a.Explain = "Fixed per planned parent batch";
                break;
            default:
                a.Reason = "BOM0106";
                a.Message = Pic.Str(200, "Usage basis ", Pic.T(a.UsageBasis), " is not a recognised basis");
                reject();
                break;
        }
    }

    private static void RoundUp(MathArea a, Action reject)
    {
        if (a.Quantum <= 0)
        {
            a.Reason = "BOM0103";
            a.Message = "The allowed stock quantum must be positive";
            reject();
            return;
        }
        a.Value = a.Unrounded;
        a.Result = Ceiling(a.Value, Pic.N(a.Quantum, 15, 6));
        a.ReqQty = a.Result;
        a.RoundInc = (Cd.F(a.ReqQty, 6) - Cd.F(a.Unrounded, 9)).Store(13, 6);
        if (a.RoundInc < 0) a.RoundInc = Pic.N(0m, 13, 6);
        a.Explain = Pic.Str(120, "Rounded up by ", Pic.E(a.RoundInc, 6), " to the allowed stock quantum");
    }

    private static void Pack(MathArea a)
    {
        var toBuy = (Cd.F(a.ReqQty, 6) - Cd.F(a.FreeStock, 6)).Store(15, 6);
        if (toBuy < 0) toBuy = Pic.N(0m, 15, 6);
        a.PackQty = toBuy;
        a.Excess = Pic.N(0m, 15, 6);
        if (toBuy == 0)
        {
            a.Explain = "Free stock covers the requirement: nothing to buy";
            return;
        }
        if (a.PackMult > 0)
        {
            a.Value = Pic.N(toBuy, 15, 9);
            a.Result = Ceiling(a.Value, Pic.N(a.PackMult, 15, 6));
            a.PackQty = a.Result;
        }
        if (a.MinOrder > a.PackQty)
        {
            a.PackQty = Pic.N(a.MinOrder, 15, 6);
            if (a.PackMult > 0)
            {
                a.Value = Pic.N(a.PackQty, 15, 9);
                a.Result = Ceiling(a.Value, Pic.N(a.PackMult, 15, 6));
                a.PackQty = a.Result;
            }
        }
        a.Excess = (Cd.F(a.PackQty, 6) - Cd.F(toBuy, 6)).Store(15, 6);
        if (a.Excess < 0) a.Excess = Pic.N(0m, 15, 6);
        a.Explain = Pic.Str(120, "Purchase proposal ", Pic.E(a.PackQty, 6), " with excess ");
        a.Explain = Pic.Str(120, Pic.T(a.Explain), Pic.E(a.Excess, 6));
    }

    /// <summary>
    /// Ceiling-To-Step: quotient = value / step truncated to 9 decimals; whole = INTEGER; whole + 1 when the fraction exceeds
    /// 0.000000001; result = whole x step (PIC S9(15)V9(6)); step &lt;= 0 returns the value; value &lt;= 0 returns 0.
    /// </summary>
    public static decimal Ceiling(decimal value, decimal steps)
    {
        if (steps <= 0) return Pic.N(value, 15, 6);
        if (value <= 0) return Pic.N(0m, 15, 6);
        var quotient = (Cd.F(value, 9) / Cd.F(steps, 6)).Store(15, 9);
        var whole = Pic.N(Math.Floor(quotient), 15, 9);
        var fraction = (Cd.F(quotient, 9) - Cd.F(whole, 9)).Store(15, 9);
        if (fraction > Eps) whole = Pic.N(whole + 1m, 15, 9);
        return (Cd.F(whole, 9) * Cd.F(steps, 6)).Store(15, 6);
    }
}
