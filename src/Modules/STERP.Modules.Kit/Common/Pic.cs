using System.Globalization;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Kit.Common;

/// <summary>
/// COBOL field semantics used by the kit programs. Text fields are carried right-trimmed (the PIC X(n) value without its
/// trailing spaces); a MOVE into PIC X(n) is <see cref="X"/> (Windows-1252, right truncation). Numeric fields carry their
/// PIC scale; a MOVE or COMPUTE without ROUNDED truncates (<see cref="N"/>).
/// </summary>
internal static class Pic
{
    /// <summary>MOVE value TO PIC X(n): the stored value, right-trimmed.</summary>
    public static string X(string? value, int n) => CobolText.Fit(value, n).TrimEnd(' ');

    /// <summary>The PIC X(n) field as stored (space padded to n).</summary>
    public static string Pad(string? value, int n) => CobolText.Fit(value, n);

    /// <summary>"field = SPACES".</summary>
    public static bool Blank(string? value) => string.IsNullOrEmpty(value) || value.All(c => c == ' ');

    /// <summary>FUNCTION TRIM (spaces on both sides; an all-space field gives the empty string).</summary>
    public static string Trim(string? value) => (value ?? "").Trim(' ');

    /// <summary>STRING ... DELIMITED BY SIZE INTO a PIC X(n) field after MOVE SPACES (the concatenation cut at n).</summary>
    public static string Str(int n, params string?[] parts) => X(string.Concat(parts), n);

    /// <summary>STRING ... DELIMITED BY SIZE INTO CA-MESSAGE X(200) after MOVE SPACES.</summary>
    public static string Msg(params string?[] parts) => X(string.Concat(parts), 200);

    /// <summary>MOVE/COMPUTE (no ROUNDED) into PIC S9(intDigits)V9(scale).</summary>
    public static decimal N(decimal value, int intDigits, int scale) => CobolNumeric.Store(value, intDigits, scale);

    /// <summary>COMPUTE ... ROUNDED into PIC S9(intDigits)V9(scale).</summary>
    public static decimal R(decimal value, int intDigits, int scale) => CobolNumeric.Store(value, intDigits, scale, rounded: true);

    /// <summary>MOVE into PIC S9(13)V9(6) (the kit quantity fields).</summary>
    public static decimal Q(decimal value) => CobolNumeric.Store(value, 13, 6);

    /// <summary>MOVE into PIC S9(11)V99 (the kit value fields).</summary>
    public static decimal M(decimal value) => CobolNumeric.Store(value, 11, 2);

    /// <summary>The text of an edited quantity: PIC -(13)9.9(6) after FUNCTION TRIM (Ws-Edit of the kit programs).</summary>
    public static string Ed(decimal value) => CobolEdit.Format(value, "-(13)9.9(6)");

    /// <summary>A PIC 9(8) date as STRING shows it (eight digits).</summary>
    public static string D8(int yyyymmdd) =>
        (yyyymmdd < 0 ? 0 : yyyymmdd % 100_000_000).ToString("D8", CultureInfo.InvariantCulture);

    /// <summary>An unsigned PIC 9(n) (or a positive PIC S9(n) DISPLAY) field as STRING shows it: n digits, leading zeros.</summary>
    public static string Digits(long value, int n)
    {
        var text = Math.Abs(value).ToString(CultureInfo.InvariantCulture).PadLeft(n, '0');
        return text[^n..];
    }

    /// <summary>A date as PIC 9(8) yyyymmdd (null = 0).</summary>
    public static int Date(DateOnly? date) => CobolDate.ToYyyymmdd(date);

    /// <summary>A PIC 9(8) date as JSON date text (0 or not a date = null, contracts 4.3).</summary>
    public static string? Json(int yyyymmdd) =>
        CobolDate.FromYyyymmdd(yyyymmdd)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>A decimal carrying exactly the field scale (serialization, contracts 4.3).</summary>
    public static decimal S(decimal value, int scale) => CobolNumeric.WithScale(value, scale);

    /// <summary>FUNCTION NUMVAL of a fetched text slot.</summary>
    public static decimal NumVal(string? text) => CobolNumeric.NumVal(text);

    /// <summary>"IF slot NUMERIC MOVE slot TO PIC 9(8) ELSE 0" for a date fetched as yyyymmdd text.</summary>
    public static int DateSlot(string? slot)
    {
        var s = slot ?? "";
        if (s.Length == 0 || !s.All(char.IsAsciiDigit)) return 0;
        return int.Parse(s.Length > 8 ? s[^8..] : s, NumberStyles.None, CultureInfo.InvariantCulture);
    }
}
