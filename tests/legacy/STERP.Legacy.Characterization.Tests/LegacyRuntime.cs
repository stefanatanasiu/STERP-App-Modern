using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using STERP.Testing;

namespace STERP.Legacy.Characterization;

/// <summary>
/// Launches the isolated legacy runtime (binaries built from the pinned commit in the profile
/// isolated_test_root) with exactly the environment of build/env.ps1, pointed at the isolated instance.
/// </summary>
public static class LegacyRuntime
{
    public static string Root => ParityEnvironment.Get("STERP_LEGACY_ROOT", Path.Combine(ParityEnvironment.IsolatedAttemptRoot, "legacy"));

    public static string GnuCobol => ParityEnvironment.Get("STERP_GNUCOBOL", Path.Combine(ParityEnvironment.WorkspaceRoot, "GC32-BDB-SP1")).TrimEnd('\\') + "\\";

    public static string SqlcmdDirectory => ParityEnvironment.Get("STERP_SQLCMD_DIR",
        Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "Microsoft SQL Server", "Client SDK", "ODBC", "180", "Tools", "Binn"));

    public static string OutputRoot => ParityEnvironment.Get("STERP_LEGACY_OUTPUT", Path.Combine(ParityEnvironment.IsolatedAttemptRoot, "out", "characterization"));

    public static string BinDirectory => Path.Combine(Root, "bin", "x86");

    /// <summary>The OEM console code page the legacy console programs write with.</summary>
    public static Encoding ConsoleEncoding { get; } = CreateConsoleEncoding();

    private static Encoding CreateConsoleEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
    }

    public static string TestBinDirectory => Path.Combine(Root, "bin", "tests");

    public static string Odbc(string database) =>
        $"Driver={{ODBC Driver 18 for SQL Server}};Server={ParityEnvironment.SqlServer};Database={database};Trusted_Connection=yes;TrustServerCertificate=yes;MARS_Connection=yes;APP=STERP-COBOL";

    public static string AdoNet(string database) =>
        $"Data Source={ParityEnvironment.SqlServer};Initial Catalog={database};Integrated Security=SSPI";

    public static IReadOnlyDictionary<string, string> Environment(string database)
    {
        ParityEnvironment.AssertIsolatedServer(ParityEnvironment.SqlServer);
        var path = System.Environment.GetEnvironmentVariable("PATH") ?? "";
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["STERP_SQLSERVER"] = ParityEnvironment.SqlServer,
            ["STERP_DATABASE"] = database,
            ["STERP_ODBC"] = Odbc(database),
            ["STERP_RUN"] = Path.Combine(Root, "run"),
            ["STERP_GNUCOBOL"] = GnuCobol.TrimEnd('\\'),
            ["COB_MAIN_DIR"] = GnuCobol,
            ["COB_CONFIG_DIR"] = GnuCobol + "config",
            ["COB_COPY_DIR"] = GnuCobol + "copy",
            ["COB_CFLAGS"] = $"-I\"{GnuCobol}include\"",
            ["COB_LDFLAGS"] = $"-L\"{GnuCobol}lib\"",
            ["COB_LIBRARY_PATH"] = GnuCobol + "extras",
            ["COB_EXIT_WAIT"] = "0",
            ["PATH"] = $"{GnuCobol}bin;{SqlcmdDirectory};{path}",
        };
    }

    /// <summary>Runs a command through cmd.exe so stdout and stderr interleave exactly as in the legacy runners (2>&1).</summary>
    public static async Task<(int ExitCode, string[] Lines, TimeSpan Elapsed)> RunAsync(string command, string workingDirectory, string database)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/d /s /c \"{command} 2>&1\"")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = ConsoleEncoding,
            StandardErrorEncoding = ConsoleEncoding,
            CreateNoWindow = true,
        };
        foreach (var (k, v) in Environment(database)) start.Environment[k] = v;
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        watch.Stop();
        var text = (await stdout) + (await stderr);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (lines.Length > 0 && lines[^1].Length == 0) lines = lines[..^1];
        return (process.ExitCode, lines, watch.Elapsed);
    }

    /// <summary>Empties the runtime folders that jobs\SETENV.cmd:24-26 creates for a legacy installation.</summary>
    public static void ResetRunFolders()
    {
        var run = Path.Combine(Root, "run");
        foreach (var name in new[] { "logs", "spool", "bacs", "mtd", "einvoice", "extracts", "work" })
        {
            var dir = Path.Combine(run, name);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            Directory.CreateDirectory(dir);
        }
    }

    /// <summary>Pins the legacy .NET layer (STERP.INI and appSettings) to the isolated instance and database.</summary>
    public static void PinDotNetConfiguration(string database)
    {
        File.WriteAllLines(Path.Combine(BinDirectory, "STERP.INI"),
        [
            "; Agent 07 isolated legacy oracle environment - pins the owned isolated instance",
            $"STERP.Sql={AdoNet(database)}",
            $"STERP.Odbc={Odbc(database)}",
            "STERP.Trace=N",
        ], Encoding.ASCII);
        foreach (var config in Directory.GetFiles(BinDirectory, "*.config"))
        {
            var xml = XDocument.Load(config);
            var changed = false;
            foreach (var add in xml.Descendants("appSettings").Elements("add"))
            {
                var key = (string?)add.Attribute("key");
                if (key == "STERP.Sql") { add.SetAttributeValue("value", AdoNet(database)); changed = true; }
                if (key == "STERP.Odbc") { add.SetAttributeValue("value", Odbc(database)); changed = true; }
            }
            if (changed) xml.Save(config);
            var text = File.ReadAllText(config);
            if (text.Contains("Data Source=.;", StringComparison.OrdinalIgnoreCase) || text.Contains("Server=.;", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{config} still names the shared server");
            }
        }
    }
}
