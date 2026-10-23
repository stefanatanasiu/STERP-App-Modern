using System.Globalization;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Xmd.Jobs;

/// <summary>DISPLAY renderings of numeric items and the edit masks the XMD programs use.</summary>
internal static class Disp
{
    /// <summary>DISPLAY of a signed numeric DISPLAY item S9(n): n digits (low-order kept) and a trailing sign.</summary>
    public static string Signed(decimal value, int digits)
    {
        var v = Math.Abs(decimal.Truncate(value));
        var text = Digits(v, digits);
        return text + (value < 0 && v != 0 ? "-" : "+");
    }

    /// <summary>DISPLAY of an unsigned numeric DISPLAY item 9(n).</summary>
    public static string Unsigned(decimal value, int digits) => Digits(Math.Abs(decimal.Truncate(value)), digits);

    /// <summary>DISPLAY of a signed item with decimals S9(i)V9(s): digits with the implied point omitted and a trailing sign.</summary>
    public static string SignedScaled(decimal value, int intDigits, int scale)
    {
        var v = Math.Abs(CobolNumeric.Store(value, intDigits, scale));
        var p = 1m;
        for (var i = 0; i < scale; i++) p *= 10m;
        var text = Digits(decimal.Truncate(v * p), intDigits + scale);
        return text + (value < 0 && v != 0 ? "-" : "+");
    }

    private static string Digits(decimal v, int digits)
    {
        var limit = 1m;
        for (var i = 0; i < digits; i++) limit *= 10m;
        if (v >= limit) v %= limit;
        return v.ToString(new string('0', digits), CultureInfo.InvariantCulture);
    }

    /// <summary>MOVE number TO XB-MON-ED (-(14)9.99) and FUNCTION TRIM.</summary>
    public static string Mon(decimal value) => CobolEdit.Format(value, "-(14)9.99");

    /// <summary>MOVE number TO XB-NUM-ED (-(14)9.999999) and FUNCTION TRIM.</summary>
    public static string Num(decimal value) => CobolEdit.Format(value, "-(14)9.999999");

    /// <summary>MOVE number TO XB-INT-ED (-(16)9) and FUNCTION TRIM.</summary>
    public static string Int(decimal value) => CobolEdit.Format(value, "-(16)9");

    /// <summary>A numeric-edited report field.</summary>
    public static string Ed(decimal value, string picture) => CobolEdit.Field(value, picture);

    /// <summary>A PIC 9(8) date as its 8 digits.</summary>
    public static string D8(int yyyymmdd) => Digits(Math.Max(0, yyyymmdd), 8);
}
