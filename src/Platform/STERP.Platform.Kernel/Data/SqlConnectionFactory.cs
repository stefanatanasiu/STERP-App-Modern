using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace Sterp.Platform.Kernel.Data;

/// <summary>Database configuration (contracts 5.6, 6.2-6.3). No secret exists: integrated security only.</summary>
public sealed class SterpDatabaseOptions
{
    public const string Section = "Sterp:Database";

    /// <summary>Binding production default (contracts 5.6); the application name is set per host.</summary>
    public string ConnectionString { get; set; } =
        "Server=.;Database=STERP;Integrated Security=True;Encrypt=Mandatory;TrustServerCertificate=True";

    public string ApplicationName { get; set; } = "STERP.Api";

    /// <summary>When true the host refuses to start against the shared server (development and parity runs).</summary>
    public bool IsolatedOnly { get; set; }

    /// <summary>Legacy SqlHelper command timeout (dotnet/STERP.Data/SqlHelper.cs:18).</summary>
    public int CommandTimeoutSeconds { get; set; } = 120;

    /// <summary>
    /// Text the legacy ODBC shim prefixed to a server message (FR-STERP-PLATFORM-010; sterpsql.c diag()).
    /// </summary>
    public string LegacyDriverMessagePrefix { get; set; } = "[Microsoft][ODBC Driver 18 for SQL Server][SQL Server]";
}

public interface ISqlConnectionFactory
{
    string DataSource { get; }
    string Database { get; }
    SqlConnection Create();
}

public sealed class SqlConnectionFactory : ISqlConnectionFactory
{
    private static readonly string[] SharedServerNames = [".", "(local)", "localhost", "127.0.0.1"];
    private readonly string _connectionString;

    public SqlConnectionFactory(IOptions<SterpDatabaseOptions> options)
    {
        var o = options.Value;
        var b = new SqlConnectionStringBuilder(o.ConnectionString) { ApplicationName = o.ApplicationName };
        // No pool blocking period unless configured: SqlClient's default refuses every open with the cached error for up to
        // 60 s after a failed one, so requests kept failing after the database was back. The legacy batch programs and
        // COBOL engine open one connection per run and see the database as soon as it is available again.
        if (!Configured(o.ConnectionString, "Pool Blocking Period", "PoolBlockingPeriod"))
        {
            b.PoolBlockingPeriod = PoolBlockingPeriod.NeverBlock;
        }
        // SqlClient retries an open refused with a transient error (4060: database cannot be opened) after
        // ConnectRetryInterval (default 10 s); the legacy ODBC connect fails at once. 1 s keeps the idle-connection
        // recovery (ConnectRetryCount) and makes an unavailable database answer quickly.
        if (!Configured(o.ConnectionString, "Connect Retry Interval", "ConnectRetryInterval"))
        {
            b.ConnectRetryInterval = 1;
        }
        DataSource = b.DataSource;
        Database = b.InitialCatalog;
        if (o.IsolatedOnly) AssertIsolated(DataSource);
        _connectionString = b.ConnectionString;
    }

    public string DataSource { get; }
    public string Database { get; }

    /// <summary>The effective connection string (application name and pool settings applied).</summary>
    public string ConnectionString => _connectionString;

    public SqlConnection Create() => new(_connectionString);

    private static bool Configured(string connectionString, params string[] keywords) =>
        connectionString.Split(';').Select(p => p.Split('=')[0].Trim()).Any(k => keywords.Contains(k, StringComparer.OrdinalIgnoreCase));

    /// <summary>Profile hazard guard: the shared database (server ".") is never contacted by development or test runs.</summary>
    public static void AssertIsolated(string dataSource)
    {
        var s = (dataSource ?? "").Trim();
        if (s.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) s = s[4..];
        if (s.StartsWith("np:", StringComparison.OrdinalIgnoreCase)) s = s[3..];
        var host = s.Split('\\', ',')[0];
        var instance = s.Contains('\\') ? s.Split('\\')[1].Split(',')[0] : "";
        var isShared = SharedServerNames.Any(n => string.Equals(host, n, StringComparison.OrdinalIgnoreCase)) ||
                       string.Equals(host, Environment.MachineName, StringComparison.OrdinalIgnoreCase);
        var isDefaultInstance = instance.Length == 0 || string.Equals(instance, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase);
        if (isShared && isDefaultInstance || s.Length == 0)
        {
            throw new InvalidOperationException(
                $"Refusing database server '{dataSource}': Sterp:Database:IsolatedOnly is set and this is the shared server. " +
                "Use the isolated instance declared in the application profile (isolated_environments.database).");
        }
    }
}
