using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Kernel.Services;

/// <summary>DATECALCCA body (DATECALCCA.cpy:28-52).</summary>
public sealed class DatecalcArea : Commarea
{
    public int Date1 { get; set; }
    public int Date2 { get; set; }
    public int ResultDate { get; set; }

    /// <summary>DC-NUMBER S9(9) COMP-5: days, months or business days (may be negative).</summary>
    public int Number { get; set; }

    public int ResultNum { get; set; }

    /// <summary>DC-CALENDAR X(10): ENG (default), SCT, NIR, BACS - never validated (an unknown code is weekends only, SME-1257).</summary>
    public string Calendar { get; set; } = "";

    /// <summary>DC-DIRECTION X(4): NEXT, PREV or NONE.</summary>
    public string Direction { get; set; } = "";

    public string TextIn { get; set; } = "";
    public string TextOut { get; set; } = "";

    /// <summary>DC-FORMAT-IN X(8): YYYYMMDD, DDMMYYYY, ISO or blank.</summary>
    public string FormatIn { get; set; } = "";

    /// <summary>DC-FORMAT-OUT X(8): YYYYMMDD, DDMMYYYY, ISO or blank.</summary>
    public string FormatOut { get; set; } = "";

    public string DayName { get; set; } = "";
    public string MonthName { get; set; } = "";
    public string IsBusiness { get; set; } = "";
    public string IsHoliday { get; set; } = "";
    public string IsWeekend { get; set; } = "";
    public string Adjusted { get; set; } = "";
    public string HolidayName { get; set; } = "";

    /// <summary>DC-CAL-SOURCE X(8): SQL or BUILTIN on exit; BUILTIN on entry forces the built-in table without a database read.</summary>
    public string CalSource { get; set; } = "";

    public int CalRows { get; set; }

    /// <summary>Always 0 in the target (SME-074).</summary>
    public int CacheHits { get; set; }

    /// <summary>Always 0 in the target (SME-074).</summary>
    public int CacheLoads { get; set; }
}

/// <summary>DATECALC - date validation, arithmetic, formatting and UK business days (FR-STERP-PLATFORM-019, COM-009, COM-026).</summary>
public interface IBusinessCalendar
{
    /// <summary>
    /// CALL "DATECALC" USING area: VALIDATE, ADDDAYS, ADDMONTH, EOMONTH, DOW, DAYSBTWN, FORMAT, PARSE, ISBUSDAY, ADDBUS, ADJUST,
    /// CACHECLR (other DAT0010). DATECALC never returns RC 12: a holiday load failure falls back to the built-in table.
    /// </summary>
    Task<LegacyResult> ExecuteAsync(DatecalcArea area, CancellationToken ct = default);

    /// <summary>Builds an INITIALIZEd area (as every legacy caller did), lets the caller fill it, and executes it.</summary>
    Task<DatecalcArea> CallAsync(string function, Action<DatecalcArea> fill, CancellationToken ct = default);

    Task<DatecalcArea> AddDaysAsync(int date, int days, CancellationToken ct = default);
    Task<DatecalcArea> AddMonthsAsync(int date, int months, CancellationToken ct = default);
    Task<DatecalcArea> EndOfMonthAsync(int date, int months = 0, CancellationToken ct = default);
    Task<DatecalcArea> DayOfWeekAsync(int date, CancellationToken ct = default);
    Task<DatecalcArea> DaysBetweenAsync(int date1, int date2, CancellationToken ct = default);
    Task<DatecalcArea> FormatAsync(int date, string format, CancellationToken ct = default);
    Task<DatecalcArea> ParseAsync(string text, string? format = null, CancellationToken ct = default);
    Task<DatecalcArea> IsBusinessDayAsync(int date, string? calendar = null, CancellationToken ct = default);
    Task<DatecalcArea> AddBusinessDaysAsync(int date, int days, string? calendar = null, CancellationToken ct = default);
    Task<DatecalcArea> AdjustAsync(int date, string? direction = null, string? calendar = null, CancellationToken ct = default);
}

/// <summary>
/// DATECALC.cob:124-958 with CALBUILT.cpy. Holidays are read on the caller's engine connection (SQLIO slot 4) per
/// calendar-year: core.CalendarDay (IsBusinessDay = 0 AND HolidayName IS NOT NULL) when CalendarDay.CalDate exists, else
/// core.Holiday; the built-in table when the database yields no rows or the load fails. Business day = not Saturday or
/// Sunday and not a holiday (core.Calendar WorksSaturday/WorksSunday ignored, SME-1256); BACS uses the ENG holidays.
/// </summary>
public sealed class BusinessCalendar(ISqlRunner sql, ICalendarCache cache) : IBusinessCalendar
{
    private static readonly string[] DayNames = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"];

    private static readonly string[] MonthNames =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    // per request (scoped): the table probe, the database identity, and the calendar-years used without the shared cache
    // (built-in fallbacks after a load failure, and loads when the database identity cannot be established)
    private string? _probe;
    private string? _databaseKey;
    private readonly Dictionary<(string Cal, int Year), CalendarYear> _requestYears = [];

    /// <summary>
    /// The database identity of the shared calendar cache: server, database, creation time and the current recovery fork.
    /// sys.databases.create_date survives RESTORE ... WITH REPLACE; recovery_fork_guid changes on every RESTORE WITH
    /// RECOVERY (the parity harness restores before each scenario). sys.database_recovery_status is visible to the
    /// connection's login only with VIEW ANY DATABASE (granted to public by default) or database ownership; without it
    /// the fork is NULL and the shared cache is not used.
    /// </summary>
    public const string DatabaseIdentityStatement =
        "SELECT ISNULL(@@SERVERNAME, ''), DB_NAME(), CONVERT(varchar(30), d.create_date, 126), CONVERT(varchar(36), r.recovery_fork_guid) " +
        "FROM sys.databases d LEFT JOIN sys.database_recovery_status r ON r.database_id = d.database_id WHERE d.database_id = DB_ID()";

    public async Task<DatecalcArea> CallAsync(string function, Action<DatecalcArea> fill, CancellationToken ct = default)
    {
        var area = new DatecalcArea();
        area.Header.Prepare(KernelCaller.Empty, function);
        fill(area);
        await ExecuteAsync(area, ct);
        return area;
    }

    public Task<DatecalcArea> AddDaysAsync(int date, int days, CancellationToken ct = default) =>
        CallAsync("ADDDAYS", a => { a.Date1 = date; a.Number = days; }, ct);

    public Task<DatecalcArea> AddMonthsAsync(int date, int months, CancellationToken ct = default) =>
        CallAsync("ADDMONTH", a => { a.Date1 = date; a.Number = months; }, ct);

    public Task<DatecalcArea> EndOfMonthAsync(int date, int months = 0, CancellationToken ct = default) =>
        CallAsync("EOMONTH", a => { a.Date1 = date; a.Number = months; }, ct);

    public Task<DatecalcArea> DayOfWeekAsync(int date, CancellationToken ct = default) =>
        CallAsync("DOW", a => a.Date1 = date, ct);

    public Task<DatecalcArea> DaysBetweenAsync(int date1, int date2, CancellationToken ct = default) =>
        CallAsync("DAYSBTWN", a => { a.Date1 = date1; a.Date2 = date2; }, ct);

    public Task<DatecalcArea> FormatAsync(int date, string format, CancellationToken ct = default) =>
        CallAsync("FORMAT", a => { a.Date1 = date; a.FormatOut = format; }, ct);

    public Task<DatecalcArea> ParseAsync(string text, string? format = null, CancellationToken ct = default) =>
        CallAsync("PARSE", a => { a.TextIn = text; a.FormatIn = format ?? ""; }, ct);

    public Task<DatecalcArea> IsBusinessDayAsync(int date, string? calendar = null, CancellationToken ct = default) =>
        CallAsync("ISBUSDAY", a => { a.Date1 = date; a.Calendar = calendar ?? ""; }, ct);

    public Task<DatecalcArea> AddBusinessDaysAsync(int date, int days, string? calendar = null, CancellationToken ct = default) =>
        CallAsync("ADDBUS", a => { a.Date1 = date; a.Number = days; a.Calendar = calendar ?? ""; }, ct);

    public Task<DatecalcArea> AdjustAsync(int date, string? direction = null, string? calendar = null, CancellationToken ct = default) =>
        CallAsync("ADJUST", a => { a.Date1 = date; a.Direction = direction ?? ""; a.Calendar = calendar ?? ""; }, ct);

    public async Task<LegacyResult> ExecuteAsync(DatecalcArea a, CancellationToken ct = default)
    {
        var run = new Run(this, a);
        await run.ExecuteAsync(ct);
        return a.Header.Result;
    }

    /// <summary>The working storage of one DATECALC call.</summary>
    private sealed class Run(BusinessCalendar owner, DatecalcArea a)
    {
        private readonly CommareaHeader _h = a.Header;
        private bool _bad;
        private bool _forceBuiltin;
        private string _cal = "";
        private int _workDate;
        private int _step;
        private int _guard;
        private CalendarYear? _slot;

        public async Task ExecuteAsync(CancellationToken ct)
        {
            Initialise();
            switch (_h.FunctionCode)
            {
                case "CACHECLR":
                    owner.ClearCache();
                    a.CacheHits = 0;
                    a.CacheLoads = 0;
                    _h.Message = "Calendar cache cleared";
                    break;
                case "VALIDATE": Validate(); break;
                case "ADDDAYS": AddDays(); break;
                case "ADDMONTH": AddMonths(); break;
                case "EOMONTH": EndOfMonth(); break;
                case "DOW": DayOfWeek(); break;
                case "DAYSBTWN": DaysBetween(); break;
                case "FORMAT": Format(); break;
                case "PARSE": Parse(); break;
                case "ISBUSDAY": await IsBusinessDayAsync(ct); break;
                case "ADDBUS": await AddBusinessDaysAsync(ct); break;
                case "ADJUST": await AdjustAsync(ct); break;
                default:
                    Reject("DAT0010", "DATECALC does not support function " + K.Trim(_h.Function));
                    break;
            }
        }

        /// <summary>0100-INITIALISE.</summary>
        private void Initialise()
        {
            _h.Reset("DATECALC", "COM-026");
            a.DayName = "";
            a.MonthName = "";
            a.HolidayName = "";
            a.TextOut = "";
            a.IsBusiness = "N";
            a.IsHoliday = "N";
            a.IsWeekend = "N";
            a.Adjusted = "N";
            a.ResultNum = 0;
            a.ResultDate = 0;
            a.CalRows = 0;
            _forceBuiltin = K.X(a.CalSource, 8) == "BUILTIN ";
            a.CalSource = "";
            a.CacheHits = 0;
            a.CacheLoads = 0;
            if (K.Blank(a.Calendar, 10)) a.Calendar = "ENG";
            _cal = CobolText.Upper(K.X(a.Calendar, 10));
            a.Calendar = _cal.TrimEnd(' ');
        }

        private void Validate()
        {
            CheckDate1();
            if (_bad) return;
            a.ResultDate = a.Date1;
            a.ResultNum = 0;
            _h.Message = "Date is valid";
        }

        private void AddDays()
        {
            CheckDate1();
            if (_bad) return;
            var value = Truncate9(CobolDate.IntegerOfDate(a.Date1) + (long)a.Number);
            if (value is < 1 or > CobolDate.MaxIntegerDate)
            {
                Reject("DAT0005", "Resulting date is outside the supported range");
                return;
            }
            a.ResultDate = CobolDate.DateOfIntegerYyyymmdd(value);
            a.Date2 = a.ResultDate;
            _h.Message = "Calendar days added";
        }

        /// <summary>1200-ADD-MONTHS: the target month's last day when the day does not exist (31 Jan + 1 month = 28/29 Feb).</summary>
        private void AddMonths()
        {
            CheckDate1();
            if (_bad) return;
            var yyyy = a.Date1 / 10000;
            var mm = a.Date1 / 100 % 100;
            var dd = a.Date1 % 100;
            var months = Truncate9(yyyy * 12L + mm - 1 + a.Number);
            var targetY = Truncate9(months / 12);
            var targetM = Truncate9(months - targetY * 12 + 1);
            if (targetY is < 1601 or > 9999)
            {
                Reject("DAT0005", "Resulting date is outside the supported range");
                return;
            }
            var lastDay = CobolDate.DaysInMonth((int)targetY, (int)targetM);
            if (dd > lastDay) dd = lastDay;
            a.ResultDate = (int)(targetY * 10000 + Math.Abs(targetM) % 100 * 100 + dd);
            a.Date2 = a.ResultDate;
            _h.Message = "Calendar months added, day clamped to month end";
        }

        private void EndOfMonth()
        {
            CheckDate1();
            if (_bad) return;
            int work;
            if (a.Number != 0)
            {
                AddMonths();
                if (_bad) return;
                work = a.ResultDate;
            }
            else
            {
                work = a.Date1;
            }
            a.ResultDate = CobolDate.EndOfMonth(work);
            a.Date2 = a.ResultDate;
            _h.Message = "Last calendar day of the month";
        }

        private void DayOfWeek()
        {
            CheckDate1();
            if (_bad) return;
            a.ResultNum = CobolDate.WeekdayOf(a.Date1);
            a.DayName = DayName(a.ResultNum);
            a.MonthName = MonthName(a.Date1 / 100 % 100);
            if (a.ResultNum is 6 or 7) a.IsWeekend = "Y";
            a.ResultDate = a.Date1;
            _h.Message = K.Msg(K.Trim(a.DayName));
        }

        private void DaysBetween()
        {
            CheckDate1();
            if (_bad) return;
            if (CobolDate.TestDate(a.Date2) != 0)
            {
                Reject("DAT0002", "Second date is not a valid calendar date");
                return;
            }
            a.ResultNum = CobolDate.IntegerOfDate(a.Date2) - CobolDate.IntegerOfDate(a.Date1);
            _h.Message = K.Msg(K.Ed(a.ResultNum, "-(9)9"), " day(s) between the dates");
        }

        /// <summary>1600-FORMAT (COM-024: dd/mm/yyyy for users, yyyy-mm-dd for interfaces, yyyymmdd for storage).</summary>
        private void Format()
        {
            CheckDate1();
            if (_bad) return;
            var d = a.Date1;
            a.ResultDate = d;
            a.MonthName = MonthName(d / 100 % 100);
            var dd = (d % 100).ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            var mm = (d / 100 % 100).ToString("D2", System.Globalization.CultureInfo.InvariantCulture);
            var yyyy = (d / 10000).ToString("D4", System.Globalization.CultureInfo.InvariantCulture);
            var format = K.X(a.FormatOut, 8);
            switch (format.TrimEnd(' '))
            {
                case "DDMMYYYY":
                    a.TextOut = dd + "/" + mm + "/" + yyyy;
                    break;
                case "ISO":
                    a.TextOut = yyyy + "-" + mm + "-" + dd;
                    break;
                case "YYYYMMDD":
                case "" when CobolText.IsSpaces(format):
                    a.TextOut = K.D8(d);
                    break;
                default:
                    Reject("DAT0003", "Unknown date format " + K.Trim(format));
                    return;
            }
            _h.Message = "Date formatted";
        }

        /// <summary>1700-PARSE: digits of the first 10 bytes; 8 digits by format, 6 digits always DDMMYY with pivot 50 (RC 04 DAT0007).</summary>
        private void Parse()
        {
            var text = K.X(a.TextIn, 10);
            var digits = new System.Text.StringBuilder(8);
            foreach (var c in text)
            {
                if (c is >= '0' and <= '9' && digits.Length < 8) digits.Append(c);
            }
            int work;
            if (digits.Length == 8)
            {
                var fin = K.X(a.FormatIn, 8);
                var s = digits.ToString();
                int y, m, d;
                if (fin == "DDMMYYYY")
                {
                    d = int.Parse(s[..2]);
                    m = int.Parse(s.Substring(2, 2));
                    y = int.Parse(s.Substring(4, 4));
                }
                else if (fin.TrimEnd(' ') is "YYYYMMDD" or "ISO" || CobolText.IsSpaces(fin))
                {
                    y = int.Parse(s[..4]);
                    m = int.Parse(s.Substring(4, 2));
                    d = int.Parse(s.Substring(6, 2));
                }
                else
                {
                    Reject("DAT0003", "Unknown date format " + K.Trim(fin));
                    return;
                }
                work = y * 10000 + m * 100 + d;
            }
            else if (digits.Length == 6)
            {
                var s = digits.ToString();
                var d = int.Parse(s[..2]);
                var m = int.Parse(s.Substring(2, 2));
                var y2 = int.Parse(s.Substring(4, 2));
                var y = y2 < 50 ? 2000 + y2 : 1900 + y2;
                work = y * 10000 + m * 100 + d;
                _h.ReturnCode = 4;
                _h.ReasonCode = "DAT0007";
            }
            else
            {
                Reject("DAT0004", string.Concat("Cannot read a date from [", K.Trim(text), "]"));
                return;
            }
            if (CobolDate.TestDate(work) != 0)
            {
                Reject("DAT0001", string.Concat("[", K.Trim(text), "] is not a valid calendar date"));
                return;
            }
            a.ResultDate = work;
            a.Date1 = work;
            _h.Message = "Date read";
        }

        private async Task IsBusinessDayAsync(CancellationToken ct)
        {
            CheckDate1();
            if (_bad) return;
            _workDate = a.Date1;
            await TestBusinessDayAsync(ct);
            a.ResultDate = a.Date1;
            if (a.IsBusiness == "Y")
            {
                a.ResultNum = 1;
                _h.Message = K.Msg(K.Trim(a.DayName), " is a business day on calendar ", K.Trim(a.Calendar));
            }
            else
            {
                a.ResultNum = 0;
                NotBusinessMessage();
            }
        }

        /// <summary>2100-ADD-BUSINESS-DAYS: the start is never counted; more than 3650 steps is DAT0006 even when the count completes on step 3651.</summary>
        private async Task AddBusinessDaysAsync(CancellationToken ct)
        {
            CheckDate1();
            if (_bad) return;
            long wanted = a.Number;
            if (wanted < 0)
            {
                _step = -1;
                wanted = -wanted;
            }
            else
            {
                _step = 1;
            }
            _workDate = a.Date1;
            var counted = 0;
            _guard = 0;
            while (!(counted >= wanted || _guard > 3650 || _bad))
            {
                _guard++;
                _workDate = CobolDate.DateOfIntegerYyyymmdd(Truncate9(CobolDate.IntegerOfDate(_workDate) + (long)_step));
                await TestBusinessDayAsync(ct);
                if (a.IsBusiness == "Y") counted++;
            }
            if (_bad) return;
            if (_guard > 3650)
            {
                Reject("DAT0006", "Business day search exceeded ten years");
                return;
            }
            if (wanted == 0)
            {
                // zero business days still lands on a business day
                _workDate = a.Date1;
                await TestBusinessDayAsync(ct);
                if (a.IsBusiness != "Y")
                {
                    a.Direction = "NEXT";
                    await MoveToBusinessAsync(ct);
                }
            }
            a.ResultDate = _workDate;
            a.Date2 = _workDate;
            _h.Message = K.Msg(K.Ed(a.Number, "-(9)9"), " business day(s) on calendar ", K.Trim(a.Calendar));
        }

        /// <summary>2200-ADJUST (AC-112): NEXT / PREV / NONE within 60 days.</summary>
        private async Task AdjustAsync(CancellationToken ct)
        {
            CheckDate1();
            if (_bad) return;
            if (K.Blank(a.Direction, 4)) a.Direction = "NEXT";
            a.Direction = CobolText.Upper(K.X(a.Direction, 4)).TrimEnd(' ');
            if (a.Direction is not ("NEXT" or "PREV" or "NONE"))
            {
                Reject("DAT0008", "Adjustment direction must be NEXT, PREV or NONE");
                return;
            }
            _workDate = a.Date1;
            await TestBusinessDayAsync(ct);
            if (_bad) return;
            if (a.IsBusiness == "Y")
            {
                a.ResultDate = a.Date1;
                a.ResultNum = 0;
                _h.Message = K.Msg("Already a business day on calendar ", K.Trim(a.Calendar));
                return;
            }
            if (a.Direction == "NONE")
            {
                a.ResultDate = a.Date1;
                a.ResultNum = 0;
                NotBusinessMessage();
                return;
            }
            await MoveToBusinessAsync(ct);
            if (_bad) return;
            a.ResultDate = _workDate;
            a.Date2 = _workDate;
            a.Adjusted = "Y";
            _h.ReturnCode = 4;
            _h.ReasonCode = "DAT0009";
            _h.Message = K.Msg("Moved ", K.Ed(a.ResultNum, "-(9)9"), " day(s) to the ", K.Trim(a.Direction),
                " business day on calendar ", K.Trim(a.Calendar));
        }

        /// <summary>8000-CHECK-DATE-1.</summary>
        private void CheckDate1()
        {
            if (CobolDate.TestDate(a.Date1) != 0) Reject("DAT0001", string.Concat("Date ", K.D8(a.Date1), " is not a valid calendar date"));
        }

        /// <summary>8100-TEST-BUSINESS-DAY on the working date and calendar.</summary>
        private async Task TestBusinessDayAsync(CancellationToken ct)
        {
            a.IsBusiness = "N";
            a.IsHoliday = "N";
            a.IsWeekend = "N";
            a.HolidayName = "";
            var year = _workDate / 10000 % 10000;
            _slot = await owner.FindCalendarAsync(_cal, year, _forceBuiltin, ct);
            a.CalSource = _slot.Source;
            a.CalRows = _slot.Rows;
            var dow = CobolDate.WeekdayOf(_workDate);
            a.DayName = DayName(dow);
            a.MonthName = MonthName(_workDate / 100 % 100);
            if (dow is 6 or 7) a.IsWeekend = "Y";
            var doy = BuiltInCalendar.DayOfYear(_workDate);
            if (_slot.IsHoliday(doy))
            {
                a.IsHoliday = "Y";
                a.HolidayName = K.Store(_slot.NameOf(doy), 28);
            }
            if (a.IsWeekend == "N" && a.IsHoliday == "N") a.IsBusiness = "Y";
        }

        /// <summary>8200-MOVE-TO-BUSINESS: at most 60 steps in DC-DIRECTION (PREV back, anything else forward).</summary>
        private async Task MoveToBusinessAsync(CancellationToken ct)
        {
            _step = K.X(a.Direction, 4) == "PREV" ? -1 : 1;
            _guard = 0;
            while (!(a.IsBusiness == "Y" || _guard > 60 || _bad))
            {
                _guard++;
                _workDate = CobolDate.DateOfIntegerYyyymmdd(Truncate9(CobolDate.IntegerOfDate(_workDate) + (long)_step));
                await TestBusinessDayAsync(ct);
            }
            if (_guard > 60) Reject("DAT0006", "No business day found within 60 days");
            else a.ResultNum = _guard;
        }

        /// <summary>8300-NOT-BUSINESS-MESSAGE.</summary>
        private void NotBusinessMessage()
        {
            _h.Message = a.IsHoliday == "Y"
                ? K.Msg(K.Trim(a.DayName), " is ", K.Trim(a.HolidayName), " on calendar ", K.Trim(a.Calendar))
                : K.Msg(K.Trim(a.DayName), " is not a business day on calendar ", K.Trim(a.Calendar));
        }

        private void Reject(string reason, string message)
        {
            _bad = true;
            _h.Reject(reason, message);
        }

        private static long Truncate9(long value) => value % 1_000_000_000L;

        private static string DayName(long dow) => dow is >= 1 and <= 7 ? DayNames[dow - 1] : "";

        private static string MonthName(long month) => month is >= 1 and <= 12 ? MonthNames[month - 1] : "";
    }

    private void ClearCache()
    {
        cache.Clear();
        _requestYears.Clear();
        _probe = null;
    }

    /// <summary>7000-FIND-CALENDAR / 7200-LOAD-CALENDAR.</summary>
    private async Task<CalendarYear> FindCalendarAsync(string calendar, int year, bool forceBuiltin, CancellationToken ct)
    {
        // Bacs does not process on an England and Wales bank holiday, so BACS is loaded from the ENG holiday set
        var loadCal = calendar.TrimEnd(' ') == "BACS" ? "ENG" : calendar.TrimEnd(' ');
        if (forceBuiltin) return BuiltInCalendar.Load(loadCal, year);
        if (_requestYears.TryGetValue((loadCal, year), out var known)) return known;

        string dbKey;
        try
        {
            dbKey = await DatabaseKeyAsync(ct);
        }
        catch (SqlioException)
        {
            return ForThisRequest(loadCal, year, BuiltInCalendar.Load(loadCal, year));
        }
        if (dbKey.Length > 0 && cache.TryGet(dbKey, loadCal, year, out var cached)) return cached;

        CalendarYear entry;
        try
        {
            _probe ??= await ProbeAsync(ct);
            entry = _probe is "CD" or "HD" ? await LoadFromSqlAsync(loadCal, year, _probe, ct) : new CalendarYear(loadCal, year, "");
        }
        catch (SqlioException)
        {
            // a calendar read failure must not stop a date calculation; it is not cached (BC-PLATFORM-06)
            return ForThisRequest(loadCal, year, BuiltInCalendar.Load(loadCal, year));
        }
        if (entry.Rows == 0) entry = BuiltInCalendar.Load(loadCal, year);
        if (dbKey.Length == 0) return ForThisRequest(loadCal, year, entry);
        cache.Set(dbKey, entry);
        return entry;
    }

    private CalendarYear ForThisRequest(string loadCal, int year, CalendarYear entry)
    {
        _requestYears[(loadCal, year)] = entry;
        return entry;
    }

    /// <summary>The <see cref="DatabaseIdentityStatement"/> key, or "" when the recovery fork is not visible (no shared cache).</summary>
    private async Task<string> DatabaseKeyAsync(CancellationToken ct)
    {
        if (_databaseKey is not null) return _databaseKey;
        var row = await sql.SingleAsync(DatabaseIdentityStatement, [], [128, 128, 30, 36], ct);
        _databaseKey = row.Found && row.Text(3).Length > 0
            ? string.Join('|', row.Text(0), row.Text(1), row.Text(2), row.Text(3))
            : "";
        return _databaseKey;
    }

    /// <summary>7300-PROBE-TABLES: CD when core.CalendarDay.CalDate exists, else HD when core.Holiday.HolidayDate exists, else NO.</summary>
    private async Task<string> ProbeAsync(CancellationToken ct)
    {
        var row = await sql.SingleAsync(
            "SELECT CASE WHEN COL_LENGTH('core.CalendarDay','CalDate') IS NOT NULL THEN 'CD' " +
            "WHEN COL_LENGTH('core.Holiday','HolidayDate') IS NOT NULL THEN 'HD' ELSE 'NO' END", [], [2], ct);
        return row.Found ? row.Text(0) : "NO";
    }

    /// <summary>7400-LOAD-FROM-SQL.</summary>
    private async Task<CalendarYear> LoadFromSqlAsync(string loadCal, int year, string table, CancellationToken ct)
    {
        var statement = table == "CD"
            ? "SELECT CONVERT(char(8), CalDate, 112), HolidayName FROM core.CalendarDay WHERE CalendarCode = ? AND YEAR(CalDate) = ? " +
              "AND IsBusinessDay = 0 AND HolidayName IS NOT NULL ORDER BY CalDate"
            : "SELECT CONVERT(char(8), HolidayDate, 112), ISNULL(Name, 'Bank holiday') FROM core.Holiday WHERE CalendarCode = ? " +
              "AND YEAR(HolidayDate) = ? ORDER BY HolidayDate";
        var rows = await sql.FetchAllAsync(statement, [SqlioParm.Text(loadCal), SqlioParm.Integer(year)], [8, 28], ct);
        var entry = new CalendarYear(loadCal, year, "");
        foreach (var r in rows)
        {
            var date = (int)CobolNumeric.NumVal(r.Raw(0));
            if (!r.Raw(0).All(char.IsAsciiDigit)) date = 0;
            if (CobolDate.TestDate(date) != 0) continue;
            entry.Mark(BuiltInCalendar.DayOfYear(date), r.Text(1));
        }
        if (entry.Rows > 0) entry.SetSource("SQL");
        return entry;
    }
}
