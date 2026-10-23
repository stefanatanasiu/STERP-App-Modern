using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using Sterp.Platform.Kernel;

namespace Sterp.Platform.Hosting.Pipeline;

/// <summary>JSON conventions of the API (contracts 4.2-4.3): camelCase, nulls emitted, decimal scale kept.</summary>
public static class SterpJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            NumberHandling = JsonNumberHandling.Strict,
        };
        o.Converters.Add(new DateOnlyIsoConverter());
        return o;
    }

    private sealed class DateOnlyIsoConverter : JsonConverter<DateOnly>
    {
        public override DateOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateOnly.Parse(reader.GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        public override void Write(Utf8JsonWriter writer, DateOnly value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
    }
}

/// <summary>RFC 9457 problem responses (contracts 4.6-4.7).</summary>
public static class ProblemResults
{
    public const string TypeBase = "https://sterp.local/problems/";

    public static IResult Create(
        int status, string kind, string? detail, SterpContext? session, string? reasonCode = null, int? returnCode = null,
        string? severity = null, int? messageCount = null, string? title = null, object? data = null,
        IDictionary<string, string[]>? errors = null, string? instance = null)
    {
        var body = new JsonObject
        {
            ["type"] = TypeBase + kind,
            ["title"] = title ?? DefaultTitle(kind),
            ["status"] = status,
            ["detail"] = detail,
            ["instance"] = instance,
            ["returnCode"] = returnCode,
            ["reasonCode"] = reasonCode,
            ["severity"] = severity,
            ["messageCount"] = messageCount,
            ["correlationId"] = session?.CorrelationId,
            ["traceId"] = session?.TraceId,
        };
        if (errors is not null)
        {
            var e = new JsonObject();
            foreach (var (k, v) in errors) e[k] = new JsonArray(v.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            body["errors"] = e;
        }
        if (data is not null) body["data"] = data as JsonNode ?? JsonSerializer.SerializeToNode(data, SterpJson.Options);
        return Results.Json(body, SterpJson.Options, "application/problem+json", status);
    }

    public static string DefaultTitle(string kind) => kind switch
    {
        ProblemKinds.BusinessRule => "Business rule refused the request",
        ProblemKinds.Severe => "Severe error",
        ProblemKinds.NotFound => "Not found",
        ProblemKinds.Validation => "Invalid request",
        ProblemKinds.Forbidden => "Forbidden",
        ProblemKinds.Unauthenticated => "Not authenticated",
        ProblemKinds.NotImplemented => "Not yet delivered in the modernized application",
        _ => "Error",
    };

    /// <summary>Legacy result -> envelope (RC 0/4/6) or problem (RC 8/12/16/other), contracts 4.6.</summary>
    public static IResult FromLegacy(LegacyResult r, object? data, SterpContext session)
    {
        r = r.Normalized();
        switch (r.ReturnCode)
        {
            case 0 or 4 or 6:
                var envelope = new JsonObject
                {
                    ["outcome"] = OutcomeNode(r, session),
                    ["data"] = data as JsonNode ?? (data is null ? null : JsonSerializer.SerializeToNode(data, SterpJson.Options)),
                };
                return Results.Json(envelope, SterpJson.Options, "application/json", r.ReturnCode == 6 ? 202 : 200);
            case 8:
                return Create(422, ProblemKinds.BusinessRule, r.Message, session, r.ReasonCode, 8, "ERROR", r.MessageCount, data: data);
            case 16:
                return Create(404, ProblemKinds.NotFound, r.Message, session, r.ReasonCode, 16, "NOT FOUND", r.MessageCount, data: data);
            default:
                return Create(500, ProblemKinds.Severe, r.Message, session, r.ReasonCode, r.ReturnCode, r.Severity, r.MessageCount, data: data);
        }
    }

    public static JsonObject OutcomeNode(LegacyResult r, SterpContext session) => new()
    {
        ["returnCode"] = r.ReturnCode,
        ["severity"] = r.Severity,
        ["reasonCode"] = r.ReasonCode,
        ["message"] = r.Message,
        ["messageCount"] = r.MessageCount,
        ["documentRef"] = r.DocumentRef,
        ["correlationId"] = session.CorrelationId,
    };

    /// <summary>Exception -> problem (contracts 4.6, 6.6).</summary>
    public static IResult FromException(Exception ex, SterpContext session) => ex switch
    {
        LegacyOutcomeException lo => FromLegacy(lo.Result, lo.Payload, session),
        BusinessRuleException bre => Create(422, ProblemKinds.BusinessRule, bre.Message, session, bre.Code, null, bre.Severity.ToString(), 1),
        SqlioException sx => Create(500, ProblemKinds.Severe, sx.DriverText, session, "SQL0001", 12, "SEVERE", 1),
        ApiProblemException ap => Create(ap.Status, ap.Kind, ap.Detail, session, ap.ReasonCode, null, null, null, ap.Title, errors: ap.Errors),
        _ => Create(500, ProblemKinds.Severe, ex.Message, session, "SYS0001", null, "SEVERE", 1),
    };
}
