using System.Text.Encodings.Web;
using System.Text.Json;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Endpoints;

public static class CentralEndpoints
{
    public static void MapCentralEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/central");
        group.MapGet("/status", StatusAsync);
        group.MapPost("/create", CreateAsync);
        group.MapPost("/setup", SetupAsync);
        group.MapPost("/test", TestAsync);
        group.MapPost("/promote", PromoteAsync);
        group.MapPost("/link", LinkAsync);
        group.MapPost("/bootstrap", BootstrapAsync);
        group.MapPost("/sync", SyncAsync);
        group.MapPost("/branch-remote", PrepareRemoteAsync);
    }

    private static async Task<IResult> StatusAsync(HttpContext http, AuthService auth, SyncEndpointStore store, CancellationToken cancellationToken)
    {
        if (await RequireAdminAsync(http, auth, cancellationToken) is { } denied)
        {
            return denied;
        }

        return Results.Ok(new
        {
            central = await store.GetAsync(DatabaseRole.Central, cancellationToken),
            branchRemote = await store.GetAsync(DatabaseRole.BranchRemote, cancellationToken),
            branchRemoteSyncImplemented = BranchRemoteSync.IsImplemented,
            futureCustomersRegistered = false,
        });
    }

    private static async Task<IResult> CreateAsync(
        CentralConnectionRequest? request,
        HttpContext http,
        AuthService auth,
        CentralDatabaseService databases,
        SyncEndpointStore store,
        CancellationToken cancellationToken)
    {
        if (await RequireAdminAsync(http, auth, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!TrySettings(request, DatabaseRole.Central, out var settings, out var error))
        {
            return Results.BadRequest(new { message = error });
        }

        try
        {
            await databases.CreateIfMissingAsync(settings, cancellationToken);
            var saved = await store.SaveAsync(settings, SqlEndpointSettings.Linked, cancellationToken);
            return Results.Ok(saved);
        }
        catch (CentralRejectedException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
    }

    private static async Task SetupAsync(
        HttpContext http,
        AuthService auth,
        CentralSetupExecutor executor,
        CancellationToken cancellationToken)
    {
        if (await RequireAdminAsync(http, auth, cancellationToken) is { } denied)
        {
            await denied.ExecuteAsync(http);
            return;
        }

        CentralSetupRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<CentralSetupRequest>(cancellationToken);
        }
        catch (JsonException)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { message = "بيانات الإعداد غير صالحة." }, cancellationToken);
            return;
        }

        http.Response.ContentType = "application/x-ndjson; charset=utf-8";
        var json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        await executor.ExecuteAsync(request ?? new CentralSetupRequest(), async (setupEvent, token) =>
        {
            await http.Response.WriteAsync(JsonSerializer.Serialize(setupEvent, json) + "\n", token);
            await http.Response.Body.FlushAsync(token);
        }, cancellationToken);
    }

    private static async Task<IResult> TestAsync(
        CentralConnectionRequest? request,
        HttpContext http,
        AuthService auth,
        CentralDatabaseService databases,
        CancellationToken cancellationToken)
    {
        if (await RequireAdminAsync(http, auth, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!TrySettings(request, DatabaseRole.Central, out var settings, out var error))
        {
            return Results.BadRequest(new { message = error });
        }

        try
        {
            await databases.VerifyExistingAsync(settings, cancellationToken);
            return Results.Ok(new { compatible = true });
        }
        catch (CentralRejectedException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
    }

    private static async Task<IResult> PromoteAsync(
        HttpContext http,
        AuthService auth,
        SyncEndpointStore store,
        SharedDataSync sync,
        CancellationToken cancellationToken)
    {
        if (await RequireAdminAsync(http, auth, cancellationToken) is { } denied)
        {
            return denied;
        }

        var settings = await store.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (settings is null)
        {
            return Results.BadRequest(new { message = "لم يتم إعداد القاعدة المركزية." });
        }

        var items = await sync.PromoteAsync(settings, cancellationToken);
        return Results.Ok(new { items });
    }

    private static async Task<IResult> LinkAsync(
        HttpContext http,
        AuthService auth,
        SyncEndpointStore store,
        CentralDatabaseService databases,
        CancellationToken cancellationToken)
    {
        if (await RequireAdminAsync(http, auth, cancellationToken) is { } denied)
        {
            return denied;
        }

        var settings = await store.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (settings is null)
        {
            return Results.BadRequest(new { message = "لم يتم إعداد القاعدة المركزية." });
        }

        await databases.VerifyExistingAsync(settings, cancellationToken);
        var saved = await store.SaveAsync(settings, SqlEndpointSettings.Linked, cancellationToken);
        return Results.Ok(saved);
    }

    private static async Task<IResult> BootstrapAsync(
        HttpContext http,
        AuthService auth,
        SyncEndpointStore store,
        SharedDataSync sync,
        CancellationToken cancellationToken)
    {
        if (await RequireAdminAsync(http, auth, cancellationToken) is { } denied)
        {
            return denied;
        }

        var view = await store.GetAsync(DatabaseRole.Central, cancellationToken);
        var settings = await store.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (view is null || settings is null)
        {
            return Results.BadRequest(new { message = "لم يتم ربط الفرع بقاعدة مركزية." });
        }

        var items = await sync.BootstrapAsync(settings, cancellationToken);
        await sync.PullAsync(view.EndpointId, settings, cancellationToken);
        return Results.Ok(new { items });
    }

    private static async Task<IResult> SyncAsync(
        HttpContext http,
        AuthService auth,
        SyncEndpointStore store,
        SharedDataSync sync,
        CancellationToken cancellationToken)
    {
        if (await RequireAdminAsync(http, auth, cancellationToken) is { } denied)
        {
            return denied;
        }

        var view = await store.GetAsync(DatabaseRole.Central, cancellationToken);
        var settings = await store.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (view is null || settings is null)
        {
            return Results.BadRequest(new { message = "لم يتم ربط الفرع بقاعدة مركزية." });
        }

        var result = await sync.PullAsync(view.EndpointId, settings, cancellationToken);
        return Results.Ok(result);
    }

    private static async Task<IResult> PrepareRemoteAsync(
        CentralConnectionRequest? request,
        HttpContext http,
        AuthService auth,
        SyncEndpointStore store,
        CancellationToken cancellationToken)
    {
        if (await RequireAdminAsync(http, auth, cancellationToken) is { } denied)
        {
            return denied;
        }

        if (!TrySettings(request, DatabaseRole.BranchRemote, out var settings, out var error))
        {
            return Results.BadRequest(new { message = error });
        }

        try
        {
            await using var connection = new Microsoft.Data.SqlClient.SqlConnection(settings.BuildConnectionString("master"));
            await connection.OpenAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            return Results.BadRequest(new { message = SqlEndpointSettings.DescribeConnectionFailure(exception) });
        }

        var saved = await store.SaveAsync(settings, SqlEndpointSettings.PendingRemoteSync, cancellationToken);
        return Results.Ok(saved);
    }

    private static bool TrySettings(
        CentralConnectionRequest? request,
        string role,
        out SqlEndpointSettings settings,
        out string error)
    {
        settings = null!;
        if (request is null || string.IsNullOrWhiteSpace(request.DatabaseName))
        {
            error = "يرجى إدخال اسم قاعدة البيانات.";
            return false;
        }

        var databaseName = request.DatabaseName.Trim();
        if (string.Equals(role, DatabaseRole.Central, StringComparison.Ordinal) && !CentralDatabase.IsAllowedName(databaseName))
        {
            error = "اسم القاعدة المركزية غير مسموح.";
            return false;
        }

        if (string.Equals(role, DatabaseRole.BranchRemote, StringComparison.Ordinal)
            && (!BranchDatabase.IsSafeIdentifier(databaseName) || string.Equals(databaseName, CentralDatabase.Name, StringComparison.OrdinalIgnoreCase)))
        {
            error = "اسم القاعدة البعيدة يجب أن يكون مختلفاً عن القاعدة المركزية.";
            return false;
        }

        var location = string.Equals(request.LocationKind, SqlEndpointSettings.Remote, StringComparison.Ordinal)
            ? SqlEndpointSettings.Remote
            : SqlEndpointSettings.Local;
        var server = string.IsNullOrWhiteSpace(request.Server) ? "localhost" : request.Server.Trim();
        var authentication = string.Equals(request.AuthenticationType, SqlEndpointSettings.SqlLogin, StringComparison.Ordinal)
            ? SqlEndpointSettings.SqlLogin
            : SqlEndpointSettings.Integrated;
        if (authentication == SqlEndpointSettings.SqlLogin && (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password)))
        {
            error = "يرجى إدخال اسم المستخدم وكلمة المرور.";
            return false;
        }

        settings = new SqlEndpointSettings(
            role,
            location,
            server,
            request.InstanceName,
            request.Port,
            databaseName,
            authentication,
            request.Username,
            request.Password,
            request.Encrypt ?? true,
            request.TrustServerCertificate ?? true);
        error = "";
        return true;
    }

    private static async Task<IResult?> RequireAdminAsync(HttpContext http, AuthService auth, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null)
        {
            return Results.Unauthorized();
        }

        if (!current.Permissions.Contains(PermissionCodes.SystemAdminister))
        {
            return Results.Forbid();
        }

        return null;
    }
}

public sealed class CentralConnectionRequest
{
    public string? LocationKind { get; set; }
    public string? Server { get; set; }
    public string? InstanceName { get; set; }
    public int? Port { get; set; }
    public string? DatabaseName { get; set; }
    public string? AuthenticationType { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool? Encrypt { get; set; }
    public bool? TrustServerCertificate { get; set; }
}
