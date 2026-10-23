namespace Sterp.Modules.Ap.Engine;

/// <summary>APSVCARE (cobol/copy/APSVCARE.cpy): the request area of the purchase ledger service wrapper APSVC.</summary>
public sealed class ApSvcArea
{
    public string Func { get; set; } = "";
    public string Company { get; set; } = "";
    public string Site { get; set; } = "";
    public string User { get; set; } = "";
    public int Date { get; set; }
    public string Correlation { get; set; } = "";
    public int Rc { get; set; }
    public string Reason { get; set; } = "";
    public string Message { get; set; } = "";
    public string Text1 { get; set; } = "";
    public string Text2 { get; set; } = "";
    public string Text3 { get; set; } = "";
    public string TextOut { get; set; } = "";
    public string Flag1 { get; set; } = "";
    public string Flag2 { get; set; } = "";
    public int Date1 { get; set; }
    public int Date2 { get; set; }
    public int DateOut { get; set; }
    public int DateOut2 { get; set; }
    public decimal Num1 { get; set; }
    public decimal Num2 { get; set; }
    public decimal Num3 { get; set; }
    public decimal NumOut { get; set; }
    public decimal NumOut2 { get; set; }
    public int Int1 { get; set; }
    public int IntOut { get; set; }
    public int IntOut2 { get; set; }
    public string AudEntity { get; set; } = "";
    public string AudKey { get; set; } = "";
    public string AudAction { get; set; } = "";
    public string AudField { get; set; } = "";
    public string AudOld { get; set; } = "";
    public string AudNew { get; set; } = "";
    public string AudReason { get; set; } = "";
    public string AudAuth { get; set; } = "";
    public string AudSens { get; set; } = "";
    public string ExcSeverity { get; set; } = "";
    public string ExcCategory { get; set; } = "";
    public string ExcDocType { get; set; } = "";
    public string ExcDocRef { get; set; } = "";
    public string ExcOwner { get; set; } = "";
    public string ExcRole { get; set; } = "";
    public string ExcMessage { get; set; } = "";
    public decimal ExcAmount { get; set; }
    public int ExcId { get; set; }

    /// <summary>INITIALIZE WS-APSVC, MOVE func TO SVC-FUNC, PERFORM 8900-SVC-HEADER (company, site, user, date, correlation).</summary>
    public static ApSvcArea For(CaHeader ca, string func) => new()
    {
        Func = func,
        Company = ca.Company,
        Site = ca.Site,
        User = ca.User,
        Date = ca.BusinessDate,
        Correlation = ca.CorrelationId,
    };

    public bool Ok => Rc == 0;

    /// <summary>SVC-BAD: 08 thru 16.</summary>
    public bool Bad => Rc is >= 8 and <= 16;
}
