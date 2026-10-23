using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Platform.Kernel.Services;

/// <summary>STRUTILCA body (STRUTILCA.cpy:31-48).</summary>
public sealed class StrutilArea : Commarea
{
    /// <summary>SU-TEXT-IN X(200).</summary>
    public string TextIn { get; set; } = "";

    /// <summary>SU-TEXT-OUT X(200).</summary>
    public string TextOut { get; set; } = "";

    /// <summary>SU-MAX-LEN S9(4): outside 1..200 becomes 40.</summary>
    public int MaxLength { get; set; }

    /// <summary>SU-LENGTH-OUT S9(4): position of the last non-blank, non-low-value output character.</summary>
    public int LengthOut { get; set; }

    /// <summary>SU-VALID X.</summary>
    public string Valid { get; set; } = "";

    /// <summary>SU-CHANGED X.</summary>
    public string Changed { get; set; } = "";

    /// <summary>SU-AMOUNT S9(13)V9(6).</summary>
    public decimal Amount { get; set; }

    /// <summary>SU-PLACES S9(4) (never used by QTY, SME-072).</summary>
    public int Places { get; set; }

    /// <summary>SU-CURRENCY X(3): GBP by default (not upper-cased).</summary>
    public string Currency { get; set; } = "";

    /// <summary>SU-SYMBOL X: "Y" with GBP gives the X'A3' pound sign.</summary>
    public string Symbol { get; set; } = "";

    /// <summary>SU-ZERO-POLICY X(8): KEEP (default) or exactly STRIP.</summary>
    public string ZeroPolicy { get; set; } = "";

    /// <summary>SU-BAD-CHAR X.</summary>
    public string BadChar { get; set; } = "";

    /// <summary>SU-BAD-POS S9(4).</summary>
    public int BadPosition { get; set; }
}

/// <summary>
/// STRUTIL - text handling shared by every module (FR-STERP-PLATFORM-023, COM-004, COM-024): UPPER, CODE, DOCREF, MONEY, QTY,
/// SQUEEZE with STRUTIL's reason codes and messages (STRUTIL.cob:66-402). Pure functions; the pound sign of MONEY is
/// '£' (U+00A3), which is the single byte X'A3' in Windows-1252.
/// </summary>
public static class CobolString
{
    private const string Allowed = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-./_";

    /// <summary>CALL "STRUTIL" USING area.</summary>
    public static LegacyResult Execute(StrutilArea a)
    {
        var h = a.Header;
        // 0100-INITIALISE
        h.Reset("STRUTIL", "COM-004");
        a.TextOut = "";
        a.Valid = "Y";
        a.Changed = "N";
        a.BadChar = " ";
        a.BadPosition = 0;
        a.LengthOut = 0;
        a.MaxLength = (int)CobolNumeric.Store(a.MaxLength, 4, 0);
        if (a.MaxLength is <= 0 or > 200) a.MaxLength = 40;
        if (K.Blank(a.Currency, 3)) a.Currency = "GBP";
        if (K.Blank(a.ZeroPolicy, 8)) a.ZeroPolicy = "KEEP";
        a.Amount = CobolNumeric.Store(a.Amount, 13, 6);
        var textIn = K.X(a.TextIn, 200);
        var upper = CobolText.Upper(textIn);

        switch (h.FunctionCode)
        {
            case "UPPER":
                SetOut(a, upper);
                if (K.X(a.TextOut, 200) != textIn) a.Changed = "Y";
                h.Message = "Folded to upper case";
                break;
            case "CODE":
                ValidateCode(a, upper, textIn);
                break;
            case "DOCREF":
                NormaliseRef(a, upper, textIn);
                break;
            case "MONEY":
                FormatMoney(a);
                break;
            case "QTY":
                FormatQuantity(a);
                break;
            case "SQUEEZE":
                Squeeze(a, textIn);
                break;
            default:
                Reject(a, "STR0009", "STRUTIL does not support function " + K.Trim(h.Function));
                break;
        }
        return h.Result;
    }

    public static StrutilArea Upper(string text) => Call("UPPER", a => a.TextIn = text);

    /// <summary>CODE (D-21): A-Z 0-9 - . / _ only, at most <paramref name="maxLength"/> (default 40).</summary>
    public static StrutilArea Code(string text, int maxLength = 0) => Call("CODE", a => { a.TextIn = text; a.MaxLength = maxLength; });

    /// <summary>DOCREF (COM-004): A-Z and 0-9 only; under exactly "STRIP" the leading zeros of the trailing digit run go.</summary>
    public static StrutilArea DocRef(string text, string? zeroPolicy = null) => Call("DOCREF", a => { a.TextIn = text; a.ZeroPolicy = zeroPolicy ?? ""; });

    /// <summary>MONEY: two decimals (truncated) with commas, "&lt;currency&gt; " prefix or '£' for GBP with symbol "Y".</summary>
    public static StrutilArea Money(decimal amount, string? currency = null, string? symbol = null) =>
        Call("MONEY", a => { a.Amount = amount; a.Currency = currency ?? ""; a.Symbol = symbol ?? ""; });

    /// <summary>QTY: six decimals with commas; trailing zeros trimmed under exactly "STRIP".</summary>
    public static StrutilArea Quantity(decimal amount, string? zeroPolicy = null) =>
        Call("QTY", a => { a.Amount = amount; a.ZeroPolicy = zeroPolicy ?? ""; });

    /// <summary>SQUEEZE: runs of spaces/low-values collapsed, both ends trimmed (not upper-cased).</summary>
    public static StrutilArea Squeeze(string text) => Call("SQUEEZE", a => a.TextIn = text);

    private static StrutilArea Call(string function, Action<StrutilArea> fill)
    {
        var a = new StrutilArea();
        a.Header.Prepare(KernelCaller.Empty, function);
        fill(a);
        Execute(a);
        return a;
    }

    /// <summary>1100-VALIDATE-CODE.</summary>
    private static void ValidateCode(StrutilArea a, string upper, string textIn)
    {
        var h = a.Header;
        SetOut(a, upper);
        var len = a.LengthOut;
        if (len == 0)
        {
            a.Valid = "N";
            Reject(a, "STR0001", "A code is required");
            return;
        }
        if (len > a.MaxLength)
        {
            a.Valid = "N";
            a.BadPosition = a.MaxLength;
            Reject(a, "STR0002", string.Concat("Code is longer than ", K.Ed(a.MaxLength, "-(9)9"), " characters"));
            return;
        }
        var outText = K.X(a.TextOut, 200);
        for (var i = 1; i <= len && a.Valid != "N"; i++)
        {
            var ch = outText[i - 1];
            if (Allowed.IndexOf(ch) < 0)
            {
                a.Valid = "N";
                a.BadChar = ch.ToString();
                a.BadPosition = i;
            }
        }
        if (a.Valid == "N")
        {
            Reject(a, "STR0003", string.Concat("Character [", a.BadChar, "] at position ", K.Ed(a.BadPosition, "-(9)9"),
                " is not allowed in a code (A-Z 0-9 and - . / _ only)"));
            return;
        }
        if (outText != textIn)
        {
            a.Changed = "Y";
            h.ReturnCode = 4;
            h.ReasonCode = "STR0004";
            h.Message = "Code accepted and stored in upper case";
        }
        else
        {
            h.Message = "Code accepted";
        }
    }

    /// <summary>2000-NORMALISE-REF: "SI-000123", "si 000123", "SI/000123" -> SI123 under STRIP, SI000123 under KEEP.</summary>
    private static void NormaliseRef(StrutilArea a, string upper, string textIn)
    {
        var h = a.Header;
        var build = new System.Text.StringBuilder(200);
        foreach (var ch in upper)
        {
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9') build.Append(ch);
        }
        if (build.Length == 0)
        {
            a.Valid = "N";
            Reject(a, "STR0005", "The reference has no letters or digits");
            return;
        }
        var text = build.ToString();
        if (K.X(a.ZeroPolicy, 8) == "STRIP   ") text = StripLeadingZeros(text);
        SetOut(a, text);
        if (K.X(a.TextOut, 200) != textIn)
        {
            a.Changed = "Y";
            h.ReturnCode = 4;
            h.ReasonCode = "STR0006";
        }
        h.Message = K.Msg("Reference normalised to ", K.Trim(a.TextOut));
    }

    /// <summary>2100-STRIP-LEADING-ZEROS of the trailing digit run, always leaving one digit.</summary>
    private static string StripLeadingZeros(string build)
    {
        var k = build.Length;
        var firstDigit = 0;
        for (var i = k; i >= 1; i--)
        {
            if (build[i - 1] is >= '0' and <= '9') firstDigit = i;
            else break;
        }
        if (firstDigit == 0) return build;
        var j = firstDigit;
        while (!(j >= k || build[j - 1] != '0')) j++;
        if (j <= firstDigit) return build;
        return build[..(firstDigit - 1)] + build[(j - 1)..];
    }

    /// <summary>3000-FORMAT-MONEY: picture ---,---,---,---,--9.99 (decimals truncated).</summary>
    private static void FormatMoney(StrutilArea a)
    {
        var edited = K.Ed(a.Amount, "---,---,---,---,--9.99");
        var currency = K.X(a.Currency, 3);
        var text = K.X(a.Symbol, 1) == "Y" && currency == "GBP"
            ? "\u00A3" + edited
            : string.Concat(K.Trim(currency), " ", edited);
        SetOut(a, text);
        a.Header.Message = "Amount formatted";
    }

    /// <summary>3100-FORMAT-QTY: picture ---,---,---,---,--9.9(6); SU-PLACES is ignored (SME-072).</summary>
    private static void FormatQuantity(StrutilArea a)
    {
        var text = K.Ed(a.Amount, "---,---,---,---,--9.9(6)");
        if (K.X(a.ZeroPolicy, 8) == "STRIP   ")
        {
            // 3200-TRIM-DECIMALS
            var point = text.IndexOf('.');
            if (point >= 0)
            {
                var len = text.Length;
                while (len > point + 1 && text[len - 1] == '0') len--;
                text = len == point + 1 ? text[..point] : text[..len];
            }
        }
        SetOut(a, text);
        a.Header.Message = "Quantity formatted";
    }

    /// <summary>4000-SQUEEZE.</summary>
    private static void Squeeze(StrutilArea a, string textIn)
    {
        var build = new System.Text.StringBuilder(200);
        var prev = ' ';
        foreach (var ch in textIn)
        {
            if (ch is ' ' or '\0')
            {
                if (build.Length > 0 && prev != ' ')
                {
                    build.Append(' ');
                    prev = ' ';
                }
            }
            else
            {
                build.Append(ch);
                prev = ch;
            }
        }
        SetOut(a, build.ToString());
        if (K.X(a.TextOut, 200) != textIn) a.Changed = "Y";
        a.Header.Message = "Spaces squeezed";
    }

    /// <summary>MOVE into SU-TEXT-OUT and 8000-OUT-LENGTH.</summary>
    private static void SetOut(StrutilArea a, string text)
    {
        var field = K.X(text, 200);
        a.TextOut = field.TrimEnd(' ');
        var len = 0;
        for (var i = 200; i >= 1; i--)
        {
            if (field[i - 1] != ' ' && field[i - 1] != '\0')
            {
                len = i;
                break;
            }
        }
        a.LengthOut = len;
    }

    private static void Reject(StrutilArea a, string reason, string message)
    {
        a.Valid = "N";
        a.Header.Reject(reason, message);
    }
}
