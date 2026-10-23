using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Fin.Common;

/// <summary>
/// The STCAHDR request fields a program receives (CA-FUNCTION, CA-COMPANY, CA-SITE, CA-USER, CA-BUSINESS-DATE,
/// CA-CORRELATION-ID), normalized to their PIC X widths (CobolGateway.PrepareHeader; contracts 4.4-4.5).
/// </summary>
public sealed record CaRequest(string Function, string Company, string Site, string User, int BusinessDate, string CorrelationId)
{
    public static CaRequest From(SterpContext s, string function, string? correlationId = null) => new(
        CobolText.Trimmed(function.ToUpperInvariant(), 8),
        CobolText.Trimmed(s.CompanyCode, 10),
        CobolText.Trimmed(s.SiteCode, 10),
        CobolText.Trimmed(s.UserCode, 20),
        CobolDate.ToYyyymmdd(s.BusinessDate),
        CobolText.Trimmed(correlationId ?? s.CorrelationId, 36));

    /// <summary>The header a caller passes to a callee (MOVE CA-COMPANY TO xH-COMPANY ...), with the callee's function.</summary>
    public CaRequest For(string function) => this with { Function = CobolText.Trimmed(function.ToUpperInvariant(), 8) };

    /// <summary>
    /// The header fields a COBOL caller moved into a kernel COMMAREA after INITIALIZE: company, user and business date always;
    /// site and correlation id only when the caller moved them (otherwise spaces).
    /// </summary>
    public CaRequest Callee(string function, bool site, bool correlation) => this with
    {
        Function = CobolText.Trimmed(function.ToUpperInvariant(), 8),
        Site = site ? Site : "",
        CorrelationId = correlation ? CorrelationId : "",
    };

    public DateOnly BusinessDay => CobolDate.FromYyyymmdd(BusinessDate) ?? DateOnly.FromDateTime(DateTime.Today);
}
