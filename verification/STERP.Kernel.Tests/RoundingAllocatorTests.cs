using Sterp.Platform.Kernel.Services;

namespace Sterp.Kernel.Tests;

/// <summary>RNDALLOC (FR-STERP-PLATFORM-022; BR-RNDALLOC-001 .. 010).</summary>
public sealed class RoundingAllocatorTests
{
    private readonly RoundingAllocator _r = new();

    [Fact]
    public void Round_takes_exact_halves_away_from_zero_and_keeps_the_input_as_unrounded()
    {
        // FR-022 AC1
        var up = _r.Round(1.005m, 2);
        var down = _r.Round(-1.005m, 2);
        Assert.Equal(1.01m, up.ResultValue);
        Assert.Equal(-1.01m, down.ResultValue);
        Assert.Equal(1.005m, up.Unrounded);
        Assert.Equal(0, up.Header.ReturnCode);
        Assert.Equal("RNDALLOC", up.Header.Program);
        Assert.Equal("COM-011", up.Header.RequestRef);
        Assert.Equal("Rounded to nearest minor unit, halves away from zero", up.Header.Message);
        Assert.Equal(500, up.MaxLines);
    }

    [Fact]
    public void Equal_allocation_of_ten_pence_over_three_lines_gives_the_spare_unit_to_the_first_line()
    {
        // FR-022 AC2 (equal remainders go to the earlier line, SME-052)
        var a = _r.Allocate(0.10m, 2, "EQUAL", [0m, 0m, 0m]);
        Assert.Equal(0, a.Header.ReturnCode);
        Assert.Equal([0.04m, 0.03m, 0.03m], a.Lines.Select(l => l.Part).ToArray());
        Assert.Equal(1, a.Bumped);
        Assert.Equal(0.10m, a.Allocated);
        Assert.Equal(0m, a.Residual);
        Assert.Equal("Allocated in full, 1 line(s) took a spare minor unit", a.Header.Message);
        // EQUAL overwrites the caller's weights with 1
        Assert.All(a.Lines, l => Assert.Equal(1m, l.Weight));
    }

    [Fact]
    public void Mode_is_not_case_folded()
    {
        // FR-022 AC3
        var a = _r.Allocate(0.10m, 2, "equal", [1m, 1m, 1m]);
        Assert.Equal(8, a.Header.ReturnCode);
        Assert.Equal("RND0003", a.Header.ReasonCode);
        Assert.Equal("Allocation mode must be EQUAL or WEIGHT", a.Header.Message);
        Assert.Equal(1, a.Header.MessageCount);
    }

    [Fact]
    public void A_zero_divisor_is_refused()
    {
        // FR-022 AC4
        var a = _r.UnitPrice(10m, 0m, 2);
        Assert.Equal(8, a.Header.ReturnCode);
        Assert.Equal("RND0006", a.Header.ReasonCode);
        Assert.Equal("Divisor is zero - a missing divisor is not one", a.Header.Message);
    }

    [Fact]
    public void Extend_rounds_the_twelve_decimal_product_once()
    {
        Assert.Equal(1.01m, _r.Extend(1m, 1.005m, 2).ResultValue);
        var e = _r.Extend(3m, 0.333333333333m, 2);
        Assert.Equal(0.999999999999m, e.Unrounded);
        Assert.Equal(1.00m, e.ResultValue);
        Assert.Equal("Extended at twelve decimals then rounded once", e.Header.Message);
    }

    [Fact]
    public void UnitPrice_is_held_to_twelve_decimals_then_rounded()
    {
        var u = _r.UnitPrice(10m, 3m, 6);
        Assert.Equal(3.333333333333m, u.Unrounded);
        Assert.Equal(3.333333m, u.ResultValue);
        Assert.Equal(6.666667m, _r.UnitPrice(20m, 3m, 6).ResultValue);
    }

    [Fact]
    public void Trunc_goes_toward_zero()
    {
        Assert.Equal(-1.23m, _r.Truncate(-1.239m, 2).ResultValue);
        Assert.Equal(1.23m, _r.Truncate(1.239m, 2).ResultValue);
        Assert.Equal("Truncated toward zero", _r.Truncate(1m, 2).Header.Message);
    }

    [Fact]
    public void Validation_reason_codes_and_order()
    {
        var places = _r.Round(1m, 7);
        Assert.Equal(("RND0001", 8, "Decimal places must be between 0 and 6"), (places.Header.ReasonCode, places.Header.ReturnCode, places.Header.Message));
        Assert.Equal("RND0002", _r.Allocate(1m, 2, "EQUAL", []).Header.ReasonCode);
        Assert.Equal("RND0004", _r.Allocate(1m, 2, "WEIGHT", [1m, -1m]).Header.ReasonCode);
        var zero = _r.Allocate(1m, 2, "WEIGHT", [0m, 0m]);
        Assert.Equal("RND0005", zero.Header.ReasonCode);
        Assert.Equal("Total allocation weight is zero - there is nothing to allocate to", zero.Header.Message);
        // places are validated before the function, so RND0001 wins over RND0008
        var a = new RndallocArea { Places = 9 };
        a.Header.Function = "SPLIT";
        _r.Execute(a);
        Assert.Equal("RND0001", a.Header.ReasonCode);
        var b = new RndallocArea { Places = 2 };
        b.Header.Function = "SPLIT";
        _r.Execute(b);
        Assert.Equal(("RND0008", "RNDALLOC does not support function SPLIT"), (b.Header.ReasonCode, b.Header.Message));
    }

    [Fact]
    public void Negative_amounts_restore_the_sign_and_weights_share_proportionally()
    {
        var neg = _r.Allocate(-0.10m, 2, "EQUAL", [1m, 1m, 1m]);
        Assert.Equal([-0.04m, -0.03m, -0.03m], neg.Lines.Select(l => l.Part).ToArray());
        Assert.Equal(0m, neg.Residual);

        var w = _r.Allocate(100m, 2, "WEIGHT", [1m, 1m, 1m]);
        Assert.Equal([33.34m, 33.33m, 33.33m], w.Lines.Select(l => l.Part).ToArray());

        var weighted = _r.Allocate(10m, 2, "WEIGHT", [1m, 2m, 3m]);
        Assert.Equal(10m, weighted.Lines.Sum(l => l.Part));
        Assert.Equal([1.67m, 3.33m, 5.00m], weighted.Lines.Select(l => l.Part).ToArray());
    }

    [Fact]
    public void Three_thousand_random_allocations_all_reconcile()
    {
        // SME-073: RND0009 was never produced in 3,000 random allocations
        var rnd = new Random(20261020);
        for (var n = 0; n < 3000; n++)
        {
            var places = rnd.Next(0, 7);
            var amount = Math.Round((decimal)(rnd.NextDouble() * 2_000_000 - 1_000_000), 6);
            var lines = rnd.Next(1, 40);
            var weights = Enumerable.Range(0, lines).Select(_ => Math.Round((decimal)(rnd.NextDouble() * 1000), rnd.Next(0, 7))).ToArray();
            if (weights.All(x => x == 0)) weights[0] = 1;
            var a = _r.Allocate(amount, places, "WEIGHT", weights);
            Assert.True(a.Header.ReturnCode == 0, $"{amount} over {lines} at {places}: {a.Header.ReasonCode} {a.Header.Message}");
            Assert.Equal(Sterp.Platform.Kernel.Cobol.CobolNumeric.RoundAway(amount, places), a.Lines.Take(lines).Sum(l => l.Part));
        }
    }
}
