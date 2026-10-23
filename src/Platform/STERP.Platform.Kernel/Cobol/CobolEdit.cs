using System.Globalization;
using System.Text;

namespace Sterp.Platform.Kernel.Cobol;

/// <summary>
/// MOVE of a number into a numeric-edited PICTURE (the legacy edit masks: <c>-(12)9.9(6)</c>, <c>-(5)9.9(8)</c>,
/// <c>---,---,---,---,--9.99</c>, <c>Z(9)9.99-</c>, <c>ZZZ9</c> ...). Decimals beyond the picture are truncated (a MOVE never
/// rounds), integer digits beyond the picture are lost (high-order truncation), a floating '-' string prints the sign
/// immediately left of the first significant digit, Z and floating positions suppress leading zeros and the comma
/// insertion characters left of the first significant digit.
/// </summary>
public static class CobolEdit
{
    /// <summary>The edited field as stored (full picture width, right aligned).</summary>
    public static string Field(decimal value, string picture)
    {
        var pic = Expand(picture);
        var (core, trailing) = Build(value, pic);
        var width = pic.Length - (trailing is null ? 0 : 1);
        return core.PadLeft(width) + (trailing ?? "");
    }

    /// <summary>The edited text as the programs use it: FUNCTION TRIM of the edited field.</summary>
    public static string Format(decimal value, string picture) => Field(value, picture).Trim(' ');

    /// <summary>Expands repetition factors: "-(12)9.9(6)" -> "------------9.999999".</summary>
    public static string Expand(string picture)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < picture.Length; i++)
        {
            var c = picture[i];
            if (i + 1 < picture.Length && picture[i + 1] == '(')
            {
                var close = picture.IndexOf(')', i + 2);
                var n = int.Parse(picture.AsSpan(i + 2, close - i - 2), CultureInfo.InvariantCulture);
                sb.Append(c, n);
                i = close;
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString().ToUpperInvariant();
    }

    private static (string Core, string? Trailing) Build(decimal value, string pic)
    {
        string? trailingSign = null;
        var body = pic;
        if (body.Length > 1 && (body[^1] == '-' || body[^1] == '+') && body[..^1].Any(ch => ch is '9' or 'Z'))
        {
            trailingSign = body[^1].ToString();
            body = body[..^1];
        }
        var point = body.IndexOf('.');
        var intPic = point >= 0 ? body[..point] : body;
        var fracDigits = point >= 0 ? body[(point + 1)..].Count(ch => ch == '9') : 0;

        // leading floating sign string ('-' or '+' with embedded commas): the first symbol is the sign position only
        var floating = intPic.Length > 0 && (intPic[0] == '-' || intPic[0] == '+') && intPic.TrimStart(',').Count(ch => ch == intPic[0]) > 1;
        var fixedSign = !floating && intPic.Length > 0 && (intPic[0] == '-' || intPic[0] == '+') ? intPic[0] : '\0';
        var floatChar = floating ? intPic[0] : '\0';
        var positions = new List<char>();   // per integer picture symbol after the sign position
        var start = floating || fixedSign != '\0' ? 1 : 0;
        for (var i = start; i < intPic.Length; i++) positions.Add(intPic[i]);
        var digitPositions = positions.Count(ch => ch is '9' or 'Z' || (floating && ch == floatChar));

        var negative = value < 0;
        var a = Math.Abs(CobolNumeric.Truncated(value, fracDigits));
        var whole = decimal.Truncate(a);
        var frac = a - whole;
        var limit = 1m;
        for (var i = 0; i < digitPositions; i++) limit *= 10m;
        if (digitPositions < 29 && whole >= limit) whole %= limit;
        var digits = whole.ToString("0", CultureInfo.InvariantCulture).PadLeft(digitPositions, '0');
        if (digits.Length > digitPositions) digits = digits[^digitPositions..];
        if (whole == 0 && frac == 0) negative = false;

        var sb = new StringBuilder();
        var d = 0;
        var significant = false;
        foreach (var ch in positions)
        {
            if (ch is '9' or 'Z' || (floating && ch == floatChar))
            {
                var digit = digits[d++];
                if (!significant && (digit != '0' || ch == '9')) significant = true;
                if (significant) sb.Append(digit);
            }
            else if (ch == ',')
            {
                if (significant) sb.Append(',');
            }
            else if (ch == 'B')
            {
                if (significant) sb.Append(' ');
            }
            else if (ch == '0')
            {
                sb.Append('0');
            }
            else
            {
                sb.Append(ch);
            }
        }
        if (point >= 0)
        {
            sb.Append('.');
            if (fracDigits > 0)
            {
                sb.Append(frac.ToString("F" + fracDigits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture).AsSpan(2));
            }
        }
        var core = sb.ToString();
        if (floating)
        {
            if (negative && floatChar == '-') core = "-" + core;
            else if (floatChar == '+') core = (negative ? "-" : "+") + core;
        }
        else if (fixedSign != '\0')
        {
            var signChar = fixedSign == '-' ? (negative ? "-" : " ") : (negative ? "-" : "+");
            var width = intPic.Length - 1 + (point >= 0 ? 1 + fracDigits : 0);
            core = signChar + core.PadLeft(width);
        }
        string? trailing = null;
        if (trailingSign is not null)
        {
            trailing = trailingSign == "-" ? (negative ? "-" : " ") : (negative ? "-" : "+");
        }
        return (core, trailing);
    }
}
