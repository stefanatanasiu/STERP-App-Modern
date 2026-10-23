using System.Data;
using Dapper;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Modules.Fin.Common;

/// <summary>
/// SQLIO call shapes used by the FIN programs on the request's engine unit of work: SNGL (first row or not found), EXEC +
/// FTCH loop (rows) and EXEC without a result (row count). Statement text keeps the legacy SQL with the positional markers
/// written @p1..@pn; parameters follow the SQLIO binding rules of <see cref="ISqlRunner"/>.
/// </summary>
public static class SqlioCalls
{
    /// <summary>
    /// Positional parameters @p1..@pn bound as SQLIO bound them (STSQLPRM: SQL_VARCHAR text, trailing spaces trimmed, at
    /// most 256 characters; NULL when the program set the null flag). Numbers and dates keep their .NET type.
    /// </summary>
    public static DynamicParameters Args(object?[] args)
    {
        var d = new DynamicParameters();
        for (var i = 0; i < args.Length; i++)
        {
            var name = "@p" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            switch (args[i])
            {
                case null:
                    d.Add(name, null, DbType.AnsiString, ParameterDirection.Input, 1);
                    break;
                case string s:
                {
                    var t = s.TrimEnd(' ', '\0');
                    if (t.Length > 256) t = t[..256];
                    d.Add(name, t, DbType.AnsiString, ParameterDirection.Input, Math.Max(1, t.Length));
                    break;
                }
                case DateOnly date:
                    d.Add(name, date.ToDateTime(TimeOnly.MinValue), DbType.Date);
                    break;
                default:
                    d.Add(name, args[i]);
                    break;
            }
        }
        return d;
    }

    /// <summary>SNGL: the first row's values (DBNull -> null), or null when no row came back (SQLCODE 100).</summary>
    public static Task<object?[]?> SingleAsync(this ISqlRunner sql, string text, params object?[] args) =>
        sql.WithReaderAsync(text, Args(args), async r =>
        {
            if (!await r.ReadAsync()) return null;
            var v = new object?[r.FieldCount];
            for (var i = 0; i < v.Length; i++) v[i] = r.IsDBNull(i) ? null : r.GetValue(i);
            return (object?[]?)v;
        });

    /// <summary>EXEC + FTCH until not found or <paramref name="max"/> rows (0 = all).</summary>
    public static Task<List<object?[]>> RowsAsync(this ISqlRunner sql, string text, int max, params object?[] args) =>
        sql.WithReaderAsync(text, Args(args), async r =>
        {
            var rows = new List<object?[]>();
            while ((max <= 0 || rows.Count < max) && await r.ReadAsync())
            {
                var v = new object?[r.FieldCount];
                for (var i = 0; i < v.Length; i++) v[i] = r.IsDBNull(i) ? null : r.GetValue(i);
                rows.Add(v);
            }
            return rows;
        });

    /// <summary>EXEC of a statement without a result set: SQLIO-ROWCOUNT.</summary>
    public static Task<int> ExecAsync(this ISqlRunner sql, string text, params object?[] args) =>
        sql.ExecuteAsync(text, Args(args));
}
