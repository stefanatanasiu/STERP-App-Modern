using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace STERP.Testing;

/// <summary>
/// Placeholders of the parity DSL: <c>${outputRoot}</c> (STERP_TARGET_OUTPUT_ROOT = the hosts' Sterp:Jobs:OutputRoot)
/// and <c>${legacyRunRoot}</c> (STERP_TARGET_LEGACY_RUN_ROOT = the hosts' Sterp:Jobs:LegacyRunRoot). They are expanded
/// in every string value of a step or an assertion: inside <c>sql</c> text as T-SQL literal content (single quotes
/// doubled), inside <c>matches</c> as a regular-expression literal, everywhere else verbatim. A scenario that uses a
/// placeholder fails when its variable is not set; any other <c>${name}</c> is an authoring error.
/// <para>
/// Saved-value references <c>${&lt;save&gt;:&lt;json path&gt;}</c> (for example <c>${files:$.items[0].path}</c>) insert
/// one value of a result saved by an EARLIER step, selected like a <c>json</c> assertion selects it (from the parsed
/// body of an HTTP result, otherwise from the saved value itself); the path must select exactly one non-null value,
/// whose text is inserted with the same per-key encoding. A reference to a name no earlier step saved is an authoring
/// error (static validation).
/// </para>
/// </summary>
public static partial class ScenarioPlaceholders
{
    public static IReadOnlyList<string> Names { get; } = ["outputRoot", "legacyRunRoot"];

    public static string Value(string name) => name switch
    {
        "outputRoot" => ParityEnvironment.RequireWritableRoot("STERP_TARGET_OUTPUT_ROOT",
            "${outputRoot} names the target hosts' Sterp:Jobs:OutputRoot"),
        "legacyRunRoot" => ParityEnvironment.RequireWritableRoot("STERP_TARGET_LEGACY_RUN_ROOT",
            "${legacyRunRoot} names the target hosts' Sterp:Jobs:LegacyRunRoot"),
        _ => throw new InvalidOperationException($"Unknown placeholder ${{{name}}}; the DSL knows ${{outputRoot}} and ${{legacyRunRoot}}."),
    };

    /// <summary>Placeholder names used anywhere in the node (static validation; no environment needed).</summary>
    public static IEnumerable<string> NamesUsed(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => NamesUsed(p.Value)),
        JsonArray a => a.SelectMany(NamesUsed),
        JsonValue v when v.TryGetValue<string>(out var s) => Token().Matches(s).Select(m => m.Groups["name"].Value),
        _ => [],
    };

    /// <summary>Names of the saved results referenced by <c>${save:path}</c> anywhere in the node (static validation).</summary>
    public static IEnumerable<string> SavedReferences(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => SavedReferences(p.Value)),
        JsonArray a => a.SelectMany(SavedReferences),
        JsonValue v when v.TryGetValue<string>(out var s) => SavedToken().Matches(s).Select(m => m.Groups["save"].Value),
        _ => [],
    };

    /// <summary>A copy of <paramref name="node"/> with every placeholder expanded (the node itself when it uses none).</summary>
    public static JsonObject Expand(JsonObject node) => Expand(node, null);

    /// <summary>
    /// A copy of <paramref name="node"/> with every placeholder and every saved-value reference expanded (the node itself
    /// when it uses none); <paramref name="saved"/> holds the results saved by the steps executed so far.
    /// </summary>
    public static JsonObject Expand(JsonObject node, IReadOnlyDictionary<string, JsonNode?>? saved)
    {
        if (!NamesUsed(node).Any() && (saved is null || !SavedReferences(node).Any())) return node;
        var copy = node.DeepClone().AsObject();
        ExpandChildren(copy, saved);
        return copy;
    }

    public static string Expand(string text, Func<string, string>? encode = null) => Expand(text, encode, null);

    public static string Expand(string text, Func<string, string>? encode, IReadOnlyDictionary<string, JsonNode?>? saved)
    {
        if (!text.Contains("${", StringComparison.Ordinal)) return text;
        var enc = encode ?? (s => s);
        var expanded = Token().Replace(text, m => enc(Value(m.Groups["name"].Value)));
        return saved is null
            ? expanded
            : SavedToken().Replace(expanded, m => enc(SavedValue(m.Groups["save"].Value, m.Groups["path"].Value, saved)));
    }

    private static string SavedValue(string save, string path, IReadOnlyDictionary<string, JsonNode?> saved)
    {
        if (!saved.TryGetValue(save, out var target))
        {
            throw new InvalidOperationException($"${{{save}:{path}}} refers to '{save}', which no earlier step saved.");
        }
        JsonNode? root = target is JsonObject obj && obj.ContainsKey("json") ? obj["json"] : target;
        var nodes = JsonPathLite.Select(root, path).Where(n => n is not null).ToList();
        if (nodes.Count != 1)
        {
            throw new InvalidOperationException($"${{{save}:{path}}} selects {nodes.Count} value(s); exactly one is required.");
        }
        return JsonPathLite.AsText(nodes[0]) ?? "";
    }

    private static void ExpandChildren(JsonNode node, IReadOnlyDictionary<string, JsonNode?>? saved)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToList())
            {
                var child = obj[key];
                if (child is JsonValue v && v.TryGetValue<string>(out var s))
                {
                    obj[key] = Expand(s, Encoder(key), saved);
                }
                else if (child is not null)
                {
                    ExpandChildren(child, saved);
                }
            }
        }
        else if (node is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] is JsonValue v && v.TryGetValue<string>(out var s)) arr[i] = Expand(s, null, saved);
                else if (arr[i] is { } child) ExpandChildren(child, saved);
            }
        }
    }

    private static Func<string, string>? Encoder(string key) => key switch
    {
        "sql" => s => s.Replace("'", "''", StringComparison.Ordinal),
        "matches" => Regex.Escape,
        _ => null,
    };

    [GeneratedRegex(@"\$\{(?<name>[A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex Token();

    [GeneratedRegex(@"\$\{(?<save>[A-Za-z][A-Za-z0-9_\-]*):(?<path>\$[^}]*)\}")]
    private static partial Regex SavedToken();
}

/// <summary>
/// <c>file</c> step (arrange): writes one scenario-supplied text file, or copies one hashed fixture, below a configured
/// target root and removes it after the scenario.
/// <code>
/// {"file": {"root": "target-output" | "legacy-run", "name": "&lt;relative path below the root&gt;",
///           "lines": ["...", ...] | "content": "..." | "fixture": "&lt;path below tests/parity/STERP.Target.Parity.Tests/fixtures&gt;",
///           "sha256": "&lt;hex, fixture only&gt;", "encoding": "windows-1252" | "ascii" | "utf-8", "newline": "crlf" | "lf"}}
/// </code>
/// Roots: target-output = STERP_TARGET_OUTPUT_ROOT, legacy-run = STERP_TARGET_LEGACY_RUN_ROOT (never the live legacy
/// folder). <c>lines</c> are written each followed by the newline; <c>content</c> is written as given with its line
/// breaks normalised to the newline; a fixture is copied byte for byte after its sha256 is verified. Encoding default
/// windows-1252 (contracts 4.2), newline default crlf; characters the encoding cannot represent are an error.
/// Saved value: {path, root, sizeBytes, sha256}.
/// </summary>
internal static class ScenarioFiles
{
    private static readonly string[] Keys = ["root", "name", "lines", "content", "fixture", "sha256", "encoding", "newline"];
    private static readonly string[] Roots = ["target-output", "legacy-run"];
    private static readonly string[] Encodings = ["windows-1252", "ascii", "utf-8"];
    private static readonly string[] Newlines = ["crlf", "lf"];

    static ScenarioFiles() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static void Validate(string scenarioId, JsonNode? node)
    {
        if (node is not JsonObject spec) throw Bad(scenarioId, "file needs an object");
        foreach (var (key, _) in spec)
        {
            if (!Keys.Contains(key)) throw Bad(scenarioId, $"file has unknown key '{key}'");
        }
        if (Text(spec, "root") is { } root && !Roots.Contains(root)) throw Bad(scenarioId, $"file root '{root}' is not one of {string.Join(", ", Roots)}");
        var name = Text(spec, "name");
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) ||
            name.Split('\\', '/').Any(part => part is ".." or "."))
        {
            throw Bad(scenarioId, $"file name '{name}' must be a relative path below the root");
        }
        var sources = new[] { "lines", "content", "fixture" }.Count(spec.ContainsKey);
        if (sources != 1) throw Bad(scenarioId, "file needs exactly one of lines, content or fixture");
        if (spec["lines"] is { } lines && (lines is not JsonArray arr || arr.Any(l => l is not JsonValue v || !v.TryGetValue<string>(out _))))
        {
            throw Bad(scenarioId, "file lines must be an array of strings");
        }
        if (spec.ContainsKey("fixture"))
        {
            if (Text(spec, "fixture") is null || !Regex.IsMatch(Text(spec, "sha256") ?? "", "^[0-9a-fA-F]{64}$"))
            {
                throw Bad(scenarioId, "file fixture needs the fixture path and its sha256");
            }
            if (spec.ContainsKey("encoding") || spec.ContainsKey("newline")) throw Bad(scenarioId, "a fixture is copied byte for byte (no encoding or newline)");
        }
        else if (spec.ContainsKey("sha256"))
        {
            throw Bad(scenarioId, "sha256 belongs to a fixture");
        }
        if (Text(spec, "encoding") is { } enc && !Encodings.Contains(enc)) throw Bad(scenarioId, $"file encoding '{enc}' is not one of {string.Join(", ", Encodings)}");
        if (Text(spec, "newline") is { } nl && !Newlines.Contains(nl)) throw Bad(scenarioId, $"file newline '{nl}' is not one of {string.Join(", ", Newlines)}");
    }

    public static async Task<JsonObject> WriteAsync(string scenarioId, JsonObject spec, List<string> staged)
    {
        Validate(scenarioId, spec);
        var rootName = Text(spec, "root") ?? "target-output";
        var root = rootName == "legacy-run"
            ? ParityEnvironment.RequireWritableRoot("STERP_TARGET_LEGACY_RUN_ROOT", "file steps with root legacy-run write below the hosts' Sterp:Jobs:LegacyRunRoot")
            : ParityEnvironment.RequireWritableRoot("STERP_TARGET_OUTPUT_ROOT", "file steps with root target-output write below the hosts' Sterp:Jobs:OutputRoot");
        var path = Path.GetFullPath(Path.Combine(root, Text(spec, "name")!));
        if (!ParityEnvironment.IsUnder(path, root) || ParityEnvironment.IsUnder(root, path))
        {
            throw Bad(scenarioId, $"file {Text(spec, "name")} resolves outside the {rootName} root {root}");
        }

        byte[] bytes;
        if (Text(spec, "fixture") is { } fixture)
        {
            var source = Path.GetFullPath(Path.Combine(ParityEnvironment.FixturesDirectory, fixture));
            if (!ParityEnvironment.IsUnder(source, ParityEnvironment.FixturesDirectory) || !File.Exists(source))
            {
                throw Bad(scenarioId, $"fixture {fixture} is not a file below {ParityEnvironment.FixturesDirectory}");
            }
            bytes = await File.ReadAllBytesAsync(source);
            var actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (!string.Equals(actual, Text(spec, "sha256"), StringComparison.OrdinalIgnoreCase))
            {
                throw Bad(scenarioId, $"fixture {fixture} has sha256 {actual}, the scenario pins {Text(spec, "sha256")}");
            }
        }
        else
        {
            var newline = Text(spec, "newline") == "lf" ? "\n" : "\r\n";
            var text = spec["lines"] is JsonArray lines
                ? string.Concat(lines.Select(l => JsonPathLite.AsText(l) + newline))
                : (Text(spec, "content") ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", newline, StringComparison.Ordinal);
            var encoding = Encoding.GetEncoding((Text(spec, "encoding") ?? "windows-1252") switch
            {
                "ascii" => "us-ascii",
                "utf-8" => "utf-8",
                _ => "windows-1252",
            }, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            try
            {
                bytes = encoding.GetBytes(text);
            }
            catch (EncoderFallbackException ex)
            {
                throw Bad(scenarioId, $"file {Text(spec, "name")}: {ex.Message}");
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        staged.Add(path);
        await File.WriteAllBytesAsync(path, bytes);
        return new JsonObject
        {
            ["path"] = path,
            ["root"] = rootName,
            ["sizeBytes"] = bytes.Length,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes)),
        };
    }

    /// <summary>Removes every staged file (newest first); a file that cannot be removed is reported.</summary>
    public static async Task<List<string>> RemoveAsync(List<string> staged)
    {
        var problems = new List<string>();
        foreach (var path in Enumerable.Reverse(staged).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    break;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    if (attempt == 10)
                    {
                        problems.Add($"staged file {path} could not be removed: {ex.Message}");
                        break;
                    }
                    await Task.Delay(300);
                }
            }
        }
        staged.Clear();
        return problems;
    }

    private static InvalidOperationException Bad(string scenarioId, string message) => new($"{scenarioId}: {message}");

    private static string? Text(JsonObject node, string name) =>
        node[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>
/// <c>fault</c> step (closed vocabulary; isolated instance only, after the isolation check; always undone at the end
/// of the scenario):
/// <list type="bullet">
/// <item><c>"database-offline"</c> / <c>"database-online"</c>: ALTER DATABASE [STERP] SET OFFLINE WITH ROLLBACK IMMEDIATE / SET ONLINE (the database is always brought back ONLINE).</item>
/// <item><c>{"renameObject": "schema.name"}</c>: sp_rename to name_A07FAULT (renamed back afterwards; the next restore replaces the database anyway).</item>
/// <item><c>{"renameColumn": "schema.table.column"}</c>: sp_rename ... 'COLUMN' to column_A07FAULT (renamed back afterwards).</item>
/// <item><c>{"lockTable": "schema.table"}</c>: holds an exclusive table lock (TABLOCKX, HOLDLOCK) in an open transaction on a
/// separate connection, so every writer of the table (and every locking reader) waits - for example a run held executing at
/// its next core.JobRun registration; released by <c>"release-locks"</c> or at the end of the scenario (rolled back, nothing
/// is written). The database reads with READ_COMMITTED_SNAPSHOT, so ordinary reads are not blocked.</item>
/// <item><c>"jobs-host-restart"</c> / <c>{"restartHost": "Api" | "Jobs" | "Web" | "Portal"}</c>: runs STERP_TARGET_RESTART_COMMAND with {host}, which kills the host (as a crash) and starts it again, and waits until it reports ready.</item>
/// </list>
/// After the database is brought back ONLINE the runner waits until the reachable API/job hosts are ready again.
/// DDL in <c>sql</c> steps stays forbidden; these are the only schema faults a scenario can arrange.
/// </summary>
internal sealed partial class ScenarioFaults
{
    public const string RenameSuffix = "_A07FAULT";
    private static readonly string[] Hosts = ["Api", "Jobs", "Web", "Portal"];
    private readonly List<(string What, Func<Task> Undo)> _undo = [];
    private readonly List<IDisposable> _locks = [];
    private bool _offline;

    public static (string Kind, string? Argument) Parse(string scenarioId, JsonNode? spec)
    {
        if (spec is JsonValue v && v.TryGetValue<string>(out var word))
        {
            return word switch
            {
                "database-offline" => ("database-offline", null),
                "database-online" => ("database-online", null),
                "jobs-host-restart" => ("restartHost", "Jobs"),
                "release-locks" => ("release-locks", null),
                _ => throw Bad(scenarioId, $"unknown fault '{word}'"),
            };
        }
        if (spec is JsonObject o && o.Count == 1)
        {
            var (key, value) = o.First();
            var arg = value is JsonValue av && av.TryGetValue<string>(out var s) ? s : null;
            switch (key)
            {
                case "renameObject" when arg is not null && TwoPart().IsMatch(arg):
                    return (key, arg);
                case "renameColumn" when arg is not null && ThreePart().IsMatch(arg):
                    return (key, arg);
                case "lockTable" when arg is not null && TwoPart().IsMatch(arg):
                    return (key, arg);
                case "restartHost" when arg is not null && Hosts.Contains(arg):
                    return (key, arg);
            }
        }
        throw Bad(scenarioId, $"fault {spec?.ToJsonString()} is not in the vocabulary: \"database-offline\", \"database-online\", " +
                              "\"jobs-host-restart\", \"release-locks\", {\"renameObject\": \"schema.name\"}, " +
                              "{\"renameColumn\": \"schema.table.column\"}, {\"lockTable\": \"schema.table\"}, " +
                              "{\"restartHost\": \"Api|Jobs|Web|Portal\"}");
    }

    public async Task<JsonObject> ApplyAsync(string scenarioId, JsonNode? spec)
    {
        var (kind, arg) = Parse(scenarioId, spec);
        await IsolatedDatabase.VerifyIsolationAsync();
        switch (kind)
        {
            case "database-offline":
                _offline = true;
                await IsolatedDatabase.SetDatabaseOnlineAsync(false);
                return new JsonObject { ["fault"] = kind, ["database"] = ParityEnvironment.Database, ["state"] = "OFFLINE" };
            case "database-online":
                await IsolatedDatabase.SetDatabaseOnlineAsync(true);
                _offline = false;
                return new JsonObject { ["fault"] = kind, ["database"] = ParityEnvironment.Database, ["state"] = "ONLINE" };
            case "renameObject":
            {
                var schema = arg!.Split('.')[0];
                var name = arg.Split('.')[1];
                if (!await IsolatedDatabase.ExistsAsync(arg)) throw Bad(scenarioId, $"fault renameObject: {arg} does not exist on the isolated database");
                var renamed = name + RenameSuffix;
                await IsolatedDatabase.RenameAsync(arg, renamed);
                _undo.Add(($"rename {schema}.{renamed} back to {arg}", () => IsolatedDatabase.RenameAsync($"{schema}.{renamed}", name)));
                return new JsonObject { ["fault"] = kind, ["object"] = arg, ["renamedTo"] = $"{schema}.{renamed}" };
            }
            case "renameColumn":
            {
                var parts = arg!.Split('.');
                var table = $"{parts[0]}.{parts[1]}";
                if (!await IsolatedDatabase.ExistsAsync(table, parts[2])) throw Bad(scenarioId, $"fault renameColumn: {arg} does not exist on the isolated database");
                var renamed = parts[2] + RenameSuffix;
                await IsolatedDatabase.RenameAsync(arg, renamed, "COLUMN");
                _undo.Add(($"rename column {table}.{renamed} back to {parts[2]}", () => IsolatedDatabase.RenameAsync($"{table}.{renamed}", parts[2], "COLUMN")));
                return new JsonObject { ["fault"] = kind, ["column"] = arg, ["renamedTo"] = $"{table}.{renamed}" };
            }
            case "lockTable":
            {
                if (!await IsolatedDatabase.ExistsAsync(arg!)) throw Bad(scenarioId, $"fault lockTable: {arg} does not exist on the isolated database");
                _locks.Add(await IsolatedDatabase.LockTableAsync(arg!));
                return new JsonObject { ["fault"] = kind, ["table"] = arg, ["lock"] = "TABLOCKX, HOLDLOCK in an open transaction" };
            }
            case "release-locks":
            {
                var released = ReleaseLocks();
                return new JsonObject { ["fault"] = kind, ["released"] = released };
            }
            default:
                return await RestartHostAsync(scenarioId, arg!);
        }
    }

    /// <summary>Rolls back and closes every lock-holding connection; returns how many were released.</summary>
    private int ReleaseLocks()
    {
        var count = _locks.Count;
        foreach (var held in _locks) held.Dispose();
        _locks.Clear();
        return count;
    }

    /// <summary>Releases the table locks, brings the database back ONLINE and undoes every rename (newest first); a
    /// rename that cannot be undone is replaced by a dataset restore. Returns the problems that remain.</summary>
    public async Task<List<string>> RecoverAsync(string dataset)
    {
        var problems = new List<string>();
        try
        {
            ReleaseLocks();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problems.Add($"fault recovery: a table lock could not be released: {ex.Message}");
        }
        if (_offline)
        {
            try
            {
                await IsolatedDatabase.SetDatabaseOnlineAsync(true);
                _offline = false;
                await WaitUntilHostsReadyAsync(problems);
            }
            catch (Exception ex)
            {
                problems.Add($"fault recovery: the isolated database could not be brought back ONLINE: {ex.Message}");
            }
        }
        var failed = new List<string>();
        foreach (var (what, undo) in Enumerable.Reverse(_undo).ToList())
        {
            try
            {
                await undo();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed.Add($"{what}: {ex.Message}");
            }
        }
        _undo.Clear();
        if (failed.Count > 0)
        {
            try
            {
                await IsolatedDatabase.RestoreAsync(dataset);
            }
            catch (Exception ex)
            {
                problems.Add($"fault recovery: {string.Join("; ", failed)}; the dataset restore that replaces it failed too: {ex.Message}");
            }
        }
        return problems;
    }

    /// <summary>
    /// After the database is back ONLINE, waits (60 s at most) until every reachable API/job host answers
    /// /health/ready 200 again, so the next scenario does not meet connections the outage broke.
    /// </summary>
    private static async Task WaitUntilHostsReadyAsync(List<string> problems)
    {
        var headers = new Dictionary<string, string>();
        foreach (var host in new[] { ParityEnvironment.ApiBaseUrl, ParityEnvironment.JobsBaseUrl }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            var last = "";
            while (true)
            {
                try
                {
                    var ready = await TargetClient.SendAsync(HttpMethod.Get, $"{host.TrimEnd('/')}/health/ready", headers, null);
                    if (ready.Status == 200) break;
                    last = ready.Status.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
                {
                    break; // host not running: nothing to recover
                }
                if (DateTime.UtcNow >= deadline)
                {
                    problems.Add($"fault recovery: {host} /health/ready did not answer 200 within 60 s after the database came back ONLINE (last {last})");
                    break;
                }
                await Task.Delay(500);
            }
        }
    }

    private static async Task<JsonObject> RestartHostAsync(string scenarioId, string host)
    {
        var template = ParityEnvironment.TargetRestartCommand ?? throw Bad(scenarioId,
            "fault restartHost needs STERP_TARGET_RESTART_COMMAND (scripts/run-parity.ps1 exports it with the hosts' configuration)");
        var log = Path.Combine(Path.GetTempPath(), $"sterp-parity-restart-{host}-{Guid.NewGuid():N}.log");
        var quotedLog = log.Replace("'", "''", StringComparison.Ordinal);
        var command = template.Replace("{host}", host, StringComparison.Ordinal);
        var script = "$ErrorActionPreference = 'Stop'; " +
                     $"try {{ & {{ {command} }} *>&1 | Out-File -LiteralPath '{quotedLog}' -Encoding utf8; exit [int]$LASTEXITCODE }} " +
                     $"catch {{ ($_ | Out-String) | Out-File -LiteralPath '{quotedLog}' -Append -Encoding utf8; exit 1 }}";
        var start = new ProcessStartInfo("pwsh.exe",
            $"-NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}")
        {
            // Shell execution: the restarted host outlives the test and must not inherit the test runner's standard handles.
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start) ?? throw Bad(scenarioId, "fault restartHost: pwsh did not start");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw Bad(scenarioId, $"fault restartHost: the restart of {host} did not finish within 300 s");
        }
        var output = File.Exists(log) ? (await File.ReadAllTextAsync(log)).Trim() : "";
        try { File.Delete(log); } catch (IOException) { }
        if (process.ExitCode != 0) throw Bad(scenarioId, $"fault restartHost: the restart of {host} failed (exit {process.ExitCode}): {output}");
        return new JsonObject
        {
            ["fault"] = "restartHost",
            ["host"] = host,
            ["exitCode"] = process.ExitCode,
            ["elapsedMilliseconds"] = watch.ElapsedMilliseconds,
            ["output"] = output,
        };
    }

    private static InvalidOperationException Bad(string scenarioId, string message) => new($"{scenarioId}: {message}");

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex TwoPart();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*\.[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ThreePart();
}

/// <summary>
/// Suite isolation: every job run a scenario starts - a <c>job</c> step, a <c>call</c> whose response carries a
/// jobRunId or a /api/jobs/v1/runs/{id} Location, and every core.JobRun row above the restored dataset's highest id
/// (runs started indirectly, also through the GUI) - must have stopped executing in the job host before the next
/// scenario restores the database, otherwise its late writes would land in that scenario's data. At the end of the
/// scenario the runner waits, through the binding jobs_getRun, while a run's execution state is ACCEPTED or RUNNING,
/// bounded by STERP_PARITY_JOB_TIMEOUT_SECONDS. Rows without a target execution block (dataset rows, arranged rows,
/// legacy writers) and LOST or ENDED runs are not waited for; no run is cancelled (the contracts have no cancellation).
/// </summary>
internal sealed partial class ScenarioRunTracker(long datasetMaxJobRunId)
{
    private static readonly Dictionary<string, string> NoHeaders = new();
    private readonly SortedSet<long> _ids = [];

    public void Track(JsonNode? saved)
    {
        if (saved is not JsonObject result) return;
        Add(result["jobRunId"]);
        foreach (var http in new[] { result, result["submit"] as JsonObject })
        {
            if (http is null) continue;
            if (http["json"] is JsonObject json) Add(json["jobRunId"]);
            if (JsonPathLite.AsText(http["headers"]?["location"]) is { } location && RunLocation().Match(location) is { Success: true } m)
            {
                _ids.Add(long.Parse(m.Groups["id"].Value, System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }

    /// <summary>Waits for the scenario's runs; returns a diagnostic naming the runs still executing after the bound.</summary>
    public async Task<string?> WaitAsync()
    {
        var ids = new SortedSet<long>(_ids);
        try
        {
            if (await IsolatedDatabase.IsOnlineAsync())
            {
                foreach (var id in await IsolatedDatabase.JobRunIdsAboveAsync(datasetMaxJobRunId)) ids.Add(id);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // database not readable: the tracked ids still apply
        }
        if (ids.Count == 0) return null;
        var bound = ParityEnvironment.JobTimeout;
        var deadline = DateTime.UtcNow + bound;
        while (true)
        {
            var executing = new SortedDictionary<long, string>();
            foreach (var id in ids)
            {
                if (await ExecutionStateAsync(id) is { } state and ("ACCEPTED" or "RUNNING")) executing[id] = state;
            }
            if (executing.Count == 0) return null;
            if (DateTime.UtcNow >= deadline)
            {
                return "suite isolation: job run(s) " +
                       string.Join(", ", executing.Select(e => $"{e.Key} (execution state {e.Value})")) +
                       $" still executing in the job host after {bound.TotalSeconds:F0} s (STERP_PARITY_JOB_TIMEOUT_SECONDS); " +
                       "the next scenario's dataset restore would let them write into the restored database";
            }
            ids = new SortedSet<long>(executing.Keys);
            await Task.Delay(1000);
        }
    }

    private void Add(JsonNode? node)
    {
        if (JsonPathLite.TryDecimal(node, out var id) && id > 0) _ids.Add((long)id);
    }

    private static async Task<string?> ExecutionStateAsync(long id)
    {
        try
        {
            var run = await TargetClient.SendAsync(HttpMethod.Get,
                $"{ParityEnvironment.JobsBaseUrl.TrimEnd('/')}/api/jobs/v1/runs/{id}", NoHeaders, null);
            return run.Status == 200 ? JsonPathLite.AsText(run.Json?["execution"]?["state"]) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return null; // job host not reachable: it cannot write either
        }
    }

    [GeneratedRegex(@"/api/jobs/v1/runs/(?<id>\d+)")]
    private static partial Regex RunLocation();
}

/// <summary>State a scenario arranges outside the database restore: staged files, injected faults and started runs.</summary>
internal sealed class ScenarioSession(ParityScenario scenario, long datasetMaxJobRunId)
{
    public ScenarioRunTracker Runs { get; } = new(datasetMaxJobRunId);

    public ScenarioFaults Faults { get; } = new();

    public List<string> StagedFiles { get; } = [];

    /// <summary>Undoes the faults, waits for the scenario's job runs, then removes the staged files.</summary>
    public async Task<List<string>> FinishAsync()
    {
        var problems = await Faults.RecoverAsync(scenario.Dataset);
        try
        {
            if (await Runs.WaitAsync() is { } isolation) problems.Add(isolation);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            problems.Add($"suite isolation: the scenario's job runs could not be checked: {ex.Message}");
        }
        problems.AddRange(await ScenarioFiles.RemoveAsync(StagedFiles));
        return problems;
    }
}
