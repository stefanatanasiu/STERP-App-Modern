using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Kit.Common;

/// <summary>
/// The STCAHDR header of a kit COMMAREA (cobol/copy/STCAHDR.cpy). The inputs are filled as the legacy CobolGateway did
/// (CobolGateway.cs:69-83: company, site, user of the session, business date, correlation id); the outputs are what the
/// program left (CA-RETURN-CODE, CA-REASON-CODE, CA-MESSAGE, CA-MSG-COUNT, CA-REQ-REF).
/// </summary>
internal abstract class KitArea
{
    /// <summary>CA-PROGRAM X(8).</summary>
    public string Program { get; set; } = "";

    /// <summary>CA-FUNCTION X(8) (upper-cased by the gateway).</summary>
    public string Function { get; set; } = "";

    /// <summary>CA-COMPANY X(10).</summary>
    public string Company { get; set; } = "";

    /// <summary>CA-SITE X(10).</summary>
    public string Site { get; set; } = "";

    /// <summary>CA-USER X(20).</summary>
    public string User { get; set; } = "";

    /// <summary>CA-BUSINESS-DATE 9(8).</summary>
    public int BusinessDate { get; set; }

    /// <summary>CA-CORRELATION-ID X(36).</summary>
    public string CorrelationId { get; set; } = "";

    /// <summary>CA-RETURN-CODE 9(2).</summary>
    public int ReturnCode { get; set; }

    /// <summary>CA-REASON-CODE X(8).</summary>
    public string ReasonCode { get; set; } = "";

    /// <summary>CA-MESSAGE X(200).</summary>
    public string Message { get; set; } = "";

    /// <summary>CA-REQ-REF X(10).</summary>
    public string ReqRef { get; set; } = "";

    /// <summary>CA-MSG-COUNT 9(2).</summary>
    public int MsgCount { get; set; }

    /// <summary>CA-FAILED: 08 thru 16.</summary>
    public bool Failed => ReturnCode is >= 8 and <= 16;

    /// <summary>The STCAHDR outcome.</summary>
    public LegacyResult Result => new(ReturnCode, Pic.X(ReasonCode, 8), Pic.X(Message, 200), MsgCount, Pic.X(ReqRef, 10));

    /// <summary>CobolGateway.PrepareHeader: the session values, the upper-cased function, the outcome cleared.</summary>
    public void Prepare(KitSession s, string function, string? correlationId = null)
    {
        Function = Pic.X(function.ToUpperInvariant(), 8);
        Company = Pic.X(s.Company, 10);
        Site = Pic.X(s.Site, 10);
        User = Pic.X(s.User, 20);
        BusinessDate = s.BusinessDate;
        CorrelationId = Pic.X(string.IsNullOrEmpty(correlationId) ? s.CorrelationId : correlationId, 36);
        ReturnCode = 0;
        ReasonCode = "";
        Message = "";
        MsgCount = 0;
    }

    /// <summary>Sets the outcome (MOVE n TO CA-RETURN-CODE, MOVE code TO CA-REASON-CODE, MOVE text TO CA-MESSAGE).</summary>
    public void Set(int rc, string reason, string message)
    {
        ReturnCode = rc;
        ReasonCode = Pic.X(reason, 8);
        Message = Pic.X(message, 200);
    }
}

/// <summary>The sign-on context values a kit facade passed to the gateway (SterpSession).</summary>
internal sealed record KitSession(string Company, string Site, string User, int BusinessDate, string CorrelationId)
{
    public static KitSession From(SterpContext s) =>
        new(s.CompanyCode, s.SiteCode, s.UserCode, CobolDate.ToYyyymmdd(s.BusinessDate), s.CorrelationId);
}
