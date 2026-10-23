using Dapper;

namespace Sterp.Modules.Bom.Data;

/// <summary>
/// SqlHelper.Params (dotnet/STERP.Data/SqlHelper.cs:21-37) for the procedure-backed operations: an empty string or null
/// becomes SQL NULL, dates are sent as date values, everything else as is. Built as Dapper parameters so a NULL needs no
/// type inference.
/// </summary>
internal static class SqlHelperArgs
{
    public static DynamicParameters Of(params (string Name, object? Value)[] values)
    {
        var d = new DynamicParameters();
        foreach (var (name, value) in values)
        {
            var v = value switch
            {
                null => null,
                string s when s.Length == 0 => null,
                DateOnly date => date.ToDateTime(TimeOnly.MinValue),
                _ => value,
            };
            d.Add(name, v);
        }
        return d;
    }
}
