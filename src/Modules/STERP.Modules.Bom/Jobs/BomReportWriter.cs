using Sterp.Platform.Hosting.Jobs;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Bom.Jobs;

/// <summary>
/// The GnuCOBOL 3.2 Report Writer run-time (libcob/reportio.c: report_line, do_page_heading, do_page_footing,
/// cob_report_generate, cob_report_terminate) for the BOM listings: an RD with PAGE LIMIT, HEADING, FIRST DETAIL,
/// LAST DETAIL, the STRPTHDR page heading (absolute LINE 1-4 and 6), LINE PLUS body lines and CONTROLS ARE FINAL.
/// Reproduced as the legacy BOMRP010/BOMRP030 listings show them (captured from the legacy executables on the isolated
/// database): a page overflow found at the start of a GENERATE fills the page to line 59 (the counter is one ahead), an
/// overflow found inside a group adds the extra skip-to-end-of-page line, TERMINATE fills the last page to the PAGE LIMIT,
/// a CONTROL HEADING FINAL group is never printed, a TERMINATE without any GENERATE prints nothing, and pages are
/// separated by blank lines (no form feed). Same algorithm as the AR work stream's ArReportWriter (to be consolidated in
/// the kernel; see the Agent 08 BOM work-stream report).
/// </summary>
internal sealed class BomReportWriter(SpoolWriter spool, ReportHeading heading, int firstDetail, int lastDetail, int pageLimit = 60, int headingLine = 1)
{
    private static readonly int[] HeadingLineNumbers = [1, 2, 3, 4, 6];

    private int _line;
    private bool _incrLine = true;
    private bool _firstDetail = true;
    private bool _firstGenerate = true;
    private bool _inPageHeading;
    private bool _inPageFooting;

    /// <summary>PAGE-COUNTER.</summary>
    public int Page { get; private set; } = 1;

    /// <summary>One print line of a report group: LINE PLUS n and the 132-column text.</summary>
    public readonly record struct GroupLine(int Plus, string Text);

    public static GroupLine L(int plus, Report132.Line line) => new(plus, line.Text);

    public static GroupLine L(int plus, string text) => new(plus, text);

    /// <summary>A 132-column line by COLUMN (1-based) and field width.</summary>
    public static Report132.Line Cols() => new();

    /// <summary>GENERATE of a DETAIL group (CONTROLS ARE FINAL only: the FINAL control heading is never printed).</summary>
    public void Generate(params GroupLine[] lines)
    {
        Begin();
        Detail(lines);
        End();
    }

    /// <summary>
    /// Start of GENERATE: the first one prints the page heading; later ones advance the page when the previous detail ended
    /// past LAST DETAIL.
    /// </summary>
    private void Begin()
    {
        if (_incrLine)
        {
            _incrLine = false;
            _line++;
        }
        if (_firstGenerate)
        {
            DoPageHeading();
            return;
        }
        if (_line > lastDetail)
        {
            DoPageFooting();
            _line = 1;
            DoPageHeading();
            _firstDetail = false;
        }
        else if (_line <= 1 || _firstDetail)
        {
            if (_firstDetail) _line = 1;
            DoPageHeading();
            _firstDetail = false;
        }
    }

    /// <summary>A CONTROL FOOTING group (report_line_and: the 01 entry, then each LINE).</summary>
    public void Group(params GroupLine[] lines)
    {
        ReportLine(null, null);
        foreach (var l in lines) ReportLine(l.Plus, l.Text);
    }

    /// <summary>The DETAIL group of the GENERATE (a group of several lines is kept together when its LINE PLUS &gt; 1 fit).</summary>
    private void Detail(GroupLine[] lines)
    {
        if (lines.Length > 1)
        {
            var plus = lines.Where(l => l.Plus > 1).Sum(l => l.Plus);
            if (_line + plus > lastDetail)
            {
                DoPageFooting();
                _line = 1;
                DoPageHeading();
                _firstDetail = false;
            }
        }
        foreach (var l in lines) ReportLine(l.Plus, l.Text);
    }

    private void End()
    {
        _firstGenerate = false;
        _line--;
        _incrLine = true;
    }

    /// <summary>TERMINATE: the control footing FINAL (caller) and the page fill; nothing when no GENERATE ran.</summary>
    public void Terminate(Action footings)
    {
        if (_firstGenerate) return;
        footings();
        DoPageFooting();
    }

    private void WriteRec(string text, int lines)
    {
        spool.WriteLine(text);
        for (var i = 1; i < lines; i++) spool.WriteLine("");
    }

    private void DoPageFooting()
    {
        if (_inPageFooting) return;
        _inPageFooting = true;
        if (_line < pageLimit)
        {
            WriteRec("", pageLimit - _line);
            _line = pageLimit;
            _incrLine = false;
        }
        else
        {
            _line = 1;
        }
        _firstDetail = true;
        _inPageFooting = false;
    }

    private void DoPageHeading()
    {
        if (_inPageHeading) return;
        if (!_firstGenerate && pageLimit > 0 && headingLine > 0 && _line <= pageLimit && _line > headingLine)
        {
            while (_line <= pageLimit)
            {
                WriteRec("", 1);
                _line++;
            }
            if (_line > pageLimit) _line = 1;
        }
        _inPageHeading = true;
        if (!_firstGenerate) Page++;
        _firstDetail = false;
        while (_line < headingLine)
        {
            WriteRec("", 1);
            _line++;
        }
        var texts = Report132.HeadingLines(heading, Page);
        ReportLine(null, null);
        for (var i = 0; i < HeadingLineNumbers.Length; i++)
        {
            AbsoluteLine(HeadingLineNumbers[i], texts[i == 4 ? 5 : i]);
        }
        while (_line < firstDetail)
        {
            WriteRec("", 1);
            _line++;
        }
        _inPageHeading = false;
    }

    private void AbsoluteLine(int lineNo, string text)
    {
        if (_line > lastDetail && !_inPageFooting)
        {
            DoPageFooting();
            DoPageHeading();
        }
        if (_line > lineNo)
        {
            DoPageFooting();
            DoPageHeading();
            _firstDetail = false;
        }
        while (_line < lineNo)
        {
            WriteRec("", 1);
            _line++;
        }
        WriteRec(text, 1);
        _line++;
    }

    /// <summary>report_line for a LINE PLUS line (plus null = a group entry without fields or LINE clause).</summary>
    private void ReportLine(int? plus, string? text)
    {
        if (_line > lastDetail && !_inPageFooting)
        {
            DoPageFooting();
            DoPageHeading();
        }
        if (plus > 1 && (_line != firstDetail || firstDetail == 0))
        {
            WriteRec("", plus.Value - 1);
            _line += plus.Value - 1;
        }
        if (_line > lastDetail && !_inPageHeading && !_inPageFooting)
        {
            DoPageFooting();
            DoPageHeading();
        }
        if (text is null) return;
        WriteRec(text, 1);
        _line++;
    }
}
