using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Itm.Services;

/// <summary>
/// ITMDATE's DATECALC call (ITMDATE.cob:48-62) on the PLATFORM kernel DATECALC (<see cref="IBusinessCalendar"/>):
/// INITIALIZE WS-DATECALC, function = ITD-FUNC, company STC, user ITM, DC-DATE-1/2, DC-NUMBER, input format YYYYMMDD,
/// output format DD/MM/YYYY, blank calendar.
/// </summary>
internal sealed class KernelItmCalendar(IBusinessCalendar calendar) : IItmCalendar
{
    public async Task<(int Rc, string Message, int ResultDate, int ResultNum, string TextOut)> CallAsync(
        string function, int date1, int date2, int number, CancellationToken ct)
    {
        var area = new DatecalcArea();
        area.Header.Prepare(new KernelCaller("STC", "", "ITM", 0, ""), function);
        area.Date1 = date1;
        area.Date2 = date2;
        area.Number = number;
        area.FormatIn = "YYYYMMDD";
        area.FormatOut = "DD/MM/YYYY";
        await calendar.ExecuteAsync(area, ct);
        return (area.Header.ReturnCode, area.Header.Message, area.ResultDate, area.ResultNum, area.TextOut);
    }
}
