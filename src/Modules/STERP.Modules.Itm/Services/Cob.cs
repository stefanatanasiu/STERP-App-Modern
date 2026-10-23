using System.Globalization;
using System.Text;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Itm.Services;

/// <summary>COBOL field and edit-picture helpers used by the ITM programs.</summary>
internal static class Cob
{
    /// <summary>MOVE into PIC X(n): Windows-1252, right truncation; held right-trimmed.</summary>
    public static string X(string? value, int n) => CobolText.Fit(value, n).TrimEnd(' ');

    /// <summary>"= SPACES" (also true for an empty field).</summary>
    public static bool Spaces(string? value) => CobolText.IsSpaces(value);

    /// <summary>FUNCTION UPPER-CASE into PIC X(n).</summary>
    public static string Upper(string? value, int n) => X((value ?? "").ToUpperInvariant(), n);

    /// <summary>FUNCTION TRIM.</summary>
    public static string Trim(string? value) => (value ?? "").Trim(' ');

    /// <summary>STRING ... DELIMITED BY SIZE INTO a PIC X(n) target after MOVE SPACES.</summary>
    public static string Str(int n, params string?[] parts) => X(string.Concat(parts), n);

    /// <summary>
    /// A numeric edited picture after FUNCTION TRIM: the value truncated to <paramref name="scale"/> decimals, the integer
    /// part cut to its low-order <paramref name="intDigits"/> digits, zero suppression to one integer digit, a leading minus
    /// sign for a negative value of a signed picture (an unsigned picture shows the magnitude).
    /// </summary>
    public static string Ed(decimal value, int intDigits, int scale, bool signed)
    {
        var v = CobolNumeric.Truncated(value, scale);
        var neg = v < 0;
        v = Math.Abs(v);
        var whole = decimal.Truncate(v);
        var frac = v - whole;
        var limit = Pow10(intDigits);
        if (whole >= limit) whole %= limit;
        var sb = new StringBuilder();
        if (neg && signed && (whole != 0 || frac != 0)) sb.Append('-');
        sb.Append(whole.ToString("0", CultureInfo.InvariantCulture));
        if (scale > 0)
        {
            sb.Append('.');
            var digits = decimal.Truncate(frac * Pow10(scale)).ToString("0", CultureInfo.InvariantCulture).PadLeft(scale, '0');
            sb.Append(digits);
        }
        return sb.ToString();
    }

    /// <summary>WS-QTY-ED PIC Z(9)9.9(6).</summary>
    public static string QtyEd(decimal v) => Ed(v, 10, 6, false);

    /// <summary>WS-NUM-ED PIC -(13)9.9(6).</summary>
    public static string NumEd(decimal v) => Ed(v, 13, 6, true);

    /// <summary>SQLIO-NUM-EDIT PIC -(12)9.9(6) (parameter text).</summary>
    public static string SqlNum(decimal v) => Ed(v, 12, 6, true);

    /// <summary>WS-INT-ED PIC -(9)9.</summary>
    public static string IntEd(decimal v) => Ed(v, 9, 0, true);

    /// <summary>SQLIO-INT-EDIT PIC -(17)9 (parameter text).</summary>
    public static string SqlInt(decimal v) => Ed(v, 17, 0, true);

    /// <summary>WS-MONEY-ED PIC -(11)9.99.</summary>
    public static string MoneyEd(decimal v) => Ed(v, 11, 2, true);

    /// <summary>A PIC 9(8) date as text (eight digits, leading zeros).</summary>
    public static string D8(int yyyymmdd) => (yyyymmdd < 0 ? -yyyymmdd : yyyymmdd % 100_000_000).ToString("D8", CultureInfo.InvariantCulture);

    /// <summary>MOVE into PIC S9(13)V9(6) (truncation).</summary>
    public static decimal Q(decimal v) => CobolNumeric.Store(v, 13, 6);

    /// <summary>COMPUTE ... ROUNDED into PIC S9(13)V9(6).</summary>
    public static decimal QR(decimal v) => CobolNumeric.Store(v, 13, 6, rounded: true);

    /// <summary>MOVE into PIC S9(9)V9(9).</summary>
    public static decimal F(decimal v) => CobolNumeric.Store(v, 9, 9);

    /// <summary>COMPUTE ... ROUNDED into PIC S9(9)V9(9).</summary>
    public static decimal FR(decimal v) => CobolNumeric.Store(v, 9, 9, rounded: true);

    /// <summary>yyyymmdd of a nullable date (null = 0).</summary>
    public static int Date8(DateOnly? d) => CobolDate.ToYyyymmdd(d);

    /// <summary>A PIC 9(8) value as a nullable date (0 or invalid = null).</summary>
    public static DateOnly? Date(int yyyymmdd) => CobolDate.FromYyyymmdd(yyyymmdd);

    /// <summary>FUNCTION CURRENT-DATE(1:8).</summary>
    public static int Today() => CobolDate.ToYyyymmdd(DateTime.Now);

    private static decimal Pow10(int n)
    {
        var d = 1m;
        for (var i = 0; i < n; i++) d *= 10m;
        return d;
    }
}
