using Sterp.Modules.Itm.Contracts;
using Sterp.Platform.Kernel;

namespace Sterp.Modules.Itm.Services;

/// <summary>
/// The STCAHDR header (cobol/copy/STCAHDR.cpy) of an ITM COMMAREA while a program runs: the caller's inputs and the
/// outcome fields the program sets. Text is held right-trimmed with the PIC X(n) widths applied.
/// </summary>
internal sealed class CaHeader
{
    public string Program { get; set; } = "";
    public string Function { get; set; } = "";
    public string Company { get; set; } = "";
    public string Site { get; set; } = "";
    public string User { get; set; } = "";
    public int BusinessDate { get; set; }
    public string CorrelationId { get; set; } = "";
    public int ReturnCode { get; set; }
    public string ReasonCode { get; set; } = "";
    public string Message { get; set; } = "";
    public string ReqRef { get; set; } = "";
    public int MsgCount { get; set; }

    /// <summary>CA-FAILED: 08 thru 16.</summary>
    public bool Failed => ReturnCode is >= 8 and <= 16;

    public static CaHeader For(string function, ItmCall call) => new()
    {
        Function = Cob.X(function, 8),
        Company = Cob.X(call.Company, 10),
        Site = Cob.X(call.Site, 10),
        User = Cob.X(call.User, 20),
        BusinessDate = Cob.Date8(call.BusinessDate),
        CorrelationId = Cob.X(call.CorrelationId, 36),
    };

    /// <summary>A header as INITIALIZE leaves it, with the given caller fields (the COBOL caller's MOVEs).</summary>
    public static CaHeader Init(string function, string company, string site, string user, int businessDate, string correlation = "") => new()
    {
        Function = Cob.X(function, 8),
        Company = Cob.X(company, 10),
        Site = Cob.X(site, 10),
        User = Cob.X(user, 20),
        BusinessDate = businessDate,
        CorrelationId = Cob.X(correlation, 36),
    };

    public void SetMessage(string text) => Message = Cob.X(text, 200);

    /// <summary>MOVE ... TO CA-MSG-COUNT (PIC 9(2)): two low-order digits.</summary>
    public void AddMessage() => MsgCount = (MsgCount + 1) % 100;

    public LegacyResult Result => new(ReturnCode % 100, ReasonCode, Message, MsgCount, ReqRef);
}
