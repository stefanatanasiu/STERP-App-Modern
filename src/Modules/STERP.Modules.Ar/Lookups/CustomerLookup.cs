using Sterp.Platform.Hosting.PlatformContracts;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Ar.Lookups;

/// <summary>
/// Named F2 lookup AR.CUSTOMER (STERP-PLATFORM 4.4): DebtorsService.CustomerPickSql (DebtorsService.vb:17-23), the customer
/// picker of the enquiry and receipt screens - a read of ar.vw_CustomerBalance by company and search text.
/// </summary>
public sealed class CustomerLookup(IProcedureRunner sql) : ILookupSource
{
    public const string CustomerPickSql =
        "SELECT CustomerCode, LegalName AS CustomerName, CurrencyCode, CreditState, BalanceGBP " +
        "FROM ar.vw_CustomerBalance WHERE CompanyCode = @CompanyCode " +
        "AND (@Search IS NULL OR CustomerCode LIKE @Search + '%' OR LegalName LIKE '%' + @Search + '%') " +
        "ORDER BY CustomerCode";

    public string LookupCode => "AR.CUSTOMER";

    public string Slice => "STERP-AR";

    public Task<DynamicRows> FindAsync(LookupRequest request, CancellationToken ct) => LookupQuery.RunAsync(sql, CustomerPickSql, request, ct);
}
