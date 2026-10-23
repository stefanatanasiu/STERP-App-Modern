using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Kernel.Tests;

/// <summary>
/// Ports of the legacy COBOL test drivers (cobol/tests/common/T*.cob) whose recorded runs are the Agent 07 golden masters
/// (tests/legacy/.../fixtures/cobol/freshc/T*.tap, every check "ok"). Each test names the driver check it reproduces;
/// inputs and expected values are taken from the driver source.
/// </summary>
public sealed class TstfuncDriverTests
{
    [Theory]
    [InlineData("1.005", 2, "1.01")]       // ok 1 (AC-006)
    [InlineData("-1.005", 2, "-1.01")]     // ok 2 (AC-006)
    [InlineData("2.675", 2, "2.68")]       // ok 3
    [InlineData("12.34", 2, "12.34")]      // ok 4
    [InlineData("0.5", 0, "1")]            // ok 5
    [InlineData("-0.5", 0, "-1")]          // ok 6
    [InlineData("7.6", -3, "8")]           // ok 7 negative places as none
    [InlineData("1.1234565", 6, "1.123457")] // ok 8
    public void Round_away(string value, int places, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture),
            CobolNumeric.RoundAway(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture), places));

    [Fact]
    public void Sign_of()
    {
        Assert.Equal(1, CobolNumeric.SignOf(-250.00m) * -1);   // ok 9
        Assert.Equal(0, CobolNumeric.SignOf(0m));              // ok 10
        Assert.Equal(1, CobolNumeric.SignOf(0.000001m));       // ok 11
    }

    [Theory]
    [InlineData(2028, 1)] // ok 12
    [InlineData(2027, 0)] // ok 13
    [InlineData(2000, 1)] // ok 14
    [InlineData(1900, 0)] // ok 15
    public void Is_leap(int year, int expected) => Assert.Equal(expected, CobolDate.IsLeapFunction(year));

    [Theory]
    [InlineData(2028, 2, 29)]  // ok 16
    [InlineData(2027, 2, 28)]  // ok 17
    [InlineData(2026, 9, 30)]  // ok 18
    [InlineData(2026, 12, 31)] // ok 19
    [InlineData(2026, 13, 0)]  // ok 20
    public void Days_in_month(int year, int month, int expected) => Assert.Equal(expected, CobolDate.DaysInMonth(year, month));

    [Theory]
    [InlineData(20280201, 20280229)] // ok 21
    [InlineData(20270215, 20270228)] // ok 22
    [InlineData(20261130, 20261130)] // ok 23
    [InlineData(20260230, 20260228)] // ok 24 an impossible day is pulled back
    [InlineData(20261301, 0)]        // ok 25 an impossible month is 0
    public void End_of_month(int date, int expected) => Assert.Equal(expected, CobolDate.EndOfMonth(date));

    [Theory]
    [InlineData(20261015, 4)] // ok 26
    [InlineData(20261018, 7)] // ok 27
    [InlineData(20261225, 5)] // ok 28
    [InlineData(20280229, 2)] // ok 29
    [InlineData(20261345, 0)] // ok 30
    public void Weekday_of(int date, int expected) => Assert.Equal(expected, CobolDate.WeekdayOf(date));

    [Fact]
    public async Task Through_the_linkable_services()
    {
        // ok 31: RNDALLOC ROUND reaches ROUND-AWAY
        var ra = new RoundingAllocator().Round(1.005m, 2);
        Assert.Equal((0, 1.01m), (ra.Header.ReturnCode, ra.ResultValue));
        // ok 32 / 33: DATECALC EOMONTH and DOW
        var cal = new BusinessCalendar(new NoSqlRunner(), new CalendarCache(TimeProvider.System, Options.Create(new SterpKernelOptions())));
        var eom = await cal.CallAsync("EOMONTH", a => { a.Header.Company = "STC"; a.Date1 = 20280201; });
        Assert.Equal((0, 20280229), (eom.Header.ReturnCode, eom.ResultDate));
        var dow = await cal.CallAsync("DOW", a => { a.Header.Company = "STC"; a.Date1 = 20261015; });
        Assert.Equal((0, 4), (dow.Header.ReturnCode, dow.ResultNum));
    }
}

public sealed class TrndallDriverTests
{
    private readonly RoundingAllocator _r = new();

    private static decimal D(string s) => decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("ROUND", "1.005", 2, "1.01")]
    [InlineData("ROUND", "-1.005", 2, "-1.01")]
    [InlineData("ROUND", "2.675", 2, "2.68")]
    [InlineData("ROUND", "1.0049", 2, "1.00")]
    [InlineData("ROUND", "0.5", 0, "1")]
    [InlineData("ROUND", "-0.5", 0, "-1")]
    [InlineData("ROUND", "-2.5", 0, "-3")]
    [InlineData("ROUND", "0.0000005", 6, "0.000001")]
    [InlineData("TRUNC", "1.009", 2, "1.00")]
    [InlineData("TRUNC", "-1.009", 2, "-1.00")]
    public void Rounding(string function, string value, int places, string expected)
    {
        var a = function == "ROUND" ? _r.Round(D(value), places) : _r.Truncate(D(value), places);
        Assert.Equal((0, D(expected)), (a.Header.ReturnCode, a.ResultValue));
    }

    [Fact]
    public void Precision()
    {
        Assert.Equal(14.285714m, _r.UnitPrice(100.00m, 7m, 6).ResultValue);
        var ext = _r.Extend(7m, 14.285714m, 2);
        Assert.Equal((100.00m, 99.999998m), (ext.ResultValue, ext.Unrounded));
        Assert.Equal(100.03m, _r.Extend(7m, 14.29m, 2).ResultValue);
        Assert.Equal(33.333333m, _r.UnitPrice(100.00m, 3m, 6).ResultValue);
    }

    private static decimal[] Parts(RndallocArea a, int n) => a.Lines.Take(n).Select(l => l.Part).ToArray();

    [Fact]
    public void Allocation()
    {
        var one = _r.Allocate(1.00m, 2, "EQUAL", [0m, 0m, 0m]);
        Assert.Equal([0.34m, 0.33m, 0.33m], Parts(one, 3));
        Assert.Equal((1.00m, 0m, 1), (one.Allocated, one.Residual, one.Bumped));
        var neg = _r.Allocate(-1.00m, 2, "EQUAL", [0m, 0m, 0m]);
        Assert.Equal([-0.34m, -0.33m, -0.33m], Parts(neg, 3));
        Assert.Equal(-1.00m, neg.Allocated);
        var hundred = _r.Allocate(100.00m, 2, "EQUAL", [0m, 0m, 0m]);
        Assert.Equal([33.34m, 33.33m, 33.33m], Parts(hundred, 3));
        Assert.Equal(100.00m, hundred.Allocated);
        Assert.Equal([0.01m, 0m, 0m], Parts(_r.Allocate(0.01m, 2, "EQUAL", [0m, 0m, 0m]), 3));
        var w = _r.Allocate(10.00m, 2, "WEIGHT", [1m, 2m, 3m]);
        Assert.Equal([1.67m, 3.33m, 5.00m], Parts(w, 3));
        Assert.Equal(10.00m, w.Allocated);
        var disc = _r.Allocate(50.00m, 2, "WEIGHT", [120.00m, 80.00m, 55.55m]);
        Assert.Equal([23.48m, 15.65m, 10.87m], Parts(disc, 3));
        Assert.Equal(50.00m, disc.Allocated);
        var zero = _r.Allocate(9.99m, 2, "WEIGHT", [1m, 0m, 1m]);
        Assert.Equal((0m, 9.99m), (zero.Lines[1].Part, zero.Allocated));
        Assert.Equal(100.00m, _r.Allocate(100.00m, 2, "EQUAL", new decimal[500]).Lines.Sum(l => l.Part));
        Assert.Equal(1.23m, _r.Allocate(1.23m, 2, "EQUAL", new decimal[500]).Lines.Sum(l => l.Part));
        Assert.Equal([0.333334m, 0.333333m, 0.333333m], Parts(_r.Allocate(1.000000m, 6, "EQUAL", [0m, 0m, 0m]), 3));
    }

    [Fact]
    public void Errors()
    {
        Assert.Equal((8, "RND0001"), Rc(_r.Round(0m, 9)));
        Assert.Equal((8, "RND0002"), Rc(_r.Allocate(0m, 2, "EQUAL", [])));
        Assert.Equal((8, "RND0003"), Rc(_r.Allocate(0m, 2, "PRORATA", [0m, 0m])));
        Assert.Equal((8, "RND0004"), Rc(_r.Allocate(10.00m, 2, "WEIGHT", [-1m, 3m])));
        Assert.Equal((8, "RND0005"), Rc(_r.Allocate(10.00m, 2, "WEIGHT", [0m, 0m])));
        Assert.Equal((8, "RND0006"), Rc(_r.UnitPrice(100.00m, 0m, 6)));
        var split = new RndallocArea { Places = 2 };
        split.Header.Function = "SPLIT";
        _r.Execute(split);
        Assert.Equal((8, "RND0008"), Rc(split));
    }

    private static (int, string) Rc(RndallocArea a) => (a.Header.ReturnCode, a.Header.ReasonCode);

    [Fact]
    public void Functions()
    {
        Assert.Equal(1.01m, CobolNumeric.RoundAway(1.005m, 2));
        Assert.Equal((-1, 0, 1), (CobolNumeric.SignOf(-0.000001m), CobolNumeric.SignOf(0m), CobolNumeric.SignOf(12345.678m)));
    }
}

public sealed class TstrutilDriverTests
{
    private static StrutilArea Call(string function, Action<StrutilArea> fill)
    {
        var a = new StrutilArea();
        a.Header.Company = "STC";
        a.Header.Function = function;
        fill(a);
        CobolString.Execute(a);
        return a;
    }

    [Fact]
    public void Code()
    {
        var folded = Call("CODE", a => a.TextIn = "brk-disc/fr.01");
        Assert.Equal(("BRK-DISC/FR.01", "Y", "Y", 4), (folded.TextOut, folded.Changed, folded.Valid, folded.Header.ReturnCode));
        var ok = Call("CODE", a => a.TextIn = "ASSY_900.A/2-B");
        Assert.Equal(("Y", 0), (ok.Valid, ok.Header.ReturnCode));
        var space = Call("CODE", a => a.TextIn = "BRK DISC");
        Assert.Equal(("N", "STR0003"), (space.Valid, space.Header.ReasonCode));
        var amp = Call("CODE", a => a.TextIn = "AB&CD");
        Assert.Equal(("N", "&", 3), (amp.Valid, amp.BadChar, amp.BadPosition));
        var forty = Call("CODE", a => a.TextIn = "ABCDEFGHIJABCDEFGHIJABCDEFGHIJABCDEFGHIJ");
        Assert.Equal(("Y", 40), (forty.Valid, forty.LengthOut));
        var fortyOne = Call("CODE", a => a.TextIn = "ABCDEFGHIJABCDEFGHIJABCDEFGHIJABCDEFGHIJX");
        Assert.Equal(("N", "STR0002"), (fortyOne.Valid, fortyOne.Header.ReasonCode));
        Assert.Equal("N", Call("CODE", a => { a.MaxLength = 20; a.TextIn = "ABCDEFGHIJABCDEFGHIJX"; }).Valid);
        var empty = Call("CODE", a => a.TextIn = "");
        Assert.Equal(("N", "STR0001"), (empty.Valid, empty.Header.ReasonCode));
    }

    [Theory]
    [InlineData("STRIP", "SI-000123", "SI123")]
    [InlineData("STRIP", "si 000123", "SI123")]
    [InlineData("STRIP", "SI/000123", "SI123")]
    [InlineData("KEEP", "SI-000123", "SI000123")]
    [InlineData("STRIP", "SI-0000", "SI0")]
    [InlineData("STRIP", "0001234", "1234")]
    [InlineData("STRIP", "SI-0001234", "SI1234")]
    public void Document_reference(string policy, string text, string expected) =>
        Assert.Equal(expected, Call("DOCREF", a => { a.ZeroPolicy = policy; a.TextIn = text; }).TextOut);

    [Fact]
    public void Document_reference_without_letters_or_digits()
    {
        var a = Call("DOCREF", a => a.TextIn = "---");
        Assert.Equal((8, "STR0005"), (a.Header.ReturnCode, a.Header.ReasonCode));
    }

    [Fact]
    public void Money()
    {
        Assert.Equal("GBP 1,234.56", Call("MONEY", a => a.Amount = 1234.56m).TextOut);
        var symbol = Call("MONEY", a => { a.Amount = 1234.56m; a.Symbol = "Y"; });
        Assert.Equal(("\u00A31,234.56", 9), (symbol.TextOut, symbol.LengthOut));
        Assert.Equal("GBP -1,250.00", Call("MONEY", a => a.Amount = -1250.00m).TextOut);
        Assert.Equal("EUR 1,150.00", Call("MONEY", a => { a.Amount = 1150.00m; a.Currency = "EUR"; a.Symbol = "Y"; }).TextOut);
        Assert.Equal("GBP 0.00", Call("MONEY", a => a.Amount = 0m).TextOut);
        Assert.Equal("GBP 1,234,567.89", Call("MONEY", a => a.Amount = 1234567.89m).TextOut);
    }

    [Fact]
    public void Quantity()
    {
        Assert.Equal("12.500000", Call("QTY", a => a.Amount = 12.5m).TextOut);
        Assert.Equal("12.5", Call("QTY", a => { a.Amount = 12.5m; a.ZeroPolicy = "STRIP"; }).TextOut);
        Assert.Equal("12", Call("QTY", a => { a.Amount = 12m; a.ZeroPolicy = "STRIP"; }).TextOut);
        Assert.Equal("0.000001", Call("QTY", a => { a.Amount = 0.000001m; a.ZeroPolicy = "STRIP"; }).TextOut);
    }

    [Fact]
    public void Miscellaneous()
    {
        Assert.Equal("STELLAR COMPONENTS LTD", Call("UPPER", a => a.TextIn = "Stellar Components Ltd").TextOut);
        Assert.Equal("Stellar Components Ltd", Call("SQUEEZE", a => a.TextIn = "  Stellar    Components   Ltd  ").TextOut);
        var unknown = Call("SOUNDEX", _ => { });
        Assert.Equal((8, "STR0009"), (unknown.Header.ReturnCode, unknown.Header.ReasonCode));
    }
}

/// <summary>TDATECAL: the business-day checks force DC-CAL-SOURCE "BUILTIN" (no database read), as the driver does.</summary>
public sealed class TdatecalDriverTests
{
    private readonly BusinessCalendar _cal = new(new NoSqlRunner(), new CalendarCache(TimeProvider.System, Options.Create(new SterpKernelOptions())));

    private Task<DatecalcArea> Call(string function, Action<DatecalcArea> fill) => _cal.CallAsync(function, fill);

    private Task<DatecalcArea> Builtin(string function, int date, string calendar, int number = 0, string direction = "") =>
        Call(function, a => { a.CalSource = "BUILTIN"; a.Date1 = date; a.Calendar = calendar; a.Number = number; a.Direction = direction; });

    [Fact]
    public async Task Validation()
    {
        var feb29 = await Call("VALIDATE", a => a.Date1 = 20260229);                       // ok 1
        Assert.Equal((8, "DAT0001"), (feb29.Header.ReturnCode, feb29.Header.ReasonCode));
        Assert.Equal(0, (await Call("VALIDATE", a => a.Date1 = 20280229)).Header.ReturnCode); // ok 2
        Assert.Equal(8, (await Call("VALIDATE", a => a.Date1 = 20261305)).Header.ReturnCode); // ok 3
        var someday = await Call("SOMEDAY", a => a.Date1 = 20261005);                        // ok 4
        Assert.Equal((8, "DAT0010"), (someday.Header.ReturnCode, someday.Header.ReasonCode));
    }

    [Theory]
    [InlineData("ADDDAYS", 20260228, 1, 20260301)]   // ok 5
    [InlineData("ADDDAYS", 20280228, 1, 20280229)]   // ok 6
    [InlineData("ADDDAYS", 20261001, 30, 20261031)]  // ok 7
    [InlineData("ADDDAYS", 20270101, -1, 20261231)]  // ok 8
    [InlineData("ADDMONTH", 20270131, 1, 20270228)]  // ok 9
    [InlineData("ADDMONTH", 20280131, 1, 20280229)]  // ok 10
    [InlineData("ADDMONTH", 20261231, 14, 20280229)] // ok 11 (the check name says 28 Feb; the driver expects 20280229)
    [InlineData("EOMONTH", 20261005, 0, 20261031)]   // ok 12
    [InlineData("EOMONTH", 20270115, 1, 20270228)]   // ok 13
    public async Task Arithmetic(string function, int date, int number, int expected) =>
        Assert.Equal(expected, (await Call(function, a => { a.Date1 = date; a.Number = number; })).ResultDate);

    [Fact]
    public async Task Days_between_and_day_names()
    {
        Assert.Equal(30, (await Call("DAYSBTWN", a => { a.Date1 = 20261001; a.Date2 = 20261031; })).ResultNum);  // ok 14
        Assert.Equal(-30, (await Call("DAYSBTWN", a => { a.Date1 = 20261031; a.Date2 = 20261001; })).ResultNum); // ok 15
        Assert.Equal("Monday", (await Call("DOW", a => a.Date1 = 20261005)).DayName);                            // ok 16
        Assert.Equal("Friday", (await Call("DOW", a => a.Date1 = 20261225)).DayName);                            // ok 17
    }

    [Fact]
    public async Task Format_and_parse()
    {
        Assert.Equal("05/10/2026", (await Call("FORMAT", a => { a.Date1 = 20261005; a.FormatOut = "DDMMYYYY"; })).TextOut); // ok 18
        var iso = await Call("FORMAT", a => { a.Date1 = 20270403; a.FormatOut = "ISO"; });
        Assert.Equal(("2027-04-03", "April"), (iso.TextOut, iso.MonthName));                                               // ok 19, 20
        Assert.Equal(20261005, (await Call("PARSE", a => { a.TextIn = "05/10/2026"; a.FormatIn = "DDMMYYYY"; })).ResultDate); // ok 21
        Assert.Equal(20261005, (await Call("PARSE", a => { a.TextIn = "2026-10-05"; a.FormatIn = "ISO"; })).ResultDate);      // ok 22
        Assert.Equal(8, (await Call("PARSE", a => { a.TextIn = "31/02/2027"; a.FormatIn = "DDMMYYYY"; })).Header.ReturnCode); // ok 23
        var rubbish = await Call("PARSE", a => a.TextIn = "NEXT WEEK");                                                     // ok 24
        Assert.Equal((8, "DAT0004"), (rubbish.Header.ReturnCode, rubbish.Header.ReasonCode));
    }

    [Theory]
    [InlineData(20261225, "ENG", "N")] // ok 25
    [InlineData(20261228, "ENG", "N")] // ok 26
    [InlineData(20261229, "ENG", "Y")] // ok 27
    [InlineData(20261226, "ENG", "N")] // ok 28
    [InlineData(20270104, "SCT", "N")] // ok 29
    [InlineData(20270104, "ENG", "Y")] // ok 30
    [InlineData(20260102, "SCT", "N")] // ok 31
    [InlineData(20260102, "ENG", "Y")] // ok 32
    [InlineData(20260406, "SCT", "Y")] // ok 33
    [InlineData(20260317, "NIR", "N")] // ok 34
    [InlineData(20260317, "ENG", "Y")] // ok 34
    [InlineData(20260713, "NIR", "N")] // ok 35
    [InlineData(20261130, "SCT", "N")] // ok 36
    public async Task Business_days(int date, string calendar, string business) =>
        Assert.Equal(business, (await Builtin("ISBUSDAY", date, calendar)).IsBusiness);

    [Fact]
    public async Task Business_day_details()
    {
        var xmas = await Builtin("ISBUSDAY", 20261225, "ENG");
        Assert.Equal(("Y", "Christmas Day"), (xmas.IsHoliday, xmas.HolidayName));             // ok 25, 37
        var boxing = await Builtin("ISBUSDAY", 20261228, "ENG");
        Assert.Equal(("Y", "N"), (boxing.IsHoliday, boxing.IsWeekend));                       // ok 26
        Assert.Equal("Y", (await Builtin("ISBUSDAY", 20261226, "ENG")).IsWeekend);           // ok 28
        Assert.Equal("Y", (await Builtin("ISBUSDAY", 20270104, "SCT")).IsHoliday);           // ok 29
        var deflt = await Builtin("ISBUSDAY", 20270104, "");
        Assert.Equal(("ENG", "Y"), (deflt.Calendar, deflt.IsBusiness));                      // ok 38
    }

    [Fact]
    public async Task Bacs()
    {
        var next = await Builtin("ADJUST", 20261225, "BACS", direction: "NEXT");
        Assert.Equal((20261229, 4, "Y"), (next.ResultDate, next.Header.ReturnCode, next.Adjusted));                // ok 39, 40
        Assert.Equal(20261224, (await Builtin("ADDBUS", 20261229, "BACS", -1)).ResultDate);                       // ok 41
        Assert.Equal(20261223, (await Builtin("ADDBUS", 20261229, "BACS", -2)).ResultDate);                       // ok 42
        Assert.Equal(20261224, (await Builtin("ADJUST", 20261225, "BACS", direction: "PREV")).ResultDate);        // ok 43
        var none = await Builtin("ADJUST", 20261225, "BACS", direction: "NONE");
        Assert.Equal((20261225, "N"), (none.ResultDate, none.IsBusiness));                                         // ok 44
        var already = await Builtin("ADJUST", 20261224, "BACS", direction: "NEXT");
        Assert.Equal((20261224, 0), (already.ResultDate, already.Header.ReturnCode));                              // ok 45
        Assert.Equal(20271229, (await Builtin("ADDBUS", 20271224, "ENG", 1)).ResultDate);                         // ok 46
        Assert.Equal(20280104, (await Builtin("ADDBUS", 20271231, "ENG", 1)).ResultDate);                         // ok 47
        Assert.Equal(20280105, (await Builtin("ADDBUS", 20271231, "SCT", 1)).ResultDate);                         // ok 47
        Assert.Equal(20261012, (await Builtin("ADDBUS", 20261005, "ENG", 5)).ResultDate);                         // ok 48
    }

    [Fact]
    public async Task Cache()
    {
        var cleared = await Call("CACHECLR", _ => { });
        Assert.Equal((0, 0), (cleared.Header.ReturnCode, cleared.CacheHits));                                      // ok 49
        // ok 50-52 (per-process load/hit counters 1/0, 1/1, 2) are not reproduced: FR-STERP-PLATFORM-019 reports the
        // counters as 0 (SME-074)
        var first = await Builtin("ISBUSDAY", 20261005, "ENG");
        Assert.Equal((0, 0, 8), (first.CacheLoads, first.CacheHits, first.CalRows));                              // ok 53
    }

    [Fact]
    public void Functions()
    {
        Assert.Equal((1, 0, 1, 0), (CobolDate.IsLeapFunction(2028), CobolDate.IsLeapFunction(2027), CobolDate.IsLeapFunction(2000), CobolDate.IsLeapFunction(1900))); // ok 54, 55
        Assert.Equal((29, 28, 0), (CobolDate.DaysInMonth(2028, 2), CobolDate.DaysInMonth(2027, 2), CobolDate.DaysInMonth(2027, 13)));                              // ok 56-58
        Assert.Equal(20280229, CobolDate.EndOfMonth(20280201));                                                                                                       // ok 59
    }
}

/// <summary>TTERMCAL on the isolated database (fresh dataset), as the driver ran: DATECALC reads the ENG holidays for BACS.</summary>
[Collection(LocalDbCollection.Name)]
public sealed class TtermcalDriverTests(LocalDbFixture db) : IAsyncLifetime
{
    private Microsoft.Extensions.DependencyInjection.ServiceProvider _root = null!;
    private Microsoft.Extensions.DependencyInjection.AsyncServiceScope _scope;

    public ValueTask InitializeAsync()
    {
        _root = db.Services();
        _scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(_root);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _root.DisposeAsync();
    }

    private Task<TermcalcArea> Due(Action<TermcalcArea> fill, string function = "DUEDATE") =>
        Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IPaymentTermsCalculator>(_scope.ServiceProvider)
            .CallAsync(Callers.For("STC", "", 0), function, fill);

    [Theory]
    [InlineData(31, 20270115, 20270228)] // ok 1
    [InlineData(31, 20280115, 20280229)] // ok 2
    [InlineData(31, 20270131, 20270228)] // ok 3
    [InlineData(15, 20261020, 20261115)] // ok 4
    [InlineData(31, 20261210, 20270131)] // ok 5
    public async Task Day_of_next_month(int day, int invoice, int expected)
    {
        var a = await Due(t => { t.BasisType = "DAYOFNEXTMONTH"; t.DayOfMonth = day; t.InvoiceDate = invoice; });
        Assert.Equal(expected, a.DueDate);
        if (day == 31 && invoice == 20261210) Assert.Equal("Day 31 of the following month", a.TermsDescription); // ok 6
    }

    [Fact]
    public async Task Days_and_end_of_month()
    {
        var days = await Due(t => { t.BasisType = "INVOICE"; t.Days = 30; t.InvoiceDate = 20261005; });
        Assert.Equal((20261104, 30), (days.DueDate, days.TermDays));                                       // ok 7, 8
        var eom = await Due(t => { t.BasisType = "EOM"; t.Days = 30; t.InvoiceDate = 20261005; });
        Assert.Equal((20261130, "30 days end of month"), (eom.DueDate, eom.TermsDescription));            // ok 9, 10
        var eom0 = await Due(t => { t.BasisType = "EOM"; t.Days = 0; t.InvoiceDate = 20280210; });
        Assert.Equal((20280229, "29/02/2028"), (eom0.DueDate, eom0.DueDateUk));                           // ok 11, 12
    }

    [Fact]
    public async Task Statutory()
    {
        var none = await Due(t => t.InvoiceDate = 20261005);
        Assert.Equal((20261104, "STATUTORY", "30 days - Late Payment Act default"), (none.DueDate, none.Source, none.TermsDescription)); // ok 13-15
        Assert.Equal(20261111, (await Due(t => { t.SupplyDate = 20261001; t.InvoiceDate = 20261001; t.ReceivedDate = 20261012; })).DueDate); // ok 16
        Assert.Equal(20261119, (await Due(t => { t.SupplyDate = 20261020; t.InvoiceDate = 20261001; t.ReceivedDate = 20261005; })).DueDate); // ok 17
        var nosuch = await Due(t => { t.TermsCode = "NOSUCH"; t.InvoiceDate = 20261005; });
        Assert.Equal((20261104, 4, "STATUTORY"), (nosuch.DueDate, nosuch.Header.ReturnCode, nosuch.Source)); // ok 18
    }

    [Fact]
    public async Task Discount()
    {
        var net = await Due(t =>
        {
            t.BasisType = "INVOICE"; t.Days = 30; t.DiscountDays = 10; t.DiscountPct = 2.5m; t.NetAmount = 1000.00m; t.VatAmount = 200.00m;
            t.DiscountBasis = "NET"; t.InvoiceDate = 20261005;
        });
        Assert.Equal((25.00m, 20261015), (net.DiscountAmount, net.DiscountDate));                           // ok 19, 20
        var gross = await Due(t =>
        {
            t.BasisType = "INVOICE"; t.Days = 30; t.DiscountDays = 10; t.DiscountPct = 2.5m; t.NetAmount = 1000.00m; t.VatAmount = 200.00m;
            t.DiscountBasis = "GROSS"; t.InvoiceDate = 20261005;
        });
        Assert.Equal(30.00m, gross.DiscountAmount);                                                          // ok 21
        Assert.Equal(0.50m, (await Due(t => { t.BasisType = "INVOICE"; t.Days = 30; t.DiscountPct = 1.5m; t.NetAmount = 33.33m; t.InvoiceDate = 20261005; })).DiscountAmount); // ok 22
        Assert.Equal(0, (await Due(t => { t.BasisType = "INVOICE"; t.Days = 30; t.InvoiceDate = 20261005; })).DiscountDate); // ok 23
    }

    [Fact]
    public async Task Limits_and_bacs()
    {
        var ninety = await Due(t => { t.BasisType = "INVOICE"; t.Days = 90; t.InvoiceDate = 20261005; });
        Assert.Equal(("Y", 4, "TRM0007"), (ninety.LongTermFlag, ninety.Header.ReturnCode, ninety.Header.ReasonCode)); // ok 24
        var sixty = await Due(t => { t.BasisType = "INVOICE"; t.Days = 60; t.InvoiceDate = 20261005; });
        Assert.Equal(("N", 0), (sixty.LongTermFlag, sixty.Header.ReturnCode));                                       // ok 25
        Assert.Equal("Y", (await Due(t => { t.BasisType = "INVOICE"; t.Days = 60; t.MaxTermDays = 45; t.InvoiceDate = 20261005; })).LongTermFlag); // ok 26
        var next = await Due(t =>
        {
            t.BasisType = "INVOICE"; t.Days = 81; t.InvoiceDate = 20261005; t.MaxTermDays = 200; t.BacsAdjust = "Y"; t.BacsDirection = "NEXT"; t.Calendar = "BACS";
        });
        Assert.Equal((20261225, 20261229), (next.DueDateRaw, next.DueDate));                                         // ok 27, 28
        var prev = await Due(t =>
        {
            t.BasisType = "INVOICE"; t.Days = 81; t.InvoiceDate = 20261005; t.MaxTermDays = 200; t.BacsAdjust = "Y"; t.BacsDirection = "PREV"; t.Calendar = "BACS";
        });
        Assert.Equal(20261224, prev.DueDate);                                                                         // ok 29
        Assert.Equal(20261225, (await Due(t => { t.BasisType = "INVOICE"; t.Days = 81; t.MaxTermDays = 200; t.InvoiceDate = 20261005; })).DueDate); // ok 30
    }

    [Fact]
    public async Task Errors()
    {
        var date = await Due(t => { t.BasisType = "INVOICE"; t.Days = 30; t.InvoiceDate = 20260230; });
        Assert.Equal((8, "TRM0003"), (date.Header.ReturnCode, date.Header.ReasonCode));        // ok 31
        var basis = await Due(t => { t.BasisType = "QUARTERLY"; t.InvoiceDate = 20261005; });
        Assert.Equal((8, "TRM0004"), (basis.Header.ReturnCode, basis.Header.ReasonCode));      // ok 32
        var dom = await Due(t => { t.BasisType = "DAYOFNEXTMONTH"; t.DayOfMonth = 0; t.InvoiceDate = 20261005; });
        Assert.Equal((8, "TRM0005"), (dom.Header.ReturnCode, dom.Header.ReasonCode));          // ok 33
        var fn = await Due(t => t.InvoiceDate = 20261005, "AGEING");
        Assert.Equal((8, "TRM0009"), (fn.Header.ReturnCode, fn.Header.ReasonCode));            // ok 34
    }
}

/// <summary>TFXCONV: every check supplies its rate, so no database read happens (NoSqlRunner proves it).</summary>
public sealed class TfxconvDriverTests
{
    private readonly FxConverter _fx = new(new NoSqlRunner(), new RoundingAllocator());

    private Task<FxconvArea> Call(string function, Action<FxconvArea> fill) => _fx.CallAsync(Callers.For("STC", "", 0), function, fill);

    [Fact]
    public async Task Conversions()
    {
        Assert.Equal(1000.00m, (await Call("TOGBP", a => { a.Currency = "USD"; a.Rate = 1.25m; a.ForeignAmount = 1250.00m; })).GbpAmount);   // ok 1
        Assert.Equal(1000.00m, (await Call("TOGBP", a => { a.Currency = "EUR"; a.Rate = 1.15m; a.ForeignAmount = 1150.00m; })).GbpAmount);   // ok 2
        Assert.Equal(1150.00m, (await Call("TOFCY", a => { a.Currency = "EUR"; a.Rate = 1.15m; a.GbpAmount = 1000.00m; })).ForeignAmount);   // ok 3
        var awkward = await Call("TOGBP", a => { a.Currency = "EUR"; a.Rate = 1.17m; a.ForeignAmount = 1000.00m; });
        Assert.Equal(854.70m, awkward.GbpAmount);                                                                                          // ok 4
        Assert.True(awkward.Residual != 0 && awkward.Residual < 0.01m && awkward.Residual > -0.01m);                                       // ok 5
        Assert.True(awkward.Unrounded > 854.700854m && awkward.Unrounded < 854.700855m);                                                   // ok 6
        var gbp = await Call("TOGBP", a => { a.Currency = "GBP"; a.ForeignAmount = 500.00m; });
        Assert.Equal((500.00m, 1m, 0), (gbp.GbpAmount, gbp.Rate, gbp.Header.ReturnCode));                                                 // ok 7
        Assert.Equal("1.25000000 USD per GBP 1", (await Call("TOGBP", a => { a.Currency = "USD"; a.Rate = 1.25m; a.ForeignAmount = 100.00m; })).RateText); // ok 8
        Assert.Equal(854.700855m, (await Call("TOGBP", a => { a.Currency = "EUR"; a.Rate = 1.17m; a.ForeignAmount = 1000.00m; a.Places = 6; })).GbpAmount); // ok 9
    }

    [Fact]
    public async Task Direction()
    {
        var refused = await Call("TOGBP", a => { a.Currency = "USD"; a.Rate = 0.8m; a.QuoteBasis = "GBPPERFCY"; a.ForeignAmount = 1250.00m; });
        Assert.Equal((8, "FX0003"), (refused.Header.ReturnCode, refused.Header.ReasonCode));                                               // ok 10
        var turned = await Call("TOGBP", a => { a.Currency = "USD"; a.Rate = 0.8m; a.QuoteBasis = "GBPPERFCY"; a.AllowInvert = "Y"; a.ForeignAmount = 1250.00m; });
        Assert.Equal((1.25m, 1000.00m), (turned.Rate, turned.GbpAmount));                                                                  // ok 11
        Assert.Equal((0.8m, 1.25m, "FCYPERGBP"), (turned.RateEntered, turned.Rate, turned.QuoteBasis));                                    // ok 12
        Assert.Equal((4, "FX0004"), (turned.Header.ReturnCode, turned.Header.ReasonCode));                                                 // ok 13
    }

    [Fact]
    public async Task Realised()
    {
        var asset = await Call("REALISED", a => { a.Currency = "EUR"; a.Rate = 1.15m; a.Rate2 = 1.20m; a.ForeignAmount = 1150.00m; a.Position = "ASSET"; });
        Assert.Equal(-41.67m, asset.Realised);                                                                                             // ok 14
        Assert.Equal((1000.00m, 958.33m), (asset.GbpAmount, asset.GbpAmount2));                                                            // ok 15
        Assert.Equal(41.67m, (await Call("REALISED", a => { a.Currency = "EUR"; a.Rate = 1.15m; a.Rate2 = 1.20m; a.ForeignAmount = 1150.00m; a.Position = "LIABILITY"; })).Realised); // ok 16
        Assert.Equal(0m, (await Call("REALISED", a => { a.Currency = "USD"; a.Rate = 1.25m; a.Rate2 = 1.25m; a.ForeignAmount = 1250.00m; })).Realised); // ok 17
        var zero = await Call("REALISED", a => { a.Currency = "USD"; a.Rate = 1.25m; a.Rate2 = 0m; a.ForeignAmount = 1250.00m; });
        Assert.Equal((8, "FX0009"), (zero.Header.ReturnCode, zero.Header.ReasonCode));                                                     // ok 18
    }

    [Fact]
    public async Task Errors()
    {
        var missing = await Call("TOGBP", a => { a.Currency = "USD"; a.Rate = 0m; a.ForeignAmount = 1250.00m; });
        Assert.Equal((8, "FX0001", 0m), (missing.Header.ReturnCode, missing.Header.ReasonCode, missing.GbpAmount));                       // ok 19, 20
        var negative = await Call("TOGBP", a => { a.Currency = "USD"; a.Rate = -1.25m; a.ForeignAmount = 1250.00m; });
        Assert.Equal((8, "FX0002"), (negative.Header.ReturnCode, negative.Header.ReasonCode));                                             // ok 21
        var blank = await Call("TOGBP", a => { a.Currency = ""; a.Rate = 1.25m; a.ForeignAmount = 1250.00m; });
        Assert.Equal((8, "FX0005"), (blank.Header.ReturnCode, blank.Header.ReasonCode));                                                   // ok 22
        var places = await Call("TOGBP", a => { a.Currency = "USD"; a.Rate = 1.25m; a.Places = 9; a.ForeignAmount = 1250.00m; });
        Assert.Equal((8, "FX0006"), (places.Header.ReturnCode, places.Header.ReasonCode));                                                 // ok 23
        var date = await Call("GETRATE", a => { a.Currency = "USD"; a.RateDate = 20260230; });
        Assert.Equal((8, "FX0007"), (date.Header.ReturnCode, date.Header.ReasonCode));                                                     // ok 24
        var fn = await Call("REVALUE", a => a.Currency = "USD");
        Assert.Equal((8, "FX0010"), (fn.Header.ReturnCode, fn.Header.ReasonCode));                                                         // ok 25
    }
}

/// <summary>
/// TDBSVC on the isolated database (fresh dataset): fixtures and service calls inside one SQLIO unit that is rolled back
/// (checks 1-45), then the input and business refusals in autocommit (46-62), with the driver's WF-* values.
/// </summary>
[Collection(LocalDbCollection.Name)]
public sealed class TdbsvcDriverTests(LocalDbFixture db) : IAsyncLifetime
{
    private const string Co = "STC", Site = "RDG", User = "SYSADM", Series = "ZZTEST", Param = "STERP.TEST.SETTLEDAYS";
    private const int OpenDate = 20261015, SoftDate = 20260915, HardDate = 20260515;

    private ServiceProvider _root = null!;
    private AsyncServiceScope _scope;

    public ValueTask InitializeAsync()
    {
        _root = db.Services();
        _scope = _root.CreateAsyncScope();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _root.DisposeAsync();
    }

    private T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    private static KernelCaller Caller(string user = User, int date = OpenDate, string site = "", string correlation = "") =>
        new(Co, site, user, date, correlation);

    [Fact]
    public async Task Services_inside_a_rolled_back_unit()
    {
        var uow = Get<IUnitOfWork>();
        var sql = Get<ISqlRunner>();
        await uow.BeginAsync();                                                                                              // ok 1
        try
        {
            await sql.ExecAsync("INSERT INTO core.Parameter (ParamCode, CompanyCode, EffectiveFrom, Value, DataType, UnitOfValue, DecisionRef, " +
                "Description, OwnerRole) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                [SqlioParm.Text(Param), SqlioParm.Text(Co), SqlioParm.Text("20200101"), SqlioParm.Text("42.5"), SqlioParm.Text("DECIMAL"),
                 SqlioParm.Text("DAYS"), SqlioParm.Text("D-09"), SqlioParm.Text("TDBSVC fixture - settlement days"), SqlioParm.Text("FINCTRL")]); // ok 2
            await sql.ExecAsync("INSERT INTO core.NumberSeries (CompanyCode, SeriesCode, DocumentType, Description, Prefix, NumberLength, " +
                "NextNumber, MinNumber, MaxNumber, IsLegalSequence, IsActive) VALUES (?, ?, ?, ?, ?, 6, 1, 1, 999999, 1, 1)",
                [SqlioParm.Text(Co), SqlioParm.Text(Series), SqlioParm.Text("TEST"), SqlioParm.Text("TDBSVC fixture series"), SqlioParm.Text("ZZ")]); // ok 3

            var parm = await Get<IParameterService>().GetAsync(Caller(), Param, numeric: true);
            Assert.Equal((0, "Y", 42.5m, "D-09", 20200101, "Y"),
                (parm.Header.ReturnCode, parm.Found, parm.ValueNum, parm.DecisionRef, parm.EffectiveFrom, parm.IsNumeric));    // ok 4-8

            var numbers = Get<IDocumentNumberService>();
            var n1 = await numbers.NextAsync(Caller(correlation: "TDBSVC-NUMGEN-0000000000000000000001"), Series);
            Assert.Equal((0, 1L, "ZZ000001"), (n1.Header.ReturnCode, n1.Sequence, n1.DocumentNumber));                       // ok 9-11
            var replay = await numbers.NextAsync(Caller(correlation: "TDBSVC-NUMGEN-0000000000000000000001"), Series);
            Assert.True(replay.Header.ReturnCode < 8 && replay.DocumentNumber == n1.DocumentNumber);                          // ok 12
            var n2 = await numbers.NextAsync(Caller(correlation: "TDBSVC-NUMGEN-0000000000000000000002"), Series);
            Assert.Equal(2L, n2.Sequence);                                                                                   // ok 13
            var voided = await numbers.VoidAsync(Caller(correlation: "TDBSVC-NUMGEN-0000000000000000000002"), Series, n2.DocumentNumber,
                "Abandoned by the TDBSVC unit test");
            Assert.Equal((0, "VOID"), (voided.Header.ReturnCode, voided.Status));                                             // ok 14, 15
            var n3 = await numbers.NextAsync(Caller(correlation: "TDBSVC-NUMGEN-0000000000000000000003"), Series);
            Assert.Equal(3L, n3.Sequence);                                                                                   // ok 16

            var periods = Get<IPeriodService>();
            var open = await periods.CheckAsync(Caller(), OpenDate, OpenDate, OpenDate, "POST");
            Assert.Equal((0, "Y", 2026, 7, "OPEN"), (open.Header.ReturnCode, open.Allowed, open.FiscalYear, open.PeriodNo, open.PeriodStatus)); // ok 17-19
            var soft = await periods.CheckAsync(Caller("ARCLERK", SoftDate), SoftDate, action: "POST");
            Assert.Equal((6, "N", "FIN.PERIOD.SOFTPOST"), (soft.Header.ReturnCode, soft.Allowed, soft.OverridePermission));    // ok 20, 21
            var controller = await periods.CheckAsync(Caller("FINCTRL", SoftDate), SoftDate, action: "POST");
            Assert.Equal((0, "Y"), (controller.Header.ReturnCode, controller.Allowed));                                       // ok 22
            var hard = await periods.CheckAsync(Caller(User, HardDate), HardDate, action: "POST");
            Assert.Equal((8, "N"), (hard.Header.ReturnCode, hard.Allowed));                                                   // ok 23

            var perms = Get<IPermissionService>();
            var sysadm = await perms.CheckAsync(Caller(site: Site), "AR.RECEIPT.POST");
            Assert.Equal((0, "Y"), (sysadm.Header.ReturnCode, sysadm.Allowed));                                               // ok 24
            var auditor = await perms.CheckAsync(Caller("AUDITOR"), "AR.RECEIPT.POST");
            Assert.Equal((6, "N"), (auditor.Header.ReturnCode, auditor.Allowed));                                             // ok 25
            Assert.NotEqual("", auditor.ReasonText);                                                                         // ok 26

            var audit = await Get<IAuditWriter>().WriteAsync(Caller(), new AuditlogArea
            {
                EntityType = "core.CreditLimit", EntityKey = "STC/ZZCUST0001", Action = "AMEND", FieldName = "CreditLimit", OldValue = "5000.00",
                NewValue = "7500.00", Reason = "Annual review of the credit limit", AuthorityRef = "APPR-2026-000123", Source = "TDBSVC", Sensitive = "Y",
            });
            Assert.Equal(0, audit.Header.ReturnCode);                                                                         // ok 27
            Assert.True(audit.AuditId > 0);                                                                                   // ok 28
            var kept = await sql.SingleAsync("SELECT CAST(COUNT(*) AS varchar(10)) FROM core.AuditLog WHERE EntityKey = ? AND Action = ? " +
                "AND OldValue = ? AND NewValue = ?",
                [SqlioParm.Text("STC/ZZCUST0001"), SqlioParm.Text("AMEND"), SqlioParm.Text("5000.00"), SqlioParm.Text("7500.00")], [12]);
            Assert.Equal(1m, kept.NumVal(0));                                                                                // ok 29

            var raiser = Get<IExceptionRaiser>();
            var holdItem = await raiser.RaiseAsync(Caller(), new ExcpraisArea
            {
                Module = "AR", Severity = "HOLD", Category = "CREDIT-LIMIT", MessageText = "Credit limit exceeded by 1,250.00 GBP", DocumentType = "SORDER",
                DocumentRef = "ZZSO-2026-004417", OwnerUser = "CREDMGR", OwnerRole = "CREDIT", Priority = 1, AmountGbp = 1250.00m,
            });
            Assert.Equal((6, true, "OPEN"), (holdItem.Header.ReturnCode, holdItem.ExceptionId > 0, holdItem.Status));        // ok 30-32
            var warning = await raiser.RaiseAsync(new KernelCaller(Co, "", User, 0, ""), new ExcpraisArea
            {
                Module = "AP", Severity = "WARNING", Category = "PRICE-VARIANCE", MessageText = "Invoice price differs from the order price", OwnerRole = "APMGR",
            });
            Assert.Equal(4, warning.Header.ReturnCode);                                                                       // ok 33

            var idem = Get<IIdempotencyService>();
            var a = new KernelCaller(Co, "", User, 0, "TDBSVC-IDEMPOT-000000000000000000A1");
            var begun = await idem.BeginAsync(a, "AR.RECEIPT.POST");
            Assert.Equal((0, "STARTED"), (begun.Header.ReturnCode, begun.Outcome));                                           // ok 34
            var again = await idem.BeginAsync(a, "AR.RECEIPT.POST");
            Assert.Equal((6, "INPROGRESS"), (again.Header.ReturnCode, again.Outcome));                                        // ok 35
            var ended = await idem.EndAsync(a, "COMPLETED", "ZZRCPT-2026-000911", "Receipt posted");
            Assert.Equal(0, ended.Header.ReturnCode);                                                                         // ok 36
            var replayed = await idem.BeginAsync(a, "AR.RECEIPT.POST");
            Assert.Equal((4, "COMPLETED", "ZZRCPT-2026-000911"), (replayed.Header.ReturnCode, replayed.Outcome, replayed.ResultRef)); // ok 37, 38
            var b = await idem.BeginAsync(a with { CorrelationId = "TDBSVC-IDEMPOT-000000000000000000B2" }, "AR.RECEIPT.POST");
            Assert.Equal((0, "STARTED"), (b.Header.ReturnCode, b.Outcome));                                                   // ok 39

            var joblog = Get<IJobRunRegistry>();
            var start = await joblog.StartAsync(Caller(site: Site), "ZZTDBSVC", "CO=STC ASAT=20261015");
            Assert.Equal((0, true, "RUNNING"), (start.Header.ReturnCode, start.JobRunId > 0, start.Status));                  // ok 40, 41
            var end = await joblog.EndAsync(Caller(site: Site), "ZZTDBSVC", start.JobRunId, "COMPLETE", 0, 417, null,
                @"run\spool\ZZTDBSVC-STC-20261015120000.lst", "417 rows reported");
            Assert.Equal((0, "COMPLETE"), (end.Header.ReturnCode, end.Status));                                               // ok 42, 43
            var stored = await sql.SingleAsync("SELECT Status FROM core.JobRun WHERE JobRunId = ?", [SqlioParm.Integer(start.JobRunId)], [12]);
            Assert.Equal("OK", stored.Text(0));                                                                               // ok 44
        }
        finally
        {
            await uow.RollbackAsync();                                                                                        // ok 45
        }
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM core.NumberSeries WHERE SeriesCode = 'ZZTEST'"));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM core.JobRun WHERE JobName = 'ZZTDBSVC'"));
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT COUNT(*) FROM core.IdempotencyKey WHERE CorrelationId LIKE 'TDBSVC-%'"));
    }

    [Fact]
    public async Task Input_refusals_need_no_database_writes()
    {
        Assert.Equal(8, (await Get<IParameterService>().GetAsync(new KernelCaller(Co, "", "", 0, ""), "")).Header.ReturnCode);           // ok 46
        Assert.Equal(8, (await Get<IDocumentNumberService>().NextAsync(new KernelCaller(Co, "", "", 0, ""), "")).Header.ReturnCode);     // ok 47
        var feb30 = await Get<IPeriodService>().CheckAsync(new KernelCaller(Co, "", "", 0, ""), 20260230, action: "POST");
        Assert.Equal((8, "N"), (feb30.Header.ReturnCode, feb30.Allowed));                                                                // ok 48
        Assert.Equal(8, (await Get<IPermissionService>().CheckAsync(new KernelCaller(Co, "", User, 0, ""), "")).Header.ReturnCode);       // ok 49
        var noType = await Get<IAuditWriter>().WriteAsync(new KernelCaller(Co, "", User, 0, ""), new AuditlogArea { Action = "AMEND" });
        Assert.Equal(8, noType.Header.ReturnCode);                                                                                       // ok 50
        var noUser = await Get<IAuditWriter>().WriteAsync(new KernelCaller(Co, "", "", 0, ""),
            new AuditlogArea { EntityType = "core.CreditLimit", EntityKey = "STC/ZZCUST0001", Action = "AMEND" });
        Assert.Equal(8, noUser.Header.ReturnCode);                                                                                       // ok 51
        var raiser = Get<IExceptionRaiser>();
        Assert.Equal(8, (await raiser.RaiseAsync(new KernelCaller(Co, "", User, 0, ""), new ExcpraisArea
            { Module = "AP", Severity = "SHOUTING", Category = "TEST", MessageText = "Severity that does not exist", OwnerRole = "APMGR" })).Header.ReturnCode); // ok 52
        Assert.Equal(8, (await raiser.RaiseAsync(new KernelCaller(Co, "", User, 0, ""), new ExcpraisArea
            { Module = "AR", Severity = "ERROR", Category = "TEST", MessageText = "", OwnerRole = "CREDMGR" })).Header.ReturnCode);                          // ok 53
        Assert.Equal(8, (await raiser.RaiseAsync(new KernelCaller(Co, "", User, 0, ""), new ExcpraisArea
            { Module = "AR", Severity = "ERROR", Category = "TEST", MessageText = "An exception that nobody owns" })).Header.ReturnCode);                    // ok 54
        Assert.Equal(8, (await Get<IIdempotencyService>().BeginAsync(new KernelCaller(Co, "", "", 0, ""), "AR.RECEIPT.POST")).Header.ReturnCode);          // ok 55
        var joblog = Get<IJobRunRegistry>();
        Assert.Equal(8, (await joblog.StartAsync(new KernelCaller(Co, "", User, 0, ""), "", null)).Header.ReturnCode);                    // ok 56
        Assert.Equal(8, (await joblog.EndAsync(new KernelCaller(Co, "", "", 0, ""), "", 0, "COMPLETE", 0)).Header.ReturnCode);             // ok 57
        Assert.Equal(8, (await joblog.EndAsync(new KernelCaller(Co, "", "", 0, ""), "", 1, "FINISHED", 0)).Header.ReturnCode);             // ok 58
    }

    [Fact]
    public async Task Business_refusals()
    {
        var parameters = Get<IParameterService>();
        var fallback = await parameters.GetAsync(new KernelCaller(Co, "", "", OpenDate, ""), "STERP.TEST.NOTHELD", defaultText: "14");
        Assert.Equal((4, "PRM0004", "14"), (fallback.Header.ReturnCode, fallback.Header.ReasonCode, fallback.ValueText));                 // ok 59
        var notHeld = await parameters.GetAsync(new KernelCaller(Co, "", "", OpenDate, ""), "STERP.TEST.NOTHELD");
        Assert.Equal((16, "N"), (notHeld.Header.ReturnCode, notHeld.Found));                                                              // ok 60
        Assert.True((await Get<IDocumentNumberService>().NextAsync(new KernelCaller(Co, "", User, 0, ""), "ZZNOSUCH")).Header.ReturnCode >= 8); // ok 61
        var noPeriod = await Get<IPeriodService>().CheckAsync(new KernelCaller(Co, "", User, 0, ""), 19900101, action: "POST");
        Assert.True(noPeriod.Header.ReturnCode >= 8 && noPeriod.Allowed == "N");                                                         // ok 62
    }
}

/// <summary>TSQLIO / TSQLIO2 on the isolated database: SQLIO unit-of-work state, SNGL, not found, error, and PING.</summary>
[Collection(LocalDbCollection.Name)]
public sealed class TsqlioDriverTests(LocalDbFixture db) : IAsyncLifetime
{
    private ServiceProvider _root = null!;
    private AsyncServiceScope _scope;

    public ValueTask InitializeAsync()
    {
        _root = db.Services();
        _scope = _root.CreateAsyncScope();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _root.DisposeAsync();
    }

    private T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    [Fact]
    public async Task Tsqlio()
    {
        var uow = Get<IUnitOfWork>();
        var sql = Get<ISqlRunner>();
        Assert.Equal(1, uow.TransactionState);                                                                    // ok 1 TSTA autocommit
        await uow.BeginAsync();
        Assert.Equal(0, uow.TransactionState);                                                                    // ok 2 caller-owned unit
        await uow.RollbackAsync();
        Assert.Equal(1, uow.TransactionState);                                                                    // ok 3
        var row = await sql.SingleAsync("SELECT ?, CAST(? AS decimal(18,2)) * 2", [SqlioParm.Text("ABC"), SqlioParm.Number(-12.5m)], [10, 20]);
        Assert.Equal((0, "ABC", -25m), (row.SqlCode, row.Text(0), row.NumVal(1)));                                // ok 4
        Assert.Equal(100, (await sql.SingleAsync("SELECT name FROM sys.objects WHERE 1 = 0", [], [10])).SqlCode); // ok 5
        var error = await Assert.ThrowsAsync<Sterp.Platform.Kernel.SqlioException>(() => sql.ExecAsync("SELECT * FROM dbo.NoSuchTable", []));
        Assert.NotEqual("", error.DriverText.Trim());                                                             // ok 6
        // ok 7, 8 (PING ECHO COMP-3 round trip) exercise the COBOL engine and are not part of FR-STERP-PLATFORM-030
        var ping = await Get<IDatabaseProbe>().ProbeAsync();
        Assert.Equal((0, "STERP"), (ping.Result.ReturnCode, ping.DatabaseName));                                  // ok 9
    }

    [Fact]
    public async Task Tsqlio2_select_after_insert_without_nocount_returns_the_row()
    {
        var uow = Get<IUnitOfWork>();
        await uow.BeginAsync();
        try
        {
            var row = await Get<ISqlRunner>().SingleAsync(
                "DECLARE @t TABLE (i int IDENTITY, v int); INSERT @t (v) VALUES (7); SELECT CAST(SCOPE_IDENTITY() AS int);", [], [20]);
            Assert.Equal((0, 1m), (row.SqlCode, row.NumVal(0)));                                                  // TSQLIO2 ok 1
        }
        finally
        {
            await uow.RollbackAsync();
        }
    }
}

/// <summary>TCMDARG checks 1-16 (CMDARGS PARSE / GET) on the kernel CommandLineArgs; RUNPATH (17-27) is the jobs framework's OutputPaths.</summary>
public sealed class TcmdargDriverTests
{
    [Fact]
    public void Parse()
    {
        var job = Sterp.Platform.Kernel.Jobs.CommandLineArgs.Parse("AGEDDEBT CO=STC ASAT=20261004 USER=ARCLERK1");
        Assert.Equal(3, job.Pairs.Count);                                                             // ok 1
        Assert.Equal(("STC", "20261004", "ARCLERK1"), (job.Co, job.AsAt, job.User));                  // ok 2
        Assert.Equal("CO", job.Pairs[0].Key);                                                         // ok 3
        var folded = Sterp.Platform.Kernel.Jobs.CommandLineArgs.Parse("co=stc mode=commit");
        Assert.Equal(("CO", "stc", "COMMIT"), (folded.Pairs[0].Key, folded.Co, folded.Mode));         // ok 4
        Assert.Equal("monthly run.lst", Sterp.Platform.Kernel.Jobs.CommandLineArgs.Parse("CO=STC OUT=\"monthly run.lst\" MODE=PREVIEW").Out); // ok 5
        var spaced = Sterp.Platform.Kernel.Jobs.CommandLineArgs.Parse("CO=STC    PERIOD=202607   MODE=COMMIT");
        Assert.Equal((3, "202607"), (spaced.Pairs.Count, spaced.Period));                              // ok 6
        var empty = Sterp.Platform.Kernel.Jobs.CommandLineArgs.Parse("");
        Assert.Equal((0, 0), (empty.Result.ReturnCode, empty.Pairs.Count));                            // ok 7
        var site = Sterp.Platform.Kernel.Jobs.CommandLineArgs.Parse("CO=STC SITE=");
        Assert.Equal((2, ""), (site.Pairs.Count, site.Site.Trim()));                                   // ok 8
        var range = Sterp.Platform.Kernel.Jobs.CommandLineArgs.Parse("RPT FROM=20260401 TO=20270331 CO=CAL");
        Assert.Equal(("20260401", "20270331", "CAL"), (range.From, range.To, range.Co));              // ok 9
    }

    [Fact]
    public void Lookup()
    {
        var args = Sterp.Platform.Kernel.Jobs.CommandLineArgs.Parse("AGEDDEBT CO=STC BUCKETS=30,60,90");
        Assert.Equal("30,60,90", args.Get("BUCKETS"));                                                // ok 10
        Assert.Null(args.Get("SITE"));                                                                // ok 11 (legacy RC 16, CM-FOUND N)
        Assert.True(args.Has("buckets"));                                                             // ok 12
    }

    [Theory]
    [InlineData("AGEDDEBT CO=STC PREVIEW", "CMD0001")]  // ok 13
    [InlineData("AGEDDEBT =STC", "CMD0002")]            // ok 14
    [InlineData("AGEDDEBT OUT=\"run.lst", "CMD0003")]   // ok 15
    public void Parse_errors(string line, string reason)
    {
        var r = Sterp.Platform.Kernel.Jobs.CommandLineArgs.Parse(line).Result;
        Assert.Equal((8, reason), (r.ReturnCode, r.ReasonCode));
    }
    // ok 16 (function SPLIT -> CMD0009) has no equivalent: the C# API has no function code
}
