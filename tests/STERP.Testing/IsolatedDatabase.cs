using System.Data;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;

namespace STERP.Testing;

/// <summary>
/// The isolated SQL Server instance used by the safety net. Before the first connection it checks that
/// the server is not the shared one and that it is not a default instance (the shared STERP database
/// lives on the default instance), then restores datasets and runs arrange/read statements.
/// </summary>
public static class IsolatedDatabase
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _verified;

    public static string CurrentWindowsLogin => $@"{Environment.UserDomainName}\{Environment.UserName}";

    public static string ConnectionString(string database) => new SqlConnectionStringBuilder
    {
        DataSource = ParityEnvironment.SqlServer,
        InitialCatalog = database,
        IntegratedSecurity = true,
        TrustServerCertificate = true,
        Encrypt = SqlConnectionEncryptOption.Optional,
        ConnectTimeout = 60,
        ApplicationName = "STERP-Agent07-Parity",
    }.ConnectionString;

    public static async Task VerifyIsolationAsync()
    {
        if (_verified) return;
        ParityEnvironment.AssertIsolatedServer(ParityEnvironment.SqlServer);
        await using var connection = new SqlConnection(ConnectionString("master"));
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT CAST(SERVERPROPERTY('InstanceName') AS nvarchar(128))", connection);
        var instance = await command.ExecuteScalarAsync();
        if (instance is null || instance is DBNull)
        {
            throw new InvalidOperationException(
                $"Refusing {ParityEnvironment.SqlServer}: it is a default instance; the isolated database must be a separate named or LocalDB instance.");
        }
        _verified = true;
    }

    /// <summary>Restores a dataset backup over the isolated database (single test at a time).</summary>
    public static async Task RestoreAsync(string dataset, string? databaseName = null)
    {
        await VerifyIsolationAsync();
        await Gate.WaitAsync();
        try
        {
            var backup = ParityEnvironment.BackupFor(dataset);
            if (!File.Exists(backup)) throw new FileNotFoundException($"Isolated dataset backup missing: {backup}");
            await using var connection = new SqlConnection(ConnectionString("master"));
            await connection.OpenAsync();
            var files = new List<(string Logical, string Type)>();
            await using (var list = new SqlCommand($"RESTORE FILELISTONLY FROM DISK = N'{Escape(backup)}'", connection))
            await using (var reader = await list.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync()) files.Add((reader.GetString(0), reader.GetString(2)));
            }
            var dataDir = Path.Combine(Path.GetDirectoryName(backup)!, "..", "data");
            Directory.CreateDirectory(dataDir);
            var db = databaseName ?? ParityEnvironment.Database;
            var moves = string.Join(", ", files.Select(f =>
                $"MOVE N'{Escape(f.Logical)}' TO N'{Escape(Path.GetFullPath(Path.Combine(dataDir, $"{db}.{f.Logical}.{(f.Type == "L" ? "ldf" : "mdf")}")))}'"));
            // A scenario fault ("database-offline") is always undone by its scenario; an interrupted run may still have
            // left the database OFFLINE, which SINGLE_USER cannot alter, so it is brought online first.
            var sql = $"IF DATABASEPROPERTYEX(N'{db}', 'Status') = 'OFFLINE' ALTER DATABASE [{db}] SET ONLINE; " +
                      $"IF DB_ID(N'{db}') IS NOT NULL ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
                      $"RESTORE DATABASE [{db}] FROM DISK = N'{Escape(backup)}' WITH REPLACE, {moves}; " +
                      $"ALTER DATABASE [{db}] SET MULTI_USER;";
            await using var restore = new SqlCommand(sql, connection) { CommandTimeout = 300 };
            await restore.ExecuteNonQueryAsync();
            SqlConnection.ClearAllPools();
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Maps the Windows identity running the tests to one STERP user (or to none). The audit rows that
    /// core.tr_AppUser_Audit writes for this arrange step are removed and the identity reseeded, so every
    /// scenario starts from the unmodified dataset audit history.
    /// </summary>
    public static async Task MapLoginAsync(string? userCode)
    {
        var login = CurrentWindowsLogin;
        var map = string.IsNullOrEmpty(userCode) || userCode == "<unmapped>"
            ? ""
            : " UPDATE core.AppUser SET WindowsLogin = @login WHERE UserCode = @user; IF @@ROWCOUNT <> 1 THROW 50000, 'unknown STERP user', 1;";
        var sql = "DECLARE @max bigint = (SELECT ISNULL(MAX(AuditId), 0) FROM core.AuditLog);" +
                  " UPDATE core.AppUser SET WindowsLogin = NULL WHERE WindowsLogin = @login;" + map +
                  " DELETE FROM core.AuditLog WHERE AuditId > @max AND EntityType = 'core.AppUser';" +
                  " DBCC CHECKIDENT ('core.AuditLog', RESEED, @max) WITH NO_INFOMSGS;";
        await ExecuteAsync(sql, ("@login", login), ("@user", userCode ?? ""));
    }

    /// <summary>
    /// GUI context arrange: maps the test login to the acting user and sets that user's default
    /// company/site (the browser shell signs on with them), removing the audit rows this arrange causes.
    /// </summary>
    public static async Task SetUserDefaultsAsync(string userCode, string? company, string? site)
    {
        if (company is null && site is null) return;
        var sql = "DECLARE @max bigint = (SELECT ISNULL(MAX(AuditId), 0) FROM core.AuditLog);" +
                  " UPDATE core.AppUser SET DefaultCompanyCode = COALESCE(@co, DefaultCompanyCode), DefaultSiteCode = COALESCE(@site, DefaultSiteCode) WHERE UserCode = @user;" +
                  " DELETE FROM core.AuditLog WHERE AuditId > @max AND EntityType = 'core.AppUser';" +
                  " DBCC CHECKIDENT ('core.AuditLog', RESEED, @max) WITH NO_INFOMSGS;";
        await ExecuteAsync(sql, ("@user", userCode), ("@co", (object?)company ?? DBNull.Value), ("@site", (object?)site ?? DBNull.Value));
    }

    public static async Task<JsonArray> QueryAsync(string sql, string? databaseName = null)
    {
        await VerifyIsolationAsync();
        await using var connection = new SqlConnection(ConnectionString(databaseName ?? ParityEnvironment.Database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new JsonArray();
        do
        {
            rows = new JsonArray();
            while (await reader.ReadAsync())
            {
                var row = new JsonObject();
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    var name = string.IsNullOrEmpty(reader.GetName(i)) ? $"col{i}" : reader.GetName(i);
                    row[name] = reader.IsDBNull(i) ? null : ToNode(reader.GetValue(i));
                }
                rows.Add(row);
            }
        } while (await reader.NextResultAsync() && reader.FieldCount > 0);
        return rows;
    }

    public static async Task ExecuteAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await VerifyIsolationAsync();
        await using var connection = new SqlConnection(ConnectionString(ParityEnvironment.Database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Highest core.JobRun id of the database as restored (suite isolation: runs above it belong to the scenario).</summary>
    public static async Task<long> MaxJobRunIdAsync()
    {
        var rows = await QueryAsync("SELECT CAST(ISNULL(MAX(JobRunId), 0) AS bigint) AS maxId FROM core.JobRun");
        return JsonPathLite.TryDecimal(rows.Count > 0 ? rows[0]?["maxId"] : null, out var max) ? (long)max : 0;
    }

    /// <summary>core.JobRun ids written after the dataset restore (above <paramref name="datasetMax"/>), in id order.</summary>
    public static async Task<IReadOnlyList<long>> JobRunIdsAboveAsync(long datasetMax)
    {
        var rows = await QueryAsync($"SELECT CAST(JobRunId AS bigint) AS id FROM core.JobRun WHERE JobRunId > {datasetMax} ORDER BY JobRunId");
        return rows.Select(r => JsonPathLite.TryDecimal(r?["id"], out var id) ? (long)id : 0).Where(id => id > 0).ToList();
    }

    /// <summary>True when the isolated database is ONLINE (state read from master, so it works while it is offline).</summary>
    public static async Task<bool> IsOnlineAsync()
    {
        var rows = await QueryAsync($"SELECT state_desc FROM sys.databases WHERE name = N'{Escape(ParityEnvironment.Database)}'", "master");
        return rows.Count > 0 && JsonPathLite.AsText(rows[0]?["state_desc"]) == "ONLINE";
    }

    /// <summary>
    /// Fault injection (scenario step "fault"): takes the isolated database OFFLINE (ROLLBACK IMMEDIATE) or ONLINE.
    /// Only after <see cref="VerifyIsolationAsync"/>; never the shared server.
    /// </summary>
    public static async Task SetDatabaseOnlineAsync(bool online)
    {
        await VerifyIsolationAsync();
        var db = ParityEnvironment.Database.Replace("]", "]]", StringComparison.Ordinal);
        await using (var connection = new SqlConnection(ConnectionString("master")))
        {
            await connection.OpenAsync();
            var sql = online ? $"ALTER DATABASE [{db}] SET ONLINE;" : $"ALTER DATABASE [{db}] SET OFFLINE WITH ROLLBACK IMMEDIATE;";
            await using var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
            await command.ExecuteNonQueryAsync();
        }
        SqlConnection.ClearAllPools();
    }

    /// <summary>True when the object (schema.name) or, with a column, the column exists in the isolated database.</summary>
    public static async Task<bool> ExistsAsync(string schemaQualifiedObject, string? column = null)
    {
        var sql = column is null
            ? "SELECT CASE WHEN OBJECT_ID(@o) IS NULL THEN 0 ELSE 1 END AS present"
            : "SELECT CASE WHEN COL_LENGTH(@o, @c) IS NULL THEN 0 ELSE 1 END AS present";
        await VerifyIsolationAsync();
        await using var connection = new SqlConnection(ConnectionString(ParityEnvironment.Database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        command.Parameters.AddWithValue("@o", schemaQualifiedObject);
        if (column is not null) command.Parameters.AddWithValue("@c", column);
        return Convert.ToInt32(await command.ExecuteScalarAsync()) == 1;
    }

    /// <summary>
    /// Fault injection (scenario step "fault"): sp_rename of one object (schema.name -> newName) or, with
    /// objectType COLUMN, of one column (schema.table.column -> newName) on the isolated database.
    /// </summary>
    public static async Task RenameAsync(string objectName, string newName, string? objectType = null)
    {
        await VerifyIsolationAsync();
        await using var connection = new SqlConnection(ConnectionString(ParityEnvironment.Database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            objectType is null
                ? "EXEC sp_rename @objname = @old, @newname = @new;"
                : "EXEC sp_rename @objname = @old, @newname = @new, @objtype = @type;", connection) { CommandTimeout = 300 };
        command.Parameters.AddWithValue("@old", objectName);
        command.Parameters.AddWithValue("@new", newName);
        if (objectType is not null) command.Parameters.AddWithValue("@type", objectType);
        await command.ExecuteNonQueryAsync();
        SqlConnection.ClearAllPools();
    }

    /// <summary>
    /// Fault injection (scenario step "fault" lockTable): opens a separate connection, begins a transaction and takes an
    /// exclusive table lock (TABLOCKX, HOLDLOCK) on schema.table; disposing the handle rolls back and closes it.
    /// </summary>
    public static async Task<IDisposable> LockTableAsync(string schemaQualifiedTable)
    {
        await VerifyIsolationAsync();
        var parts = schemaQualifiedTable.Split('.');
        var quoted = string.Join(".", parts.Select(p => "[" + p.Replace("]", "]]", StringComparison.Ordinal) + "]"));
        var connection = new SqlConnection(ConnectionString(ParityEnvironment.Database) + ";Pooling=false");
        await connection.OpenAsync();
        try
        {
            var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await using (var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM {quoted} WITH (TABLOCKX, HOLDLOCK);", connection, transaction) { CommandTimeout = 300 })
            {
                await command.ExecuteScalarAsync();
            }
            return new HeldLock(connection, transaction);
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class HeldLock(SqlConnection connection, SqlTransaction transaction) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                if (connection.State == ConnectionState.Open) transaction.Rollback();
            }
            catch (InvalidOperationException)
            {
                // the session is already gone (for example killed by a restore): nothing is held any more
            }
            catch (SqlException)
            {
            }
            finally
            {
                transaction.Dispose();
                connection.Dispose();
            }
        }
    }

    private static JsonNode? ToNode(object value) => value switch
    {
        string s => JsonValue.Create(s),
        decimal d => JsonValue.Create(d),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        short sh => JsonValue.Create(sh),
        byte b => JsonValue.Create(b),
        bool bo => JsonValue.Create(bo),
        double db => JsonValue.Create((decimal)db),
        DateTime dt => JsonValue.Create(dt.ToString("yyyy-MM-ddTHH:mm:ss.fffffff")),
        DateOnly d0 => JsonValue.Create(d0.ToString("yyyy-MM-dd")),
        byte[] bytes => JsonValue.Create(Convert.ToBase64String(bytes)),
        Guid g => JsonValue.Create(g.ToString()),
        _ => JsonValue.Create(value.ToString()),
    };

    private static string Escape(string text) => text.Replace("'", "''");
}
