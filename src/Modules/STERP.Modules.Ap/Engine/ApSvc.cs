namespace Sterp.Modules.Ap.Engine;

/// <summary>
/// APSVC (cobol/online/ap/APSVC.cob; FR-STERP-AP-050, spec 4.12): the purchase ledger's single route to the common services.
/// Each function maps the APSVCARE fields onto the PLATFORM kernel service of the legacy program it called (PERMCHK, PARMGET,
/// AUDITLOG, EXCPRAIS, NUMGEN, PERIODCK, TERMCALC, FXCONV, STRUTIL, RNDALLOC, DATECALC, IDEMPOT) and copies the results back
/// exactly as the COBOL paragraphs did. Stateless; the kernel services run on the caller's unit of work.
/// </summary>
public sealed partial class ApSvc(IServiceProvider services)
{
    public IServiceProvider Services { get; } = services;

    /// <summary>CALL "APSVC" USING WS-APSVC.</summary>
    public async Task CallAsync(ApSvcArea a, CancellationToken ct = default)
    {
        a.Rc = 0;
        a.Reason = "";
        a.Message = "";
        switch (a.Func)
        {
            case "PERM": await PermAsync(a, ct); break;
            case "PARMNUM" or "PARMTXT": await ParmAsync(a, ct); break;
            case "AUDIT": await AuditAsync(a, ct); break;
            case "EXCEPT": await ExceptAsync(a, ct); break;
            case "NUMBER": await NumberAsync(a, ct); break;
            case "PERIOD": await PeriodAsync(a, ct); break;
            case "TERMS": await TermsAsync(a, ct); break;
            case "FXTOGBP": await FxToGbpAsync(a, ct); break;
            case "FXREAL": await FxRealAsync(a, ct); break;
            case "NORMREF": await NormRefAsync(a, ct); break;
            case "ROUND": await RoundAsync(a, ct); break;
            case "ADDDAYS" or "DAYSBTWN" or "BUSDAY" or "ADJUST" or "ADDBUS" or "ADDMONTH": await DatesAsync(a, ct); break;
            case "IDEMBEG" or "IDEMEND": await IdemAsync(a, ct); break;
            default:
                a.Rc = 8;
                a.Reason = "APV0099";
                a.Message = "Unsupported purchase ledger service request";
                break;
        }
    }

    /// <summary>Convenience for 8000-CHECK-PERM style callers: PERM for the permission code; SVC-FLAG-1 = "Y" when allowed.</summary>
    public async Task<ApSvcArea> PermAsync(CaHeader ca, string permission, CancellationToken ct = default)
    {
        var a = ApSvcArea.For(ca, "PERM");
        a.Text1 = permission;
        await CallAsync(a, ct);
        return a;
    }

    /// <summary>PARMNUM with the caller's default (SVC-NUM-1); SVC-NUM-OUT is the value.</summary>
    public async Task<ApSvcArea> ParmNumAsync(CaHeader ca, string code, decimal defaultValue, CancellationToken ct = default)
    {
        var a = ApSvcArea.For(ca, "PARMNUM");
        a.Text1 = code;
        a.Num1 = defaultValue;
        await CallAsync(a, ct);
        return a;
    }
}
