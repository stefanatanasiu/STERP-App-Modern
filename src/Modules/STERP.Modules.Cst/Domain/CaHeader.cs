using Sterp.Platform.Kernel;

namespace Sterp.Modules.Cst.Domain;

/// <summary>
/// STCAHDR (cobol/copy/STCAHDR.cpy): the 400-byte header every CST COMMAREA starts with. Text fields are carried right-trimmed
/// and cut to their PIC X(n) length; CA-BUSINESS-DATE is a PIC 9(8) yyyymmdd integer (0 = not given).
/// </summary>
internal sealed class CaHeader
{
    /// <summary>CA-PROGRAM X(8).</summary>
    public string Program { get; set; } = "";

    /// <summary>CA-FUNCTION X(8).</summary>
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

    /// <summary>The STCAHDR outcome as the gateway read it back (CobolResult).</summary>
    public LegacyResult Result => new(ReturnCode, Pic.X(ReasonCode, 8), Pic.X(Message, 200), MsgCount, Pic.X(ReqRef, 10));

    /// <summary>
    /// CobolGateway.PrepareHeader (CobolGateway.cs:69-83): the facade's header from the session - program, function, company,
    /// site, effective user, business date and correlation id; return code, reason, message and count cleared.
    /// </summary>
    public static CaHeader FromSession(SterpContext session, string program, string function) => new()
    {
        Program = program,
        Function = Pic.X(function, 8),
        Company = Pic.X(session.CompanyCode, 10),
        Site = Pic.X(session.SiteCode, 10),
        User = Pic.X(session.UserCode, 20),
        BusinessDate = session.BusinessDate.Year * 10000 + session.BusinessDate.Month * 100 + session.BusinessDate.Day,
        CorrelationId = Pic.X(session.CorrelationId, 36),
    };

    /// <summary>The header of an INITIALIZEd callee area into which the caller moved selected fields.</summary>
    public static CaHeader Initialized(string function, string company, string site, string user, int businessDate) => new()
    {
        Function = Pic.X(function, 8),
        Company = company,
        Site = site,
        User = user,
        BusinessDate = businessDate,
    };

    /// <summary>The common 0100-INITIALISE header reset of the CST programs.</summary>
    public void Reset(string program, string reqRef)
    {
        Program = program;
        ReturnCode = 0;
        MsgCount = 0;
        ReasonCode = "";
        Message = "";
        ReqRef = reqRef;
    }

    /// <summary>9000-REJECT: RC 08 and one more message.</summary>
    public void Reject(string reason, string message)
    {
        ReasonCode = Pic.X(reason, 8);
        Message = Pic.X(message, 200);
        ReturnCode = 8;
        MsgCount = Math.Min(MsgCount + 1, 99);
    }

    /// <summary>8900-SQL-ERROR: CST9001, CA-MESSAGE = SQLIO-ERRMSG (first 200 bytes), RC 12.</summary>
    public void SqlError(string driverText)
    {
        ReasonCode = "CST9001";
        Message = Pic.X(driverText, 200);
        ReturnCode = 12;
    }

    /// <summary>ADD 1 TO CA-MSG-COUNT (PIC 9(2)).</summary>
    public void CountMessage() => MsgCount = (MsgCount + 1) % 100;
}
