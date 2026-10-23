using System.Text.Json.Nodes;
using STERP.Testing;

namespace STERP.Legacy.Characterization;

public sealed class FreshDatabase : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await IsolatedDatabase.RestoreAsync("fresh");

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>Legacy menu oracle: core.usp_UiMenuLoad per seeded user at the user's default company/site (contracts 9.1/9.2).</summary>
public sealed class MenuOracleTests : IClassFixture<FreshDatabase>
{
    public static TheoryData<string, string, string> Users() => new()
    {
        { "APCLERK", "STC", "RDG" }, { "APMGR", "STC", "RDG" }, { "ARCLERK", "STC", "RDG" }, { "AUDITOR", "STC", "RDG" },
        { "BUYER", "STC", "BHM" }, { "COSTACC", "STC", "RDG" }, { "CREDMGR", "STC", "RDG" }, { "DIRECTOR", "STC", "RDG" },
        { "ENGINEER", "STC", "RDG" }, { "ENGMGR", "STC", "RDG" }, { "FINCTRL", "STC", "RDG" }, { "GLCLERK", "STC", "RDG" },
        { "INVMGR", "STC", "BHM" }, { "OPS", "STC", "RDG" }, { "OPSCAL", "CAL", "GLW" }, { "OPSVIEW", "STC", "RDG" },
        { "PURMGR", "STC", "BHM" }, { "QUALITY", "STC", "BHM" }, { "RESTRICT", "STC", "RDG" }, { "SALESMGR", "STC", "RDG" },
        { "SALESREP", "STC", "RDG" }, { "SYSADM", "STC", "RDG" }, { "TREASURY", "STC", "RDG" }, { "TREASURY2", "STC", "RDG" },
        { "VATOWNER", "STC", "RDG" }, { "WHSE", "STC", "BHM" },
    };

    [Theory]
    [MemberData(nameof(Users))]
    [Trait("tier", "legacy-characterization")]
    [Trait("layer", "integration")]
    public async Task Menu_for_user_matches_pinned_legacy_observation(string user, string company, string site)
    {
        var rows = await IsolatedDatabase.QueryAsync(
            $"EXEC core.usp_UiMenuLoad @UserCode = '{user}', @CompanyCode = '{company}', @SiteCode = '{site}'");
        Characterization.PinJson($"menu/{user}.json", rows);
    }
}

/// <summary>
/// The frozen shared-database contract (contracts 6.1/6.11, tests/contracts/sql-object-mapping.csv) observed on the
/// isolated legacy deploy: every drift-checked object exists with its legacy kind, and the column/parameter
/// catalogue of each schema is pinned (the snapshot the target drift check must equal).
/// </summary>
public sealed class DataContractCharacterizationTests : IClassFixture<FreshDatabase>
{
    private static readonly Dictionary<string, string[]> KindTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["TABLE"] = ["U"], ["VIEW"] = ["V"], ["PROCEDURE"] = ["P"], ["FUNCTION"] = ["FN", "IF", "TF"],
        ["TRIGGER"] = ["TR"], ["INDEX"] = [], ["DATABASE"] = [], ["SCHEMA"] = [], ["JOB"] = [], ["TYPE"] = [],
    };

    public static TheoryData<string> Schemas()
    {
        var data = new TheoryData<string>();
        foreach (var s in Contract().Select(r => SchemaOf(r.Object)).Where(s => s.Length > 0).Distinct().OrderBy(s => s, StringComparer.Ordinal))
        {
            data.Add(s);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(Schemas))]
    [Trait("tier", "legacy-characterization")]
    [Trait("layer", "integration")]
    public async Task Schema_matches_frozen_data_contract_on_isolated_legacy_deploy(string schema)
    {
        var missing = new List<string>();
        foreach (var (obj, kind) in Contract().Where(r => SchemaOf(r.Object) == schema))
        {
            if (kind == "INDEX")
            {
                var parts = obj.Split('#');
                var idx = await IsolatedDatabase.QueryAsync(
                    $"SELECT COUNT(*) AS n FROM sys.indexes WHERE object_id = OBJECT_ID(N'{parts[0]}') AND name = N'{parts[1]}'");
                if (JsonPathLite.AsText(idx[0]!["n"]) != "1") missing.Add($"INDEX {obj}");
                continue;
            }
            if (!KindTypes.TryGetValue(kind, out var types) || types.Length == 0) continue;
            var rows = await IsolatedDatabase.QueryAsync(
                $"SELECT type FROM sys.objects WHERE object_id = OBJECT_ID(N'{obj.Replace("'", "''")}')");
            var type = rows.Count == 0 ? null : JsonPathLite.AsText(rows[0]!["type"])?.Trim();
            if (type is null || !types.Contains(type)) missing.Add($"{kind} {obj} (found {type ?? "none"})");
        }
        Assert.True(missing.Count == 0, "Frozen contract objects absent from the isolated legacy deploy: " + string.Join("; ", missing));
        var catalogue = await IsolatedDatabase.QueryAsync($"""
            SELECT o.name AS objectName, o.type_desc AS objectType, c.column_id AS ordinal, c.name AS columnName,
                   TYPE_NAME(c.user_type_id) AS dataType, c.max_length AS maxLength, c.precision, c.scale, c.is_nullable AS isNullable
            FROM sys.objects o JOIN sys.columns c ON c.object_id = o.object_id
            WHERE SCHEMA_NAME(o.schema_id) = N'{schema}' AND o.type IN ('U','V','TF','IF')
            UNION ALL
            SELECT o.name, o.type_desc, p.parameter_id, p.name, TYPE_NAME(p.user_type_id), p.max_length, p.precision, p.scale, p.is_nullable
            FROM sys.objects o JOIN sys.parameters p ON p.object_id = o.object_id
            WHERE SCHEMA_NAME(o.schema_id) = N'{schema}' AND o.type IN ('P','FN','IF','TF')
            ORDER BY 1, 2, 3
            """);
        Characterization.PinJson($"data-contract/{schema}.json", catalogue);
    }

    private static string SchemaOf(string obj)
    {
        var dot = obj.IndexOf('.');
        return dot > 0 ? obj[..dot] : "";
    }

    private static IEnumerable<(string Object, string Kind)> Contract()
    {
        var file = BindingContracts.ContractFile("sql-object-mapping.csv");
        foreach (var line in File.ReadLines(file).Skip(1))
        {
            var cells = SplitCsv(line);
            if (cells.Count < 15 || cells[14] != "yes") continue;
            yield return (cells[0], cells[1]);
        }
    }

    private static List<string> SplitCsv(string line)
    {
        var cells = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else if (ch == '"') quoted = false;
                else sb.Append(ch);
            }
            else if (ch == '"') quoted = true;
            else if (ch == ',') { cells.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(ch);
        }
        cells.Add(sb.ToString());
        return cells;
    }
}

/// <summary>
/// First legacy performance baseline (Agent 06 04-application-integration-and-runtime-design.md section 10, SME-008):
/// elapsed time of the legacy demo-workflow acceptance flows through .NET, COBOL and SQL on the isolated runtime.
/// </summary>
public sealed class PerformanceBaselineTests
{
    public static TheoryData<string> Workflows() =>
        new("OrderToCashTests", "ProcureToPayTests", "PeriodCloseTests", "AssemblyKitTests", "CostingBomTests", "ControlsTests");

    [Theory]
    [MemberData(nameof(Workflows))]
    [Trait("tier", "legacy-characterization")]
    [Trait("layer", "e2e")]
    public async Task Legacy_demo_workflow_elapsed_time_is_recorded_as_baseline(string workflow)
    {
        await IsolatedDatabase.RestoreAsync("demo");
        LegacyRuntime.ResetRunFolders();
        LegacyRuntime.PinDotNetConfiguration("STERP");
        var harness = Path.Combine(LegacyRuntime.BinDirectory, "STERP.Tests.exe");
        var (code, lines, elapsed) = await LegacyRuntime.RunAsync($"\"{harness}\" \"/filter:^{workflow}$\"", LegacyRuntime.BinDirectory, "STERP");
        Assert.True(lines.Any(l => l.StartsWith("1..", StringComparison.Ordinal)), $"{workflow}: harness did not run");
        var file = Path.Combine(Characterization.FixtureRoot, "performance", $"{workflow}.json");
        var measurement = new JsonObject
        {
            ["workflow"] = workflow,
            ["dataset"] = "demo",
            ["elapsedMilliseconds"] = (long)elapsed.TotalMilliseconds,
            ["exitCode"] = code,
            ["measuredAtUtc"] = DateTime.UtcNow.ToString("o"),
        };
        Characterization.WriteRunOutput($"performance/{workflow}.json", [measurement.ToJsonString()]);
        if (Characterization.CaptureMode)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, measurement.ToJsonString() + "\n", TestContext.Current.CancellationToken);
            return;
        }
        var baseline = JsonNode.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken))!;
        Assert.Equal(baseline["exitCode"]!.GetValue<int>(), code);
        var limit = Math.Max(5 * baseline["elapsedMilliseconds"]!.GetValue<long>(), 5000);
        Assert.True(elapsed.TotalMilliseconds <= limit, $"{workflow}: {elapsed.TotalMilliseconds:F0} ms exceeds 5x the pinned legacy baseline ({limit} ms)");
    }
}
