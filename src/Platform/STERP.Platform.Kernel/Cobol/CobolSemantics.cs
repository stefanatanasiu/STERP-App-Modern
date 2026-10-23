using System.Globalization;
using System.Numerics;
using System.Text;

namespace Sterp.Platform.Kernel.Cobol;

/// <summary>
/// COBOL alphanumeric (PIC X(n)) semantics as the legacy marshalling applied them
/// (dotnet/STERP.CobolInterop/CobolCodec.cs:15,37-43; contracts 4.4): Windows-1252 encoding with '?' for unrepresentable
/// characters, right truncation to the field length, space padding.
/// </summary>
public static class CobolText
{
    static CobolText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Ansi = Encoding.GetEncoding(1252, new EncoderReplacementFallback("?"), new DecoderReplacementFallback("?"));
    }

    /// <summary>Windows-1252 with '?' replacement (legacy CobolCodec.Ansi).</summary>
    public static Encoding Ansi { get; }

    /// <summary>The value as the COBOL field holds it: exactly <paramref name="length"/> characters, space padded.</summary>
    public static string Fit(string? value, int length)
    {
        if (length <= 0) return "";
        if (string.IsNullOrEmpty(value)) return new string(' ', length);
        var roundTrip = Ansi.GetString(Ansi.GetBytes(value));
        return roundTrip.Length >= length ? roundTrip[..length] : roundTrip.PadRight(length);
    }

    /// <summary>The field value as the API emits it (contracts 4.3: right-trimmed, leading spaces preserved).</summary>
    public static string Trimmed(string? value, int length) => Fit(value, length).TrimEnd();

    /// <summary>COBOL comparison of two alphanumerics: the shorter operand is space padded (trailing spaces insignificant).</summary>
    public static bool EqualsPadded(string? a, string? b) =>
        string.Equals((a ?? "").TrimEnd(' '), (b ?? "").TrimEnd(' '), StringComparison.Ordinal);

    /// <summary>True when the field is SPACES (or empty/null).</summary>
    public static bool IsSpaces(string? value) => string.IsNullOrEmpty(value) || value.All(c => c == ' ');

    /// <summary>True when the field is LOW-VALUES (every byte X'00'); an empty value is SPACES, not LOW-VALUES.</summary>
    public static bool IsLowValues(string? value) => !string.IsNullOrEmpty(value) && value.All(c => c == '\0');

    /// <summary>The common legacy test "= SPACES OR = LOW-VALUES" on a PIC X(n) field.</summary>
    public static bool IsBlank(string? value, int length)
    {
        var f = Fit(value, length);
        return f.All(c => c == ' ') || f.All(c => c == '\0');
    }

    /// <summary>
    /// FUNCTION UPPER-CASE on single-byte text. GnuCOBOL runs with LC_CTYPE "C", so only a-z are folded
    /// (accented Windows-1252 letters are left as they are).
    /// </summary>
    public static string Upper(string? value)
    {
        var s = value ?? "";
        if (!s.Any(c => c is >= 'a' and <= 'z')) return s;
        return string.Create(s.Length, s, (span, src) =>
        {
            for (var i = 0; i < src.Length; i++) span[i] = src[i] is >= 'a' and <= 'z' ? (char)(src[i] - 32) : src[i];
        });
    }

    /// <summary>FUNCTION TRIM (both sides, spaces only).</summary>
    public static string Trim(string? value) => (value ?? "").Trim(' ');

    /// <summary>Text as stored by ODBC fetch into a fixed slot: NULL -> spaces, truncated/padded to the width (sterpsql.c STSQLFET).</summary>
    public static string Slot(object? value, int width)
    {
        if (value is null || value is DBNull) return new string(' ', width);
        return Fit(Convert.ToString(value, CultureInfo.InvariantCulture), width);
    }
}

/// <summary>
/// COBOL numeric semantics (contracts 7.1): decimal only; ROUNDED = half away from zero; no ROUNDED = truncation toward
/// zero at every receiving field; storing into PIC 9(n)V9(s) keeps the low-order digits (high-order truncation).
/// </summary>
public static class CobolNumeric
{
    private static readonly decimal[] Powers = Enumerable.Range(0, 29).Select(i => Pow10(i)).ToArray();

    private static decimal Pow10(int n)
    {
        var d = 1m;
        for (var i = 0; i < n; i++) d *= 10m;
        return d;
    }

    /// <summary>COMPUTE ... ROUNDED into a field of the given scale (MidpointRounding.AwayFromZero).</summary>
    public static decimal Rounded(decimal value, int scale) => Math.Round(value, scale, MidpointRounding.AwayFromZero);

    /// <summary>MOVE/COMPUTE without ROUNDED: truncation toward zero at the receiving scale.</summary>
    public static decimal Truncated(decimal value, int scale)
    {
        var p = Powers[scale];
        return decimal.Truncate(value * p) / p;
    }

    /// <summary>
    /// Store a value into PIC [S]9(intDigits)V9(scale): round or truncate to the scale, keep the low-order integer digits,
    /// drop the sign of an unsigned field. Returns the value with the field's scale (so it serializes with that scale).
    /// </summary>
    public static decimal Store(decimal value, int intDigits, int scale, bool signed = true, bool rounded = false)
    {
        var v = rounded ? Rounded(value, scale) : Truncated(value, scale);
        if (!signed) v = Math.Abs(v);
        var limit = Powers[intDigits];
        var whole = decimal.Truncate(v);
        var frac = v - whole;
        if (Math.Abs(whole) >= limit) whole %= limit;
        return WithScale(whole + frac, scale);
    }

    /// <summary>
    /// Legacy input normalization of a numeric field (CobolCodec.PutZoned/PutPacked, CobolCodec.cs:24-29,71-86): round half
    /// away from zero to the scale, keep the low-order <paramref name="totalDigits"/> digits, absolute value when unsigned.
    /// </summary>
    public static decimal Normalize(decimal value, int totalDigits, int scale, bool signed)
    {
        var neg = value < 0;
        var unscaled = Math.Abs(Math.Round(value * Powers[scale], 0, MidpointRounding.AwayFromZero));
        var limit = Powers[totalDigits];
        if (unscaled >= limit) unscaled %= limit;
        var result = unscaled / Powers[scale];
        if (neg && signed) result = -result;
        return WithScale(result, scale);
    }

    /// <summary>Returns the same value carrying exactly <paramref name="scale"/> decimal places (for serialization).</summary>
    public static decimal WithScale(decimal value, int scale)
    {
        var r = Math.Round(value, scale, MidpointRounding.AwayFromZero);
        // add 0.000.. with the wanted scale so trailing zeros are kept by System.Text.Json
        return scale == 0 ? decimal.Truncate(r) + 0m : r + new decimal(0, 0, 0, false, (byte)scale);
    }

    /// <summary>True when a value does not fit PIC 9(intDigits) (ON SIZE ERROR condition).</summary>
    public static bool SizeError(decimal value, int intDigits) => Math.Abs(decimal.Truncate(value)) >= Powers[intDigits];

    /// <summary>FUNCTION SIGN.</summary>
    public static int Sign(decimal value) => Math.Sign(value);

    /// <summary>
    /// STFUNCS FUNCTION ROUND-AWAY(value, places) (STFUNCS.cob:41-80, FR-STERP-PLATFORM-024): places clamped to 0..8, the
    /// value held as S9(18)V9(12) (decimals beyond 12 truncated on entry), scaled, 0.5 added (subtracted for a negative
    /// value), truncated to an integer and unscaled - exact halves away from zero.
    /// </summary>
    public static decimal RoundAway(decimal value, int places)
    {
        var p = Math.Clamp(places, 0, 8);
        var v = Store(value, 18, 12);
        var scaled = v * Powers[p];
        scaled = decimal.Truncate(v < 0 ? scaled - 0.5m : scaled + 0.5m);
        return scaled / Powers[p];
    }

    /// <summary>STFUNCS FUNCTION SIGN-OF: -1, +1 or 0.</summary>
    public static int SignOf(decimal value) => value < 0 ? -1 : value > 0 ? 1 : 0;

    /// <summary>
    /// FUNCTION NUMVAL exactly as the deployed GnuCOBOL 3.2 runtime evaluates it (characterized on GC32-BDB-SP1 in every
    /// legacy dialect, SME-039): the argument is not validated. Scanning left to right, digits are collected, the first '.'
    /// starts the fraction (later '.' are skipped), any '-' makes the value negative, a CR or DB pair (any case) makes it
    /// negative and ends the scan, and every other character (blank, '+', ',', currency sign, letter) is skipped.
    /// "1         0" is 10, "12.3.4" is 12.34, "2026-10-20" is -20261020, "cr12" is 0, text without digits is 0.
    /// </summary>
    public static decimal NumVal(string? text)
    {
        var s = text ?? "";
        var negative = false;
        var point = false;
        var digits = new StringBuilder();
        var scale = 0;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i + 1 < s.Length && ((c is 'C' or 'c') && (s[i + 1] is 'R' or 'r') || (c is 'D' or 'd') && (s[i + 1] is 'B' or 'b')))
            {
                negative = true;
                break;
            }
            if (c == '-')
            {
                negative = true;
            }
            else if (c == '.')
            {
                point = true;
            }
            else if (char.IsAsciiDigit(c))
            {
                digits.Append(c);
                if (point) scale++;
            }
        }
        var mantissaText = digits.ToString().TrimStart('0');
        // System.Decimal keeps at most 28 significant digits; excess low-order fraction digits are truncated, then excess
        // high-order integer digits (no receiving field holds more than 18)
        if (mantissaText.Length > 28)
        {
            var drop = Math.Min(mantissaText.Length - 28, scale);
            mantissaText = mantissaText[..^drop];
            scale -= drop;
        }
        if (mantissaText.Length > 28) mantissaText = mantissaText[^28..];
        if (mantissaText.Length == 0) return 0m;
        var mantissa = decimal.Parse(mantissaText, NumberStyles.None, CultureInfo.InvariantCulture);
        var result = scale == 0 ? mantissa : mantissa / Powers[scale];
        return negative ? -result : result;
    }

    /// <summary>
    /// COMPUTE r = a * b into PIC [S]9(intDigits)V9(scale): the exact product (GnuCOBOL arbitrary-precision intermediate),
    /// truncated (or ROUNDED half away from zero) at the receiving scale, high-order digits beyond the field dropped.
    /// </summary>
    public static decimal Multiply(decimal a, decimal b, int intDigits, int scale, bool signed = true, bool rounded = false)
    {
        var (ma, sa) = Decompose(a);
        var (mb, sb) = Decompose(b);
        return StoreExact(ma * mb, sa + sb, intDigits, scale, signed, rounded);
    }

    /// <summary>
    /// COMPUTE r = a / b into PIC [S]9(intDigits)V9(scale) with the quotient carried far beyond the receiving scale
    /// (GnuCOBOL divides with 38 extra digits), then truncated or ROUNDED. A zero divisor throws DivideByZeroException.
    /// </summary>
    public static decimal Divide(decimal a, decimal b, int intDigits, int scale, bool signed = true, bool rounded = false)
    {
        var (ma, sa) = Decompose(a);
        var (mb, sb) = Decompose(b);
        if (mb.IsZero) throw new DivideByZeroException();
        // a/b = ma*10^sb / (mb*10^sa); keep scale+1 digits for the rounding decision
        var num = ma * BigInteger.Pow(10, sb + scale + 1);
        var den = mb * BigInteger.Pow(10, sa);
        var q = BigInteger.Divide(num, den);
        return StoreExact(q, scale + 1, intDigits, scale, signed, rounded);
    }

    /// <summary>
    /// COMPUTE r = a * b / c into PIC [S]9(intDigits)V9(scale): exact product, quotient carried beyond the receiving scale,
    /// then truncated or ROUNDED (the left-to-right evaluation GnuCOBOL performs with arbitrary-precision intermediates).
    /// </summary>
    public static decimal MultiplyDivide(decimal a, decimal b, decimal c, int intDigits, int scale, bool signed = true, bool rounded = false)
    {
        var (ma, sa) = Decompose(a);
        var (mb, sb) = Decompose(b);
        var (mc, sc) = Decompose(c);
        if (mc.IsZero) throw new DivideByZeroException();
        // (ma*mb / 10^(sa+sb)) / (mc / 10^sc) = ma*mb*10^sc / (mc*10^(sa+sb))
        var num = ma * mb * BigInteger.Pow(10, sc + scale + 1);
        var den = mc * BigInteger.Pow(10, sa + sb);
        return StoreExact(BigInteger.Divide(num, den), scale + 1, intDigits, scale, signed, rounded);
    }

    private static (BigInteger Mantissa, int Scale) Decompose(decimal d)
    {
        var bits = decimal.GetBits(d);
        var mag = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
        var scale = (bits[3] >> 16) & 0xFF;
        return (bits[3] < 0 ? -mag : mag, scale);
    }

    private static decimal StoreExact(BigInteger mantissa, int mantissaScale, int intDigits, int scale, bool signed, bool rounded)
    {
        BigInteger v;
        if (mantissaScale <= scale)
        {
            v = mantissa * BigInteger.Pow(10, scale - mantissaScale);
        }
        else
        {
            var shift = BigInteger.Pow(10, mantissaScale - scale);
            v = BigInteger.DivRem(mantissa, shift, out var rem);
            if (rounded && BigInteger.Abs(rem) * 2 >= shift) v += mantissa.Sign;
        }
        if (!signed) v = BigInteger.Abs(v);
        var limit = BigInteger.Pow(10, intDigits + scale);
        if (BigInteger.Abs(v) >= limit) v %= limit;
        var negative = v.Sign < 0;
        var mag = BigInteger.Abs(v);
        if (mag.GetBitLength() > 96) throw new OverflowException("Value does not fit System.Decimal.");
        var bytes = mag.ToByteArray(isUnsigned: true, isBigEndian: false);
        Span<byte> buf = stackalloc byte[12];
        bytes.AsSpan(0, Math.Min(bytes.Length, 12)).CopyTo(buf);
        var lo = BitConverter.ToInt32(buf[..4]);
        var mid = BitConverter.ToInt32(buf[4..8]);
        var hi = BitConverter.ToInt32(buf[8..12]);
        return new decimal(lo, mid, hi, negative, (byte)scale);
    }

    /// <summary>
    /// Edited numeric text as SQLIO-NUM-EDIT -(12)9.9(6) then trimmed (SQLIO parameter binding): six decimals truncated,
    /// at most 12 integer digits (high-order digits beyond the edit picture are lost).
    /// </summary>
    public static string SqlioNumber(decimal value)
    {
        var t = Truncated(value, 6);
        var a = Math.Abs(t);
        var whole = decimal.Truncate(a) % Powers[12];
        var r = whole + (a - decimal.Truncate(a));
        var s = r.ToString("0.000000", CultureInfo.InvariantCulture);
        return t < 0 && r != 0 ? "-" + s : s;
    }

    /// <summary>Edited integer text as SQLIO-INT-EDIT -(17)9 then trimmed (at most 17 integer digits).</summary>
    public static string SqlioInteger(long value)
    {
        var a = value == long.MinValue ? 0 : Math.Abs(value) % 100_000_000_000_000_000L;
        return value < 0 && a != 0 ? "-" + a.ToString(CultureInfo.InvariantCulture) : a.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>COBOL date handling: PIC 9(8) yyyymmdd, INTEGER-OF-DATE / DATE-OF-INTEGER (day 1 = 1601-01-01).</summary>
public static class CobolDate
{
    private static readonly DateOnly Epoch = new(1600, 12, 31);

    /// <summary>yyyymmdd as integer, 0 for null.</summary>
    public static int ToYyyymmdd(DateOnly? date) => date is { } d ? d.Year * 10000 + d.Month * 100 + d.Day : 0;

    public static int ToYyyymmdd(DateTime date) => date.Year * 10000 + date.Month * 100 + date.Day;

    /// <summary>yyyymmdd text, spaces/zero -> null (contracts 4.3).</summary>
    public static DateOnly? FromYyyymmdd(int value)
    {
        if (value <= 0) return null;
        var y = value / 10000;
        var m = value / 100 % 100;
        var d = value % 100;
        return IsValid(y, m, d) ? new DateOnly(y, m, d) : null;
    }

    public static DateOnly? FromYyyymmdd(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        if (t.Length != 8 || !int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var v)) return null;
        return FromYyyymmdd(v);
    }

    public static string Format(DateOnly? date) => date is { } d ? d.ToString("yyyyMMdd", CultureInfo.InvariantCulture) : "00000000";

    public static bool IsValid(int y, int m, int d) =>
        y is >= 1601 and <= 9999 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m);

    /// <summary>FUNCTION INTEGER-OF-DATE: days since 1600-12-31 (1601-01-01 = 1).</summary>
    public static int IntegerOfDate(DateOnly date) => date.DayNumber - Epoch.DayNumber;

    /// <summary>FUNCTION DATE-OF-INTEGER.</summary>
    public static DateOnly DateOfInteger(int days) => DateOnly.FromDayNumber(Epoch.DayNumber + days);

    /// <summary>Last valid integer date (9999-12-31).</summary>
    public const int MaxIntegerDate = 3067671;

    /// <summary>FUNCTION TEST-DATE-YYYYMMDD: 0 valid, 1 year outside 1601..9999, 2 month invalid, 3 day invalid.</summary>
    public static int TestDate(int yyyymmdd)
    {
        if (yyyymmdd < 0) return 1;
        var y = yyyymmdd / 10000;
        var m = yyyymmdd / 100 % 100;
        var d = yyyymmdd % 100;
        if (y is < 1601 or > 9999) return 1;
        if (m is < 1 or > 12) return 2;
        if (d < 1 || d > DateTime.DaysInMonth(y, m)) return 3;
        return 0;
    }

    /// <summary>FUNCTION INTEGER-OF-DATE on a yyyymmdd number; 0 for an invalid date (EC-ARGUMENT-FUNCTION).</summary>
    public static int IntegerOfDate(int yyyymmdd) =>
        TestDate(yyyymmdd) == 0 ? IntegerOfDate(new DateOnly(yyyymmdd / 10000, yyyymmdd / 100 % 100, yyyymmdd % 100)) : 0;

    /// <summary>FUNCTION DATE-OF-INTEGER as a yyyymmdd number; 0 outside 1..3067671.</summary>
    public static int DateOfIntegerYyyymmdd(long days) =>
        days is < 1 or > MaxIntegerDate ? 0 : ToYyyymmdd(DateOfInteger((int)days));

    /// <summary>STFUNCS IS-LEAP (as a flag): Gregorian leap year, false outside 1601..9999 (BR-IS-LEAP-001).</summary>
    public static bool IsLeap(int year) => year is >= 1601 and <= 9999 && DateTime.IsLeapYear(year);

    /// <summary>STFUNCS FUNCTION IS-LEAP: 1 leap, 0 common or outside 1601..9999.</summary>
    public static int IsLeapFunction(int year) => IsLeap(year) ? 1 : 0;

    /// <summary>STFUNCS FUNCTION DAYS-IN-MONTH: 28..31, 0 when the year is outside 1601..9999 or the month outside 1..12.</summary>
    public static int DaysInMonth(int year, int month) =>
        year is < 1601 or > 9999 || month is < 1 or > 12 ? 0 : DateTime.DaysInMonth(year, month);

    public static DateOnly EndOfMonth(DateOnly d) => new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    /// <summary>
    /// STFUNCS FUNCTION END-OF-MONTH(yyyymmdd): the day replaced by the month's last day without validating the input day
    /// (20260231 and 20260200 give 20260228); 0 when the year is outside 1601..9999 or the month outside 1..12.
    /// </summary>
    public static int EndOfMonth(int yyyymmdd)
    {
        var y = yyyymmdd / 10000 % 10000;
        var m = yyyymmdd / 100 % 100;
        var last = DaysInMonth(y, m);
        return last == 0 ? 0 : y * 10000 + m * 100 + last;
    }

    /// <summary>STFUNCS FUNCTION WEEKDAY-OF(yyyymmdd): MOD(INTEGER-OF-DATE - 1, 7) + 1 (1 Monday..7 Sunday), 0 when invalid.</summary>
    public static int WeekdayOf(int yyyymmdd) => TestDate(yyyymmdd) != 0 ? 0 : (IntegerOfDate(yyyymmdd) - 1) % 7 + 1;

    /// <summary>ISO weekday 1 = Monday .. 7 = Sunday (STFUNCS WEEKDAY-OF convention is confirmed per caller).</summary>
    public static int IsoWeekday(DateOnly d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;
}
