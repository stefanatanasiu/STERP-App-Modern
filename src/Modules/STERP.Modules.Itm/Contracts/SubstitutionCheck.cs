using Sterp.Platform.Kernel;

namespace Sterp.Modules.Itm.Contracts;

/// <summary>
/// IC-ITM-SUBSTCHK (FR-STERP-ITM-025): substitution check - <c>CALL "SUBSTCHK" USING SUBSTCHKCA</c>. Read-only on the
/// caller's connection; RC 00, 06, 08, 12.
/// </summary>
public interface ISubstitutionCheckService
{
    /// <summary>
    /// Executes CHECK (<see cref="SubstchkFunctions"/>; compared exactly). LIST, REVCHECK and APPLY are test-harness-only and
    /// deferred (D-ITM-04): they throw <see cref="NotSupportedException"/>; any other code is RC 08 SUB0008. The area is
    /// updated in place.
    /// </summary>
    Task<LegacyResult> ExecuteAsync(string function, SubstchkCommarea area, ItmCall call, CancellationToken ct = default);
}

/// <summary>CA-FUNCTION values of SUBSTCHK in production scope (SUBSTCHK.cob:57-63).</summary>
public static class SubstchkFunctions
{
    public const string Check = "CHECK";
}

/// <summary>SUBSTCHKCA body (cobol/copy/SUBSTCHKCA.cpy, prefix SB-; OpenAPI component ItmSubstchkCommarea).</summary>
public sealed class SubstchkCommarea
{
    /// <summary>SB-ITEM-CODE X(40).</summary>
    public string ItemCode { get; set; } = "";
    /// <summary>SB-REVISION X(4).</summary>
    public string Revision { get; set; } = "";
    /// <summary>SB-SUBSTITUTE X(40).</summary>
    public string Substitute { get; set; } = "";
    /// <summary>SB-SUBSTITUTE-REV X(4).</summary>
    public string SubstituteRev { get; set; } = "";
    /// <summary>SB-CONTEXT X(10): PURCHASE SALES KIT BOM; blank = KIT.</summary>
    public string Context { get; set; } = "";
    /// <summary>SB-AS-AT 9(8); null = CA-BUSINESS-DATE, else 20261005.</summary>
    public DateOnly? AsAt { get; set; }
    /// <summary>SB-QTY-REQUIRED S9(13)V9(6).</summary>
    public decimal QtyRequired { get; set; }
    /// <summary>SB-QTY-SUBSTITUTE S9(13)V9(6) (output: required x ratio ROUNDED 6 dp).</summary>
    public decimal QtySubstitute { get; set; }
    /// <summary>SB-RATIO S9(9)V9(9) (output).</summary>
    public decimal Ratio { get; set; }
    /// <summary>SB-NEEDS-AGREEMENT X (output).</summary>
    public string NeedsAgreement { get; set; } = "";
    /// <summary>SB-AGREEMENT-REF X(40) (input: the customer agreement reference).</summary>
    public string AgreementRef { get; set; } = "";
    /// <summary>SB-APPROVED-BY X(20) (output).</summary>
    public string ApprovedBy { get; set; } = "";
    /// <summary>SB-VALID-FROM 9(8) (output).</summary>
    public DateOnly? ValidFrom { get; set; }
    /// <summary>SB-VALID-TO 9(8) (output).</summary>
    public DateOnly? ValidTo { get; set; }
    /// <summary>SB-RESTRICTIONS X(200) (output).</summary>
    public string Restrictions { get; set; } = "";
    /// <summary>SB-DOCUMENT-TYPE X(20) (APPLY, deferred).</summary>
    public string DocumentType { get; set; } = "";
    /// <summary>SB-DOCUMENT-REF X(40) (APPLY, deferred).</summary>
    public string DocumentRef { get; set; } = "";
    /// <summary>SB-PERMITTED X ("Y"/"N", output).</summary>
    public string Permitted { get; set; } = "";
    /// <summary>SB-INTERCHANGEABLE X (REVCHECK, deferred).</summary>
    public string Interchangeable { get; set; } = "";
    /// <summary>SB-LINES OCCURS 15 (LIST, deferred).</summary>
    public List<SubstchkLine> Lines { get; set; } = [];
}

/// <summary>One SB-LINES entry (LIST, deferred).</summary>
public sealed class SubstchkLine
{
    public string Item { get; set; } = "";
    public string Desc { get; set; } = "";
    public decimal Ratio { get; set; }
    public string Contexts { get; set; } = "";
    public string Agreement { get; set; } = "";
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string Approver { get; set; } = "";
    public decimal FreeQty { get; set; }
}
