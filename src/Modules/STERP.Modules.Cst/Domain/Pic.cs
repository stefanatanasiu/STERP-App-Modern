using System.Globalization;
using System.Numerics;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Cst.Domain;

/// <summary>MOVE semantics of the CST programs: PIC X(n), PIC [S]9(i)V9(s), FUNCTION UPPER-CASE / TRIM, STRING ... INTO.</summary>
internal static class Pic
{
    /// <summary>MOVE to PIC X(n): Windows-1252, right truncation; carried right-trimmed (leading spaces kept).</summary>
    public static string X(string? value, int length) => CobolText.Fit(value, length).TrimEnd(' ');

    /// <summary>MOVE to PIC S9(i)V9(s): truncation at the scale, high-order truncation, the field's scale kept.</summary>
    public static decimal N(decimal value, int intDigits, int scale) => CobolNumeric.Store(value, intDigits, scale);

    /// <summary>MOVE to PIC 9(i)V9(s) (unsigned: the absolute value is stored).</summary>
    public static decimal U(decimal value, int intDigits, int scale) => CobolNumeric.Store(value, intDigits, scale, signed: false);

    /// <summary>MOVE to an integer field PIC S9(n) (high-order truncation).</summary>
    public static int I(decimal value, int digits) => (int)CobolNumeric.Store(value, digits, 0);

    /// <summary>MOVE to an unsigned integer field PIC 9(n).</summary>
    public static int UI(decimal value, int digits) => (int)CobolNumeric.Store(value, digits, 0, signed: false);

    /// <summary>FUNCTION UPPER-CASE (ASCII letters only, C locale).</summary>
    public static string Upper(string? value) => CobolText.Upper(value);

    /// <summary>FUNCTION TRIM (spaces, both sides).</summary>
    public static string T(string? value) => (value ?? "").Trim(' ');

    /// <summary>The alphanumeric is SPACES.</summary>
    public static bool Blank(string? value) => string.IsNullOrEmpty(value) || value.All(c => c == ' ');

    /// <summary>COBOL equality of two alphanumerics (the shorter operand is space padded).</summary>
    public static bool Eq(string? a, string? b) => string.Equals((a ?? "").TrimEnd(' '), (b ?? "").TrimEnd(' '), StringComparison.Ordinal);

    /// <summary>STRING ... DELIMITED BY SIZE INTO a PIC X(n) target that was SPACES: concatenation cut at the target length.</summary>
    public static string Str(int length, params string?[] pieces) => X(string.Concat(pieces), length);

    /// <summary>A PIC X(n) field DISPLAYed as stored (space padded to its length).</summary>
    public static string Pad(string? value, int length) => CobolText.Fit(value, length);

    /// <summary>
    /// DISPLAY of a numeric DISPLAY item PIC S9(n) (GnuCOBOL: the zero-padded digits and a trailing sign, '+' or '-').
    /// </summary>
    public static string DispSigned(decimal value, int digits)
    {
        var v = Math.Abs(decimal.Truncate(value));
        var limit = Pow10(digits);
        if (v >= limit) v %= limit;
        return v.ToString(new string('0', digits), CultureInfo.InvariantCulture) + (value < 0 && v != 0 ? "-" : "+");
    }

    /// <summary>The value with exactly <paramref name="scale"/> decimal places (JSON scale of a COBOL field).</summary>
    public static decimal S(decimal value, int scale) => CobolNumeric.WithScale(value, scale);

    private static decimal Pow10(int n)
    {
        var d = 1m;
        for (var i = 0; i < n; i++) d *= 10m;
        return d;
    }
}

/// <summary>
/// GnuCOBOL intermediate arithmetic (cob_decimal) for COMPUTE: an unscaled integer and a scale. Addition, subtraction and
/// multiplication are exact; division keeps 38 further digits and truncates (cob_decimal_div); storing truncates to the
/// receiving PIC (or rounds half away from zero for ROUNDED) and keeps the low-order integer digits. A division by zero is a
/// size error that leaves the receiving field unchanged.
/// </summary>
internal readonly struct Cd
{
    private const int MaxDigits = 38;
    private static readonly BigInteger[] Pow = Enumerable.Range(0, 120).Select(i => BigInteger.Pow(10, i)).ToArray();

    private readonly BigInteger _v;
    private readonly int _s;
    private readonly bool _nan;

    private Cd(BigInteger v, int s, bool nan = false)
    {
        _v = v;
        _s = s;
        _nan = nan;
    }

    /// <summary>The value of a decimal field (its own scale).</summary>
    public static Cd Of(decimal value)
    {
        var bits = decimal.GetBits(value);
        var scale = (bits[3] >> 16) & 0xFF;
        var negative = (bits[3] & unchecked((int)0x80000000)) != 0;
        var mag = new BigInteger((uint)bits[0]) | (new BigInteger((uint)bits[1]) << 32) | (new BigInteger((uint)bits[2]) << 64);
        return new Cd(negative ? -mag : mag, scale);
    }

    public static implicit operator Cd(decimal value) => Of(value);

    public static implicit operator Cd(int value) => new(value, 0);

    public bool IsSizeError => _nan;

    private static (Cd A, Cd B) Align(Cd a, Cd b)
    {
        if (a._s == b._s) return (a, b);
        return a._s < b._s ? (new Cd(a._v * Pow[b._s - a._s], b._s), b) : (a, new Cd(b._v * Pow[a._s - b._s], a._s));
    }

    public static Cd operator +(Cd a, Cd b)
    {
        if (a._nan || b._nan) return new Cd(0, 0, true);
        var (x, y) = Align(a, b);
        return new Cd(x._v + y._v, x._s);
    }

    public static Cd operator -(Cd a, Cd b)
    {
        if (a._nan || b._nan) return new Cd(0, 0, true);
        var (x, y) = Align(a, b);
        return new Cd(x._v - y._v, x._s);
    }

    public static Cd operator -(Cd a) => a._nan ? a : new Cd(-a._v, a._s);

    public static Cd operator *(Cd a, Cd b) => a._nan || b._nan ? new Cd(0, 0, true) : new Cd(a._v * b._v, a._s + b._s);

    /// <summary>cob_decimal_div: the quotient carried 38 digits beyond the operand scales, truncated.</summary>
    public static Cd operator /(Cd a, Cd b)
    {
        if (a._nan || b._nan || b._v.IsZero) return new Cd(0, 0, true);
        if (a._v.IsZero) return new Cd(0, 0);
        var scale = a._s - b._s;
        var shift = MaxDigits + (scale < 0 ? -scale : 0);
        return new Cd(BigInteger.Divide(a._v * Pow[shift], b._v), scale + shift);
    }

    /// <summary>
    /// Stores into PIC S9(intDigits)V9(scale): truncation (or half away from zero when <paramref name="rounded"/>), then
    /// high-order truncation. A size error (division by zero) keeps <paramref name="previous"/>.
    /// </summary>
    public decimal Store(int intDigits, int scale, bool rounded = false, decimal previous = 0m, bool signed = true)
    {
        if (_nan) return previous;
        BigInteger q;
        if (_s > scale)
        {
            var div = Pow[_s - scale];
            q = BigInteger.DivRem(_v, div, out var rem);
            if (rounded && BigInteger.Abs(rem) * 2 >= div) q += _v.Sign;
        }
        else
        {
            q = _v * Pow[scale - _s];
        }
        q = BigInteger.Remainder(q, Pow[intDigits + scale]);
        if (!signed) q = BigInteger.Abs(q);
        var negative = q.Sign < 0;
        var mag = BigInteger.Abs(q);
        var lo = (int)(uint)(mag & uint.MaxValue);
        var mid = (int)(uint)((mag >> 32) & uint.MaxValue);
        var hi = (int)(uint)((mag >> 64) & uint.MaxValue);
        return new decimal(lo, mid, hi, negative, (byte)scale);
    }
}
