using System.Data;
using System.Text;
using System.Text.Json.Nodes;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Sterp.Platform.Kernel.Data;

/// <summary>
/// SQLIO statement execution on the request's engine connection and unit of work (FR-STERP-PLATFORM-011). Every failure
/// is raised as <see cref="SqlioException"/> carrying the legacy driver text; uncaught it becomes RC 12 SQL0001.
/// </summary>
public interface ISqlRunner
{
    IUnitOfWork UnitOfWork { get; }

    Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default);

    Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null, CancellationToken ct = default);

    /// <summary>EXEC: returns the row count (SQLIO-ROWCOUNT).</summary>
    Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default);

    Task<T?> ScalarAsync<T>(string sql, object? param = null, CancellationToken ct = default);

    Task<JsonArray> QueryJsonAsync(string sql, object? param = null, CancellationToken ct = default);

    /// <summary>Runs a reader on the engine connection (multiple result sets, OUTPUT parameters).</summary>
    Task<T> WithReaderAsync<T>(string sql, object? param, Func<System.Data.Common.DbDataReader, Task<T>> read, CancellationToken ct = default);

    /// <summary>Executes a command with explicit parameters (OUTPUT parameters, return values).</summary>
    Task<int> ExecuteCommandAsync(string sql, CommandType type, Action<SqlParameterCollection> parameters, CancellationToken ct = default);

    /// <summary>The legacy SQLIO-ERRMSG text for a driver failure (prefix + first server message, at most 512 bytes).</summary>
    string DriverText(SqlException ex);
}

public sealed class SqlRunner(IUnitOfWork uow, IOptions<SterpDatabaseOptions> options) : ISqlRunner
{
    private readonly SterpDatabaseOptions _options = options.Value;

    public IUnitOfWork UnitOfWork => uow;

    private async Task<(SqlConnection Cn, SqlTransaction? Tx)> OpenAsync(CancellationToken ct)
    {
        try
        {
            return (await uow.ConnectionAsync(ct), uow.Transaction);
        }
        catch (SqlException ex)
        {
            // a connection that cannot be opened is a SQLIO failure too (sterpsql.c ensure_conn -> SQLCODE -1)
            throw Wrap(ex);
        }
    }

    private CommandDefinition Cmd(string sql, object? param, SqlTransaction? tx, CancellationToken ct) =>
        new(sql, SqlParameters.Build(param, SqlParameters.SqlioValue), tx, _options.CommandTimeoutSeconds, CommandType.Text, cancellationToken: ct);

    public async Task<IReadOnlyList<T>> QueryAsync<T>(string sql, object? param = null, CancellationToken ct = default)
    {
        var (cn, tx) = await OpenAsync(ct);
        try { return (await cn.QueryAsync<T>(Cmd(sql, param, tx, ct))).AsList(); }
        catch (SqlException ex) { throw Wrap(ex); }
    }

    public async Task<T?> QueryFirstOrDefaultAsync<T>(string sql, object? param = null, CancellationToken ct = default)
    {
        var (cn, tx) = await OpenAsync(ct);
        try { return await cn.QueryFirstOrDefaultAsync<T>(Cmd(sql, param, tx, ct)); }
        catch (SqlException ex) { throw Wrap(ex); }
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null, CancellationToken ct = default)
    {
        var (cn, tx) = await OpenAsync(ct);
        try { return await cn.ExecuteAsync(Cmd(sql, param, tx, ct)); }
        catch (SqlException ex) { throw Wrap(ex); }
    }

    public async Task<T?> ScalarAsync<T>(string sql, object? param = null, CancellationToken ct = default)
    {
        var (cn, tx) = await OpenAsync(ct);
        try { return await cn.ExecuteScalarAsync<T>(Cmd(sql, param, tx, ct)); }
        catch (SqlException ex) { throw Wrap(ex); }
    }

    public Task<JsonArray> QueryJsonAsync(string sql, object? param = null, CancellationToken ct = default) =>
        WithReaderAsync(sql, param, r => SqlJson.ReadRowsAsync(r, ct), ct);

    public async Task<T> WithReaderAsync<T>(string sql, object? param, Func<System.Data.Common.DbDataReader, Task<T>> read, CancellationToken ct = default)
    {
        var (cn, tx) = await OpenAsync(ct);
        try
        {
            await using var reader = await cn.ExecuteReaderAsync(Cmd(sql, param, tx, ct));
            return await read(reader);
        }
        catch (SqlException ex) { throw Wrap(ex); }
    }

    public async Task<int> ExecuteCommandAsync(string sql, CommandType type, Action<SqlParameterCollection> parameters, CancellationToken ct = default)
    {
        var (cn, tx) = await OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, cn, tx) { CommandType = type, CommandTimeout = _options.CommandTimeoutSeconds };
        parameters(cmd.Parameters);
        try { return await cmd.ExecuteNonQueryAsync(ct); }
        catch (SqlException ex) { throw Wrap(ex); }
    }

    public string DriverText(SqlException ex) => LegacyDriverText.From(ex, _options.LegacyDriverMessagePrefix);

    private SqlioException Wrap(SqlException ex) => new(DriverText(ex), LegacyDriverText.SqlState(ex), ex);
}

public static class LegacyDriverText
{
    /// <summary>SQLGetDiagRec record 1 message with the driver prefix, at most 512 bytes (sterpsql.c diag(); SQLIO-ERRMSG).</summary>
    public static string From(SqlException ex, string prefix)
    {
        var first = Record1(ex)?.Message ?? ex.Message;
        var text = prefix + first;
        var bytes = Cobol.CobolText.Ansi.GetBytes(text);
        return bytes.Length <= 512 ? text : Cobol.CobolText.Ansi.GetString(bytes, 0, 512);
    }

    /// <summary>ODBC SQLSTATE approximation (unknown states map to 42000); 28000 = the login was refused.</summary>
    public static string SqlState(SqlException ex) => (Record1(ex)?.Number ?? ex.Number) switch
    {
        2627 or 2601 or 547 or 515 => "23000",
        1205 => "40001",
        -2 => "HYT00",
        var n when IsLoginRefused(n) => "28000",
        _ => "42000",
    };

    /// <summary>
    /// The error ODBC reports as diagnostic record 1. For a database that cannot be opened (offline, restoring, absent) SQL
    /// Server sends 4060 and then 18456; ODBC Driver 18 reports the refused login first (SQLSTATE 28000, "Login failed for
    /// user ..."), so sterpsql.c ensure_conn does not retry it - SqlClient keeps the server order.
    /// </summary>
    private static SqlError? Record1(SqlException ex)
    {
        if (ex.Errors.Count == 0) return null;
        foreach (SqlError e in ex.Errors)
        {
            if (IsLoginRefused(e.Number)) return e;
        }
        return ex.Errors[0];
    }

    private static bool IsLoginRefused(int number) => number is 18456 or 18452 or 18470 or 18486 or 18487 or 18488;
}

/// <summary>
/// The legacy ADO.NET SqlHelper family (dotnet/STERP.Data/SqlHelper.cs): one short connection per call, autocommit unless the
/// procedure opens its own transaction; a name without a space is a procedure, text containing a space is ad-hoc SQL; null and
/// empty strings become NULL; command timeout 120 s; a THROW 'STERP-x-code: text' becomes <see cref="BusinessRuleException"/>.
/// </summary>
public interface IProcedureRunner
{
    Task<IReadOnlyList<T>> QueryAsync<T>(string procedureOrSql, object? param = null, CancellationToken ct = default);

    Task<T?> QueryFirstOrDefaultAsync<T>(string procedureOrSql, object? param = null, CancellationToken ct = default);

    Task<int> ExecuteAsync(string procedureOrSql, object? param = null, CancellationToken ct = default);

    Task<object?> ScalarAsync(string procedureOrSql, object? param = null, CancellationToken ct = default);

    /// <summary>First result set as JSON rows (contracts 4.3).</summary>
    Task<JsonArray> QueryJsonAsync(string procedureOrSql, object? param = null, CancellationToken ct = default);

    /// <summary>Every result set as JSON rows (DataSet equivalent).</summary>
    Task<List<JsonArray>> QueryJsonSetsAsync(string procedureOrSql, object? param = null, CancellationToken ct = default);

    Task<T> WithReaderAsync<T>(string procedureOrSql, object? param, Func<System.Data.Common.DbDataReader, Task<T>> read, CancellationToken ct = default);

    /// <summary>Explicit command (OUTPUT parameters); SqlHelper rules apply to the values the caller adds.</summary>
    Task<int> ExecuteCommandAsync(string procedureOrSql, Action<SqlParameterCollection> parameters, CancellationToken ct = default);
}

public sealed class ProcedureRunner(ISqlConnectionFactory factory, IOptions<SterpDatabaseOptions> options) : IProcedureRunner
{
    private readonly int _timeout = options.Value.CommandTimeoutSeconds;

    /// <summary>SqlHelper.Build: a name is a procedure, anything containing a space is ad-hoc SQL text (SqlHelper.cs:51).</summary>
    public static CommandType TypeOf(string procedureOrSql) =>
        procedureOrSql.Trim().IndexOf(' ') > 0 ? CommandType.Text : CommandType.StoredProcedure;

    private CommandDefinition Cmd(string text, object? param, CancellationToken ct) =>
        new(text, SqlParameters.Build(param, SqlParameters.SqlHelperValue), null, _timeout, TypeOf(text), cancellationToken: ct);

    private async Task<T> RunAsync<T>(Func<SqlConnection, Task<T>> body, CancellationToken ct)
    {
        await using var cn = factory.Create();
        try
        {
            await cn.OpenAsync(ct);
            return await body(cn);
        }
        catch (SqlException ex)
        {
            var bre = BusinessRuleException.TryTranslate(ex);
            if (bre is not null) throw bre;
            throw;
        }
    }

    public Task<IReadOnlyList<T>> QueryAsync<T>(string procedureOrSql, object? param = null, CancellationToken ct = default) =>
        RunAsync<IReadOnlyList<T>>(async cn => (await cn.QueryAsync<T>(Cmd(procedureOrSql, param, ct))).AsList(), ct);

    public Task<T?> QueryFirstOrDefaultAsync<T>(string procedureOrSql, object? param = null, CancellationToken ct = default) =>
        RunAsync(cn => cn.QueryFirstOrDefaultAsync<T>(Cmd(procedureOrSql, param, ct)), ct);

    public Task<int> ExecuteAsync(string procedureOrSql, object? param = null, CancellationToken ct = default) =>
        RunAsync(cn => cn.ExecuteAsync(Cmd(procedureOrSql, param, ct)), ct);

    public Task<object?> ScalarAsync(string procedureOrSql, object? param = null, CancellationToken ct = default) =>
        RunAsync(cn => cn.ExecuteScalarAsync(Cmd(procedureOrSql, param, ct)), ct);

    public Task<JsonArray> QueryJsonAsync(string procedureOrSql, object? param = null, CancellationToken ct = default) =>
        WithReaderAsync(procedureOrSql, param, r => SqlJson.ReadRowsAsync(r, ct), ct);

    public Task<List<JsonArray>> QueryJsonSetsAsync(string procedureOrSql, object? param = null, CancellationToken ct = default) =>
        WithReaderAsync(procedureOrSql, param, async r =>
        {
            var sets = new List<JsonArray>();
            do
            {
                if (r.FieldCount > 0) sets.Add(await SqlJson.ReadRowsAsync(r, ct));
            } while (await r.NextResultAsync(ct));
            return sets;
        }, ct);

    public Task<T> WithReaderAsync<T>(string procedureOrSql, object? param, Func<System.Data.Common.DbDataReader, Task<T>> read, CancellationToken ct = default) =>
        RunAsync(async cn =>
        {
            await using var reader = await cn.ExecuteReaderAsync(Cmd(procedureOrSql, param, ct));
            return await read(reader);
        }, ct);

    public Task<int> ExecuteCommandAsync(string procedureOrSql, Action<SqlParameterCollection> parameters, CancellationToken ct = default) =>
        RunAsync(async cn =>
        {
            await using var cmd = new SqlCommand(procedureOrSql, cn) { CommandType = TypeOf(procedureOrSql), CommandTimeout = _timeout };
            parameters(cmd.Parameters);
            foreach (SqlParameter p in cmd.Parameters)
            {
                if (p.Direction is ParameterDirection.Input or ParameterDirection.InputOutput) p.Value = SqlParameters.SqlHelperValue(p.Value is DBNull ? null : p.Value);
            }
            return await cmd.ExecuteNonQueryAsync(ct);
        }, ct);
}

/// <summary>Text helpers shared by data code.</summary>
public static class SqlText
{
    /// <summary>Escapes text for the @Data XML of core.usp_UiMaintSave exactly as MaintService.Esc (MaintService.vb:65-68).</summary>
    public static string XmlEscape(string value) =>
        new StringBuilder(value).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").ToString();
}
