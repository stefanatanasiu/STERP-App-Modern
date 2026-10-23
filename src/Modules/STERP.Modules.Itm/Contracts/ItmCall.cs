using Sterp.Platform.Kernel;

namespace Sterp.Modules.Itm.Contracts;

/// <summary>
/// The STCAHDR input values a caller passes to an ITM in-process contract (cobol/copy/STCAHDR.cpy): CA-COMPANY, CA-SITE,
/// CA-USER, CA-BUSINESS-DATE (null = 00000000) and CA-CORRELATION-ID. Legacy COBOL callers filled these from their own
/// COMMAREA header or job arguments before the static CALL; the target caller does the same. The outcome (CA-RETURN-CODE,
/// CA-REASON-CODE, CA-MESSAGE, CA-MSG-COUNT, CA-REQ-REF) is the <see cref="LegacyResult"/> returned by every contract.
/// </summary>
public sealed record ItmCall(string Company, string Site, string User, DateOnly? BusinessDate, string CorrelationId = "")
{
    /// <summary>The header the legacy CobolGateway built from the sign-on context (CobolGateway.cs:69-83).</summary>
    public static ItmCall From(SterpContext session, string? correlationId = null) =>
        new(session.CompanyCode, session.SiteCode, session.UserCode, session.BusinessDate, correlationId ?? session.CorrelationId);
}
