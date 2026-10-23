using Sterp.Platform.Kernel.Services;
using Sterp.Modules.Ar.Contracts;

namespace Sterp.Modules.Ar.Domain;

/// <summary>ARDISPUTCA (cobol/copy/ARDISPUTCA.cpy) - ARDISPUT disputes and collection. JSON form: ArArdisputCommarea.</summary>
public sealed class ArdisputArea : Commarea
{
    [Pic("X(30)")] public string CaseRef { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int CaseId { get; set; }
    [Pic("X(20)")] public string Customer { get; set; } = "";
    [Pic("X(12)")] public string CaseType { get; set; } = "";
    [Pic("X(30)")] public string DocumentNo { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int OpenItemId { get; set; }
    [Pic("X(20)")] public string ReasonCode { get; set; } = "";
    [Pic("X(300)")] public string Reason { get; set; } = "";
    [Pic("S9(11)V99")] public decimal DisputedAmt { get; set; }
    [Pic("S9(11)V99")] public decimal Residual { get; set; }
    [Pic("S9(11)V99")] public decimal UndisputedAmt { get; set; }
    [Pic("X(20)")] public string OwnerUser { get; set; } = "";
    [Pic("9(2)")] public int CollectionStep { get; set; }
    [Pic("9(8)", Date = true)] public int NextActionDt { get; set; }
    [Pic("X(200)")] public string NextAction { get; set; } = "";
    [Pic("X(100)")] public string ContactRestrict { get; set; } = "";
    [Pic("X(14)")] public string ActionType { get; set; } = "";
    [Pic("9(8)", Date = true)] public int ActionDate { get; set; }
    [Pic("S9(9)", Binary = true)] public int ActionSeq { get; set; }
    [Pic("X(80)")] public string ContactName { get; set; } = "";
    [Pic("X(400)")] public string Narrative { get; set; } = "";
    [Pic("S9(11)V99")] public decimal PromiseAmt { get; set; }
    [Pic("9(8)", Date = true)] public int PromiseDate { get; set; }
    [Pic("X(10)")] public string PromiseStatus { get; set; } = "";
    [Pic("X(200)")] public string SuppressReason { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int BrokenCount { get; set; }
    [Pic("X(12)")] public string Status { get; set; } = "";
    [Pic("X(400)")] public string Resolution { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int ExceptionId { get; set; }
}
