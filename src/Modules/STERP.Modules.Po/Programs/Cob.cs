using System.Globalization;
using System.Text;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Po.Programs;

/// <summary>COBOL field, STRING and edit-picture helpers used by the Purchasing programs.</summary>
internal static class Cob
{
    /// <summary>MOVE into PIC X(n): Windows-1252, right truncation; held right-trimmed.</summary>
    public static string X(string? value, int n) => CobolText.Fit(value, n).TrimEnd(' ');

    /// <summary>"= SPACES" (also true for an empty field).</summary>
    public static bool Spaces(string? value) => CobolText.IsSpaces(value);

    /// <summary>FUNCTION TRIM (an all-space argument gives a zero-length result).</summary>
    public static string Trim(string? value) => (value ?? "").Trim(' ');

    /// <summary>FUNCTION UPPER-CASE (ASCII folding, as GnuCOBOL in the C locale).</summary>
    public static string Upper(string? value)
    {
        var s = value ?? "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) sb.Append(c is >= 'a' and <= 'z' ? (char)(c - 32) : c);
        return sb.ToString();
    }

    /// <summary>MOVE SPACES + STRING ... DELIMITED BY SIZE INTO a PIC X(n) target (overflow stops the STRING).</summary>
    public static string Str(int n, params string?[] parts) => X(string.Concat(parts), n);

    /// <summary>CA-MESSAGE X(200) built by STRING.</summary>
    public static string Msg(params string?[] parts) => Str(200, parts);

    /// <summary>
    /// A floating-sign / zero-suppressed numeric edited picture after FUNCTION TRIM: the value truncated to
    /// <paramref name="scale"/> decimals, the integer part cut to its low-order <paramref name="intDigits"/> digits, at least
    /// one integer digit, a leading minus sign for a negative value of a signed picture.
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
            sb.Append(decimal.Truncate(frac * Pow10(scale)).ToString("0", CultureInfo.InvariantCulture).PadLeft(scale, '0'));
        }
        return sb.ToString();
    }

    /// <summary>FUNCTION TRIM of a numeric edited field (MOVE value TO edited item).</summary>
    public static string Ed(decimal value, string picture) => CobolEdit.Format(value, picture);

    /// <summary>PIC -(11)9.99 trimmed.</summary>
    public static string Amt(decimal v) => CobolEdit.Format(v, "-(11)9.99");

    /// <summary>PIC -(9)9.999999 trimmed.</summary>
    public static string Qty(decimal v) => CobolEdit.Format(v, "-(9)9.999999");

    /// <summary>PIC -(n)9 trimmed.</summary>
    public static string Int(long v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>A PIC 9(8) date as eight digits.</summary>
    public static string D8(int yyyymmdd) => (yyyymmdd < 0 ? -yyyymmdd : yyyymmdd % 100_000_000).ToString("D8", CultureInfo.InvariantCulture);

    /// <summary>A PIC 9(n) unsigned display field moved to text (n digits, leading zeros).</summary>
    public static string Z(long value, int n)
    {
        var s = Math.Abs(value).ToString(CultureInfo.InvariantCulture).PadLeft(n, '0');
        return s[^n..];
    }

    /// <summary>MOVE/COMPUTE into PIC S9(i)V9(s) without ROUNDED.</summary>
    public static decimal T(decimal v, int intDigits, int scale, bool signed = true) => CobolNumeric.Store(v, intDigits, scale, signed);

    /// <summary>COMPUTE ... ROUNDED into PIC S9(i)V9(s).</summary>
    public static decimal R(decimal v, int intDigits, int scale, bool signed = true) => CobolNumeric.Store(v, intDigits, scale, signed, rounded: true);

    /// <summary>FUNCTION NUMVAL of a slot.</summary>
    public static decimal NumVal(string? text) => CobolNumeric.NumVal(text);

    /// <summary>yyyymmdd of the system clock (FUNCTION CURRENT-DATE(1:8)).</summary>
    public static int Today() => CobolDate.ToYyyymmdd(DateTime.Now);

    public static int Date8(DateOnly? d) => CobolDate.ToYyyymmdd(d);

    private static decimal Pow10(int n)
    {
        var d = 1m;
        for (var i = 0; i < n; i++) d *= 10m;
        return d;
    }
}
