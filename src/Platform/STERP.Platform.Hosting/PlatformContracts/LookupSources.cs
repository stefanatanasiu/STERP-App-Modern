using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Hosting.PlatformContracts;

/// <summary>
/// Extension point of platform_lookupNamed (STERP-PLATFORM 4.4, FR-STERP-PLATFORM-035): a fixed, named F2 lookup implemented
/// by its owning slice with the legacy query (same text or procedure, predicates, ordering and the four legacy parameters).
/// Register it in the slice module: <c>services.AddScoped&lt;ILookupSource, CustomerLookup&gt;()</c>. While no source is
/// registered for a code, or its slice is not delivered, the operation answers 501 API0501 naming the owning slice.
/// </summary>
public interface ILookupSource
{
    /// <summary>The named lookup code of platform_lookupNamed, e.g. "AR.CUSTOMER".</summary>
    string LookupCode { get; }

    /// <summary>Owning slice, e.g. "STERP-AR".</summary>
    string Slice { get; }

    /// <summary>Runs the lookup; the first column is the code returned to the field.</summary>
    Task<DynamicRows> FindAsync(LookupRequest request, CancellationToken ct);
}

/// <summary>The values LookupService.Find passed (MaintService.vb:80-84): session company and site, trimmed Find text, argument.</summary>
public sealed record LookupRequest(string? CompanyCode, string? SiteCode, string? Search, string? Argument, SterpContext Session)
{
    /// <summary>@CompanyCode, @SiteCode, @Search, @Argument exactly as the legacy call bound them ('' becomes NULL on SqlHelper).</summary>
    public object Parameters => new { CompanyCode, SiteCode, Search, Argument };
}

public static class LookupQuery
{
    /// <summary>
    /// LookupService.Find: the fixed procedure name or SQL text executed with the four legacy parameters on its own SqlHelper
    /// connection (autocommit), result set returned in its column order.
    /// </summary>
    public static Task<DynamicRows> RunAsync(IProcedureRunner sql, string procedureOrSql, LookupRequest request, CancellationToken ct) =>
        sql.WithReaderAsync(procedureOrSql, request.Parameters, r => DynamicRows.ReadAsync(r, null, ct), ct);
}

/// <summary>The binding named lookups and their owning slices (STERP-PLATFORM 4.4).</summary>
public static class NamedLookups
{
    public static readonly IReadOnlyDictionary<string, string> Owners = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AR.CUSTOMER"] = "STERP-AR",
        ["SO.ORDER"] = "STERP-SO",
        ["SO.PICKLIST"] = "STERP-SO",
        ["PO.OPENORDERS"] = "STERP-PO",
    };
}
