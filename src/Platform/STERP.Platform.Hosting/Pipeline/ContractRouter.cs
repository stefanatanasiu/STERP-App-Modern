using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Sterp.Platform.Hosting.Contracts;
using Sterp.Platform.Kernel;
using Sterp.Platform.Kernel.Data;

namespace Sterp.Platform.Hosting.Pipeline;

/// <summary>
/// Contract-first routing (FR-STERP-API-002, -010, -012; contracts 4.5, 5.2-5.4): one route per binding operation of this host.
/// Pipeline per call: STERP user (403 API0010) -> run-as (403 API0003) -> context headers (400 API0001) -> company/site access
/// (403 API0002) -> delivered slice (501 API0501) -> screen/catalogue gate (403 PRM0006) -> slice handler -> outcome/problem
/// -> 2xx JSON body conformed to the schema's property names (<see cref="ResponseNames"/>).
/// </summary>
public static class ContractRouter
{
    private static readonly HashSet<string> HostDocuments = ["STERP-API", "STERP-JOBS"];

    public static IReadOnlyList<ContractOperation> OperationsFor(string engine) =>
        ContractCatalog.Instance.Operations
            .Where(o => !HostDocuments.Contains(o.Document))
            .Where(o => engine == "batch" ? o.IsJobsHost : !o.IsJobsHost)
            .ToList();

    public static void MapContractOperations(this IEndpointRouteBuilder app, string engine)
    {
        foreach (var op in OperationsFor(engine))
        {
            var operation = op;
            app.MapMethods(op.Path, [op.Method], (HttpContext http) => ExecuteAsync(http, operation))
               .WithName(op.OperationId)
               .WithMetadata(op)
               .RequireCors(SterpHostBuilder.CorsPolicy)
               .RequireAuthorization();
        }
    }

    public static async Task ExecuteAsync(HttpContext http, ContractOperation op)
    {
        var services = http.RequestServices;
        var session = services.GetRequiredService<SterpContext>();
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("Sterp.Operation");
        var watch = Stopwatch.StartNew();
        session.SetTrace(TraceIdOf(http), op.OperationId);
        IResult result;
        try
        {
            CorrelationAndDate(http, session);
            IResult? refusal;
            var withoutUser = false;
            try
            {
                refusal = await ResolveUserAsync(http, session);
            }
            catch (System.Data.Common.DbException ex) when (services.GetRequiredService<OperationRegistry>().AnswersWithoutDatabase(op.OperationId))
            {
                // legacy degraded answers (MenuService.vb:11-18, EngineService.vb:31-42, STERPOPS SMOKE RC 8): the signed-on
                // client kept its session, the target resolves per request, so the binding answer is given without a user
                logger.LogWarning(ex, "{OperationId}: STERP user not resolvable, database unavailable - answering without a user", op.OperationId);
                refusal = null;
                withoutUser = true;
            }
            result = refusal ?? await RunAsync(http, op, session, withoutUser);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !http.RequestAborted.IsCancellationRequested)
        {
            if (ex is not (LegacyOutcomeException or BusinessRuleException or ApiProblemException))
            {
                logger.LogError(ex, "Operation {OperationId} failed", op.OperationId);
            }
            result = ProblemResults.FromException(ex, session);
        }
        ConformResponse(op, result, logger);
        await result.ExecuteAsync(http);
        watch.Stop();
        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["traceId"] = session.TraceId,
            ["user"] = session.IsResolved ? session.UserCode : null,
            ["realUser"] = session.IsResolved ? session.RealUserCode : null,
            ["company"] = session.CompanyCode,
            ["site"] = session.SiteCode,
            ["operationId"] = op.OperationId,
            ["legacyProgram"] = session.LegacyProgram,
            ["correlationId"] = session.CorrelationId,
        }))
        {
            logger.LogInformation("{OperationId} {Method} {Path} -> {Status} in {ElapsedMs} ms", op.OperationId, op.Method,
                http.Request.Path.Value, http.Response.StatusCode, watch.ElapsedMilliseconds);
        }
    }

    /// <summary>A 2xx JSON body takes the property spelling of the binding response schema (contracts 4.3).</summary>
    private static void ConformResponse(ContractOperation op, IResult result, ILogger logger)
    {
        if (result is not IValueHttpResult { Value: JsonNode body }) return;
        var status = (result as IStatusCodeHttpResult)?.StatusCode ?? 200;
        if (status is < 200 or > 299) return;
        if (result is IContentTypeHttpResult { ContentType: { } contentType } && !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            ResponseNames.Conform(op, status, body);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Response of {OperationId} was not conformed to its schema names", op.OperationId);
        }
    }

    private static async Task<IResult> RunAsync(HttpContext http, ContractOperation op, SterpContext session, bool withoutUser)
    {
        var services = http.RequestServices;
        var problem = ValidateScope(http, op, session, checkAccess: !withoutUser);
        if (problem is not null) return problem;

        var registry = services.GetRequiredService<OperationRegistry>();
        var hostOptions = services.GetRequiredService<IOptions<SterpHostOptions>>().Value;
        var undelivered = hostOptions.UndeliveredSlices.Contains(op.Slice, StringComparer.OrdinalIgnoreCase);
        if (undelivered || !registry.TryGet(op.OperationId, out var handler))
        {
            return ProblemResults.Create(501, ProblemKinds.NotImplemented,
                $"{op.OperationId} belongs to slice {op.Slice} (wave {op.Wave}), which is not yet delivered in the modernized application - use the legacy client.",
                session, ApiReasons.NotDelivered);
        }

        if (op.ScreenGate.Count > 0 && !await ScreenGateAsync(services, op, session, http.RequestAborted))
        {
            return ProblemResults.Create(403, ProblemKinds.Forbidden, $"{session.UserCode} does not hold {op.ScreenGate[0]}",
                session, ApiReasons.ScreenGateRefused);
        }

        var uow = services.GetRequiredService<IUnitOfWork>();
        try
        {
            return await handler(new OperationContext(http, op, session));
        }
        finally
        {
            // end of request = end of the legacy engine process for this call: an open unit is rolled back
            if (uow.InTransaction) await uow.RollbackAsync();
        }
    }

    private static void CorrelationAndDate(HttpContext http, SterpContext session)
    {
        var key = http.Request.Headers["Idempotency-Key"].ToString();
        if (key.Length > 36)
        {
            throw ApiProblemException.BadHeader("Idempotency-Key must be at most 36 characters.");
        }
        session.SetCorrelation(key.Length > 0 ? key : Guid.NewGuid().ToString(), key.Length > 0);

        var date = http.Request.Headers["X-STERP-Business-Date"].ToString().Trim();
        if (date.Length > 0)
        {
            if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            {
                throw ApiProblemException.BadHeader("X-STERP-Business-Date must be an ISO date (yyyy-MM-dd).");
            }
            session.SetBusinessDate(d, true);
        }
        else
        {
            session.SetBusinessDate(DateOnly.FromDateTime(DateTime.Today), false);
        }
    }

    /// <summary>Resolves the STERP user and the SYSADM run-as user; returns a problem result on refusal.</summary>
    public static async Task<IResult?> ResolveUserAsync(HttpContext http, SterpContext session)
    {
        var resolver = http.RequestServices.GetRequiredService<UserContextResolver>();
        var login = http.User.Identity?.Name ?? "";
        var real = await resolver.ByLoginAsync(login, http.RequestAborted);
        if (real is null)
        {
            return ProblemResults.Create(403, ProblemKinds.Forbidden, $"No STERP user is mapped to {login}.", session, ApiReasons.NoUserMapped);
        }
        var effective = real;
        var runAs = http.Request.Headers["X-STERP-Run-As"].ToString().Trim();
        if (runAs.Length > 0 && !string.Equals(runAs, real.UserCode, StringComparison.OrdinalIgnoreCase))
        {
            if (!real.IsSysAdm)
            {
                return ProblemResults.Create(403, ProblemKinds.Forbidden, "Only SYSADM may continue as another user.", session, ApiReasons.RunAsNotSysAdm);
            }
            if (runAs.Length > 20) throw ApiProblemException.BadHeader("X-STERP-Run-As must be at most 20 characters.");
            var target = await resolver.ByUserCodeAsync(runAs, http.RequestAborted);
            if (target is null)
            {
                return ProblemResults.Create(403, ProblemKinds.Forbidden, $"No STERP user is mapped to {runAs}.", session, ApiReasons.NoUserMapped);
            }
            effective = target;
        }
        session.SetUser(login, real, effective);
        return null;
    }

    /// <summary>
    /// Company/site context headers (contracts 4.5, 5.3): required presence and access-list membership. Without a resolved
    /// user (an operation answering while the database is unavailable) only presence and format are checked.
    /// </summary>
    public static IResult? ValidateScope(HttpContext http, ContractOperation? op, SterpContext session, bool checkAccess = true)
    {
        var company = http.Request.Headers["X-STERP-Company"].ToString().Trim().ToUpperInvariant();
        var site = http.Request.Headers["X-STERP-Site"].ToString().Trim().ToUpperInvariant();
        if (company.Length == 0 && op?.RequiresHeader("X-STERP-Company") == true)
        {
            return ProblemResults.Create(400, ProblemKinds.Validation, "X-STERP-Company is required for this operation.", session, ApiReasons.MalformedHeader);
        }
        if (site.Length == 0 && op?.RequiresHeader("X-STERP-Site") == true)
        {
            return ProblemResults.Create(400, ProblemKinds.Validation, "X-STERP-Site is required for this operation.", session, ApiReasons.MalformedHeader);
        }
        if (company.Length > 10 || site.Length > 10)
        {
            return ProblemResults.Create(400, ProblemKinds.Validation, "X-STERP-Company and X-STERP-Site are at most 10 characters.", session, ApiReasons.MalformedHeader);
        }
        if (company.Length > 0 && checkAccess)
        {
            var allowed = site.Length > 0 ? session.User.HasCompanySite(company, site) : session.User.HasCompany(company);
            if (!allowed)
            {
                var what = site.Length > 0 ? $"{company}/{site}" : company;
                return ProblemResults.Create(403, ProblemKinds.Forbidden, $"{session.UserCode} has no access to {what}.", session, ApiReasons.CompanySiteNotAllowed);
            }
        }
        else if (site.Length > 0 && company.Length == 0)
        {
            return ProblemResults.Create(400, ProblemKinds.Validation, "X-STERP-Site requires X-STERP-Company.", session, ApiReasons.MalformedHeader);
        }
        session.SetScope(company, site);
        return null;
    }

    /// <summary>Screen and catalogue gate (contracts 5.4): any one granted code admits the call.</summary>
    private static async Task<bool> ScreenGateAsync(IServiceProvider services, ContractOperation op, SterpContext session, CancellationToken ct)
    {
        var sql = services.GetRequiredService<IProcedureRunner>();
        foreach (var code in op.ScreenGate)
        {
            if (await CheckAccessAsync(sql, session.UserCode, code, session.CompanyCode, session.SiteCode, ct)) return true;
        }
        return false;
    }

    /// <summary>core.usp_UiCheckAccess @UserCode, @PermissionCode, @CompanyCode, @SiteCode, @Allowed OUTPUT.</summary>
    public static async Task<bool> CheckAccessAsync(IProcedureRunner sql, string userCode, string permission, string company, string site, CancellationToken ct)
    {
        var allowed = new SqlParameter("@Allowed", System.Data.SqlDbType.Bit) { Direction = System.Data.ParameterDirection.Output };
        await sql.ExecuteCommandAsync("core.usp_UiCheckAccess", p =>
        {
            p.AddWithValue("@UserCode", userCode);
            p.AddWithValue("@PermissionCode", permission);
            p.AddWithValue("@CompanyCode", company);
            p.AddWithValue("@SiteCode", site);
            p.Add(allowed);
        }, ct);
        return allowed.Value is bool b && b;
    }

    /// <summary>W3C trace id of the request (traceparent), else the current activity's.</summary>
    public static string TraceIdOf(HttpContext http)
    {
        var tp = http.Request.Headers["traceparent"].ToString();
        var parts = tp.Split('-');
        if (parts.Length >= 4 && parts[1].Length == 32) return parts[1];
        return Activity.Current?.TraceId.ToString() ?? ActivityTraceId.CreateRandom().ToString();
    }

    public static string WindowsLogin(ClaimsPrincipal user) => user.Identity?.Name ?? "";
}
