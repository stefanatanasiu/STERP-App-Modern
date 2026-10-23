using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Common;

/// <summary>
/// The FIN "own the unit only when outermost" pattern (e.g. JNLENTRY 2400-BEGIN-WORK, VATRET9/BANKREC 9500-BEGIN-WORK):
/// SELECT CAST(@@TRANCOUNT AS varchar(8)); when 0 (or the read fails) the program issues BEGN and later COMT/ROLB itself.
/// </summary>
public sealed class OwnedUnit(ISqlRunner sql)
{
    public bool Owned { get; private set; }

    public async Task BeginAsync(CancellationToken ct = default)
    {
        Owned = false;
        decimal depth;
        try
        {
            var row = await sql.SingleAsync("SELECT CAST(@@TRANCOUNT AS varchar(8))");
            depth = row is null ? 0 : Slot.NumVal(Slot.Text(row[0], 8));
        }
        catch (SqlioException)
        {
            depth = 0;
        }
        if (depth == 0)
        {
            Owned = true;
            await sql.UnitOfWork.BeginAsync(ct);
        }
    }

    public async Task CommitAsync(CancellationToken ct = default)
    {
        if (!Owned) return;
        await sql.UnitOfWork.CommitAsync(ct);
        Owned = false;
    }

    public async Task RollbackAsync(CancellationToken ct = default)
    {
        if (!Owned) return;
        await sql.UnitOfWork.RollbackAsync(ct);
        Owned = false;
    }
}
