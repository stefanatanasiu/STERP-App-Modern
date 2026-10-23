using System.Collections.Concurrent;
using System.Reflection;
using Dapper;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Ar.Data;

/// <summary>
/// Parameter bags for the two legacy session families with NULLs Dapper accepts. The shared
/// <c>SqlParameters.Build</c> turns null/"" into <c>DBNull.Value</c>, which Dapper's <c>DynamicParameters</c> refuses
/// ("The member X of type System.DBNull cannot be used as a parameter value"); here the same conversion rules are applied
/// and a NULL is added as a null value (sent as DBNull by Dapper).
/// </summary>
public static class ArParams
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Props = new();

    /// <summary>SqlHelper.Params rules (SqlHelper.cs:21-37): null and "" -> NULL, DateOnly -> date.</summary>
    public static DynamicParameters Helper(object values) => Build(values, SqlParameters.SqlHelperValue);

    /// <summary>SQLIO binding rules: trailing spaces trimmed, at most 256 characters, null -> NULL.</summary>
    public static DynamicParameters Sqlio(object values) => Build(values, SqlParameters.SqlioValue);

    private static DynamicParameters Build(object values, Func<object?, object?> convert)
    {
        var d = new DynamicParameters();
        foreach (var p in Props.GetOrAdd(values.GetType(), t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)))
        {
            var v = convert(p.GetValue(values));
            d.Add("@" + p.Name, v is DBNull ? null : v);
        }
        return d;
    }
}
