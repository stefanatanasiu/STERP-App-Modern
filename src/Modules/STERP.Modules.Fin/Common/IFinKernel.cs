using Sterp.Platform.Kernel;

namespace Sterp.Modules.Fin.Common;

/// <summary>PERIODCK CHECK answer (PERIODCKCA body fields FIN reads).</summary>
public sealed record PeriodCheckAnswer(LegacyResult Result, string PeriodStatus, int FiscalYear, int PeriodNo, string PeriodYyyypp, string Allowed);

/// <summary>PARMGET answer (PARMGETCA body fields FIN reads).</summary>
public sealed record ParmAnswer(LegacyResult Result, bool Found, decimal ValueNum, string ValueText);

/// <summary>AUDITLOG WRITE body (AUDITLOGCA.cpy).</summary>
public sealed record AuditEntry(
    string EntityType,
    string EntityKey,
    string Action,
    string FieldName = "",
    string OldValue = "",
    string NewValue = "",
    string Reason = "",
    string AuthorityRef = "",
    string Source = "COBOL",
    string Sensitive = "");

/// <summary>EXCPRAIS RAISE body (EXCPRAISCA.cpy).</summary>
public sealed record ExceptionEntry(
    string Module,
    string Severity,
    string Category,
    string MessageText,
    string DocumentType,
    string DocumentRef,
    string OwnerUser,
    string OwnerRole,
    int Priority,
    int DueAt,
    decimal AmountGbp,
    decimal Quantity);

/// <summary>
/// The shared kernel programs FIN calls (contracts 7), seen with the COMMAREA header values each FIN caller moves
/// (company, user, business date always; site and correlation id only where the legacy caller moved them). Bound to the
/// PLATFORM kernel services; FIN never re-implements them.
/// </summary>
public interface IFinKernel
{
    /// <summary>PERMCHK CHECK: RC 0 allowed, RC 06 PRM0006 "&lt;user&gt; does not hold &lt;permission&gt;".</summary>
    Task<LegacyResult> PermCheckAsync(CaRequest caller, string permission, CancellationToken ct = default);

    /// <summary>PERIODCK CHECK for a posting date / document date and action.</summary>
    Task<PeriodCheckAnswer> PeriodCheckAsync(CaRequest caller, int postingDate, int documentDate, string action, CancellationToken ct = default);

    /// <summary>AUDITLOG WRITE (the callers never test the outcome).</summary>
    Task<LegacyResult> AuditAsync(CaRequest caller, AuditEntry entry, CancellationToken ct = default);

    /// <summary>PARMGET GETNUM / GETTEXT with PM-USE-DEFAULT = N.</summary>
    Task<ParmAnswer> ParmGetAsync(CaRequest caller, string paramCode, int asOf, bool numeric, CancellationToken ct = default);

    /// <summary>EXCPRAIS RAISE.</summary>
    Task<LegacyResult> RaiseExceptionAsync(CaRequest caller, ExceptionEntry entry, CancellationToken ct = default);
}
