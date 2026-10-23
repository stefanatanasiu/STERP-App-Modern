using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Platform.Kernel.Services;

/// <summary>One RA-LINE entry (RA-LINE-REF X(8), RA-WEIGHT and RA-PART S9(13)V9(6)).</summary>
public sealed class AllocationLine
{
    public string LineRef { get; set; } = "";
    public decimal Weight { get; set; }
    public decimal Part { get; set; }
}

/// <summary>RNDALLOCCA body (RNDALLOCCA.cpy:24-43).</summary>
public sealed class RndallocArea : Commarea
{
    public const int MaxLinesHeld = 500;

    /// <summary>RA-PLACES S9(4): 0..6.</summary>
    public int Places { get; set; }

    /// <summary>RA-VALUE S9(13)V9(12).</summary>
    public decimal Value { get; set; }

    /// <summary>RA-VALUE2 S9(13)V9(12).</summary>
    public decimal Value2 { get; set; }

    /// <summary>RA-RESULT S9(13)V9(12).</summary>
    public decimal ResultValue { get; set; }

    /// <summary>RA-UNROUNDED S9(13)V9(12).</summary>
    public decimal Unrounded { get; set; }

    /// <summary>RA-AMOUNT S9(13)V9(6): the amount to allocate.</summary>
    public decimal Amount { get; set; }

    /// <summary>RA-MODE X(8): exactly EQUAL or WEIGHT (not case-folded).</summary>
    public string Mode { get; set; } = "";

    /// <summary>RA-LINE-CNT S9(9) COMP-5.</summary>
    public int LineCount { get; set; }

    public decimal Allocated { get; set; }

    public decimal Residual { get; set; }

    /// <summary>RA-BUMPED: lines that received a spare minor unit.</summary>
    public int Bumped { get; set; }

    /// <summary>RA-MAX-LINES: returned as 500.</summary>
    public int MaxLines { get; set; }

    /// <summary>RA-LINE OCCURS 500: entries beyond the list are zero lines (as an INITIALIZEd table).</summary>
    public List<AllocationLine> Lines { get; } = [];

    internal AllocationLine Line(int i)
    {
        while (Lines.Count < i) Lines.Add(new AllocationLine());
        return Lines[i - 1];
    }
}

/// <summary>RNDALLOC - financial rounding and allocation (FR-STERP-PLATFORM-022, COM-011).</summary>
public interface IRoundingAllocator
{
    /// <summary>CALL "RNDALLOC" USING area: ROUND, TRUNC, UNITPRC, EXTEND, ALLOC (other RND0008 after validation).</summary>
    LegacyResult Execute(RndallocArea area);

    /// <summary>ROUND: half away from zero to <paramref name="places"/> (0..6).</summary>
    RndallocArea Round(decimal value, int places);

    /// <summary>TRUNC: toward zero at <paramref name="places"/>.</summary>
    RndallocArea Truncate(decimal value, int places);

    /// <summary>UNITPRC: value / divisor held to 12 decimals (truncated) then rounded half away; a zero divisor is RND0006.</summary>
    RndallocArea UnitPrice(decimal value, decimal divisor, int places);

    /// <summary>EXTEND: value x value2 held to 12 decimals then rounded once.</summary>
    RndallocArea Extend(decimal value, decimal value2, int places);

    /// <summary>ALLOC: largest-remainder allocation of <paramref name="amount"/> over the weights (EQUAL ignores them).</summary>
    RndallocArea Allocate(decimal amount, int places, string mode, IReadOnlyList<decimal> weights);
}

/// <summary>RNDALLOC.cob:66-395 (pure computation, System.Decimal with the legacy intermediate precisions).</summary>
public sealed class RoundingAllocator : IRoundingAllocator
{
    private static readonly decimal[] Factors = [1m, 10m, 100m, 1000m, 10000m, 100000m, 1000000m];

    public RndallocArea Round(decimal value, int places) => Call("ROUND", a => { a.Value = value; a.Places = places; });

    public RndallocArea Truncate(decimal value, int places) => Call("TRUNC", a => { a.Value = value; a.Places = places; });

    public RndallocArea UnitPrice(decimal value, decimal divisor, int places) =>
        Call("UNITPRC", a => { a.Value = value; a.Value2 = divisor; a.Places = places; });

    public RndallocArea Extend(decimal value, decimal value2, int places) =>
        Call("EXTEND", a => { a.Value = value; a.Value2 = value2; a.Places = places; });

    public RndallocArea Allocate(decimal amount, int places, string mode, IReadOnlyList<decimal> weights) =>
        Call("ALLOC", a =>
        {
            a.Amount = amount;
            a.Places = places;
            a.Mode = mode;
            a.LineCount = weights.Count;
            foreach (var w in weights) a.Lines.Add(new AllocationLine { Weight = w });
        });

    private RndallocArea Call(string function, Action<RndallocArea> fill)
    {
        var a = new RndallocArea();
        a.Header.Prepare(KernelCaller.Empty, function);
        fill(a);
        Execute(a);
        return a;
    }

    public LegacyResult Execute(RndallocArea a)
    {
        var h = a.Header;
        // 0100-INITIALISE
        h.Reset("RNDALLOC", "COM-011");
        a.MaxLines = RndallocArea.MaxLinesHeld;
        var places = (int)CobolNumeric.Store(a.Places, 4, 0);
        a.Value = CobolNumeric.Store(a.Value, 13, 12);
        a.Value2 = CobolNumeric.Store(a.Value2, 13, 12);
        a.Amount = CobolNumeric.Store(a.Amount, 13, 6);

        // 0200-VALIDATE
        if (places is < 0 or > 6)
        {
            h.Reject("RND0001", "Decimal places must be between 0 and 6");
            return h.Result;
        }
        var factor = Factors[places];
        var fn = h.FunctionCode;
        decimal totalWeight = 0;
        if (fn == "ALLOC" && !ValidateAllocation(a, out totalWeight)) return h.Result;

        switch (fn)
        {
            case "ROUND":
                a.Unrounded = a.Value;
                a.ResultValue = Fit(CobolNumeric.RoundAway(a.Value, places));
                h.Message = "Rounded to nearest minor unit, halves away from zero";
                break;
            case "TRUNC":
                a.Unrounded = a.Value;
                var units = decimal.Truncate(a.Value * factor);
                a.ResultValue = Fit(units / factor);
                h.Message = "Truncated toward zero";
                break;
            case "UNITPRC":
                if (a.Value2 == 0)
                {
                    h.Reject("RND0006", "Divisor is zero - a missing divisor is not one");
                    break;
                }
                a.Unrounded = CobolNumeric.Divide(a.Value, a.Value2, 13, 12);
                a.ResultValue = Fit(CobolNumeric.RoundAway(a.Unrounded, places));
                h.Message = "Unit price held to the requested precision";
                break;
            case "EXTEND":
                a.Unrounded = CobolNumeric.Multiply(a.Value, a.Value2, 13, 12);
                a.ResultValue = Fit(CobolNumeric.RoundAway(a.Unrounded, places));
                h.Message = "Extended at twelve decimals then rounded once";
                break;
            case "ALLOC":
                Allocate(a, places, factor, totalWeight);
                break;
            default:
                h.Reject("RND0008", "RNDALLOC does not support function " + K.Trim(h.Function));
                break;
        }
        return h.Result;
    }

    /// <summary>0300-VALIDATE-ALLOC.</summary>
    private static bool ValidateAllocation(RndallocArea a, out decimal totalWeight)
    {
        var h = a.Header;
        totalWeight = 0;
        var lines = a.LineCount;
        if (lines is < 1 or > RndallocArea.MaxLinesHeld)
        {
            h.Reject("RND0002", "Line count must be between 1 and 500");
            return false;
        }
        var mode = K.X(a.Mode, 8);
        if (mode != "EQUAL   " && mode != "WEIGHT  ")
        {
            h.Reject("RND0003", "Allocation mode must be EQUAL or WEIGHT");
            return false;
        }
        // equal allocation is weighted allocation with every weight 1 (the caller's weights are overwritten)
        if (mode == "EQUAL   ")
        {
            for (var i = 1; i <= lines; i++) a.Line(i).Weight = 1;
        }
        for (var i = 1; i <= lines; i++)
        {
            var w = CobolNumeric.Store(a.Line(i).Weight, 13, 6);
            if (w < 0)
            {
                h.Reject("RND0004", "Allocation weights cannot be negative");
                return false;
            }
            totalWeight = CobolNumeric.Store(totalWeight + w, 16, 6);
        }
        if (totalWeight == 0)
        {
            h.Reject("RND0005", "Total allocation weight is zero - there is nothing to allocate to");
            return false;
        }
        return true;
    }

    /// <summary>2000-ALLOCATE: the largest remainder method on absolute amounts.</summary>
    private static void Allocate(RndallocArea a, int places, decimal factor, decimal totalWeight)
    {
        var h = a.Header;
        var lines = a.LineCount;

        // 2100-PREPARE: the source must sit on a whole minor unit
        var sign = CobolNumeric.SignOf(a.Amount);
        if (sign == 0) sign = 1;
        var absAmount = CobolNumeric.Store(a.Amount * sign, 13, 6);
        var targetUnits = decimal.Truncate(CobolNumeric.RoundAway(absAmount, places) * factor);
        var units = new decimal[lines + 1];
        var frac = new decimal[lines + 1];
        var bumped = new bool[lines + 1];
        for (var i = 1; i <= lines; i++) a.Line(i).Part = 0;

        // 2200-WHOLE-UNITS: share held to 9 decimals (truncated), whole part assigned, fraction kept
        decimal assigned = 0;
        for (var i = 1; i <= lines; i++)
        {
            var raw = CobolNumeric.MultiplyDivide(targetUnits, CobolNumeric.Store(a.Line(i).Weight, 13, 6), totalWeight, 20, 9);
            var whole = decimal.Truncate(raw);
            frac[i] = CobolNumeric.Store(raw - whole, 4, 9);
            units[i] = whole;
            assigned += whole;
        }
        var remainder = (long)(targetUnits - assigned);

        // 2300-DISTRIBUTE-REST: spare units to the largest remainders, the earlier line winning ties
        a.Bumped = 0;
        for (long n = 0; n < remainder; n++)
        {
            var best = 0;
            var bestFrac = -1m;
            for (var j = 1; j <= lines; j++)
            {
                if (!bumped[j] && frac[j] > bestFrac)
                {
                    best = j;
                    bestFrac = frac[j];
                }
            }
            if (best > 0)
            {
                units[best] += 1;
                bumped[best] = true;
                a.Bumped++;
            }
        }

        // 2400-BUILD-PARTS
        decimal allocated = 0;
        for (var i = 1; i <= lines; i++)
        {
            var part = CobolNumeric.Store(units[i] / factor * sign, 13, 6);
            a.Line(i).Part = part;
            allocated = CobolNumeric.Store(allocated + part, 13, 6);
        }
        a.Allocated = allocated;

        // 2500-PROVE-TOTAL (COM-011 "preserve control totals")
        var check = CobolNumeric.Store(CobolNumeric.RoundAway(a.Amount, places), 13, 6);
        a.Residual = CobolNumeric.Store(check - allocated, 13, 6);
        if (a.Residual != 0)
        {
            h.ReasonCode = "RND0009";
            h.Message = K.Msg("Allocation does not reconcile, residual ", K.Ed(a.Residual, "-(12)9.9(6)"));
            h.ReturnCode = 12;
        }
        else
        {
            h.Message = K.Msg("Allocated in full, ", K.Ed(a.Bumped, "-(9)9"), " line(s) took a spare minor unit");
        }
    }

    private static decimal Fit(decimal value) => CobolNumeric.Store(value, 13, 12);
}
