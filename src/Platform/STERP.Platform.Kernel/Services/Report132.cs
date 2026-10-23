using System.Globalization;
using System.Text;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Platform.Kernel.Services;

/// <summary>STRPTWS RPT-HEAD-WORK: the working-storage half of the 132-column page heading (STRPTWS.cpy:20-43).</summary>
public sealed class ReportHeading
{
    /// <summary>RPT-COMPANY-CODE X(10).</summary>
    public string CompanyCode { get; set; } = "";

    /// <summary>RPT-COMPANY-NAME X(40).</summary>
    public string CompanyName { get; set; } = "";

    /// <summary>RPT-REG-NUMBER X(10).</summary>
    public string RegisteredNumber { get; set; } = "";

    /// <summary>RPT-REG-PLACE X(24), VALUE "England and Wales".</summary>
    public string RegisteredPlace { get; set; } = "England and Wales";

    /// <summary>RPT-REPORT-ID X(12) (= job name).</summary>
    public string ReportId { get; set; } = "";

    /// <summary>RPT-TITLE X(60).</summary>
    public string Title { get; set; } = "";

    /// <summary>RPT-SUBTITLE X(60).</summary>
    public string Subtitle { get; set; } = "";

    /// <summary>RPT-AS-AT-TEXT X(30).</summary>
    public string AsAtText { get; set; } = "";

    /// <summary>RPT-USER X(20).</summary>
    public string User { get; set; } = "";

    /// <summary>RPT-STATUS-TEXT X(20) (not printed by STRPTHDR).</summary>
    public string StatusText { get; set; } = "";

    /// <summary>RPT-RUN-DATE / RPT-RUN-TIME source: FUNCTION CURRENT-DATE, the host clock (not the as-at date).</summary>
    public DateTime RunAt { get; set; }

    /// <summary>RPT-RUN-DATE dd/mm/yyyy.</summary>
    public string RunDate => RunAt.ToString("dd'/'MM'/'yyyy", CultureInfo.InvariantCulture);

    /// <summary>RPT-RUN-TIME hh:mm:ss.</summary>
    public string RunTime => RunAt.ToString("HH':'mm':'ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// STRPTHDR - the standard 132-column Report Writer page heading (STRPTHDR.cpy:34-58, FR-STERP-PLATFORM-028 b):
/// line 1 company name (40) at column 1, report id (12) at 50, "Page " at 112, page number ZZZ9 at 117;
/// line 2 "Registered number: " + number (10) at 20, "Place of registration: " (X(25)) at 32, place (24) at 57;
/// line 3 title (60), "Run date " at 104, dd/mm/yyyy at 113; line 4 subtitle (60), as-at text (30) at 70, user (20) at 104,
/// run time (8) at 125; line 5 blank; line 6 132 dashes. Details start on line 7.
/// </summary>
public static class Report132
{
    public const int Width = 132;

    /// <summary>The six heading lines of page <paramref name="page"/> (trailing spaces removed, as the listing stores them).</summary>
    public static IReadOnlyList<string> HeadingLines(ReportHeading h, int page)
    {
        var l1 = new Line();
        l1.Put(1, h.CompanyName, 40);
        l1.Put(50, h.ReportId, 12);
        l1.Put(112, "Page ", 5);
        l1.Put(117, CobolEdit.Field(page, "ZZZ9"), 4);

        var l2 = new Line();
        l2.Put(1, "Registered number: ", 19);
        l2.Put(20, h.RegisteredNumber, 10);
        l2.Put(32, "Place of registration: ", 25);
        l2.Put(57, h.RegisteredPlace, 24);

        var l3 = new Line();
        l3.Put(1, h.Title, 60);
        l3.Put(104, "Run date ", 9);
        l3.Put(113, h.RunDate, 10);

        var l4 = new Line();
        l4.Put(1, h.Subtitle, 60);
        l4.Put(70, h.AsAtText, 30);
        l4.Put(104, h.User, 20);
        l4.Put(125, h.RunTime, 8);

        return [l1.Text, l2.Text, l3.Text, l4.Text, "", new string('-', Width)];
    }

    /// <summary>A 132-column print line built by COLUMN position (1-based) and field width (PIC X(n) semantics).</summary>
    public sealed class Line
    {
        private readonly char[] _chars = Enumerable.Repeat(' ', Width).ToArray();

        public Line Put(int column, string? value, int width)
        {
            var text = CobolText.Fit(value, width);
            for (var i = 0; i < width && column - 1 + i < Width; i++) _chars[column - 1 + i] = text[i];
            return this;
        }

        public string Text => new string(_chars).TrimEnd(' ');
    }
}

/// <summary>
/// A Report Writer page: the STRPTHDR heading on lines 1-6 of every page, details from FIRST DETAIL (7) to LAST DETAIL,
/// a form feed before every page after the first (the convention of the hosting SpoolWriter). The sink receives
/// complete lines; <paramref name="pageBreak"/> writes the page separator.
/// </summary>
public sealed class Report132Writer(Action<string> writeLine, Action pageBreak, ReportHeading heading, int firstDetail = 7, int lastDetail = 58)
{
    /// <summary>PAGE-COUNTER (0 before the first line).</summary>
    public int Page { get; private set; }

    /// <summary>LINE-COUNTER: the last line written on the current page.</summary>
    public int Line { get; private set; }

    public ReportHeading Heading => heading;

    /// <summary>Starts a new page with its heading (the first page has no form feed).</summary>
    public void NewPage()
    {
        if (Page > 0) pageBreak();
        Page++;
        foreach (var l in Report132.HeadingLines(heading, Page)) writeLine(l);
        Line = 6;
        while (Line < firstDetail - 1)
        {
            writeLine("");
            Line++;
        }
    }

    /// <summary>A detail group of one or more lines kept on one page (GENERATE of a detail line).</summary>
    public void Detail(params string[] lines)
    {
        if (Page == 0 || Line + lines.Length > lastDetail) NewPage();
        foreach (var l in lines)
        {
            writeLine(l.TrimEnd(' '));
            Line++;
        }
    }

    /// <summary>Blank lines (LINE PLUS n spacing) within the page body.</summary>
    public void Blank(int count = 1)
    {
        if (Page == 0) NewPage();
        for (var i = 0; i < count && Line < lastDetail; i++)
        {
            writeLine("");
            Line++;
        }
    }

    /// <summary>Builds a detail line by column (1-based) and width.</summary>
    public static Report132.Line Columns() => new();

    /// <summary>The page heading text as one block (tests and previews).</summary>
    public static string HeadingBlock(ReportHeading h, int page)
    {
        var sb = new StringBuilder();
        foreach (var l in Report132.HeadingLines(h, page)) sb.Append(l).Append("\r\n");
        return sb.ToString();
    }
}
