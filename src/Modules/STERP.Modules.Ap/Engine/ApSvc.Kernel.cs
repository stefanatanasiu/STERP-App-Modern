using Microsoft.Extensions.DependencyInjection;
using Sterp.Platform.Kernel.Cobol;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ap.Engine;

/// <summary>
/// APSVC bridges to the PLATFORM kernel services, one per APSVC paragraph (cobol/online/ap/APSVC.cob:97-460): the kernel
/// COMMAREA is initialised, filled from APSVCARE exactly as the paragraph did, called, and its answer copied back.
/// </summary>
public sealed partial class ApSvc
{
    private KernelCaller Caller(ApSvcArea a, bool site = true, bool correlation = true) =>
        new(a.Company, site ? a.Site : "", a.User, a.Date, correlation ? a.Correlation : "");

    private static Task NotOnRoute(string function) =>
        throw new NotSupportedException($"APSVC {function} is not reached by an implemented STERP-AP path.");

    // 1000-PERM -> PERMCHK CHECK
    private async Task PermAsync(ApSvcArea a, CancellationToken ct)
    {
        var p = new PermchkArea();
        p.Header.Prepare(Caller(a, correlation: false), "CHECK");
        p.Permission = CobolText.Trimmed(a.Text1, 60);
        if (!CobolText.IsSpaces(a.Text2)) p.CheckUser = CobolText.Trimmed(a.Text2, 60);
        await Services.GetRequiredService<IPermissionService>().ExecuteAsync(p, ct);
        a.Flag1 = CobolText.Trimmed(p.Allowed, 1);
        a.Rc = p.Header.ReturnCode;
        a.Reason = CobolText.Trimmed(p.Header.ReasonCode, 8);
        a.Message = CobolText.Trimmed(p.Header.Message, 200);
    }

    // 1100-PARM -> PARMGET GETNUM / GET (COM-025: a missing parameter returns the caller's default with RC 04)
    private async Task ParmAsync(ApSvcArea a, CancellationToken ct)
    {
        var p = new ParmgetArea();
        var numeric = a.Func == "PARMNUM";
        p.Header.Prepare(new KernelCaller(a.Company, "", a.User, a.Date, ""), numeric ? "GETNUM" : "GET");
        p.ParamCode = CobolText.Trimmed(a.Text1, 60);
        p.AsOf = a.Date;
        p.UseDefault = "Y";
        p.DefaultText = numeric ? CobolNumeric.SqlioNumber(CobolNumeric.Store(a.Num1, 15, 6)) : CobolText.Trimmed(a.Text2, 60);
        await Services.GetRequiredService<IParameterService>().ExecuteAsync(p, ct);
        a.TextOut = CobolText.Trimmed(p.ValueText, 200);
        a.NumOut = CobolNumeric.Store(p.ValueNum, 13, 6);
        a.Flag1 = CobolText.Trimmed(p.Found, 1);
        a.Rc = p.Header.ReturnCode;
        a.Reason = CobolText.Trimmed(p.Header.ReasonCode, 8);
        if (a.Rc > 4) a.Message = CobolText.Trimmed(p.Header.Message, 200);
        if (numeric && p.Found != "Y")
        {
            a.NumOut = a.Num1;
            a.Rc = 4;
        }
    }

    // 1200-AUDIT -> AUDITLOG WRITE (source COBOL)
    private async Task AuditAsync(ApSvcArea a, CancellationToken ct)
    {
        var p = new AuditlogArea();
        p.Header.Prepare(Caller(a), "WRITE");
        p.EntityType = CobolText.Trimmed(a.AudEntity, 40);
        p.EntityKey = CobolText.Trimmed(a.AudKey, 60);
        p.Action = CobolText.Trimmed(a.AudAction, 20);
        p.FieldName = CobolText.Trimmed(a.AudField, 40);
        p.OldValue = CobolText.Trimmed(a.AudOld, 100);
        p.NewValue = CobolText.Trimmed(a.AudNew, 100);
        p.Reason = CobolText.Trimmed(a.AudReason, 200);
        p.AuthorityRef = CobolText.Trimmed(a.AudAuth, 40);
        p.Source = "COBOL";
        p.Sensitive = a.AudSens == "Y" ? "Y" : "N";
        await Services.GetRequiredService<IAuditWriter>().ExecuteAsync(p, ct);
        a.Rc = p.Header.ReturnCode;
        a.Reason = CobolText.Trimmed(p.Header.ReasonCode, 8);
        if (a.Rc > 4) a.Message = CobolText.Trimmed(p.Header.Message, 200);
    }

    // 1300-EXCEPT -> EXCPRAIS RAISE (only APPAYRUN OUTCOME and the deferred programs raise exceptions)
    private Task ExceptAsync(ApSvcArea a, CancellationToken ct) => NotOnRoute("EXCEPT");

    // 1400-NUMBER -> NUMGEN NEXT
    private async Task NumberAsync(ApSvcArea a, CancellationToken ct)
    {
        var p = new NumgenArea();
        p.Header.Prepare(new KernelCaller(a.Company, "", a.User, a.Date, a.Correlation), "NEXT");
        p.SeriesCode = CobolText.Trimmed(a.Text1, 60);
        await Services.GetRequiredService<IDocumentNumberService>().ExecuteAsync(p, ct);
        a.TextOut = CobolText.Trimmed(p.DocumentNumber, 200);
        a.Rc = p.Header.ReturnCode;
        a.Reason = CobolText.Trimmed(p.Header.ReasonCode, 8);
        a.Message = CobolText.Trimmed(p.Header.Message, 200);
    }

    // 1500-PERIOD -> PERIODCK CHECK (not reached by an implemented path)
    private Task PeriodAsync(ApSvcArea a, CancellationToken ct) => NotOnRoute("PERIOD");

    // 1600-TERMS -> TERMCALC DUEDATE (APINVREG REGISTER only, no production route)
    private Task TermsAsync(ApSvcArea a, CancellationToken ct) => NotOnRoute("TERMS");

    // 1700-FXGBP / 1800-FXREAL -> FXCONV (not reached by an implemented path)
    private Task FxToGbpAsync(ApSvcArea a, CancellationToken ct) => NotOnRoute("FXTOGBP");

    private Task FxRealAsync(ApSvcArea a, CancellationToken ct) => NotOnRoute("FXREAL");

    // 1900-NORMREF -> STRUTIL DOCREF (maximum 40; RC 04 "changed" answers 00)
    private Task NormRefAsync(ApSvcArea a, CancellationToken ct)
    {
        var p = new StrutilArea();
        p.Header.Prepare(new KernelCaller(a.Company, "", a.User, a.Date, ""), "DOCREF");
        p.TextIn = CobolText.Trimmed(a.Text3, 200);
        p.MaxLength = 40;
        CobolString.Execute(p);
        a.TextOut = CobolText.Trimmed(p.TextOut, 200);
        a.Flag1 = CobolText.Trimmed(p.Changed, 1);
        a.Flag2 = CobolText.Trimmed(p.Valid, 1);
        a.Rc = p.Header.ReturnCode;
        a.Reason = CobolText.Trimmed(p.Header.ReasonCode, 8);
        if (a.Rc > 4) a.Message = CobolText.Trimmed(p.Header.Message, 200);
        if (a.Rc == 4) a.Rc = 0;
        return Task.CompletedTask;
    }

    // 2000-ROUND -> RNDALLOC ROUND (APINVLIN only, no production route)
    private Task RoundAsync(ApSvcArea a, CancellationToken ct) => NotOnRoute("ROUND");

    // 2100-DATES -> DATECALC (calendar default ENG, direction default NEXT)
    private async Task DatesAsync(ApSvcArea a, CancellationToken ct)
    {
        var p = new DatecalcArea();
        var function = a.Func switch
        {
            "ADDDAYS" => "ADDDAYS",
            "DAYSBTWN" => "DAYSBTWN",
            "BUSDAY" => "ISBUSDAY",
            "ADJUST" => "ADJUST",
            "ADDBUS" => "ADDBUS",
            _ => "ADDMONTH",
        };
        p.Header.Prepare(new KernelCaller(a.Company, "", a.User, a.Date, ""), function);
        p.Date1 = a.Date1;
        p.Date2 = a.Date2;
        p.Number = a.Int1;
        p.Calendar = CobolText.IsSpaces(a.Text1) ? "ENG" : CobolText.Trimmed(a.Text1, 10);
        p.Direction = CobolText.IsSpaces(a.Text2) ? "NEXT" : CobolText.Trimmed(a.Text2, 10);
        await Services.GetRequiredService<IBusinessCalendar>().ExecuteAsync(p, ct);
        a.DateOut = p.ResultDate;
        a.IntOut = p.ResultNum;
        a.Flag1 = CobolText.Trimmed(p.IsBusiness, 1);
        a.Flag2 = CobolText.Trimmed(p.Adjusted, 1);
        a.TextOut = CobolText.Trimmed(p.HolidayName, 200);
        a.Rc = p.Header.ReturnCode;
        a.Reason = CobolText.Trimmed(p.Header.ReasonCode, 8);
        if (a.Rc > 4) a.Message = CobolText.Trimmed(p.Header.Message, 200);
    }

    // 2200-IDEM -> IDEMPOT (not reached by an implemented path)
    private Task IdemAsync(ApSvcArea a, CancellationToken ct) => NotOnRoute("IDEM");
}
