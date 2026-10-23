using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Fin.Common;

/// <summary>Binds <see cref="IFinKernel"/> to the PLATFORM kernel services (contracts 7); adds no behaviour of its own.</summary>
public sealed class FinKernel(
    IPermissionService permissions,
    IPeriodService periods,
    IAuditWriter audit,
    IParameterService parameters,
    IExceptionRaiser exceptions) : IFinKernel
{
    private static KernelCaller Caller(CaRequest c) => new(c.Company, c.Site, c.User, c.BusinessDate, c.CorrelationId);

    public async Task<LegacyResult> PermCheckAsync(CaRequest caller, string permission, CancellationToken ct = default)
    {
        var area = await permissions.CheckAsync(Caller(caller), permission, ct: ct);
        return area.Result;
    }

    public async Task<PeriodCheckAnswer> PeriodCheckAsync(CaRequest caller, int postingDate, int documentDate, string action, CancellationToken ct = default)
    {
        var a = await periods.CheckAsync(Caller(caller), postingDate, documentDate, 0, action, "CHECK", ct);
        return new PeriodCheckAnswer(a.Result, a.PeriodStatus, a.FiscalYear, a.PeriodNo, a.PeriodKey, a.Allowed);
    }

    public async Task<LegacyResult> AuditAsync(CaRequest caller, AuditEntry e, CancellationToken ct = default)
    {
        var a = await audit.WriteAsync(Caller(caller), new AuditlogArea
        {
            EntityType = e.EntityType,
            EntityKey = e.EntityKey,
            Action = e.Action,
            FieldName = e.FieldName,
            OldValue = e.OldValue,
            NewValue = e.NewValue,
            Reason = e.Reason,
            AuthorityRef = e.AuthorityRef,
            Source = e.Source,
            Sensitive = e.Sensitive,
        }, ct);
        return a.Result;
    }

    public async Task<ParmAnswer> ParmGetAsync(CaRequest caller, string paramCode, int asOf, bool numeric, CancellationToken ct = default)
    {
        var a = await parameters.GetAsync(Caller(caller), paramCode, asOf, null, numeric, ct);
        return new ParmAnswer(a.Result, a.Found == "Y", a.ValueNum, a.ValueText);
    }

    public async Task<LegacyResult> RaiseExceptionAsync(CaRequest caller, ExceptionEntry e, CancellationToken ct = default)
    {
        var a = await exceptions.RaiseAsync(Caller(caller), new ExcpraisArea
        {
            Module = e.Module,
            Severity = e.Severity,
            Category = e.Category,
            MessageText = e.MessageText,
            DocumentType = e.DocumentType,
            DocumentRef = e.DocumentRef,
            OwnerUser = e.OwnerUser,
            OwnerRole = e.OwnerRole,
            Priority = e.Priority,
            DueAt = e.DueAt,
            AmountGbp = e.AmountGbp,
            Quantity = e.Quantity,
        }, ct);
        return a.Result;
    }
}
