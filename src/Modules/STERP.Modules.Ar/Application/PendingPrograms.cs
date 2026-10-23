using Sterp.Modules.Ar.Contracts;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Services;

namespace Sterp.Modules.Ar.Application;

/// <summary>
/// Placeholder bodies of the public contracts until their engines are delivered: the legacy gateway outcome of a program
/// missing from the engine directory (CobolGateway.cs:132-137, RC 16 PGM0001).
/// </summary>
internal static class PendingProgram
{
    public static Task<LegacyResult> Missing(Commarea area, string program)
    {
        area.Header.ReturnCode = 16;
        area.Header.ReasonCode = "PGM0001";
        area.Header.Message = "Program " + program + " is not in the engine program directory.";
        return Task.FromResult(area.Header.Outcome());
    }
}

internal sealed class PendingArDepositService : IArDepositService
{
    public Task<LegacyResult> CallAsync(ArdeposArea area, CancellationToken ct = default) => PendingProgram.Missing(area, "ARDEPOS");
}

internal sealed class PendingArInvoicePostingService : IArInvoicePostingService
{
    public Task<LegacyResult> CallAsync(ArinvpstArea area, CancellationToken ct = default) => PendingProgram.Missing(area, "ARINVPST");
}

internal sealed class PendingArCreditNoteService : IArCreditNoteService
{
    public Task<LegacyResult> CallAsync(ArcrnoteArea area, CancellationToken ct = default) => PendingProgram.Missing(area, "ARCRNOTE");
}

internal sealed class PendingCreditCheckService : ICreditCheckService
{
    public Task<LegacyResult> CallAsync(CredchkArea area, CancellationToken ct = default) => PendingProgram.Missing(area, "CREDCHK");
}
