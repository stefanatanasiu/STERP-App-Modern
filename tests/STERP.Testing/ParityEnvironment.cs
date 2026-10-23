namespace STERP.Testing;

/// <summary>
/// Locations and endpoints for the safety net. Every database address is the isolated test instance
/// (profile isolated_environments.database); the shared database (profile target.shared_database,
/// server ".") is refused before any connection is opened.
/// </summary>
public static class ParityEnvironment
{
    public static readonly string[] ForbiddenServers = [".", "(local)", "localhost", "127.0.0.1", Environment.MachineName];

    public static string TestsRoot { get; } = FindTestsRoot();

    /// <summary>
    /// The folder that holds this repository next to the legacy STERP-App clone, the isolated-test scratch area
    /// (run\STERP-App-isolated-tests) and GnuCOBOL (GC32-BDB-SP1): the base of every relative default below.
    /// </summary>
    public static string WorkspaceRoot => Path.GetFullPath(Path.Combine(TestsRoot, "..", ".."));

    /// <summary>The isolated test root of this attempt (backups, legacy runtime, characterization output).</summary>
    public static string IsolatedAttemptRoot => Path.Combine(WorkspaceRoot, "run", "STERP-App-isolated-tests", "a07-b88d28f6");

    public static string ContractsDirectory => Path.Combine(TestsRoot, "contracts");

    public static string CasesDirectory => Path.Combine(TestsRoot, "cases");

    public static string ApiBaseUrl => Get("STERP_TARGET_API_URL", "https://localhost:7101");

    public static string JobsBaseUrl => Get("STERP_TARGET_JOBS_URL", "https://localhost:7102");

    public static string WebBaseUrl => Get("STERP_TARGET_WEB_URL", "https://localhost:7100");

    public static string PortalBaseUrl => Get("STERP_TARGET_PORTAL_URL", "https://localhost:7103");

    public static string SqlServer => Get("STERP_PARITY_SQLSERVER", @"(localdb)\STERPA07_b88d28f6");

    public static string Database => Get("STERP_PARITY_DATABASE", "STERP");

    public static string BackupDirectory => Get("STERP_PARITY_BACKUPS", Path.Combine(IsolatedAttemptRoot, "backups"));

    /// <summary>Dataset name in a scenario -> backup of the isolated legacy deploy (freshc = build-db, democ = + demo-data).</summary>
    public static string BackupFor(string dataset) => Path.Combine(BackupDirectory, dataset switch
    {
        "fresh" => "freshc.bak",
        "demo" => "democ.bak",
        _ => throw new ArgumentException($"Unknown dataset {dataset}"),
    });

    /// <summary>
    /// Bound for a job step's poll and for the suite-isolation wait at the end of every scenario
    /// (runs the scenario started that the job host still executes).
    /// </summary>
    public static TimeSpan JobTimeout => TimeSpan.FromSeconds(int.Parse(Get("STERP_PARITY_JOB_TIMEOUT_SECONDS", "600")));

    /// <summary>
    /// Sterp:Jobs:OutputRoot of the running target hosts (scripts/run-parity.ps1 exports it). Required by `file`
    /// steps with root "target-output" and by the ${outputRoot} placeholder; no default, so a fixture is never
    /// written to a folder the hosts do not read.
    /// </summary>
    public static string? TargetOutputRoot => Optional("STERP_TARGET_OUTPUT_ROOT");

    /// <summary>
    /// Sterp:Jobs:LegacyRunRoot of the running target hosts: the isolated scratch stand-in for the legacy run root
    /// ..\STERP-App\run (contracts 8.5), exported by scripts/run-parity.ps1. Required by `file` steps with
    /// root "legacy-run" and by ${legacyRunRoot}. The live legacy application folder is refused.
    /// </summary>
    public static string? TargetLegacyRunRoot => Optional("STERP_TARGET_LEGACY_RUN_ROOT");

    /// <summary>
    /// PowerShell command that kills one target host and starts it again with the same configuration, exiting 0
    /// once it is ready ({host} = Api, Jobs, Web or Portal); exported by scripts/run-parity.ps1 and used only by the
    /// `fault` step restartHost / "jobs-host-restart".
    /// </summary>
    public static string? TargetRestartCommand => Optional("STERP_TARGET_RESTART_COMMAND");

    /// <summary>The live legacy application folder (its run root holds real legacy spool): never written.</summary>
    public static string LiveLegacyApplicationFolder => Path.Combine(WorkspaceRoot, "STERP-App");

    /// <summary>Hashed fixture files (captured from attributable legacy runs) that `file` steps copy.</summary>
    public static string FixturesDirectory => Path.Combine(TestsRoot, "parity", "STERP.Target.Parity.Tests", "fixtures");

    /// <summary>
    /// Resolves a configured writable root: set, fully qualified and outside the live legacy application folder.
    /// </summary>
    public static string RequireWritableRoot(string variable, string purpose)
    {
        var value = Optional(variable) ?? throw new InvalidOperationException(
            $"{variable} is not set: {purpose}. scripts/run-parity.ps1 exports it with the hosts' configuration.");
        if (!Path.IsPathFullyQualified(value))
        {
            throw new InvalidOperationException($"{variable}={value} is not a fully qualified folder.");
        }
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (IsUnder(full, LiveLegacyApplicationFolder))
        {
            throw new InvalidOperationException(
                $"Refusing {variable}={value}: it lies in the live legacy application folder {LiveLegacyApplicationFolder}; " +
                "the safety net writes only to isolated scratch roots.");
        }
        return full;
    }

    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or lies below it (case-insensitive, full paths).</summary>
    public static bool IsUnder(string path, string root)
    {
        var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(p, r, StringComparison.OrdinalIgnoreCase) ||
               p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string? Optional(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static void AssertIsolatedServer(string server)
    {
        var trimmed = server.Trim();
        foreach (var forbidden in ForbiddenServers)
        {
            if (string.Equals(trimmed, forbidden, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(trimmed, forbidden + @"\MSSQLSERVER", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Refusing to use {server}: it is the shared database server; tests run only on the isolated instance.");
            }
        }
    }

    public static string Get(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static string FindTestsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "contracts", "binding-manifest.json")) &&
                File.Exists(Path.Combine(dir.FullName, "STERP.Parity.Tests.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate the tests root (contracts/binding-manifest.json).");
    }
}
