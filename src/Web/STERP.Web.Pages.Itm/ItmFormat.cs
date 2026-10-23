using System.Globalization;

namespace Sterp.Web.Pages.Itm;

/// <summary>Page-local presentation helpers of the ITM pages (legacy STERP.Common.Fmt where the framework has no equivalent).</summary>
internal static class ItmFormat
{
    private static readonly string[] Formats = ["dd/MM/yyyy", "dd/MM/yy", "ddMMyyyy", "yyyyMMdd", "dd-MM-yyyy", "dd.MM.yyyy"];

    /// <summary>en-GB date patterns (the client runs with invariant globalization, so the culture itself is not available).</summary>
    private static readonly DateTimeFormatInfo Uk = CreateUk();

    /// <summary>
    /// Fmt.ParseDate (Fmt.cs:93-101): the six exact formats, then DateTime.TryParse in en-GB (day before month, month names,
    /// a time alone is today); null when the text is not a date. StockService.AsDate then falls back to the business date.
    /// </summary>
    public static DateOnly? ParseDate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var s = text.Trim();
        if (DateTime.TryParseExact(s, Formats, Uk, DateTimeStyles.None, out var d) || DateTime.TryParse(s, Uk, DateTimeStyles.None, out d))
        {
            return DateOnly.FromDateTime(d);
        }
        return null;
    }

    private static DateTimeFormatInfo CreateUk()
    {
        var f = (DateTimeFormatInfo)CultureInfo.InvariantCulture.DateTimeFormat.Clone();
        f.ShortDatePattern = "dd/MM/yyyy";
        f.LongDatePattern = "dd MMMM yyyy";
        f.ShortTimePattern = "HH:mm";
        f.LongTimePattern = "HH:mm:ss";
        f.FullDateTimePattern = "dd MMMM yyyy HH:mm:ss";
        f.MonthDayPattern = "d MMMM";
        f.YearMonthPattern = "MMMM yyyy";
        f.AMDesignator = "am";
        f.PMDesignator = "pm";
        return f;
    }
}
