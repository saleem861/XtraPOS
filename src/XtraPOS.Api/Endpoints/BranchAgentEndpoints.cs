using XtraPOS.Api.Setup;

namespace XtraPOS.Api.Endpoints;

public static class BranchAgentEndpoints
{
    public static void MapBranchAgentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/setup/branch-agents/pairing-codes", IssuePairingCode);
        app.MapGet("/api/setup/branch-agents", ListComputers);
        app.MapPost("/api/branch-agent/pair", Pair);
        app.MapPost("/api/branch-agent/heartbeat", Heartbeat);
        app.MapGet("/api/branch-agent/jobs", ClaimJob);
        app.MapPost("/api/branch-agent/jobs/{id:guid}/events", Report);
    }

    private static IResult IssuePairingCode(HttpContext http, BranchAgentRegistry registry, IHostEnvironment environment)
    {
        if (!AllowWizard(http, environment, out var failure))
        {
            return failure!;
        }

        var (code, expiresAt) = registry.IssuePairingCode(DateTimeOffset.UtcNow);
        return Results.Ok(new { code, expiresAt });
    }

    private static IResult ListComputers(HttpContext http, BranchAgentRegistry registry, IHostEnvironment environment)
    {
        if (!AllowWizard(http, environment, out var failure))
        {
            return failure!;
        }

        return Results.Ok(new { computers = registry.List(DateTimeOffset.UtcNow) });
    }

    private static IResult Pair(BranchAgentPairRequest? request, BranchAgentRegistry registry)
    {
        try
        {
            var (agentId, token) = registry.Pair(request?.Code, request?.MachineName, request?.Instances, DateTimeOffset.UtcNow);
            return Results.Ok(new { agentId, token });
        }
        catch (BranchAgentRejectedException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
    }

    private static IResult Heartbeat(HttpContext http, BranchAgentHeartbeatRequest? request, BranchAgentRegistry registry)
    {
        try
        {
            registry.Heartbeat(ReadBearer(http), request?.Instances, DateTimeOffset.UtcNow);
            return Results.NoContent();
        }
        catch (BranchAgentRejectedException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static IResult ClaimJob(HttpContext http, BranchAgentRegistry registry)
    {
        try
        {
            var job = registry.Claim(ReadBearer(http), DateTimeOffset.UtcNow);
            return job is null ? Results.NoContent() : Results.Ok(job);
        }
        catch (BranchAgentRejectedException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static IResult Report(Guid id, HttpContext http, BranchAgentEventRequest? request, BranchAgentRegistry registry)
    {
        try
        {
            registry.Report(
                ReadBearer(http),
                id,
                request?.Nonce,
                new BranchAgentProgress(request?.Type ?? "", request?.Step, request?.State, request?.Message, request?.RemoteAgent == true),
                DateTimeOffset.UtcNow);
            return Results.NoContent();
        }
        catch (BranchAgentRejectedException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
    }

    private static bool AllowWizard(HttpContext http, IHostEnvironment environment, out IResult? failure)
    {
        if (!InstallationState.IsConfigured(environment.ContentRootPath) || http.User.Identity?.IsAuthenticated == true)
        {
            failure = null;
            return true;
        }

        failure = Results.Json(new { message = "يلزم دخول مدير معتمد لربط جهاز الفرع." }, statusCode: StatusCodes.Status401Unauthorized);
        return false;
    }

    private static string? ReadBearer(HttpContext http)
    {
        var header = http.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? header[prefix.Length..].Trim() : null;
    }
}

public sealed class BranchAgentPairRequest
{
    public string? Code { get; set; }
    public string? MachineName { get; set; }
    public string[]? Instances { get; set; }
}

public sealed class BranchAgentHeartbeatRequest
{
    public string[]? Instances { get; set; }
}

public sealed class BranchAgentEventRequest
{
    public string? Nonce { get; set; }
    public string? Type { get; set; }
    public string? Step { get; set; }
    public string? State { get; set; }
    public string? Message { get; set; }
    public bool? RemoteAgent { get; set; }
}
