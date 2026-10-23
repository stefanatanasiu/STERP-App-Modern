using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Fin.Common;

/// <summary>Contracts 4.4 input normalization of COMMAREA-backed request fields.</summary>
public static class In
{
    /// <summary>PIC X(n): Windows-1252, right truncation; returned right-trimmed (the value SQLIO would bind).</summary>
    public static string X(string? value, int n) => CobolText.Trimmed(value, n);

    /// <summary>PIC S9(i)V9(s): rounded half away from zero to the scale, high-order truncation.</summary>
    public static decimal N(decimal? value, int intDigits, int scale, bool signed = true) =>
        CobolNumeric.Normalize(value ?? 0m, intDigits + scale, scale, signed);

    /// <summary>PIC 9(8) date: yyyymmdd, 0 when absent.</summary>
    public static int D(DateOnly? value) => CobolDate.ToYyyymmdd(value);

    /// <summary>A date carried as text in a request (yyyy-MM-dd); null when blank or invalid.</summary>
    public static DateOnly? Date(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d : null;
    }
}
