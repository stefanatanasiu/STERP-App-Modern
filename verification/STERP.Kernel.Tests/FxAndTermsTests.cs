using Microsoft.Extensions.Options;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Kernel.Tests;

/// <summary>FXCONV with supplied rates (FR-STERP-PLATFORM-021; BR-FXCONV-001 .. 012) - no rate lookup, no database.</summary>
public sealed class FxConverterTests
{
    private readonly FxConverter _fx = new(new NoSqlRunner(), new RoundingAllocator());

    private Task<FxconvArea> Call(string function, Action<FxconvArea> fill) => _fx.CallAsync(KernelCaller.Empty, function, fill);

    [Fact]
    public async Task ToGbp_divides_by_foreign_units_per_pound()
    {
        // FR-021 AC1
        var a = await Call("TOGBP", f => { f.Currency = "eur"; f.Rate = 1.15m; f.ForeignAmount = 115.00m; });
        Assert.Equal(0, a.Header.ReturnCode);
        Assert.Equal(100.00m, a.GbpAmount);
        Assert.Equal(0m, a.Residual);
        Assert.Equal("EUR", a.Currency);
        Assert.Equal("1.15000000 EUR per GBP 1", a.RateText);
        Assert.Equal("EUR converted at 1.15000000 EUR per GBP 1 to GBP 100.00", a.Header.Message);
        Assert.Equal(("FXCONV", "COM-010"), (a.Header.Program, a.Header.RequestRef));
    }

    [Fact]
    public async Task An_inverse_quotation_is_turned_round_only_on_request()
    {
        // FR-021 AC2
        var a = await Call("TOGBP", f => { f.Currency = "EUR"; f.Rate = 0.87m; f.QuoteBasis = "GBPPERFCY"; f.AllowInvert = "Y"; f.ForeignAmount = 100m; });
        Assert.Equal(4, a.Header.ReturnCode);
        Assert.Equal("FX0004", a.Header.ReasonCode);
        Assert.Equal(1.14942528m, a.Rate);
        Assert.Equal(0.87m, a.RateEntered);
        Assert.Equal("FCYPERGBP", a.QuoteBasis);
        Assert.Equal(87.00m, a.GbpAmount);
        Assert.Equal(1, a.Header.MessageCount);
        var refused = await Call("TOGBP", f => { f.Currency = "EUR"; f.Rate = 0.87m; f.QuoteBasis = "gbpperfcy"; });
        Assert.Equal((8, "FX0003", "Rate quoted as sterling per unit of EUR - STERP quotes foreign units per GBP 1"),
            (refused.Header.ReturnCode, refused.Header.ReasonCode, refused.Header.Message));
        var getrate = await Call("REALISED", f => { f.Currency = "EUR"; f.Rate = 0.87m; f.QuoteBasis = "GBPPERFCY"; f.AllowInvert = "Y"; f.Rate2 = 1m; f.ForeignAmount = 1m; });
        Assert.Equal("FX0004", getrate.Header.ReasonCode);
    }

    [Fact]
    public async Task Zero_places_means_two()
    {
        // FR-021 AC4 (BRF-FXCONV-03)
        var a = await Call("TOGBP", f => { f.Currency = "USD"; f.Rate = 1.3m; f.ForeignAmount = 100m; f.Places = 0; });
        Assert.Equal(2, a.Places);
        Assert.Equal(76.92m, a.GbpAmount);
        Assert.Equal(0.003076m, a.Residual);
        Assert.Equal(76.923076923076m, a.Unrounded);
        var four = await Call("TOGBP", f => { f.Currency = "USD"; f.Rate = 1.3m; f.ForeignAmount = 100m; f.Places = 4; });
        Assert.Equal(76.9231m, four.GbpAmount);
        Assert.Equal("USD converted at 1.30000000 USD per GBP 1 to GBP 76.92", four.Header.Message);
    }

    [Fact]
    public async Task Validation_and_rate_refusals()
    {
        var noCcy = await Call("TOGBP", f => f.Rate = 1m);
        Assert.Equal(("FX0005", "A currency code is required - parity is neverassumed", 8), (noCcy.Header.ReasonCode, noCcy.Header.Message, noCcy.Header.ReturnCode));
        Assert.Equal("FX0006", (await Call("TOGBP", f => { f.Currency = "EUR"; f.Places = 7; })).Header.ReasonCode);
        Assert.Equal("FX0006", (await Call("TOGBP", f => { f.Currency = "EUR"; f.Places = -1; })).Header.ReasonCode);
        var zero = await Call("TOGBP", f => f.Currency = "EUR");
        Assert.Equal(("FX0001", "An exchange rate is required for EUR - a missing rate is not parity"), (zero.Header.ReasonCode, zero.Header.Message));
        Assert.Equal("FX0002", (await Call("TOGBP", f => { f.Currency = "EUR"; f.Rate = -1m; })).Header.ReasonCode);
        Assert.Equal("FX0007", (await Call("GETRATE", f => { f.Currency = "EUR"; f.RateDate = 20260230; })).Header.ReasonCode);
        var unknown = await Call("CONVERT", f => f.Currency = "EUR");
        Assert.Equal(("FX0010", "FXCONV does not support function CONVERT"), (unknown.Header.ReasonCode, unknown.Header.Message));
    }

    [Fact]
    public async Task Sterling_converts_at_one()
    {
        var a = await Call("TOGBP", f => { f.Currency = "GBP"; f.ForeignAmount = 12.345m; });
        Assert.Equal((1m, "BASE CURRENCY", 12.35m, "1.00000000 GBP per GBP 1"), (a.Rate, a.RateSource, a.GbpAmount, a.RateText));
        var r = await Call("GETRATE", f => { f.Currency = "GBP"; f.RateDate = 20261019; });
        Assert.Equal((0, "GBP is the base currency - rate 1.00000000", 20261019), (r.Header.ReturnCode, r.Header.Message, r.RateFoundDate));
    }

    [Fact]
    public async Task ToFcy_multiplies_and_rounds_once()
    {
        var a = await Call("TOFCY", f => { f.Currency = "EUR"; f.Rate = 1.15m; f.GbpAmount = 100m; });
        Assert.Equal(115.00m, a.ForeignAmount);
        Assert.Equal("GBP converted at 1.15000000 EUR per GBP 1 to EUR 115.00", a.Header.Message);
    }

    [Fact]
    public async Task Realised_rounds_booked_and_settled_sterling_separately()
    {
        var asset = await Call("REALISED", f => { f.Currency = "EUR"; f.Rate = 1.15m; f.Rate2 = 1.10m; f.ForeignAmount = 1000m; });
        Assert.Equal((869.57m, 909.09m, 39.52m), (asset.GbpAmount, asset.GbpAmount2, asset.Realised));
        Assert.Equal("Realised exchange gain of GBP 39.52 on this ASSET", asset.Header.Message);
        var liability = await Call("REALISED", f => { f.Currency = "EUR"; f.Rate = 1.15m; f.Rate2 = 1.10m; f.ForeignAmount = 1000m; f.Position = "liability"; });
        Assert.Equal(-39.52m, liability.Realised);
        Assert.Equal("Realised exchange loss of GBP -39.52 on this LIABILITY", liability.Header.Message);
        var noSettle = await Call("REALISED", f => { f.Currency = "EUR"; f.Rate = 1.15m; f.ForeignAmount = 1000m; });
        Assert.Equal(("FX0009", "A positive settlement rate is required"), (noSettle.Header.ReasonCode, noSettle.Header.Message));
    }
}

/// <summary>TERMCALC paths that need no terms table and no holiday table (FR-STERP-PLATFORM-020; BR-TERMCALC-001 .. 015).</summary>
public sealed class PaymentTermsCalculatorTests
{
    private readonly PaymentTermsCalculator _terms;

    public PaymentTermsCalculatorTests()
    {
        var calendar = new BusinessCalendar(new NoSqlRunner(), new CalendarCache(TimeProvider.System, Options.Create(new SterpKernelOptions())));
        _terms = new PaymentTermsCalculator(new NoSqlRunner(), calendar, new RoundingAllocator());
    }

    private Task<TermcalcArea> DueDate(Action<TermcalcArea> fill) => _terms.CallAsync(KernelCaller.Empty, "DUEDATE", fill);

    [Fact]
    public async Task Day_31_of_the_following_month_is_clamped_to_the_month_end()
    {
        // FR-020 AC1 (AC-005)
        var a = await DueDate(t => { t.BasisType = "dayofnextmonth"; t.DayOfMonth = 31; t.InvoiceDate = 20270115; });
        Assert.Equal((0, 20270228, 20270228, "EXPLICIT"), (a.Header.ReturnCode, a.DueDate, a.DueDateRaw, a.Source));
        Assert.Equal(("28/02/2027", "Day 31 of the following month"), (a.DueDateUk, a.TermsDescription));
        Assert.Equal("Due 28/02/2027 - Day 31 of the following month", a.Header.Message);
        Assert.Equal(20280229, (await DueDate(t => { t.BasisType = "DAYOFNEXTMONTH"; t.DayOfMonth = 31; t.InvoiceDate = 20280115; })).DueDate);
        Assert.Equal(20270215, (await DueDate(t => { t.BasisType = "DAYOFNEXTMONTH"; t.DayOfMonth = 15; t.InvoiceDate = 20270131; })).DueDate);
        var bad = await DueDate(t => { t.BasisType = "DAYOFNEXTMONTH"; t.DayOfMonth = 32; t.InvoiceDate = 20270115; });
        Assert.Equal(("TRM0005", "Day of month must be between 1 and 31"), (bad.Header.ReasonCode, bad.Header.Message));
    }

    [Fact]
    public async Task A_term_longer_than_the_limit_is_flagged()
    {
        // FR-020 AC4 (D-25)
        var a = await DueDate(t => { t.BasisType = "INVOICE"; t.Days = 75; t.InvoiceDate = 20261001; });
        Assert.Equal((4, "TRM0007", "Y", 75, 20261215), (a.Header.ReturnCode, a.Header.ReasonCode, a.LongTermFlag, a.TermDays, a.DueDate));
        Assert.Equal("Payment term of 75 days needs express agreement - review it", a.Header.Message);
        Assert.Equal(1, a.Header.MessageCount);
        Assert.Equal("75 days from invoice date", a.TermsDescription);
        var limit = await DueDate(t => { t.BasisType = "INVOICE"; t.Days = 75; t.InvoiceDate = 20261001; t.MaxTermDays = 90; });
        Assert.Equal((0, "N"), (limit.Header.ReturnCode, limit.LongTermFlag));
    }

    [Fact]
    public async Task Statutory_default_is_thirty_days_after_the_latest_date()
    {
        var a = await DueDate(t => { t.InvoiceDate = 20261001; t.SupplyDate = 20261005; t.ReceivedDate = 20261010; });
        Assert.Equal((20261109, 30, "STATUTORY", "STATUTORY"), (a.DueDate, a.Days, a.BasisType, a.Source));
        Assert.Equal("Due 09/11/2026 - 30 days - Late Payment Act default", a.Header.Message);
        var supplyOnly = await DueDate(t => t.SupplyDate = 20261005);
        Assert.Equal((20261005, 20261104), (supplyOnly.InvoiceDate, supplyOnly.DueDate));
    }

    [Fact]
    public async Task End_of_month_terms_and_the_discount()
    {
        var a = await DueDate(t =>
        {
            t.BasisType = "EOM";
            t.Days = 30;
            t.InvoiceDate = 20261015;
            t.DiscountDays = 10;
            t.DiscountPct = 2.5m;
            t.NetAmount = 1000m;
            t.VatAmount = 200m;
        });
        Assert.Equal((20261130, 20261025, 25.00m), (a.DueDate, a.DiscountDate, a.DiscountAmount));
        Assert.Equal(("30/11/2026", "25/10/2026", "30 days end of month"), (a.DueDateUk, a.DiscountDateUk, a.TermsDescription));
        var gross = await _terms.CallAsync(KernelCaller.Empty, "DISCOUNT", t => { t.DiscountPct = 2.5m; t.NetAmount = 1000m; t.VatAmount = 200m; t.DiscountBasis = "GROSS"; });
        Assert.Equal((0, 30.00m, ""), (gross.Header.ReturnCode, gross.DiscountAmount, gross.Header.Message));
        var lower = await _terms.CallAsync(KernelCaller.Empty, "DISCOUNT", t => { t.DiscountPct = 2.5m; t.NetAmount = 1000m; t.VatAmount = 200m; t.DiscountBasis = "gross"; });
        Assert.Equal(25.00m, lower.DiscountAmount);
        var half = await _terms.CallAsync(KernelCaller.Empty, "DISCOUNT", t => { t.DiscountPct = 1m; t.NetAmount = 0.5m; });
        Assert.Equal(0.01m, half.DiscountAmount);
    }

    [Fact]
    public async Task Refusals()
    {
        var date = await DueDate(t => { t.BasisType = "INVOICE"; t.InvoiceDate = 20261301; });
        Assert.Equal(("TRM0003", "The invoice date is not a valid calendar date"), (date.Header.ReasonCode, date.Header.Message));
        Assert.Equal("The received date is not a valid calendar date",
            (await DueDate(t => { t.InvoiceDate = 20261001; t.ReceivedDate = 20261099; })).Header.Message);
        var basis = await DueDate(t => { t.BasisType = "weekly"; t.InvoiceDate = 20261001; });
        Assert.Equal(("TRM0004", "Payment terms basis WEEKLY is not supported"), (basis.Header.ReasonCode, basis.Header.Message));
        var code = await _terms.CallAsync(KernelCaller.Empty, "GETTERMS", _ => { });
        Assert.Equal((8, "TRM0001", "A payment terms code is required"), (code.Header.ReturnCode, code.Header.ReasonCode, code.Header.Message));
        var fn = await _terms.CallAsync(KernelCaller.Empty, "NETDUE", _ => { });
        Assert.Equal(("TRM0009", "TERMCALC does not support function NETDUE"), (fn.Header.ReasonCode, fn.Header.Message));
        var overflow = await DueDate(t => { t.BasisType = "INVOICE"; t.Days = 10; t.InvoiceDate = 99991225; });
        Assert.Equal(("TRM0008", "Resulting date is outside the supported range"), (overflow.Header.ReasonCode, overflow.Header.Message));
    }
}
