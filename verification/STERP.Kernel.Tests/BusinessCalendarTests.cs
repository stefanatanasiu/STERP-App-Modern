using Microsoft.Extensions.Options;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Kernel.Tests;

/// <summary>
/// DATECALC functions that need no database (FR-STERP-PLATFORM-019; BR-DATECALC-001 .. 023): calendar arithmetic and the
/// business-day functions on the built-in CALBUILT table (DC-CAL-SOURCE "BUILTIN" on entry, no database read).
/// </summary>
public sealed class BusinessCalendarTests
{
    private readonly BusinessCalendar _cal = new(new NoSqlRunner(), new CalendarCache(TimeProvider.System, Options.Create(new SterpKernelOptions())));

    private Task<DatecalcArea> BuiltIn(string function, int date, int number = 0, string calendar = "", string direction = "") =>
        _cal.CallAsync(function, a =>
        {
            a.Date1 = date;
            a.Number = number;
            a.Calendar = calendar;
            a.Direction = direction;
            a.CalSource = "BUILTIN";
        });

    [Fact]
    public async Task AddMonth_clamps_to_the_last_day_of_the_target_month()
    {
        // FR-019 AC1
        var a = await _cal.AddMonthsAsync(20270131, 1);
        Assert.Equal(20270228, a.ResultDate);
        Assert.Equal(20270228, a.Date2);
        Assert.Equal("Calendar months added, day clamped to month end", a.Header.Message);
        Assert.Equal(20280229, (await _cal.AddMonthsAsync(20280131, 1)).ResultDate);
        Assert.Equal(20251130, (await _cal.AddMonthsAsync(20260130, -2)).ResultDate);
        Assert.Equal("DAT0005", (await _cal.AddMonthsAsync(99991231, 1)).Header.ReasonCode);
    }

    [Fact]
    public async Task AddBus_skips_the_christmas_holidays_and_the_weekend()
    {
        // FR-019 AC2: Thursday 2026-12-24 + 2 business days on ENG (25 and 28 December are holidays) = 2026-12-30
        var a = await BuiltIn("ADDBUS", 20261224, 2, "ENG");
        Assert.Equal(0, a.Header.ReturnCode);
        Assert.Equal(20261230, a.ResultDate);
        Assert.Equal("2 business day(s) on calendar ENG", a.Header.Message);
        Assert.Equal("BUILTIN", a.CalSource);
        Assert.Equal(8, a.CalRows);
        var back = await BuiltIn("ADDBUS", 20261228, -1, "ENG");
        Assert.Equal(20261224, back.ResultDate);
        Assert.Equal("-1 business day(s) on calendar ENG", back.Header.Message);
    }

    [Fact]
    public async Task Parse_of_six_digits_is_always_ddmmyy_with_pivot_50_and_a_warning()
    {
        // FR-019 AC3
        var a = await _cal.ParseAsync("261004", "YYYYMMDD");
        Assert.Equal(20041026, a.ResultDate);
        Assert.Equal(20041026, a.Date1);
        Assert.Equal(4, a.Header.ReturnCode);
        Assert.Equal("DAT0007", a.Header.ReasonCode);
        Assert.Equal("Date read", a.Header.Message);
        Assert.Equal(0, a.Header.MessageCount);
        Assert.Equal(19991231, (await _cal.ParseAsync("311299")).ResultDate);
        var bad = await _cal.ParseAsync("310226");
        Assert.Equal((8, "DAT0001", "[310226] is not a valid calendar date"), (bad.Header.ReturnCode, bad.Header.ReasonCode, bad.Header.Message));
    }

    [Fact]
    public async Task Parse_of_eight_digits_follows_the_format()
    {
        Assert.Equal(20261019, (await _cal.ParseAsync("19/10/2026", "DDMMYYYY")).ResultDate);
        Assert.Equal(20261019, (await _cal.ParseAsync("2026-10-19", "ISO")).ResultDate);
        Assert.Equal(20261019, (await _cal.ParseAsync("20261019")).ResultDate);
        var fmt = await _cal.ParseAsync("19/10/2026", "MMDDYYYY");
        Assert.Equal(("DAT0003", "Unknown date format MMDDYYYY"), (fmt.Header.ReasonCode, fmt.Header.Message));
        var few = await _cal.ParseAsync("19/10");
        Assert.Equal(("DAT0004", "Cannot read a date from [19/10]"), (few.Header.ReasonCode, few.Header.Message));
    }

    [Fact]
    public async Task An_unknown_calendar_is_weekends_only()
    {
        // FR-019 AC4 (SME-1257): XYZ is never validated
        var a = await BuiltIn("ISBUSDAY", 20261019, calendar: "xyz");
        Assert.Equal(0, a.Header.ReturnCode);
        Assert.Equal(1, a.ResultNum);
        Assert.Equal("XYZ", a.Calendar);
        Assert.Equal("Monday is a business day on calendar XYZ", a.Header.Message);
        Assert.Equal(0, a.CalRows);
        var christmas = await BuiltIn("ISBUSDAY", 20261225, calendar: "XYZ");
        Assert.Equal(1, christmas.ResultNum);
    }

    [Fact]
    public async Task IsBusDay_names_the_holiday_and_bacs_uses_the_eng_holidays()
    {
        var eng = await BuiltIn("ISBUSDAY", 20261225, calendar: "ENG");
        Assert.Equal((0, "Y", "Christmas Day", "Friday is Christmas Day on calendar ENG"), (eng.ResultNum, eng.IsHoliday, eng.HolidayName, eng.Header.Message));
        var bacs = await BuiltIn("ISBUSDAY", 20261228, calendar: "BACS");
        Assert.Equal((0, "Boxing Day substitute"), (bacs.ResultNum, bacs.HolidayName));
        var weekend = await BuiltIn("ISBUSDAY", 20261024);
        Assert.Equal((0, "Y", "Saturday is not a business day on calendar ENG"), (weekend.ResultNum, weekend.IsWeekend, weekend.Header.Message));
        Assert.Equal("ENG", weekend.Calendar);
        var sct = await BuiltIn("ISBUSDAY", 20261130, calendar: "SCT");
        Assert.Equal("St Andrew's Day", sct.HolidayName);
    }

    [Fact]
    public async Task Adjust_moves_in_the_direction_or_only_reports()
    {
        var next = await BuiltIn("ADJUST", 20261024);
        Assert.Equal((4, "DAT0009", 20261026, 2, "Y"), (next.Header.ReturnCode, next.Header.ReasonCode, next.ResultDate, next.ResultNum, next.Adjusted));
        Assert.Equal("Moved 2 day(s) to the NEXT business day on calendar ENG", next.Header.Message);
        Assert.Equal("NEXT", next.Direction);
        var prev = await BuiltIn("ADJUST", 20261024, direction: "prev");
        Assert.Equal((20261023, 1), (prev.ResultDate, prev.ResultNum));
        var none = await BuiltIn("ADJUST", 20261024, direction: "NONE");
        Assert.Equal((0, 20261024, "Saturday is not a business day on calendar ENG"), (none.Header.ReturnCode, none.ResultDate, none.Header.Message));
        var already = await BuiltIn("ADJUST", 20261019);
        Assert.Equal((0, "Already a business day on calendar ENG"), (already.Header.ReturnCode, already.Header.Message));
        var bad = await BuiltIn("ADJUST", 20261019, direction: "UP");
        Assert.Equal(("DAT0008", "Adjustment direction must be NEXT, PREV or NONE"), (bad.Header.ReasonCode, bad.Header.Message));
    }

    [Fact]
    public async Task AddBus_zero_moves_a_non_business_start_to_the_next_business_day()
    {
        var a = await BuiltIn("ADDBUS", 20261226, 0, "ENG");
        Assert.Equal((20261229, "NEXT", 3), (a.ResultDate, a.Direction, a.ResultNum));
        Assert.Equal("0 business day(s) on calendar ENG", a.Header.Message);
    }

    [Fact]
    public async Task Simple_date_services()
    {
        var dow = await _cal.DayOfWeekAsync(20261019);
        Assert.Equal((1, "Monday", "October", "N", "Monday"), (dow.ResultNum, dow.DayName, dow.MonthName, dow.IsWeekend, dow.Header.Message));
        var between = await _cal.DaysBetweenAsync(20261019, 20261009);
        Assert.Equal((-10, "-10 day(s) between the dates"), (between.ResultNum, between.Header.Message));
        Assert.Equal("DAT0002", (await _cal.DaysBetweenAsync(20261019, 20261301)).Header.ReasonCode);
        Assert.Equal("19/10/2026", (await _cal.FormatAsync(20261019, "DDMMYYYY")).TextOut);
        Assert.Equal("2026-10-19", (await _cal.FormatAsync(20261019, "ISO")).TextOut);
        Assert.Equal("20261019", (await _cal.FormatAsync(20261019, "")).TextOut);
        var fmt = await _cal.FormatAsync(20261019, "XYZ");
        Assert.Equal(("DAT0003", "Unknown date format XYZ", "October", 20261019), (fmt.Header.ReasonCode, fmt.Header.Message, fmt.MonthName, fmt.ResultDate));
        Assert.Equal(20260131, (await _cal.EndOfMonthAsync(20260115)).ResultDate);
        Assert.Equal(20270228, (await _cal.EndOfMonthAsync(20270115, 1)).ResultDate);
        var add = await _cal.AddDaysAsync(20261019, 30);
        Assert.Equal((20261118, "Calendar days added"), (add.ResultDate, add.Header.Message));
        Assert.Equal("DAT0005", (await _cal.AddDaysAsync(99991231, 1)).Header.ReasonCode);
        var invalid = await _cal.AddDaysAsync(20260230, 1);
        Assert.Equal(("DAT0001", "Date 20260230 is not a valid calendar date", 8, 1),
            (invalid.Header.ReasonCode, invalid.Header.Message, invalid.Header.ReturnCode, invalid.Header.MessageCount));
        var valid = await _cal.CallAsync("VALIDATE", a => a.Date1 = 20261019);
        Assert.Equal((0, 20261019, "Date is valid"), (valid.Header.ReturnCode, valid.ResultDate, valid.Header.Message));
        var unknown = await _cal.CallAsync("WEEKNO", a => a.Date1 = 20261019);
        Assert.Equal(("DAT0010", "DATECALC does not support function WEEKNO"), (unknown.Header.ReasonCode, unknown.Header.Message));
        var cleared = await _cal.CallAsync("CACHECLR", _ => { });
        Assert.Equal((0, "Calendar cache cleared", 0, 0), (cleared.Header.ReturnCode, cleared.Header.Message, cleared.CacheHits, cleared.CacheLoads));
    }

    [Fact]
    public void Built_in_table_has_the_calbuilt_counts()
    {
        Assert.Equal(78, BuiltInCalendar.Entries.Count);
        Assert.Equal(24, BuiltInCalendar.Entries.Count(e => e.Code == "ENG"));
        Assert.Equal(24, BuiltInCalendar.Entries.Count(e => e.Code == "SCT"));
        Assert.Equal(30, BuiltInCalendar.Entries.Count(e => e.Code == "NIR"));
        Assert.Equal(8, BuiltInCalendar.Load("ENGLAND", 2027).Rows);
        Assert.Equal(0, BuiltInCalendar.Load("ENG", 2029).Rows);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void Calendar_cache_entries_expire_after_the_ttl_and_expired_keys_are_purged()
    {
        // contracts 6.9: TTL 5 minutes by default
        Assert.Equal(TimeSpan.FromMinutes(5), new SterpKernelOptions().CalendarCacheTtl);
        var clock = new ManualClock();
        var cache = new CalendarCache(clock, Options.Create(new SterpKernelOptions()));
        cache.Set("db|fork-1", BuiltInCalendar.Load("ENG", 2026));
        clock.Now += TimeSpan.FromMinutes(4);
        Assert.True(cache.TryGet("db|fork-1", "ENG", 2026, out _));
        Assert.False(cache.TryGet("db|fork-2", "ENG", 2026, out _));
        clock.Now += TimeSpan.FromMinutes(2);
        // the restored copy loads its own entry; the earlier copy's expired entry is dropped
        cache.Set("db|fork-2", BuiltInCalendar.Load("ENG", 2026));
        Assert.Equal(1, cache.Count);
        Assert.False(cache.TryGet("db|fork-1", "ENG", 2026, out _));
        Assert.True(cache.TryGet("db|fork-2", "ENG", 2026, out _));
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }
}
