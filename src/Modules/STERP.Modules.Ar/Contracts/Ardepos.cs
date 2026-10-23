using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Contracts;

/// <summary>
/// ARDEPOSCA (cobol/copy/ARDEPOSCA.cpy) - ARDEPOS customer deposits. Functions: DEPOSIT, ENQUIRE, APPLY, REFUND
/// (<see cref="DepositFunctions"/>). JSON form: OpenAPI schema ArInprocArdeposCommarea.
/// </summary>
public sealed class ArdeposArea : Commarea
{
    [Pic("X(20)")] public string Customer { get; set; } = "";
    [Pic("X(30)")] public string DepositRef { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int DepositId { get; set; }
    [Pic("X(12)")] public string Purpose { get; set; } = "";
    [Pic("X(10)")] public string Method { get; set; } = "";
    [Pic("X(20)")] public string BankAccount { get; set; } = "";
    [Pic("9(8)", Date = true)] public int DocDate { get; set; }
    [Pic("9(8)", Date = true)] public int PostingDate { get; set; }
    [Pic("X(3)")] public string Currency { get; set; } = "";
    [Pic("S9(9)V9(8)")] public decimal FxRate { get; set; }
    [Pic("S9(11)V99")] public decimal Amount { get; set; }
    [Pic("S9(11)V99")] public decimal AmountGbp { get; set; }
    [Pic("X(4)")] public string VatCode { get; set; } = "";
    [Pic("S9(3)V9(4)")] public decimal VatRate { get; set; }
    [Pic("S9(11)V99")] public decimal VatGbp { get; set; }
    [Pic("S9(11)V99")] public decimal NetGbp { get; set; }
    [Pic("X")] public string Cleared { get; set; } = "";
    [Pic("X(30)")] public string ApplyTo { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int ApplyItemId { get; set; }
    [Pic("S9(11)V99")] public decimal ApplyAmount { get; set; }
    [Pic("S9(11)V99")] public decimal RefundAmount { get; set; }
    [Pic("X(30)")] public string RefundRef { get; set; } = "";
    [Pic("X(200)")] public string Reason { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int ApprovalId { get; set; }
    [Pic("S9(11)V99")] public decimal AppliedTotal { get; set; }
    [Pic("S9(11)V99")] public decimal RefundedTotal { get; set; }
    [Pic("S9(11)V99")] public decimal Residual { get; set; }
    [Pic("S9(9)", Binary = true)] public int OpenItemId { get; set; }
    [Pic("S9(9)", Binary = true)] public int AllocId { get; set; }
    [Pic("X(30)")] public string JournalNo { get; set; } = "";
}

/// <summary>CA-FUNCTION values of ARDEPOS.</summary>
public static class DepositFunctions
{
    public const string Deposit = "DEPOSIT";
    public const string Enquire = "ENQUIRE";
    public const string Apply = "APPLY";
    public const string Refund = "REFUND";
}

/// <summary>
/// IC-AR-ARDEPOS - the public in-process contract of ARDEPOS consumed by STERP-SO (SOACCEPT). Mirrors
/// <c>CALL "ARDEPOS" USING ARDEPOSCA</c> on the caller's unit of work: DEPOSIT runs PERMCHK, PERIODCK, NUMGEN and the
/// ARVATWRT lookup first, then issues BEGN and ends with COMT/ROLB - which commits (or rolls back) the caller's open work
/// too (no nesting, SME-013); ENQUIRE reads; APPLY and REFUND run in the caller's transaction or autocommit
/// (STERP-AR.md section 7.2.2).
/// </summary>
public interface IArDepositService
{
    /// <summary>CALL "ARDEPOS" USING area - dispatches on <c>area.Header.Function</c>; returns the header result.</summary>
    Task<Sterp.Platform.Kernel.LegacyResult> CallAsync(ArdeposArea area, CancellationToken ct = default);

    Task<Sterp.Platform.Kernel.LegacyResult> DepositAsync(ArdeposArea area, CancellationToken ct = default) =>
        Run(area, DepositFunctions.Deposit, ct);

    Task<Sterp.Platform.Kernel.LegacyResult> EnquireAsync(ArdeposArea area, CancellationToken ct = default) =>
        Run(area, DepositFunctions.Enquire, ct);

    Task<Sterp.Platform.Kernel.LegacyResult> ApplyAsync(ArdeposArea area, CancellationToken ct = default) =>
        Run(area, DepositFunctions.Apply, ct);

    Task<Sterp.Platform.Kernel.LegacyResult> RefundAsync(ArdeposArea area, CancellationToken ct = default) =>
        Run(area, DepositFunctions.Refund, ct);

    private Task<Sterp.Platform.Kernel.LegacyResult> Run(ArdeposArea area, string function, CancellationToken ct)
    {
        area.Header.Function = function;
        return CallAsync(area, ct);
    }
}
