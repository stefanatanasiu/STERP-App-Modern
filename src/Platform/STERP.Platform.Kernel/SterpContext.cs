namespace Sterp.Platform.Kernel;

/// <summary>One row of core.usp_GetUserContext result set 2 (the legacy sign-on access list).</summary>
public sealed record CompanySiteAccess(
    string CompanyCode,
    string CompanyName,
    string SiteCode,
    string SiteName,
    string? Nation,
    string? CalendarCode,
    string? BaseCurrency);

/// <summary>core.usp_GetUserContext result set 1 plus the access list (dotnet/STERP.Business/System/LoginService.vb:23-74).</summary>
public sealed record SterpUser(
    string UserCode,
    string DisplayName,
    string? WindowsLogin,
    string? EmailAddress,
    string? JobTitle,
    string? DefaultCompanyCode,
    string? DefaultSiteCode,
    bool IsReadOnly,
    bool IsActive,
    DateOnly ServerDate,
    IReadOnlyList<CompanySiteAccess> Access)
{
    public bool IsSysAdm => string.Equals(UserCode, "SYSADM", StringComparison.Ordinal);

    public bool HasCompany(string company) =>
        Access.Any(a => string.Equals(a.CompanyCode, company, StringComparison.OrdinalIgnoreCase));

    public bool HasCompanySite(string company, string site) =>
        Access.Any(a => string.Equals(a.CompanyCode, company, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(a.SiteCode, site, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// The per-request (or per-job) session context - the stateless replacement of the legacy SterpSession singleton
/// (contracts 4.5, 5.2-5.3). Filled by the API pipeline or the job runner; read by slices and kernel services.
/// </summary>
public sealed class SterpContext
{
    private SterpUser? _user;
    private SterpUser? _realUser;

    public bool IsResolved => _user is not null;

    /// <summary>Authenticated Windows name (DOMAIN\user).</summary>
    public string WindowsLogin { get; private set; } = "";

    /// <summary>The effective STERP user: the run-as user when SYSADM continues as another user (SterpSession.UserCode).</summary>
    public SterpUser User => _user ?? throw new InvalidOperationException("The STERP user context has not been resolved.");

    /// <summary>The signed-on user (SterpSession.RealUserCode).</summary>
    public SterpUser RealUser => _realUser ?? User;

    public string UserCode => User.UserCode;

    public string RealUserCode => RealUser.UserCode;

    public bool IsRunAs => !string.Equals(UserCode, RealUserCode, StringComparison.Ordinal);

    /// <summary>X-STERP-Company (CA-COMPANY); empty when not supplied.</summary>
    public string CompanyCode { get; private set; } = "";

    /// <summary>X-STERP-Site (CA-SITE); empty when not supplied.</summary>
    public string SiteCode { get; private set; } = "";

    /// <summary>X-STERP-Business-Date (CA-BUSINESS-DATE); default the server local date (CobolGateway.cs:77).</summary>
    public DateOnly BusinessDate { get; private set; } = DateOnly.FromDateTime(DateTime.Today);

    public bool BusinessDateSupplied { get; private set; }

    /// <summary>Idempotency-Key (CA-CORRELATION-ID X(36)); default a new GUID per request.</summary>
    public string CorrelationId { get; private set; } = Guid.NewGuid().ToString();

    public bool CorrelationSupplied { get; private set; }

    public string TraceId { get; private set; } = "";

    public string? OperationId { get; private set; }

    /// <summary>Name of the legacy program the current code reproduces (structured logging, contracts 10).</summary>
    public string? LegacyProgram { get; set; }

    public CompanySiteAccess? CompanyAccess =>
        User.Access.FirstOrDefault(a => string.Equals(a.CompanyCode, CompanyCode, StringComparison.OrdinalIgnoreCase) &&
                                        (SiteCode.Length == 0 || string.Equals(a.SiteCode, SiteCode, StringComparison.OrdinalIgnoreCase)));

    public void SetUser(string windowsLogin, SterpUser realUser, SterpUser effectiveUser)
    {
        WindowsLogin = windowsLogin;
        _realUser = realUser;
        _user = effectiveUser;
    }

    public void SetScope(string companyCode, string siteCode)
    {
        CompanyCode = companyCode;
        SiteCode = siteCode;
    }

    public void SetBusinessDate(DateOnly date, bool supplied)
    {
        BusinessDate = date;
        BusinessDateSupplied = supplied;
    }

    public void SetCorrelation(string correlationId, bool supplied)
    {
        CorrelationId = correlationId;
        CorrelationSupplied = supplied;
    }

    public void SetTrace(string traceId, string? operationId)
    {
        TraceId = traceId;
        OperationId = operationId;
    }

    /// <summary>Snapshot used to run a job in the background with the submitter's context.</summary>
    public SterpContextSnapshot Snapshot() =>
        new(WindowsLogin, RealUser, User, CompanyCode, SiteCode, BusinessDate, BusinessDateSupplied, CorrelationId, TraceId, OperationId);

    public void Restore(SterpContextSnapshot s)
    {
        SetUser(s.WindowsLogin, s.RealUser, s.User);
        SetScope(s.CompanyCode, s.SiteCode);
        SetBusinessDate(s.BusinessDate, s.BusinessDateSupplied);
        SetCorrelation(s.CorrelationId, true);
        SetTrace(s.TraceId, s.OperationId);
    }
}

public sealed record SterpContextSnapshot(
    string WindowsLogin,
    SterpUser RealUser,
    SterpUser User,
    string CompanyCode,
    string SiteCode,
    DateOnly BusinessDate,
    bool BusinessDateSupplied,
    string CorrelationId,
    string TraceId,
    string? OperationId);
