using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Sterp.Platform.Kernel.Services;

/// <summary>One calendar-year of holidays as DATECALC caches it (WS-SLOT): source, rows, holiday flag per day of year, first 20 names.</summary>
public sealed class CalendarYear
{
    private readonly bool[] _holiday = new bool[367];
    private readonly List<(int Doy, string Name)> _names = [];

    public CalendarYear(string calendar, int year, string source)
    {
        Calendar = calendar;
        Year = year;
        Source = source;
    }

    /// <summary>The calendar code whose holidays were loaded (BACS is loaded from ENG).</summary>
    public string Calendar { get; }

    public int Year { get; }

    /// <summary>SQL or BUILTIN (DC-CAL-SOURCE).</summary>
    public string Source { get; private set; }

    /// <summary>Holiday rows loaded (DC-CAL-ROWS).</summary>
    public int Rows { get; private set; }

    public IReadOnlyList<(int Doy, string Name)> Names => _names;

    public bool IsHoliday(int dayOfYear) => dayOfYear is >= 1 and <= 366 && _holiday[dayOfYear];

    /// <summary>8160-HOLIDAY-NAME: the first remembered name for the day, or spaces.</summary>
    public string NameOf(int dayOfYear)
    {
        foreach (var (doy, name) in _names)
        {
            if (doy == dayOfYear) return name;
        }
        return "";
    }

    internal void Mark(int dayOfYear, string name)
    {
        if (dayOfYear is >= 1 and <= 366) _holiday[dayOfYear] = true;
        Rows++;
        // 7600-REMEMBER-NAME: at most 20 names per slot; later holidays are still marked but unnamed
        if (_names.Count < 20) _names.Add((dayOfYear, name));
    }

    internal void SetSource(string source) => Source = source;
}

/// <summary>Kernel options (Sterp:Kernel).</summary>
public sealed class SterpKernelOptions
{
    public const string Section = "Sterp:Kernel";

    /// <summary>
    /// Text the legacy ODBC shim prefixed to a server message (FR-STERP-PLATFORM-010). When set it overrides
    /// Sterp:Database:LegacyDriverMessagePrefix; an empty string is the characterization configuration of FR-010 AC5.
    /// </summary>
    public string? LegacyDriverMessagePrefix { get; set; }

    /// <summary>DATECALC holiday cache lifetime (contracts 6.9: TTL 5 minutes).</summary>
    public TimeSpan CalendarCacheTtl { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// The DATECALC calendar cache (contracts 6.9, FR-STERP-PLATFORM-019): the legacy per-process WS-CACHE becomes one shared
/// cache of calendar-years with a time-to-live (default 5 minutes). Entries are keyed by the database identity
/// (<see cref="BusinessCalendar.DatabaseIdentityStatement"/>: server, database, create_date and the recovery fork, which
/// changes on every RESTORE), so a database restored underneath a running host (parity runs restore before every scenario)
/// never answers from the previous copy's calendar. CACHECLR and target calendar maintenance (core.Calendar,
/// core.CalendarDay, core.Holiday) call <see cref="Clear"/>. Load failures are never cached (the built-in fallback applies
/// to that request only, BC-PLATFORM-06).
/// </summary>
public interface ICalendarCache
{
    bool TryGet(string databaseKey, string calendar, int year, out CalendarYear entry);

    void Set(string databaseKey, CalendarYear entry);

    /// <summary>CACHECLR / calendar maintenance: empties the cache for every database.</summary>
    void Clear();
}

public sealed class CalendarCache(TimeProvider clock, IOptions<SterpKernelOptions> options) : ICalendarCache
{
    private readonly ConcurrentDictionary<(string Db, string Cal, int Year), (CalendarYear Entry, DateTimeOffset Expires)> _entries = new();

    public bool TryGet(string databaseKey, string calendar, int year, out CalendarYear entry)
    {
        entry = null!;
        if (!_entries.TryGetValue((databaseKey, calendar, year), out var e)) return false;
        if (e.Expires <= clock.GetUtcNow())
        {
            _entries.TryRemove((databaseKey, calendar, year), out _);
            return false;
        }
        entry = e.Entry;
        return true;
    }

    public void Set(string databaseKey, CalendarYear entry)
    {
        var now = clock.GetUtcNow();
        // entries of an earlier database copy are never read again: drop whatever has expired
        foreach (var e in _entries)
        {
            if (e.Value.Expires <= now) _entries.TryRemove(e);
        }
        _entries[(databaseKey, entry.Calendar, entry.Year)] = (entry, now + options.Value.CalendarCacheTtl);
    }

    public void Clear() => _entries.Clear();

    public int Count => _entries.Count;
}

/// <summary>CALBUILT.cpy - the built-in UK bank holiday table 2026-2028 (ENG 24, SCT 24, NIR 30 entries).</summary>
public static class BuiltInCalendar
{
    /// <summary>(code X(3), date yyyymmdd, name X(28)) in table order.</summary>
    public static IReadOnlyList<(string Code, int Date, string Name)> Entries { get; } =
    [
        ("ENG", 20260101, "New Year's Day"), ("ENG", 20260403, "Good Friday"), ("ENG", 20260406, "Easter Monday"),
        ("ENG", 20260504, "Early May"), ("ENG", 20260525, "Spring"), ("ENG", 20260831, "Summer"),
        ("ENG", 20261225, "Christmas Day"), ("ENG", 20261228, "Boxing Day substitute"),
        ("ENG", 20270101, "New Year's Day"), ("ENG", 20270326, "Good Friday"), ("ENG", 20270329, "Easter Monday"),
        ("ENG", 20270503, "Early May"), ("ENG", 20270531, "Spring"), ("ENG", 20270830, "Summer"),
        ("ENG", 20271227, "Christmas substitute"), ("ENG", 20271228, "Boxing Day substitute"),
        ("ENG", 20280103, "New Year substitute"), ("ENG", 20280414, "Good Friday"), ("ENG", 20280417, "Easter Monday"),
        ("ENG", 20280501, "Early May"), ("ENG", 20280529, "Spring"), ("ENG", 20280828, "Summer"),
        ("ENG", 20281225, "Christmas Day"), ("ENG", 20281226, "Boxing Day"),
        ("SCT", 20260101, "New Year's Day"), ("SCT", 20260102, "2 January"), ("SCT", 20260403, "Good Friday"),
        ("SCT", 20260504, "Early May"), ("SCT", 20260803, "Summer"), ("SCT", 20261130, "St Andrew's Day"),
        ("SCT", 20261225, "Christmas Day"), ("SCT", 20261228, "Boxing Day substitute"),
        ("SCT", 20270101, "New Year's Day"), ("SCT", 20270104, "2 January substitute"), ("SCT", 20270326, "Good Friday"),
        ("SCT", 20270503, "Early May"), ("SCT", 20270802, "Summer"), ("SCT", 20271130, "St Andrew's Day"),
        ("SCT", 20271227, "Christmas substitute"), ("SCT", 20271228, "Boxing Day substitute"),
        ("SCT", 20280103, "New Year substitute"), ("SCT", 20280104, "2 January substitute"), ("SCT", 20280414, "Good Friday"),
        ("SCT", 20280501, "Early May"), ("SCT", 20280807, "Summer"), ("SCT", 20281130, "St Andrew's Day"),
        ("SCT", 20281225, "Christmas Day"), ("SCT", 20281226, "Boxing Day"),
        ("NIR", 20260101, "New Year's Day"), ("NIR", 20260317, "St Patrick's Day"), ("NIR", 20260403, "Good Friday"),
        ("NIR", 20260406, "Easter Monday"), ("NIR", 20260504, "Early May"), ("NIR", 20260525, "Spring"),
        ("NIR", 20260713, "Boyne substitute"), ("NIR", 20260831, "Summer"), ("NIR", 20261225, "Christmas Day"),
        ("NIR", 20261228, "Boxing Day substitute"),
        ("NIR", 20270101, "New Year's Day"), ("NIR", 20270317, "St Patrick's Day"), ("NIR", 20270326, "Good Friday"),
        ("NIR", 20270329, "Easter Monday"), ("NIR", 20270503, "Early May"), ("NIR", 20270531, "Spring"),
        ("NIR", 20270712, "Battle of the Boyne"), ("NIR", 20270830, "Summer"), ("NIR", 20271227, "Christmas substitute"),
        ("NIR", 20271228, "Boxing Day substitute"),
        ("NIR", 20280103, "New Year substitute"), ("NIR", 20280317, "St Patrick's Day"), ("NIR", 20280414, "Good Friday"),
        ("NIR", 20280417, "Easter Monday"), ("NIR", 20280501, "Early May"), ("NIR", 20280529, "Spring"),
        ("NIR", 20280712, "Battle of the Boyne"), ("NIR", 20280828, "Summer"), ("NIR", 20281225, "Christmas Day"),
        ("NIR", 20281226, "Boxing Day"),
    ];

    /// <summary>7500-LOAD-BUILT-IN: entries whose code equals the first 3 characters of the calendar, for the year.</summary>
    public static CalendarYear Load(string loadCalendar, int year)
    {
        var entry = new CalendarYear(loadCalendar, year, "BUILTIN");
        var code = K.X(loadCalendar, 10)[..3];
        foreach (var (c, date, name) in Entries)
        {
            if (c != code || date / 10000 != year) continue;
            entry.Mark(DayOfYear(date), name);
        }
        return entry;
    }

    /// <summary>8150-DAY-OF-YEAR.</summary>
    public static int DayOfYear(int yyyymmdd) =>
        Cobol.CobolDate.IntegerOfDate(yyyymmdd) - Cobol.CobolDate.IntegerOfDate(yyyymmdd / 10000 * 10000 + 101) + 1;
}
