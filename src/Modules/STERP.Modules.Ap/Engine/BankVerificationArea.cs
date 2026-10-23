using Sterp.Platform.Kernel.Cobol;

namespace Sterp.Modules.Ap.Engine;

/// <summary>APBANKVRCA body (cobol/copy/APBANKVRCA.cpy). Text fields hold the right-trimmed PIC X content.</summary>
public sealed class BankVerificationArea
{
    public string Supplier { get; set; } = "";
    public string BankRef { get; set; } = "";
    public int BankId { get; set; }
    public string Beneficiary { get; set; } = "";
    public string Country { get; set; } = "";
    public string Currency { get; set; } = "";
    public string SortCode { get; set; } = "";
    public string AccountNo { get; set; } = "";
    public string RollNumber { get; set; } = "";
    public string Iban { get; set; } = "";
    public string Bic { get; set; } = "";
    public string BankName { get; set; } = "";
    public string PayMethod { get; set; } = "";
    public string RequestSource { get; set; } = "";
    public string CopResult { get; set; } = "";
    public string CopReference { get; set; } = "";
    public string CopDecision { get; set; } = "";
    public string CallbackSource { get; set; } = "";
    public string CallbackOnFile { get; set; } = "";
    public string CallbackNote { get; set; } = "";
    public string CallbackBy { get; set; } = "";
    public string PreparedBy { get; set; } = "";
    public string Approver { get; set; } = "";
    public string Decision { get; set; } = "";
    public string Status { get; set; } = "";
    public int ApprovalId { get; set; }
    public int SupersedesId { get; set; }
    public string ContentHash { get; set; } = "";
    public string Usable { get; set; } = "";
    public string CopDone { get; set; } = "";
    public string CallbackDone { get; set; } = "";
    public int InvalidatedCount { get; set; }
    public int UnsubmittedCount { get; set; }
    public int ValidFrom { get; set; }
    public string Note { get; set; } = "";

    /// <summary>PIC X widths of the fields the facade moves with SetString (SupplierService.SetBankInputs width rule).</summary>
    public static readonly IReadOnlyDictionary<string, int> Widths = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["BV-SUPPLIER"] = 20, ["BV-BANK-REF"] = 10, ["BV-BENEFICIARY"] = 120, ["BV-COUNTRY"] = 2, ["BV-CURRENCY"] = 3,
        ["BV-SORT-CODE"] = 6, ["BV-ACCOUNT-NO"] = 8, ["BV-ROLL-NUMBER"] = 18, ["BV-IBAN"] = 34, ["BV-BIC"] = 11,
        ["BV-BANK-NAME"] = 60, ["BV-PAY-METHOD"] = 10, ["BV-REQUEST-SOURCE"] = 30, ["BV-COP-RESULT"] = 12,
        ["BV-COP-REFERENCE"] = 60, ["BV-COP-DECISION"] = 200, ["BV-CALLBACK-SOURCE"] = 120, ["BV-CALLBACK-ON-FILE"] = 1,
        ["BV-CALLBACK-NOTE"] = 300, ["BV-CALLBACK-BY"] = 20, ["BV-PREPARED-BY"] = 20, ["BV-APPROVER"] = 20, ["BV-DECISION"] = 10,
    };

    /// <summary>CommArea.SetString of one named field (Windows-1252, truncation to the element length).</summary>
    public void Set(string field, string? value)
    {
        var v = CobolText.Trimmed(value, Widths[field]);
        switch (field)
        {
            case "BV-SUPPLIER": Supplier = v; break;
            case "BV-BANK-REF": BankRef = v; break;
            case "BV-BENEFICIARY": Beneficiary = v; break;
            case "BV-COUNTRY": Country = v; break;
            case "BV-CURRENCY": Currency = v; break;
            case "BV-SORT-CODE": SortCode = v; break;
            case "BV-ACCOUNT-NO": AccountNo = v; break;
            case "BV-ROLL-NUMBER": RollNumber = v; break;
            case "BV-IBAN": Iban = v; break;
            case "BV-BIC": Bic = v; break;
            case "BV-BANK-NAME": BankName = v; break;
            case "BV-PAY-METHOD": PayMethod = v; break;
            case "BV-REQUEST-SOURCE": RequestSource = v; break;
            case "BV-COP-RESULT": CopResult = v; break;
            case "BV-COP-REFERENCE": CopReference = v; break;
            case "BV-COP-DECISION": CopDecision = v; break;
            case "BV-CALLBACK-SOURCE": CallbackSource = v; break;
            case "BV-CALLBACK-ON-FILE": CallbackOnFile = v; break;
            case "BV-CALLBACK-NOTE": CallbackNote = v; break;
            case "BV-CALLBACK-BY": CallbackBy = v; break;
            case "BV-PREPARED-BY": PreparedBy = v; break;
            case "BV-APPROVER": Approver = v; break;
            case "BV-DECISION": Decision = v; break;
            default: throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
    }

    /// <summary>The ApBankVerification response object.</summary>
    public object ToData() => new
    {
        SupplierCode = Supplier,
        BankRef,
        SupplierBankId = BankId,
        Beneficiary,
        Country,
        Currency,
        SortCode,
        AccountNo,
        RollNumber,
        Iban,
        Bic,
        BankName,
        PaymentMethod = PayMethod,
        RequestSource,
        CopResult,
        CopReference,
        CopDecision,
        CallbackSource,
        CallbackOnFile,
        CallbackNote,
        CallbackBy,
        PreparedBy,
        Approver,
        Decision,
        Status,
        ApprovalId,
        SupersedesBankId = SupersedesId,
        ContentHash,
        Usable,
        CopDone,
        CallbackDone,
        InvalidatedCount,
        UnsubmittedCount,
        ValidFrom,
        Note,
    };
}
