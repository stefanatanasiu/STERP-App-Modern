namespace Sterp.Modules.Itm.Services;

/// <summary>ITD-BLOCK (cobol/copy/ITMDATEW.cpy): the ITMDATE call block a program keeps in its working storage.</summary>
internal sealed class ItdBlock
{
    public string Func { get; set; } = "";
    public int Date1 { get; set; }
    public int Date2 { get; set; }
    public int Number { get; set; }
    public int DateR { get; set; }
    public int NumR { get; set; }
    public string Text { get; set; } = "";
    public int Rc { get; set; }
    public string Msg { get; set; } = "";
}

/// <summary>
/// The DATECALC call ITMDATE makes for every function except TODAY (ITMDATE.cob:48-62): company STC, user ITM, input
/// format YYYYMMDD, output format DD/MM/YYYY, blank calendar. Implemented over the PLATFORM DATECALC kernel service.
/// </summary>
internal interface IItmCalendar
{
    /// <summary>Returns RC, message, result date (yyyymmdd), result number and text out.</summary>
    Task<(int Rc, string Message, int ResultDate, int ResultNum, string TextOut)> CallAsync(string function, int date1, int date2, int number, CancellationToken ct);
}

/// <summary>
/// ITMDATE (cobol/online/itm/ITMDATE.cob; FR-STERP-ITM-026): TODAY answers the host system date with RC 00; every other
/// function is forwarded to DATECALC and its RC, message, result date, number and text are passed back unchanged.
/// </summary>
internal sealed class ItmDateProgram(IItmCalendar calendar)
{
    public async Task CallAsync(ItdBlock b, CancellationToken ct)
    {
        b.Rc = 0;
        b.NumR = 0;
        b.Msg = "";
        if (b.Func == "TODAY")
        {
            b.DateR = Cob.Today();
            return;
        }
        var (rc, msg, date, num, text) = await calendar.CallAsync(Cob.X(b.Func, 8), b.Date1, b.Date2, b.Number, ct);
        b.Rc = rc % 100;
        b.Msg = Cob.X(msg, 80);
        b.DateR = date;
        b.NumR = num;
        b.Text = Cob.X(text, 10);
    }
}
