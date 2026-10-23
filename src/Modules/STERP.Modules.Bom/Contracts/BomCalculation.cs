using Sterp.Modules.Bom.Domain;

namespace Sterp.Modules.Bom.Contracts;

/// <summary>
/// The retained result of one calculation (EXPLODE / COST / NET): the BOMWORK occurrence pool and leaf pool that the legacy
/// engine kept in WORKING-STORAGE for its GETLINE pages. Owned by the area that computed it.
/// </summary>
public sealed class BomCalculation
{
    internal BomCalculation(OccurrencePool pool) => Pool = pool;

    internal OccurrencePool Pool { get; }

    /// <summary>Occurrences in generation order (BOMWORK GET 1..COUNT).</summary>
    public IReadOnlyList<BomOccurrence> Occurrences => Pool.Occurrences;

    /// <summary>Aggregated leaf lines (BOMWORK GETLEAF 1..LEAFCNT).</summary>
    public IReadOnlyList<BomOccurrence> Leaves => Pool.Leaves;
}
