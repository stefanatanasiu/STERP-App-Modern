using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Dapper;
using Sterp.Platform.Kernel;

namespace Sterp.Modules.Bom.Data;

/// <summary>
/// Helpers that keep the legacy SQLIO statement text (positional <c>?</c> markers) and its fetch semantics: every column
/// arrives as text cut to the declared slot width, NULL arrives as spaces (NUMVAL = 0).
/// </summary>
internal static class BomSql
{
    /// <summary>Replaces each positional <c>?</c> marker by @p1, @p2, ... in order.</summary>
    public static string Q(string legacySql)
    {
        var sb = new StringBuilder(legacySql.Length + 16);
        var n = 0;
        foreach (var c in legacySql)
        {
            if (c == '?') sb.Append("@p").Append(++n);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// The parameter values of the markers in order, with the SQLIO binding rules (trailing spaces trimmed, at most 256
    /// characters). A NULL marker is bound as a typed varchar NULL: the platform's generic conversion hands Dapper an untyped
    /// DBNull, which it refuses (finding F1 of the Agent 08 BOM work-stream report).
    /// </summary>
    public static DynamicParameters Args(params object?[] values)
    {
        var dp = new DynamicParameters();
        for (var i = 0; i < values.Length; i++)
        {
            var name = "@p" + (i + 1).ToString(CultureInfo.InvariantCulture);
            switch (values[i])
            {
                case null:
                    dp.Add(name, DBNull.Value, DbType.AnsiString, ParameterDirection.Input, 1);
                    break;
                case string s:
                    var t = s.TrimEnd(' ', '\0');
                    dp.Add(name, t.Length > 256 ? t[..256] : t);
                    break;
                case DateOnly d:
                    dp.Add(name, d.ToDateTime(TimeOnly.MinValue));
                    break;
                default:
                    dp.Add(name, values[i]);
                    break;
            }
        }
        return dp;
    }

    /// <summary>Text column cut to its slot width (NULL = spaces), carried right-trimmed.</summary>
    public static string Text(DbDataReader r, int i, int width)
    {
        if (r.IsDBNull(i)) return "";
        var v = r.GetValue(i);
        var s = v switch
        {
            string str => str,
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
        };
        return Domain.Pic.X(s, width);
    }

    /// <summary>Numeric column as NUMVAL of its text (NULL = 0).</summary>
    public static decimal Num(DbDataReader r, int i)
    {
        if (r.IsDBNull(i)) return 0m;
        var v = r.GetValue(i);
        return v switch
        {
            decimal d => d,
            int n => n,
            long l => l,
            short s => s,
            byte b => b,
            bool bo => bo ? 1 : 0,
            string str => NumVal(str),
            _ => Convert.ToDecimal(v, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>FUNCTION NUMVAL of a text value (leading/trailing spaces, sign, decimal point); invalid text = 0.</summary>
    /// <summary>FUNCTION NUMVAL as the deployed GnuCOBOL 3.2 runtime evaluates it (kernel, SME-039).</summary>
    public static decimal NumVal(string? text) => Sterp.Platform.Kernel.Cobol.CobolNumeric.NumVal(text);

    /// <summary>yyyymmdd column read as text 'IS NUMERIC' (else 0).</summary>
    public static int Date8(DbDataReader r, int i)
    {
        var t = Text(r, i, 8);
        return t.Length == 8 && t.All(char.IsAsciiDigit) ? int.Parse(t, CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>A PIC 9(8) date moved to a parameter value: eight digits with leading zeros.</summary>
    public static string D8(int yyyymmdd) => (yyyymmdd < 0 ? 0 : yyyymmdd % 100_000_000).ToString("D8", CultureInfo.InvariantCulture);

    /// <summary>The legacy SQLIO-ERRMSG of a failure, cut to the receiving field.</summary>
    public static string ErrMsg(SqlioException ex, int width) => Domain.Pic.X(ex.DriverText, width);
}
