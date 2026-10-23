using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace STERP.Testing;

/// <summary>GUI step execution, supplied by the GUI parity project (Playwright).</summary>
public interface IGuiDriver
{
    Task<JsonObject> RunAsync(ParityScenario scenario, JsonObject step, JsonObject context);
}

/// <summary>
/// Executes one authored parity scenario (DSL sterp-parity-cases/1) against the target hosts and the
/// isolated database. Order: static validation against the binding contracts, reachability of every
/// host used (fails red until implemented), dataset restore and login mapping, steps, assertions, then
/// the scenario's own cleanup: injected faults undone, a wait while job runs the scenario started still
/// execute in the job host (suite isolation), staged files removed.
/// </summary>
public sealed class ScenarioRunner(IGuiDriver? gui = null)
{
    private static readonly string[] TerminalStates = ["OK", "WARNING", "FAILED", "ABENDED", "CANCELLED", "COMPLETE"];
    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Elapsed time of the scenario steps (excludes dataset restore, assertions and cleanup).</summary>
    public TimeSpan StepsElapsed { get; private set; }

    public async Task RunAsync(ParityScenario scenario)
    {
        Validate(scenario);
        foreach (var (url, what) in HostsUsed(scenario))
        {
            await TargetClient.EnsureReachableAsync(url, what);
        }

        var context = scenario.Context;
        var login = Text(context, "login") ?? "SYSADM";
        await IsolatedDatabase.RestoreAsync(scenario.Dataset);
        var session = new ScenarioSession(scenario, await IsolatedDatabase.MaxJobRunIdAsync());
        ExceptionDispatchInfo? failure = null;
        try
        {
            await IsolatedDatabase.MapLoginAsync(login);
            await ExecuteAsync(scenario, context, login, session);
        }
        catch (Exception ex)
        {
            failure = ExceptionDispatchInfo.Capture(ex);
        }

        var cleanup = await session.FinishAsync();
        if (cleanup.Count == 0)
        {
            failure?.Throw();
            return;
        }
        var detail = string.Join(Environment.NewLine + "  ", cleanup);
        if (failure is null) throw new ParityAssertionException($"{scenario.Id}: {detail}");
        if (failure.SourceException is TargetNotImplementedException) failure.Throw();
        throw new ParityAssertionException(failure.SourceException.Message + Environment.NewLine + "  " + detail, failure.SourceException);
    }

    private async Task ExecuteAsync(ParityScenario scenario, JsonObject context, string login, ScenarioSession session)
    {
        var saved = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var node in scenario.Steps)
        {
            var step = ScenarioPlaceholders.Expand(node!.AsObject(), saved);
            JsonNode? result;
            if (step["sql"] is { } sql)
            {
                result = await IsolatedDatabase.QueryAsync(sql.GetValue<string>());
            }
            else if (step["call"] is { } call)
            {
                result = (await CallAsync(call.GetValue<string>(), step, context, login)).ToNode();
            }
            else if (step["job"] is { } job)
            {
                result = await RunJobAsync(job.GetValue<string>(), step, context, login);
            }
            else if (step["gui"] is not null)
            {
                result = gui is null
                    ? throw new InvalidOperationException($"{scenario.Id}: GUI step requires the GUI parity runner")
                    : await gui.RunAsync(scenario, step, context);
            }
            else if (step["file"] is JsonObject file)
            {
                result = await ScenarioFiles.WriteAsync(scenario.Id, file, session.StagedFiles);
            }
            else if (step.ContainsKey("fault"))
            {
                result = await session.Faults.ApplyAsync(scenario.Id, step["fault"]);
            }
            else
            {
                throw new InvalidOperationException($"{scenario.Id}: step without action {step.ToJsonString()}");
            }
            session.Runs.Track(result);
            if (Text(step, "save") is { } name) saved[name] = result;
        }
        watch.Stop();
        StepsElapsed = watch.Elapsed;

        var failures = new List<string>();
        foreach (var node in scenario.Assertions)
        {
            var assertion = node!.AsObject();
            try
            {
                var failure = await EvaluateAsync(ScenarioPlaceholders.Expand(assertion, saved), saved);
                if (failure is not null) failures.Add($"{assertion.ToJsonString()} -> {failure}");
            }
            catch (Exception ex) when (ex is not TargetNotImplementedException)
            {
                failures.Add($"{assertion.ToJsonString()} -> {ex.GetType().Name}: {ex.Message}");
            }
        }
        if (failures.Count > 0)
        {
            throw new ParityAssertionException(
                $"{scenario.Id} ({scenario.RequirementId} AC{scenario.AcceptanceCriterion?.ToString(CultureInfo.InvariantCulture) ?? "-edge"}) " +
                $"failed {failures.Count} of {scenario.Assertions.Count} expectation(s):{Environment.NewLine}  " +
                string.Join(Environment.NewLine + "  ", failures));
        }
    }

    /// <summary>Static check that the scenario binds only to the frozen contracts and the closed DSL vocabulary.</summary>
    public static void Validate(ParityScenario scenario)
    {
        foreach (var node in scenario.Steps)
        {
            var step = node!.AsObject();
            var opId = Text(step, "call") ?? Text(step, "job");
            if (opId is not null)
            {
                var op = BindingContracts.Get(opId);
                foreach (Match m in Regex.Matches(op.Path, @"\{([^}]+)\}"))
                {
                    if (step["path"]?[m.Groups[1].Value] is null)
                    {
                        throw new InvalidOperationException($"{scenario.Id}: {opId} needs path parameter {m.Groups[1].Value}");
                    }
                }
            }
            if (Text(step, "gui") is { } route)
            {
                var path = route.Split('?')[0];
                var known = path == "/" || BindingContracts.GuiRoutes.Any(r =>
                    Regex.IsMatch(path, "^" + Regex.Replace(r, @"\{[^}]+\}", "[^/]+") + "$"));
                if (!known) throw new InvalidOperationException($"{scenario.Id}: GUI route {route} is not in STERP-WEB section 7.2");
            }
            if (step.ContainsKey("file")) ScenarioFiles.Validate(scenario.Id, step["file"]);
            if (step.ContainsKey("fault")) ScenarioFaults.Parse(scenario.Id, step["fault"]);
        }
        foreach (var name in scenario.Steps.Concat(scenario.Assertions).SelectMany(ScenarioPlaceholders.NamesUsed).Distinct())
        {
            if (!ScenarioPlaceholders.Names.Contains(name))
            {
                throw new InvalidOperationException($"{scenario.Id}: unknown placeholder ${{{name}}}; the DSL knows ${{outputRoot}} and ${{legacyRunRoot}}");
            }
        }
        // ${save:path} may refer only to a result saved by an earlier step (assertions: by any step)
        var savedSoFar = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in scenario.Steps)
        {
            var step = node!.AsObject();
            foreach (var save in ScenarioPlaceholders.SavedReferences(step).Distinct())
            {
                if (!savedSoFar.Contains(save))
                {
                    throw new InvalidOperationException($"{scenario.Id}: ${{{save}:...}} refers to '{save}', which no earlier step saves");
                }
            }
            if (Text(step, "save") is { } name) savedSoFar.Add(name);
        }
        foreach (var save in scenario.Assertions.SelectMany(ScenarioPlaceholders.SavedReferences).Distinct())
        {
            if (!savedSoFar.Contains(save))
            {
                throw new InvalidOperationException($"{scenario.Id}: ${{{save}:...}} in an assertion refers to '{save}', which no step saves");
            }
        }
    }

    public static IEnumerable<(string Url, string What)> HostsUsed(ParityScenario scenario)
    {
        var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in scenario.Steps)
        {
            var step = node!.AsObject();
            if (Text(step, "call") is { } call)
            {
                var op = BindingContracts.Get(call);
                hosts.TryAdd(op.BaseUrl, op.IsJobsHost ? "STERP.Jobs" : "STERP.Api");
            }
            if (Text(step, "job") is not null) hosts.TryAdd(ParityEnvironment.JobsBaseUrl, "STERP.Jobs");
            if (Text(step, "gui") is not null) hosts.TryAdd(ParityEnvironment.WebBaseUrl, "STERP.Web");
        }
        return hosts.Select(h => (h.Key, h.Value));
    }

    private static async Task<HttpOutcome> CallAsync(string operationId, JsonObject step, JsonObject context, string login)
    {
        var op = BindingContracts.Get(operationId);
        var path = Regex.Replace(op.Path, @"\{([^}]+)\}", m =>
            Uri.EscapeDataString(JsonPathLite.AsText(step["path"]![m.Groups[1].Value]) ?? ""));
        var url = op.BaseUrl.TrimEnd('/') + path;
        if (step["query"] is JsonObject query && query.Count > 0)
        {
            url += "?" + string.Join("&", query
                .Where(q => q.Value is not null)
                .Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(JsonPathLite.AsText(q.Value) ?? "")}"));
        }
        var headers = Headers(step, context, login);
        var body = step["body"];
        return await TargetClient.SendAsync(new HttpMethod(op.Method), url, headers, body is null ? null : body.DeepClone());
    }

    private static async Task<JsonObject> RunJobAsync(string operationId, JsonObject step, JsonObject context, string login)
    {
        var submit = await CallAsync(operationId, step, context, login);
        var result = new JsonObject { ["status"] = submit.Status, ["submit"] = submit.ToNode() };
        if (submit.Status != 202) return result;
        var id = JsonPathLite.AsText(submit.Json?["jobRunId"]) ??
                 submit.Headers.Location?.ToString().TrimEnd('/').Split('/').Last();
        if (string.IsNullOrEmpty(id)) return result;
        var runsUrl = $"{ParityEnvironment.JobsBaseUrl.TrimEnd('/')}/api/jobs/v1/runs/{Uri.EscapeDataString(id)}";
        var headers = Headers(step, context, login);
        var deadline = DateTime.UtcNow + ParityEnvironment.JobTimeout;
        HttpOutcome run;
        do
        {
            run = await TargetClient.SendAsync(HttpMethod.Get, runsUrl, headers, null);
            var status = JsonPathLite.AsText(run.Json?["status"]) ?? "";
            var state = JsonPathLite.AsText(run.Json?["execution"]?["state"]) ?? "";
            // ENDED and LOST (job host restarted while the run executed) are final execution states (jobs_getRun).
            if (run.Status != 200 || state is "ENDED" or "LOST" || TerminalStates.Contains(status)) break;
            await Task.Delay(1000);
        } while (DateTime.UtcNow < deadline);
        result["run"] = run.Json?.DeepClone();
        result["jobRunId"] = id;
        var res = await TargetClient.SendAsync(HttpMethod.Get, runsUrl + "/result", headers, null);
        result["result"] = res.Status == 200 ? res.Json?.DeepClone() : null;
        var spool = await TargetClient.SendAsync(HttpMethod.Get, runsUrl + "/spool", headers, null);
        result["spool"] = spool.Status == 200 ? spool.Text : null;
        var sysout = await TargetClient.SendAsync(HttpMethod.Get, runsUrl + "/sysout", headers, null);
        result["sysout"] = sysout.Status == 200 ? sysout.Text : null;
        return result;
    }

    private static Dictionary<string, string> Headers(JsonObject step, JsonObject context, string login)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Text(context, "company") is { } co) headers["X-STERP-Company"] = co;
        if (Text(context, "site") is { } site) headers["X-STERP-Site"] = site;
        if (Text(context, "businessDate") is { } date) headers["X-STERP-Business-Date"] = date;
        var actor = Text(step, "as") ?? Text(context, "actor");
        if (actor is not null && !string.Equals(actor, login, StringComparison.OrdinalIgnoreCase)) headers["X-STERP-Run-As"] = actor;
        if (Text(step, "idempotencyKey") is { } key) headers["Idempotency-Key"] = key;
        if (step["headers"] is JsonObject extra)
        {
            foreach (var (k, v) in extra) headers[k] = JsonPathLite.AsText(v) ?? "";
        }
        return headers;
    }

    private static async Task<string?> EvaluateAsync(JsonObject a, IReadOnlyDictionary<string, JsonNode?> saved)
    {
        if (a["sql"] is { } sqlNode)
        {
            var rows = await IsolatedDatabase.QueryAsync(sqlNode.GetValue<string>());
            if (a["rows"] is { } rowCount) return Expect(rows.Count == rowCount.GetValue<int>(), $"rows {rows.Count}");
            var first = rows.Count > 0 ? rows[0]!.AsObject().FirstOrDefault().Value : null;
            return CompareScalar(a, first, rows.Count == 0 ? "no rows" : JsonPathLite.AsText(first));
        }

        var on = Text(a, "on") ?? throw new InvalidOperationException("assertion without 'on'");
        if (!saved.TryGetValue(on, out var target)) return $"no saved result '{on}'";
        var obj = target as JsonObject;

        if (a["status"] is { } status)
        {
            var actual = obj?["status"];
            return Expect(JsonPathLite.ValueEquals(actual, status), $"status {JsonPathLite.AsText(actual)}");
        }
        if (a["outcome"] is JsonObject outcome)
        {
            var body = obj?["json"] as JsonObject;
            var env = body?["outcome"] as JsonObject ?? body ?? obj?["run"] as JsonObject;
            var messages = new[]
            {
                JsonPathLite.AsText(env?["message"]), JsonPathLite.AsText(env?["detail"]),
                JsonPathLite.AsText(obj?["run"]?["message"]), JsonPathLite.AsText(obj?["sysout"]),
            };
            var problems = new List<string>();
            Field(outcome, env, "returnCode", problems);
            Field(outcome, env, "severity", problems);
            Field(outcome, env, "reasonCode", problems);
            if (Text(outcome, "messageContains") is { } mc && !messages.Any(m => m?.Contains(mc, StringComparison.Ordinal) == true))
            {
                problems.Add($"message does not contain '{mc}' ({messages.FirstOrDefault(m => m is not null)})");
            }
            return problems.Count == 0 ? null : string.Join("; ", problems);
        }
        if (a["problem"] is JsonObject problem)
        {
            var body = obj?["json"] as JsonObject;
            var problems = new List<string>();
            if (problem["status"] is { } ps && !JsonPathLite.ValueEquals(obj?["status"], ps)) problems.Add($"status {JsonPathLite.AsText(obj?["status"])}");
            if (Text(problem, "type") is { } type && !(JsonPathLite.AsText(body?["type"]) ?? "").EndsWith("/" + type, StringComparison.Ordinal))
            {
                problems.Add($"type {JsonPathLite.AsText(body?["type"])}");
            }
            Field(problem, body, "reasonCode", problems);
            Field(problem, body, "returnCode", problems);
            Field(problem, body, "severity", problems);
            if (Text(problem, "detailContains") is { } dc && !(JsonPathLite.AsText(body?["detail"]) ?? "").Contains(dc, StringComparison.Ordinal))
            {
                problems.Add($"detail '{JsonPathLite.AsText(body?["detail"])}'");
            }
            return problems.Count == 0 ? null : string.Join("; ", problems);
        }
        if (Text(a, "json") is { } path)
        {
            JsonNode? root = obj is not null && obj.ContainsKey("json") ? obj["json"] : target;
            var nodes = JsonPathLite.Select(root, path);
            return CompareNodes(a, nodes, path);
        }
        if (a["run"] is JsonObject runExpect)
        {
            var run = obj?["run"] as JsonObject;
            if (run is null) return $"no run resource (submit status {JsonPathLite.AsText(obj?["status"])})";
            var problems = new List<string>();
            Field(runExpect, run, "status", problems);
            Field(runExpect, run, "returnCode", problems);
            if (Text(runExpect, "messageContains") is { } mc)
            {
                var hay = string.Join("\n", JsonPathLite.AsText(run["message"]), JsonPathLite.AsText(obj?["sysout"]), JsonPathLite.AsText(obj?["spool"]));
                if (!hay.Contains(mc, StringComparison.Ordinal)) problems.Add($"message/sysout/spool lacks '{mc}'");
            }
            return problems.Count == 0 ? null : string.Join("; ", problems);
        }
        if (Text(a, "spoolContains") is { } sc)
        {
            var spool = JsonPathLite.AsText(obj?["spool"]) ?? "";
            return Expect(spool.Contains(sc, StringComparison.Ordinal), "spool does not contain the text");
        }
        if (Text(a, "bodyContains") is { } bc)
        {
            return Expect(BodyContains(target, bc), "body does not contain the text");
        }
        if (Text(a, "text") is { } visible)
        {
            return Expect((JsonPathLite.AsText(obj?["text"]) ?? "").Contains(visible, StringComparison.Ordinal), "page text missing");
        }
        if (Text(a, "notText") is { } hidden)
        {
            return Expect(!(JsonPathLite.AsText(obj?["text"]) ?? "").Contains(hidden, StringComparison.Ordinal), "page text unexpectedly present");
        }
        if (Text(a, "title") is { } title)
        {
            return Expect((JsonPathLite.AsText(obj?["title"]) ?? "").Contains(title, StringComparison.Ordinal), $"title '{JsonPathLite.AsText(obj?["title"])}'");
        }
        if (Text(a, "focused") is { } focused)
        {
            return Expect(string.Equals(JsonPathLite.AsText(obj?["focused"]), focused, StringComparison.OrdinalIgnoreCase), $"focused '{JsonPathLite.AsText(obj?["focused"])}'");
        }
        return "unsupported assertion kind";
    }

    /// <summary>
    /// bodyContains compares unescaped text. An HTTP result (it has <c>text</c>): the response text as received, any
    /// string value of its parsed JSON body, or that body serialized without HTML-safe escaping. Any other saved value
    /// (job result, sql rows, GUI result): any string value inside it (unescaped, e.g. the SYSOUT text of a job), or its
    /// JSON text, default-escaped (the previous comparison, kept for JSON fragments) or relaxed. Characters such as
    /// '&lt;', '&gt;', '&amp;' and the apostrophe therefore match as written in the scenario.
    /// </summary>
    private static bool BodyContains(JsonNode? target, string text)
    {
        var obj = target as JsonObject;
        if (JsonPathLite.AsText(obj?["text"]) is { } body)
        {
            if (body.Contains(text, StringComparison.Ordinal)) return true;
            var json = obj!["json"];
            return json is not null &&
                   (StringValues(json).Any(s => s.Contains(text, StringComparison.Ordinal)) ||
                    json.ToJsonString(Relaxed).Contains(text, StringComparison.Ordinal));
        }
        if (target is null) return text.Length == 0;
        return target.ToJsonString().Contains(text, StringComparison.Ordinal) ||
               target.ToJsonString(Relaxed).Contains(text, StringComparison.Ordinal) ||
               StringValues(target).Any(s => s.Contains(text, StringComparison.Ordinal));
    }

    private static IEnumerable<string> StringValues(JsonNode? node) => node switch
    {
        JsonObject o => o.SelectMany(p => StringValues(p.Value)),
        JsonArray arr => arr.SelectMany(StringValues),
        JsonValue v when v.TryGetValue<string>(out var s) => [s],
        _ => [],
    };

    private static string? CompareNodes(JsonObject a, IReadOnlyList<JsonNode?> nodes, string path)
    {
        if (a["count"] is { } count)
        {
            var n = nodes.Count == 1 && nodes[0] is JsonArray arr ? arr.Count : nodes.Count(x => x is not null);
            return Expect(n == count.GetValue<int>(), $"{path} count {n}");
        }
        if (a["exists"] is { } exists)
        {
            var any = nodes.Any(x => x is not null);
            return Expect(any == exists.GetValue<bool>(), $"{path} exists={any}");
        }
        if (a["isNull"] is { } isNull)
        {
            var nul = nodes.Count == 0 || nodes.All(x => x is null);
            return Expect(nul == isNull.GetValue<bool>(), $"{path} isNull={nul}");
        }
        if (nodes.Count == 0) return $"{path} not found";
        foreach (var node in nodes)
        {
            if (CompareScalar(a, node, JsonPathLite.AsText(node)) is null) return null;
        }
        return $"{path} = {string.Join(" | ", nodes.Take(5).Select(JsonPathLite.AsText))}";
    }

    private static string? CompareScalar(JsonObject a, JsonNode? actual, string? actualText)
    {
        var role = Text(a, "normalize");
        if (a.ContainsKey("equals"))
        {
            var expected = a["equals"];
            var ok = role is not null
                ? Normalization.MatchesRole(role, actualText, JsonPathLite.AsText(expected))
                : JsonPathLite.ValueEquals(actual, expected);
            return Expect(ok, $"actual '{actualText}'");
        }
        if (Text(a, "contains") is { } contains) return Expect(actualText?.Contains(contains, StringComparison.Ordinal) == true, $"actual '{actualText}'");
        if (Text(a, "matches") is { } pattern) return Expect(actualText is not null && Regex.IsMatch(actualText, pattern), $"actual '{actualText}'");
        if (a["gte"] is { } gte) return Expect(JsonPathLite.TryDecimal(actual, out var v1) && JsonPathLite.TryDecimal(gte, out var e1) && v1 >= e1, $"actual '{actualText}'");
        if (a["lte"] is { } lte) return Expect(JsonPathLite.TryDecimal(actual, out var v2) && JsonPathLite.TryDecimal(lte, out var e2) && v2 <= e2, $"actual '{actualText}'");
        return "no comparison operator";
    }

    private static void Field(JsonObject expected, JsonObject? actual, string name, List<string> problems)
    {
        if (!expected.ContainsKey(name)) return;
        var e = expected[name];
        var v = actual?[name];
        var ok = name == "reasonCode"
            ? string.Equals((JsonPathLite.AsText(v) ?? "").Trim(), (JsonPathLite.AsText(e) ?? "").Trim(), StringComparison.Ordinal)
            : JsonPathLite.ValueEquals(v, e);
        if (!ok) problems.Add($"{name} '{JsonPathLite.AsText(v)}' (expected '{JsonPathLite.AsText(e)}')");
    }

    private static string? Expect(bool condition, string actual) => condition ? null : actual;

    private static string? Text(JsonObject? node, string name) =>
        node?[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
