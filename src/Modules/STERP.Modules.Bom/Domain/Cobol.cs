using System.Globalization;
using System.Numerics;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Bom.Domain;

/// <summary>
/// GnuCOBOL intermediate arithmetic (cob_decimal) for the COMPUTE statements of the BOM programs: an unscaled integer and a
/// scale. Addition, subtraction and multiplication are exact; division keeps 38 further digits and truncates
/// (cob_decimal_div); storing truncates to the receiving PIC (or rounds half away from zero for ROUNDED) and keeps the
/// low-order integer digits. A division by zero yields NaN and leaves the receiving field unchanged (size error).
/// </summary>
internal readonly struct Cd
{
    private const int MaxDigits = 38;
    private static readonly BigInteger[] Pow = Enumerable.Range(0, 120).Select(i => BigInteger.Pow(10, i)).ToArray();

    public readonly BigInteger V;
    public readonly int S;
    public readonly bool NaN;

    private Cd(BigInteger v, int s, bool nan = false)
    {
        V = v;
        S = s;
        NaN = nan;
    }

    /// <summary>The value of a field with PIC scale <paramref name="scale"/>.</summary>
    public static Cd F(decimal value, int scale)
    {
        var (v, s) = Unscale(value);
        if (s < scale) return new Cd(v * Pow[scale - s], scale);
        if (s > scale) return new Cd(BigInteger.Divide(v, Pow[s - scale]), scale);
        return new Cd(v, scale);
    }

    /// <summary>An integer field (COMP-5 or PIC S9(n)).</summary>
    public static Cd I(long value) => new(value, 0);

    private static (BigInteger Value, int Scale) Unscale(decimal d)
    {
        var bits = decimal.GetBits(d);
        var scale = (bits[3] >> 16) & 0xFF;
        var negative = (bits[3] & unchecked((int)0x80000000)) != 0;
        var mag = new BigInteger((uint)bits[0]) | (new BigInteger((uint)bits[1]) << 32) | (new BigInteger((uint)bits[2]) << 64);
        return (negative ? -mag : mag, scale);
    }

    private static (Cd A, Cd B) Align(Cd a, Cd b)
    {
        if (a.S == b.S) return (a, b);
        return a.S < b.S ? (new Cd(a.V * Pow[b.S - a.S], b.S), b) : (a, new Cd(b.V * Pow[a.S - b.S], a.S));
    }

    public static Cd operator +(Cd a, Cd b)
    {
        if (a.NaN || b.NaN) return new Cd(0, 0, true);
        var (x, y) = Align(a, b);
        return new Cd(x.V + y.V, x.S);
    }

    public static Cd operator -(Cd a, Cd b)
    {
        if (a.NaN || b.NaN) return new Cd(0, 0, true);
        var (x, y) = Align(a, b);
        return new Cd(x.V - y.V, x.S);
    }

    public static Cd operator *(Cd a, Cd b) => a.NaN || b.NaN ? new Cd(0, 0, true) : new Cd(a.V * b.V, a.S + b.S);

    /// <summary>cob_decimal_div: scale = s1 - s2, shifted by 38 digits (plus the negative scale), truncated quotient.</summary>
    public static Cd operator /(Cd a, Cd b)
    {
        if (a.NaN || b.NaN || b.V.IsZero) return new Cd(0, 0, true);
        if (a.V.IsZero) return new Cd(0, 0);
        var scale = a.S - b.S;
        var shift = MaxDigits + (scale < 0 ? -scale : 0);
        return new Cd(BigInteger.Divide(a.V * Pow[shift], b.V), scale + shift);
    }

    /// <summary>
    /// Stores into PIC S9(intDigits)V9(scale): truncation (or half away from zero when <paramref name="rounded"/>), then
    /// high-order truncation. A NaN (division by zero) keeps <paramref name="previous"/>, as the size error left the field.
    /// </summary>
    public decimal Store(int intDigits, int scale, bool rounded = false, decimal previous = 0m, bool signed = true)
    {
        if (NaN) return previous;
        BigInteger q;
        if (S > scale)
        {
            var div = Pow[S - scale];
            q = BigInteger.DivRem(V, div, out var rem);
            if (rounded && BigInteger.Abs(rem) * 2 >= div) q += V.Sign;
        }
        else
        {
            q = V * Pow[scale - S];
        }
        q = BigInteger.Remainder(q, Pow[intDigits + scale]);
        if (!signed) q = BigInteger.Abs(q);
        return ToDecimal(q, scale);
    }

    /// <summary>Store into a COMP-5 / integer field (no fraction).</summary>
    public int StoreInt(int digits = 9, bool rounded = false, int previous = 0) =>
        NaN ? previous : (int)Store(digits, 0, rounded);

    private static decimal ToDecimal(BigInteger unscaled, int scale)
    {
        var negative = unscaled.Sign < 0;
        var mag = BigInteger.Abs(unscaled);
        var lo = (int)(uint)(mag & uint.MaxValue);
        var mid = (int)(uint)((mag >> 32) & uint.MaxValue);
        var hi = (int)(uint)((mag >> 64) & uint.MaxValue);
        return new decimal(lo, mid, hi, negative, (byte)scale);
    }

    public override string ToString() => NaN ? "NaN" : ToDecimal(V, Math.Min(S, 28)).ToString(CultureInfo.InvariantCulture);
}

/// <summary>MOVE semantics for the BOM records: PIC X(n) and PIC S9(i)V9(s) receiving fields.</summary>
internal static class Pic
{
    /// <summary>MOVE to PIC X(n): right truncation; carried right-trimmed (leading spaces kept).</summary>
    public static string X(string? value, int length) => CobolText.Fit(value, length).TrimEnd();

    /// <summary>MOVE to PIC S9(i)V9(s): truncation at the scale, high-order truncation, the field's scale kept.</summary>
    public static decimal N(decimal value, int intDigits, int scale) => CobolNumeric.Store(value, intDigits, scale);

    /// <summary>MOVE to PIC 9(i)V9(s) (unsigned).</summary>
    public static decimal U(decimal value, int intDigits, int scale) => CobolNumeric.Store(value, intDigits, scale, signed: false);

    private static readonly long[] Pow10 = Enumerable.Range(0, 19).Select(i => Enumerable.Repeat(10L, i).Aggregate(1L, (a, b) => a * b)).ToArray();

    /// <summary>MOVE to PIC S9(n) / Z(n)9 integer fields (truncation of high-order digits).</summary>
    public static int I(long value, int digits) => (int)(value % Pow10[digits]);

    /// <summary>FUNCTION UPPER-CASE on single-byte text (ASCII letters only, as the C runtime toupper in the C locale).</summary>
    public static string Upper(string? value)
    {
        var s = value ?? "";
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            if (chars[i] is >= 'a' and <= 'z') chars[i] = (char)(chars[i] - 32);
        }
        return new string(chars);
    }

    /// <summary>FUNCTION TRIM (both sides, spaces).</summary>
    public static string T(string? value) => (value ?? "").Trim(' ');

    /// <summary>True when the alphanumeric is SPACES.</summary>
    public static bool Blank(string? value) => string.IsNullOrEmpty(value) || value.All(c => c == ' ');

    /// <summary>COBOL equality of two alphanumerics (shorter operand space padded).</summary>
    public static bool Eq(string? a, string? b) => string.Equals((a ?? "").TrimEnd(' '), (b ?? "").TrimEnd(' '), StringComparison.Ordinal);

    /// <summary>
    /// STRING ... DELIMITED BY SIZE INTO a PIC X(n) target: the pieces are concatenated and the result is cut at the target
    /// length (overflow), carried right-trimmed.
    /// </summary>
    public static string Str(int length, params string?[] pieces) => X(string.Concat(pieces), length);

    /// <summary>A numeric edited with Z(n)9 / -(n)9 then FUNCTION TRIM: the plain integer text.</summary>
    public static string Z(long value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Edited -(14)9.9(6) / -(12)9.9(2) then FUNCTION TRIM: sign only when negative, at least one integer digit, the
    /// field's decimals (the value is truncated to them by the MOVE).
    /// </summary>
    public static string E(decimal value, int scale)
    {
        var v = CobolNumeric.Truncated(value, scale);
        var text = Math.Abs(v).ToString("0." + new string('0', scale), CultureInfo.InvariantCulture);
        return v < 0 ? "-" + text : text;
    }
}
