using System.Text;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Platform.Kernel.Reporting;

/// <summary>
/// One LINE of a report group: <c>LINE PLUS n</c> (relative) or <c>LINE n</c> (absolute) with its print text, evaluated when
/// the line is presented (Report Writer moves the SOURCE fields at print time).
/// </summary>
public sealed record RwLine(int Number, bool Plus, Func<string> Text)
{
    public static RwLine Plus1(Func<string> text) => new(1, true, text);

    public static RwLine PlusN(int n, Func<string> text) => new(n, true, text);

    /// <summary>An absolute LINE n entry.</summary>
    public static RwLine At(int n, Func<string> text) => new(n, false, text);
}

/// <summary>A CONTROL field of the RD: its current value (get/set - the footing presents the previous value) and groups.</summary>
public sealed class RwControl(Func<string> get, Action<string> set, int width)
{
    public Func<string> Get { get; } = get;

    public Action<string> Set { get; } = set;

    public int Width { get; } = width;

    public RwLine[]? Heading { get; init; }

    public RwLine[]? Footing { get; init; }

    internal string Saved { get; set; } = "";
}

/// <summary>A report line built by COLUMN (1-based) and PIC width; the record is as wide as its last column (no 132 cut).</summary>
public sealed class RwText
{
    private readonly StringBuilder _chars = new();

    public RwText Put(int column, string? value, int width)
    {
        var text = CobolText.Fit(value, width);
        var end = column - 1 + width;
        if (_chars.Length < end) _chars.Append(' ', end - _chars.Length);
        for (var i = 0; i < width; i++) _chars[column - 1 + i] = text[i];
        return this;
    }

    /// <summary>A numeric edited field.</summary>
    public RwText Num(int column, decimal value, string picture)
    {
        var text = CobolEdit.Field(value, picture);
        return Put(column, text, text.Length);
    }

    /// <summary>A VALUE literal: the runtime copies the whole literal whatever the PIC length (reportio.c report_line).</summary>
    public RwText Lit(int column, string literal) => Put(column, literal, literal.Length);

    /// <summary>
    /// A SOURCE item as reportio.c print_field places it: the value moved into its PIC X(width) and copied without its trailing
    /// spaces (cob_field_to_string), so it does not blank an overlapping column (ITM listings, oracle-verified).
    /// </summary>
    public RwText Src(int column, string? value, int width)
    {
        var v = CobolText.Fit(value, width).TrimEnd(' ');
        return v.Length == 0 ? this : Put(column, v, v.Length);
    }

    /// <summary>A numeric edited SOURCE or SUM item, copied without trailing spaces.</summary>
    public RwText Ed(int column, decimal value, string picture)
    {
        var v = CobolEdit.Field(value, picture).TrimEnd(' ');
        return v.Length == 0 ? this : Put(column, v, v.Length);
    }

    public string Text => _chars.ToString().TrimEnd(' ');

    public override string ToString() => Text;
}

/// <summary>
/// The GnuCOBOL 3.2 Report Writer runtime (libcob/reportio.c: cob_report_initiate, cob_report_generate, cob_report_terminate,
/// report_line, do_page_heading, do_page_footing) shared by every legacy Report Writer listing, written to a LINE SEQUENTIAL file: every record is
/// written BEFORE ADVANCING n LINES (trailing spaces removed, n line ends, no form feed). It keeps the runtime's line
/// counter exactly, including the behaviour seen on the legacy listings: the first body group of a page starts on FIRST
/// DETAIL whatever its LINE PLUS; every line is tested against LAST DETAIL only (control footings included); an overflow at
/// the start of a GENERATE pads the page to PAGE LIMIT - 1 lines while an overflow inside a group pads it to PAGE LIMIT; the
/// TERMINATE footings run one line behind the physical position (no line increment), so the last page is padded to PAGE
/// LIMIT lines; TERMINATE without any GENERATE prints nothing (an empty listing).
/// </summary>
public sealed class GnuReportWriter
{
    private readonly Action<string> _writeLine;
    private readonly ReportHeading _heading;
    private readonly int _lines;
    private readonly int _headingLine;
    private readonly int _firstDetail;
    private readonly int _lastDetail;
    private readonly IReadOnlyList<RwControl> _controls;
    private readonly RwLine[]? _finalFooting;

    private int _curr;
    private bool _incrLine;
    private bool _firstGenerate;
    private bool _firstDetailFlag;
    private bool _inPageHeading;
    private bool _inPageFooting;
    private readonly RwLine[]? _reportHeading;
    private readonly RwLine[]? _pageHeading;

    /// <param name="reportHeading">TYPE REPORT HEADING group: presented by the first GENERATE before the page heading
    /// (report_line_type COB_REPORT_HEADING); an absolute LINE of it can leave the line counter past the STRPTHDR LINE 1, so
    /// that page heading starts with a page eject inside itself.</param>
    /// <param name="pageHeading">a second TYPE PAGE HEADING group after STRPTHDR (do_page_heading presents every PAGE HEADING
    /// group in source order).</param>
    public GnuReportWriter(Action<string> writeLine, ReportHeading heading, int pageLimit, int headingLine, int firstDetail, int lastDetail,
        IReadOnlyList<RwControl>? controls = null, RwLine[]? finalFooting = null, RwLine[]? reportHeading = null, RwLine[]? pageHeading = null)
    {
        _writeLine = writeLine;
        _heading = heading;
        _lines = pageLimit;
        _headingLine = headingLine;
        _firstDetail = firstDetail;
        _lastDetail = lastDetail;
        _controls = controls ?? [];
        _finalFooting = finalFooting;
        _reportHeading = reportHeading;
        _pageHeading = pageHeading;
    }

    /// <summary>PAGE-COUNTER.</summary>
    public int Page { get; private set; }

    /// <summary>True once a GENERATE has run (TERMINATE then presents the footings).</summary>
    public bool Generated => !_firstGenerate;

    /// <summary>INITIATE.</summary>
    public void Initiate()
    {
        Page = 1;
        _curr = 0;
        _incrLine = true;
        _firstDetailFlag = true;
        _firstGenerate = true;
    }

    /// <summary>GENERATE detail-group (one or more LINE entries).</summary>
    public void Generate(params RwLine[] detail)
    {
        if (_incrLine)
        {
            _incrLine = false;
            _curr++;
        }
        if (_firstGenerate)
        {
            if (_reportHeading is { } rh) Group(rh);
            PageHeading();
            foreach (var c in _controls)
            {
                if (c.Heading is { } h) Group(h);
                c.Saved = CobolText.Fit(c.Get(), c.Width);
            }
        }
        else
        {
            if (_curr > _lastDetail)
            {
                PageFooting();
                _curr = 1;
                PageHeading();
                _firstDetailFlag = false;
            }
            else if (_curr <= 1 || _firstDetailFlag)
            {
                if (_firstDetailFlag) _curr = 1;
                PageHeading();
                _firstDetailFlag = false;
            }
            ControlBreaks();
        }

        if (detail.Length > 1)
        {
            var ln = detail.Where(l => l.Plus && l.Number > 1).Sum(l => l.Number);
            if (_curr + ln > _lastDetail)
            {
                PageFooting();
                _curr = 1;
                PageHeading();
                _firstDetailFlag = false;
            }
        }
        foreach (var l in detail) Line(l);

        _firstGenerate = false;
        _curr--;
        _incrLine = true;
    }

    /// <summary>TERMINATE: the control footings (current values), CONTROL FOOTING FINAL and the last page footing.</summary>
    public void Terminate()
    {
        if (_firstGenerate) return;
        foreach (var c in _controls)
        {
            if (c.Footing is { } f) Group(f);
        }
        if (_finalFooting is { } ff) Group(ff);
        PageFooting();
    }

    /// <summary>
    /// A changed control presents its footing with the previous value moved back into the field, then the new value is
    /// put back and its heading presented (the FIN listings have one control below FINAL; a list is broken from the first
    /// changed entry down, in list order).
    /// </summary>
    private void ControlBreaks()
    {
        var first = -1;
        for (var i = 0; i < _controls.Count && first < 0; i++)
        {
            if (CobolText.Fit(_controls[i].Get(), _controls[i].Width) != _controls[i].Saved) first = i;
        }
        if (first < 0) return;
        var fresh = new string[_controls.Count];
        for (var i = first; i < _controls.Count; i++)
        {
            fresh[i] = CobolText.Fit(_controls[i].Get(), _controls[i].Width);
            _controls[i].Set(_controls[i].Saved);
        }
        for (var i = first; i < _controls.Count; i++)
        {
            if (_controls[i].Footing is { } f) Group(f);
            _controls[i].Set(fresh[i]);
        }
        for (var i = first; i < _controls.Count; i++)
        {
            if (_controls[i].Heading is { } h) Group(h);
            _controls[i].Saved = CobolText.Fit(_controls[i].Get(), _controls[i].Width);
        }
    }

    /// <summary>A control heading or footing: the 01 entry (page overflow test only) then each of its LINE entries.</summary>
    private void Group(RwLine[] lines)
    {
        if (_curr > _lastDetail && !_inPageFooting)
        {
            PageFooting();
            PageHeading();
        }
        foreach (var l in lines) Line(l);
    }

    /// <summary>report_line for one LINE entry (relative or absolute).</summary>
    private void Line(RwLine l)
    {
        if (_curr > _lastDetail && !_inPageFooting)
        {
            PageFooting();
            PageHeading();
        }
        if (!l.Plus)
        {
            if (_curr > l.Number)
            {
                PageFooting();
                PageHeading();
                _firstDetailFlag = false;
            }
            while (_curr < l.Number)
            {
                Write("", 1);
                _curr++;
            }
        }
        else if (l.Number > 1 && (_curr != _firstDetail || _firstDetail == 0))
        {
            Write("", l.Number - 1);
            _curr += l.Number - 1;
        }
        if (_curr > _lastDetail && !_inPageHeading && !_inPageFooting)
        {
            PageFooting();
            PageHeading();
        }
        Write(l.Text(), 1);
        _curr++;
    }

    private void PageFooting()
    {
        if (_inPageFooting) return;
        _inPageFooting = true;
        if (_curr < _lines)
        {
            Write("", _lines - _curr);
            _curr = _lines;
            _incrLine = false;
        }
        else
        {
            _curr = 1;
        }
        _firstDetailFlag = true;
        _inPageFooting = false;
    }

    private void PageHeading()
    {
        if (_inPageHeading) return;
        if (!_firstGenerate && _lines > 0 && _headingLine > 0 && _curr <= _lines && _curr > _headingLine)
        {
            while (_curr <= _lines)
            {
                Write("", 1);
                _curr++;
            }
            if (_curr > _lines) _curr = 1;
        }
        _inPageHeading = true;
        if (!_firstGenerate) Page++;
        _firstDetailFlag = false;
        while (_curr < _headingLine)
        {
            Write("", 1);
            _curr++;
        }
        var text = Report132.HeadingLines(_heading, Page);
        if (_reportHeading is null && _pageHeading is null)
        {
            Line(new RwLine(1, false, () => text[0]));
            Line(new RwLine(2, false, () => text[1]));
            Line(new RwLine(3, false, () => text[2]));
            Line(new RwLine(4, false, () => text[3]));
            Line(new RwLine(6, false, () => text[5]));
        }
        else
        {
            // RDs with a REPORT HEADING or a second PAGE HEADING present STRPTHDR as a group (ITM listings, oracle-verified)
            Group(
            [
                new RwLine(1, false, () => text[0]),
                new RwLine(2, false, () => text[1]),
                new RwLine(3, false, () => text[2]),
                new RwLine(4, false, () => text[3]),
                new RwLine(6, false, () => text[5]),
            ]);
            if (_pageHeading is { } ph) Group(ph);
        }
        while (_curr < _firstDetail)
        {
            Write("", 1);
            _curr++;
        }
        _inPageHeading = false;
    }

    /// <summary>WRITE ... BEFORE ADVANCING <paramref name="advance"/> LINES on a LINE SEQUENTIAL file.</summary>
    private void Write(string text, int advance)
    {
        _writeLine(text.TrimEnd(' '));
        for (var i = 1; i < advance; i++) _writeLine("");
    }
}
