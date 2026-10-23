using System.Text.RegularExpressions;
using STERP.Testing;

namespace STERP.Legacy.Characterization;

/// <summary>Runs the legacy COBOL unit-driver layer exactly as build\run-tests.ps1 does (alphabetical, one database).</summary>
public sealed class CobolLayerRun : IAsyncLifetime
{
    public Dictionary<string, IReadOnlyList<string>> Slices { get; } = new(StringComparer.Ordinal);

    public static IEnumerable<string> Drivers() =>
        Directory.Exists(LegacyRuntime.TestBinDirectory)
            ? Directory.GetFiles(LegacyRuntime.TestBinDirectory, "T*.exe").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(n => n, StringComparer.Ordinal)
            : [];

    public async ValueTask InitializeAsync()
    {
        await IsolatedDatabase.RestoreAsync("fresh");
        LegacyRuntime.ResetRunFolders();
        foreach (var driver in Drivers()) await RunAsync("freshc", driver, "STERP");

        await IsolatedDatabase.RestoreAsync("fresh", "STERP_QA_A07");
        LegacyRuntime.ResetRunFolders();
        await RunAsync("qa", "TPRCVTX", "STERP_QA_A07");

        await IsolatedDatabase.RestoreAsync("demo");
        LegacyRuntime.ResetRunFolders();
        foreach (var driver in new[] { "TKITDEMO", "TKITGAP1" }) await RunAsync("democ", driver, "STERP");
    }

    private async Task RunAsync(string dataset, string driver, string database)
    {
        var exe = Path.Combine(LegacyRuntime.TestBinDirectory, driver + ".exe");
        var (code, lines, _) = await LegacyRuntime.RunAsync($"\"{exe}\"", LegacyRuntime.TestBinDirectory, database);
        var slice = lines.Append($"# exit {code}").ToList();
        Slices[$"{dataset}/{driver}"] = slice;
        Characterization.WriteRunOutput($"cobol/{dataset}/{driver}.tap", slice);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class CobolDriverCharacterizationTests(CobolLayerRun run) : IClassFixture<CobolLayerRun>
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var d in CobolLayerRun.Drivers()) data.Add("freshc", d);
        data.Add("qa", "TPRCVTX");
        data.Add("democ", "TKITDEMO");
        data.Add("democ", "TKITGAP1");
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    [Trait("tier", "legacy-characterization")]
    [Trait("layer", "unit")]
    public void Cobol_driver_output_matches_pinned_legacy_observation(string dataset, string driver)
    {
        Assert.True(run.Slices.TryGetValue($"{dataset}/{driver}", out var lines), $"{driver} did not run");
        Characterization.Pin($"cobol/{dataset}/{driver}.tap", lines);
    }
}

/// <summary>Runs the legacy SQL test layer (build-db.ps1 -NoDeploy -Tests) on a fresh deploy and po-approval-sync on the demo dataset.</summary>
public sealed partial class SqlLayerRun : IAsyncLifetime
{
    public Dictionary<string, IReadOnlyList<string>> Slices { get; } = new(StringComparer.Ordinal);

    public static IEnumerable<string> TestFiles() =>
        Directory.Exists(Path.Combine(LegacyRuntime.Root, "db", "tests"))
            ? Directory.GetFiles(Path.Combine(LegacyRuntime.Root, "db", "tests"), "*.sql").Select(Path.GetFileNameWithoutExtension).OfType<string>().OrderBy(n => n, StringComparer.Ordinal)
            : [];

    public async ValueTask InitializeAsync()
    {
        await IsolatedDatabase.RestoreAsync("fresh");
        LegacyRuntime.ResetRunFolders();
        await RunAsync("freshc", "");

        await IsolatedDatabase.RestoreAsync("demo");
        LegacyRuntime.ResetRunFolders();
        await RunAsync("democ", " -Only po-approval-sync");
    }

    private async Task RunAsync(string dataset, string extra)
    {
        var script = Path.Combine(LegacyRuntime.Root, "build", "build-db.ps1");
        var (_, lines, _) = await LegacyRuntime.RunAsync(
            $"powershell.exe -NoProfile -ExecutionPolicy Bypass -File \"{script}\" -NoDeploy -Tests -Quiet{extra}",
            LegacyRuntime.Root, "STERP");
        Characterization.WriteRunOutput($"sql/{dataset}/sql-tests.log", lines);
        var current = "preamble";
        var section = new List<string>();
        foreach (var line in lines)
        {
            var header = Header().Match(line);
            if (header.Success || line.StartsWith("# SQL tests ", StringComparison.Ordinal))
            {
                Append(dataset, current, section);
                current = header.Success ? header.Groups[1].Value : "summary";
                section = [];
            }
            section.Add(line);
        }
        Append(dataset, current, section);
    }

    private void Append(string dataset, string name, List<string> lines)
    {
        var key = $"{dataset}/{name}";
        Slices[key] = Slices.TryGetValue(key, out var existing) ? [.. existing, .. lines] : lines;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [GeneratedRegex(@"^# ([a-z0-9][a-z0-9\-]*)$")]
    private static partial Regex Header();
}

public sealed class SqlLayerCharacterizationTests(SqlLayerRun run) : IClassFixture<SqlLayerRun>
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var f in SqlLayerRun.TestFiles()) data.Add("freshc", f);
        data.Add("freshc", "summary");
        data.Add("democ", "po-approval-sync");
        data.Add("democ", "summary");
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    [Trait("tier", "legacy-characterization")]
    [Trait("layer", "integration")]
    public void Sql_test_file_matches_pinned_legacy_observation(string dataset, string testFile)
    {
        Assert.True(run.Slices.TryGetValue($"{dataset}/{testFile}", out var lines), $"{testFile} produced no output on {dataset}");
        Characterization.Pin($"sql/{dataset}/{testFile}.log", lines);
    }
}

/// <summary>Runs the legacy .NET acceptance/integration harness (bin\x86\STERP.Tests.exe, TAP) end to end through .NET, COBOL and SQL.</summary>
public sealed partial class DotNetHarnessRun : IAsyncLifetime
{
    public static readonly (string Label, string Database, string Filter)[] QaRuns =
    [
        ("qa-ap", "STERP_QA_AP_A07", "^Ap(BacsFormat|DemoFixture|WorkflowGui)Tests$"),
        ("qa-bom", "STERP_QA_BOM_A07", "^BomBulkGuiTests$"),
        ("qa-credit", "STERP_QA_CREDIT_A07", "^CreditOverrideTests$"),
        ("qa-vat", "STERP_QA_VAT_A07", "^VatWorkflowGuiTests$"),
    ];

    public Dictionary<string, IReadOnlyList<string>> Slices { get; } = new(StringComparer.Ordinal);

    /// <summary>Test display name -> declaring class, from the [StTest("...")] attributes of the pinned source.</summary>
    public static IReadOnlyDictionary<string, string> TestClasses { get; } = ScanClasses();

    public static IEnumerable<string> Classes() => TestClasses.Values.Distinct().OrderBy(c => c, StringComparer.Ordinal);

    public async ValueTask InitializeAsync()
    {
        try
        {
            await RunAsync("freshc", "fresh", "STERP", null);
            await RunAsync("democ", "demo", "STERP", null);
            foreach (var (label, database, filter) in QaRuns) await RunAsync(label, "fresh", database, filter);
        }
        finally
        {
            LegacyRuntime.PinDotNetConfiguration("STERP");
        }
    }

    private async Task RunAsync(string label, string dataset, string database, string? filter)
    {
        await IsolatedDatabase.RestoreAsync(dataset, database);
        LegacyRuntime.ResetRunFolders();
        LegacyRuntime.PinDotNetConfiguration(database);
        var harness = Path.Combine(LegacyRuntime.BinDirectory, "STERP.Tests.exe");
        var args = filter is null ? "" : $" \"/filter:{filter}\"";
        var (code, lines, _) = await LegacyRuntime.RunAsync($"\"{harness}\"{args}", LegacyRuntime.BinDirectory, database);
        Characterization.WriteRunOutput($"net/{label}/STERP.Tests.tap", lines.Append($"# exit {code}"));
        string? current = null;
        foreach (var line in lines)
        {
            var test = TestLine().Match(line);
            if (test.Success)
            {
                var name = test.Groups["name"].Value;
                current = TestClasses.TryGetValue(name, out var cls) ? cls : "UNMAPPED";
                Add($"{label}/{current}", Renumbered().Replace(line, "${s} -"));
            }
            else if (line.StartsWith('#') && current is not null && !line.StartsWith("# tests ", StringComparison.Ordinal))
            {
                Add($"{label}/{current}", line);
            }
            else
            {
                Add($"{label}/harness", line);
            }
        }
    }

    private void Add(string key, string line)
    {
        if (!Slices.TryGetValue(key, out var list)) Slices[key] = list = new List<string>();
        ((List<string>)list).Add(line);
    }

    private static Dictionary<string, string> ScanClasses()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var dir = Path.Combine(LegacyRuntime.Root, "dotnet", "STERP.Tests");
        if (!Directory.Exists(dir)) return map;
        foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories).Where(f => !f.Contains(@"\obj\", StringComparison.Ordinal)))
        {
            string? cls = null;
            foreach (var line in File.ReadLines(file))
            {
                var c = ClassDecl().Match(line);
                if (c.Success) cls = c.Groups[1].Value;
                var t = StTestDecl().Match(line);
                if (t.Success && cls is not null) map[Regex.Unescape(t.Groups[1].Value)] = cls;
            }
        }
        return map;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [GeneratedRegex(@"^(?<s>ok|not ok) \d+ - (?<name>.+?)(?:  # | # SKIP|$)")]
    private static partial Regex TestLine();

    [GeneratedRegex(@"^(?<s>ok|not ok) \d+ -")]
    private static partial Regex Renumbered();

    [GeneratedRegex(@"\bclass\s+(\w+)")]
    private static partial Regex ClassDecl();

    [GeneratedRegex(@"\[StTest\(""((?:[^""\\]|\\.)*)""")]
    private static partial Regex StTestDecl();
}

public sealed class DotNetHarnessCharacterizationTests(DotNetHarnessRun run) : IClassFixture<DotNetHarnessRun>
{
    public static TheoryData<string, string> Cases()
    {
        var data = new TheoryData<string, string>();
        foreach (var label in new[] { "freshc", "democ" })
        {
            foreach (var cls in DotNetHarnessRun.Classes()) data.Add(label, cls);
            data.Add(label, "harness");
        }
        data.Add("qa-ap", "ApBacsFormatTests");
        data.Add("qa-ap", "ApDemoFixtureTests");
        data.Add("qa-ap", "ApWorkflowGuiTests");
        data.Add("qa-bom", "BomBulkGuiTests");
        data.Add("qa-credit", "CreditOverrideTests");
        data.Add("qa-vat", "VatWorkflowGuiTests");
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    [Trait("tier", "legacy-characterization")]
    [Trait("layer", "api-journey")]
    public void Harness_class_matches_pinned_legacy_observation(string label, string testClass)
    {
        var lines = run.Slices.TryGetValue($"{label}/{testClass}", out var found) ? found : [];
        Characterization.Pin($"net/{label}/{testClass}.tap", lines);
    }
}
