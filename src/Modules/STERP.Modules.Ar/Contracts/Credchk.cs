using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Contracts;

/// <summary>
/// CREDCHKCA (cobol/copy/CREDCHKCA.cpy) - CREDCHK credit exposure and release. Functions: EXPOSURE, CHECK, RELEASE,
/// OVERRIDE, APPROVE (<see cref="CreditCheckFunctions"/>). JSON form: OpenAPI schema ArCredchkCommarea.
/// </summary>
public sealed class CredchkArea : Commarea
{
    [Pic("X(20)")] public string Customer { get; set; } = "";
    [Pic("X(10)")] public string CheckPoint { get; set; } = "";
    [Pic("X(40)")] public string DocumentRef { get; set; } = "";
    [Pic("9(8)", Date = true)] public int EvalDate { get; set; }
    [Pic("X(3)")] public string Currency { get; set; } = "";
    [Pic("S9(9)V9(8)")] public decimal FxRate { get; set; }
    [Pic("9(8)", Date = true)] public int RateDate { get; set; }
    [Pic("S9(11)V99")] public decimal OrderValue { get; set; }
    [Pic("S9(11)V99")] public decimal OrderValueGbp { get; set; }
    [Pic("X(40)")] public string ExcludeRef { get; set; } = "";
    [Pic("S9(11)V99")] public decimal OpenDebt { get; set; }
    [Pic("S9(11)V99")] public decimal OpenCredits { get; set; }
    [Pic("S9(11)V99")] public decimal FirmOrders { get; set; }
    [Pic("S9(11)V99")] public decimal ShippedUnbill { get; set; }
    [Pic("S9(11)V99")] public decimal Deposits { get; set; }
    [Pic("S9(11)V99")] public decimal Exposure { get; set; }
    [Pic("S9(11)V99")] public decimal Limit { get; set; }
    [Pic("S9(11)V99")] public decimal Headroom { get; set; }
    [Pic("S9(11)V99")] public decimal Excess { get; set; }
    [Pic("S9(5)V99")] public decimal UtilisedPct { get; set; }
    [Pic("S9(11)V99")] public decimal OverdueGbp { get; set; }
    [Pic("S9(11)V99")] public decimal OverdueThresh { get; set; }
    [Pic("S9(5)")] public int OverdueDays { get; set; }
    [Pic("S9(5)")] public int OverdueTrigger { get; set; }
    [Pic("9(8)", Date = true)] public int OldestDueDate { get; set; }
    [Pic("S9(11)V99")] public decimal DisputedGbp { get; set; }
    [Pic("X")] public string HoldFlag { get; set; } = "";
    [Pic("X(200)")] public string HoldReason { get; set; } = "";
    [Pic("X")] public string ProformaOnly { get; set; } = "";
    [Pic("X(10)")] public string AccountStatus { get; set; } = "";
    [Pic("X(12)")] public string Sanctions { get; set; } = "";
    [Pic("X(10)")] public string Decision { get; set; } = "";
    [Pic("X(200)")] public string DecisionText { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int OverrideId { get; set; }
    [Pic("X(12)")] public string OverrideScope { get; set; } = "";
    [Pic("S9(11)V99")] public decimal OverrideAmt { get; set; }
    [Pic("X(300)")] public string OverrideReason { get; set; } = "";
    [Pic("X(20)")] public string OverrideBy { get; set; } = "";
    [Pic("X(20)")] public string OverrideAppr { get; set; } = "";
    [Pic("9(8)")] public int OverrideFrom { get; set; }
    [Pic("9(8)")] public int OverrideExpiry { get; set; }
    [Pic("X(10)")] public string OverrideStatus { get; set; } = "";
    [Pic("X")] public string OverrideActive { get; set; } = "";
    [Pic("X(40)")] public string ApproverPerm { get; set; } = "";
    [Pic("S9(9)", Binary = true)] public int ExceptionId { get; set; }

    /// <summary>88 CK-RELEASED.</summary>
    public bool Released => Decision == "RELEASE";

    /// <summary>88 CK-HELD.</summary>
    public bool Held => Decision == "HOLD";

    /// <summary>88 CK-BLOCKED.</summary>
    public bool Blocked => Decision == "BLOCK";
}

/// <summary>CA-FUNCTION values of CREDCHK.</summary>
public static class CreditCheckFunctions
{
    public const string Exposure = "EXPOSURE";
    public const string Check = "CHECK";
    public const string Release = "RELEASE";
    public const string Override = "OVERRIDE";
    public const string Approve = "APPROVE";
}

/// <summary>
/// IC-AR-CREDCHK - the public in-process contract of CREDCHK consumed by STERP-SO (DESPATCH, SOACCEPT, SOAMEND, SODEMO).
/// Mirrors <c>CALL "CREDCHK" USING CREDCHKCA</c>: the caller INITIALIZEs a <see cref="CredchkArea"/> (new instance), MOVEs
/// its header context (company, site, user, business date, correlation id) and the body inputs, sets
/// <c>Header.Function</c> and calls; the area comes back with the outputs and the STCAHDR result fields set.
/// The call runs on the caller's unit of work (the scoped <c>IUnitOfWork</c> of the same DI scope): CREDCHK issues no
/// BEGN for EXPOSURE/CHECK/RELEASE; OVERRIDE/APPROVE own a unit when the caller is in autocommit and use savepoint
/// CREDCHK otherwise (STERP-AR.md section 7.2.1).
/// </summary>
public interface ICreditCheckService
{
    /// <summary>CALL "CREDCHK" USING area - dispatches on <c>area.Header.Function</c>; returns the header result.</summary>
    Task<Sterp.Platform.Kernel.LegacyResult> CallAsync(CredchkArea area, CancellationToken ct = default);

    Task<Sterp.Platform.Kernel.LegacyResult> ExposureAsync(CredchkArea area, CancellationToken ct = default) =>
        Run(area, CreditCheckFunctions.Exposure, ct);

    Task<Sterp.Platform.Kernel.LegacyResult> CheckAsync(CredchkArea area, CancellationToken ct = default) =>
        Run(area, CreditCheckFunctions.Check, ct);

    Task<Sterp.Platform.Kernel.LegacyResult> ReleaseAsync(CredchkArea area, CancellationToken ct = default) =>
        Run(area, CreditCheckFunctions.Release, ct);

    Task<Sterp.Platform.Kernel.LegacyResult> RequestOverrideAsync(CredchkArea area, CancellationToken ct = default) =>
        Run(area, CreditCheckFunctions.Override, ct);

    Task<Sterp.Platform.Kernel.LegacyResult> ApproveOverrideAsync(CredchkArea area, CancellationToken ct = default) =>
        Run(area, CreditCheckFunctions.Approve, ct);

    private Task<Sterp.Platform.Kernel.LegacyResult> Run(CredchkArea area, string function, CancellationToken ct)
    {
        area.Header.Function = function;
        return CallAsync(area, ct);
    }
}
