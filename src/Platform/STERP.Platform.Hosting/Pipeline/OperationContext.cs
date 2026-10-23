using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Sterp.Platform.Hosting.Contracts;
using Sterp.Platform.Kernel;

namespace Sterp.Platform.Hosting.Pipeline;

/// <summary>Handler of one binding operation.</summary>
public delegate Task<IResult> OperationHandler(OperationContext ctx);

/// <summary>
/// Everything a slice handler needs for one call of a binding operation: the resolved session context, path/query/header
/// values, the JSON body, kernel services, and result builders that apply the contracts 4.6 mapping.
/// </summary>
public sealed class OperationContext
{
    private JsonNode? _body;
    private bool _bodyRead;

    public OperationContext(HttpContext http, ContractOperation operation, SterpContext session)
    {
        Http = http;
        Operation = operation;
        Session = session;
    }

    public HttpContext Http { get; }

    public ContractOperation Operation { get; }

    public SterpContext Session { get; }

    public IServiceProvider Services => Http.RequestServices;

    public CancellationToken Aborted => Http.RequestAborted;

    public T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    // ------------------------------------------------------------------ inputs

    public string Route(string name) =>
        Http.Request.RouteValues.TryGetValue(name, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    public string? Query(string name)
    {
        if (!Http.Request.Query.TryGetValue(name, out var v)) return null;
        return v.Count == 0 ? null : v[0];
    }

    public int? QueryInt(string name)
    {
        var q = Query(name);
        if (string.IsNullOrWhiteSpace(q)) return null;
        return int.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : throw ApiProblemException.BadBody($"Query parameter {name} must be an integer.");
    }

    public long? QueryLong(string name)
    {
        var q = Query(name);
        if (string.IsNullOrWhiteSpace(q)) return null;
        return long.TryParse(q, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            ? i
            : throw ApiProblemException.BadBody($"Query parameter {name} must be an integer.");
    }

    public decimal? QueryDecimal(string name)
    {
        var q = Query(name);
        if (string.IsNullOrWhiteSpace(q)) return null;
        return decimal.TryParse(q, NumberStyles.Number, CultureInfo.InvariantCulture, out var d)
            ? d
            : throw ApiProblemException.BadBody($"Query parameter {name} must be a number.");
    }

    public DateOnly? QueryDate(string name)
    {
        var q = Query(name);
        if (string.IsNullOrWhiteSpace(q)) return null;
        return DateOnly.TryParseExact(q, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : throw ApiProblemException.BadBody($"Query parameter {name} must be a date (yyyy-MM-dd).");
    }

    public bool? QueryBool(string name)
    {
        var q = Query(name);
        if (string.IsNullOrWhiteSpace(q)) return null;
        return q.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "y" or "yes" => true,
            "false" or "0" or "n" or "no" => false,
            _ => throw ApiProblemException.BadBody($"Query parameter {name} must be a boolean."),
        };
    }

    public string? Header(string name) => Http.Request.Headers.TryGetValue(name, out var v) && v.Count > 0 ? v[0] : null;

    /// <summary>The request body parsed as JSON (null when absent).</summary>
    public async Task<JsonNode?> BodyAsync()
    {
        if (_bodyRead) return _body;
        _bodyRead = true;
        if (Http.Request.ContentLength == 0) return null;
        try
        {
            using var reader = new StreamReader(Http.Request.Body);
            var text = await reader.ReadToEndAsync(Aborted);
            _body = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException ex)
        {
            throw ApiProblemException.BadBody("The request body is not valid JSON: " + ex.Message);
        }
        return _body;
    }

    /// <summary>The body as an object (an empty object when absent).</summary>
    public async Task<JsonObject> BodyObjectAsync()
    {
        var b = await BodyAsync();
        return b switch
        {
            null => [],
            JsonObject o => o,
            _ => throw ApiProblemException.BadBody("The request body must be a JSON object."),
        };
    }

    /// <summary>The body deserialized to a record (case-insensitive names); a type mismatch is 400 API0004.</summary>
    public async Task<T> BodyAsAsync<T>() where T : new()
    {
        var b = await BodyAsync();
        if (b is null) return new T();
        try
        {
            return b.Deserialize<T>(SterpJson.Options) ?? new T();
        }
        catch (JsonException ex)
        {
            throw ApiProblemException.BadBody("The request body does not match the schema: " + ex.Message);
        }
        catch (FormatException ex)
        {
            throw ApiProblemException.BadBody("The request body does not match the schema: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ results

    /// <summary>Direct payload (x-sterp-outcome: direct).</summary>
    public IResult Direct(object? payload, int status = 200) =>
        Results.Json(payload as JsonNode ?? JsonSerializer.SerializeToNode(payload, SterpJson.Options), SterpJson.Options, "application/json", status);

    /// <summary>Complete legacy result set in legacy order: { items, count } (contracts 4.10).</summary>
    public IResult List(JsonArray items) => Direct(new JsonObject { ["items"] = items, ["count"] = items.Count });

    public IResult List<T>(IEnumerable<T> items)
    {
        var arr = JsonSerializer.SerializeToNode(items.ToList(), SterpJson.Options) as JsonArray ?? [];
        return List(arr);
    }

    /// <summary>Outcome envelope or problem from a legacy result (contracts 4.6).</summary>
    public IResult Envelope(LegacyResult result, object? data = null) => ProblemResults.FromLegacy(result, data, Session);

    public IResult Problem(int status, string kind, string? detail, string? reasonCode = null, int? returnCode = null, object? data = null) =>
        ProblemResults.Create(status, kind, detail, Session, reasonCode, returnCode, data: data);

    public IResult BusinessRule(string reasonCode, string detail, int? returnCode = 8, object? data = null) =>
        ProblemResults.Create(422, ProblemKinds.BusinessRule, detail, Session, reasonCode, returnCode, returnCode == 8 ? "ERROR" : null, 1, data: data);

    public IResult NotFound(string reasonCode, string detail, int? returnCode = 16, object? data = null) =>
        ProblemResults.Create(404, ProblemKinds.NotFound, detail, Session, reasonCode, returnCode, returnCode == 16 ? "NOT FOUND" : null, 1, data: data);

    public IResult Forbidden(string reasonCode, string detail) =>
        ProblemResults.Create(403, ProblemKinds.Forbidden, detail, Session, reasonCode);

    public IResult Accepted(object payload, string location)
    {
        Http.Response.Headers.Location = location;
        return Direct(payload, 202);
    }
}

/// <summary>
/// Registry of operation handlers contributed by the slice modules. An operation of the binding contract without a handler
/// answers 501 API0501 naming its slice and wave (FR-STERP-API-002).
/// </summary>
public sealed class OperationRegistry
{
    private readonly Dictionary<string, OperationHandler> _handlers = new(StringComparer.Ordinal);
    private readonly HashSet<string> _deliveredSlices = new(StringComparer.Ordinal);
    private readonly HashSet<string> _withoutDatabase = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> DeliveredSlices => _deliveredSlices;

    public void MarkDelivered(string slice) => _deliveredSlices.Add(slice);

    public bool IsDelivered(string slice) => _deliveredSlices.Contains(slice);

    /// <param name="answersWithoutDatabase">
    /// The operation's binding acceptance criteria define its answer while the database is unavailable (menu, service status,
    /// diagnostics, console self-test): when the STERP user cannot be resolved because the database fails, the handler still
    /// runs, without a user. Allowed only for operations without a screen gate, so no permission check is skipped.
    /// </param>
    public void Handle(string operationId, OperationHandler handler, bool answersWithoutDatabase = false)
    {
        var op = ContractCatalog.Instance.Get(operationId);
        if (answersWithoutDatabase && op.ScreenGate.Count > 0)
        {
            throw new InvalidOperationException($"Operation {operationId} has a screen gate and cannot answer without the database.");
        }
        if (!_handlers.TryAdd(operationId, handler))
        {
            throw new InvalidOperationException($"Operation {operationId} already has a handler.");
        }
        if (answersWithoutDatabase) _withoutDatabase.Add(operationId);
    }

    public bool TryGet(string operationId, out OperationHandler handler) => _handlers.TryGetValue(operationId, out handler!);

    public bool AnswersWithoutDatabase(string operationId) => _withoutDatabase.Contains(operationId);

    public IReadOnlyCollection<string> Handled => _handlers.Keys;
}
