using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Devices;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Endpoints;

public static class ApiEndpoints
{
    private static readonly JsonSerializerOptions StreamJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void MapSetupEndpoints(this WebApplication app)
    {
        app.MapGet("/api/setup/status", async (SetupService setup, CancellationToken cancellationToken) =>
        {
            var status = await setup.GetStatusAsync(cancellationToken);
            return Results.Ok(status);
        });
        app.MapGet("/api/bootstrap/status", async (SetupService setup, CancellationToken cancellationToken) =>
        {
            var status = await setup.GetStatusAsync(cancellationToken);
            return Results.Ok(status);
        });

        app.MapPost("/api/setup/create-branch", CreateBranchAsync);
        app.MapPost("/api/setup/create-admin", CreateAdminAsync);
        app.MapGet("/api/setup/local-device-recovery", async (LocalDeviceRecovery recovery, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await recovery.GetContextAsync(cancellationToken));
            }
            catch (SetupRejectedException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
        });
        app.MapPost("/api/setup/recover-main-device", async (HttpContext http, LocalDeviceRecovery recovery, CancellationToken cancellationToken) =>
        {
            LocalDeviceRecoveryRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<LocalDeviceRecoveryRequest>(cancellationToken);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { message = "بيانات الربط غير صالحة." });
            }

            try
            {
                var device = await recovery.RecoverAsync(request, cancellationToken);
                return Results.Ok(device);
            }
            catch (SetupRejectedException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (Exception exception)
            {
                app.Logger.LogError(exception, "Main device recovery failed.");
                return Results.Json(
                    new { message = LocalDeviceRecovery.ServerFailureMessage },
                    statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        app.MapPost("/api/setup/new-test-branch", async (HttpContext http, NewTestBranchService branches, CancellationToken cancellationToken) =>
        {
            NewTestBranchRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<NewTestBranchRequest>(cancellationToken);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { message = "بيانات الفرع غير صالحة." });
            }

            try
            {
                return Results.Ok(await branches.CreateAsync(
                    request,
                    http.Request.Headers[IndependentOnboarding.HeaderName].FirstOrDefault(),
                    http.Connection.RemoteIpAddress?.ToString(),
                    cancellationToken));
            }
            catch (SetupRejectedException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (Exception exception)
            {
                app.Logger.LogError(exception, "New test branch setup failed.");
                return Results.Json(new { message = "تعذر إنشاء فرع التجربة." }, statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        app.MapPost("/api/setup/test-connection", TestConnectionAsync);
        app.MapGet("/api/setup/central-databases", ListCentralDatabasesAsync);
        app.MapGet("/api/setup/branch-databases", ListBranchDatabasesAsync);
        app.MapPost("/api/setup/branch-stations", ListBranchStationsAsync);
        app.MapPost("/api/setup/central-admins", ListCentralAdminsAsync);
        app.MapPost("/api/setup/verify-central-admin", VerifyCentralAdminAsync);
        app.MapPost("/api/setup/branch-code", SuggestBranchCodeAsync);
        app.MapGet("/api/setup/branches", ListBranchesAsync);
    }

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/auth/login", async (HttpContext http, AuthService auth, CancellationToken cancellationToken) =>
        {
            LoginRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<LoginRequest>(cancellationToken);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { message = "بيانات الدخول غير صالحة." });
            }

            var result = await auth.LoginAsync(http, request, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result.User)
                : Results.Json(new { message = result.Message }, statusCode: StatusCodes.Status401Unauthorized);
        });

        app.MapPost("/api/auth/logout", async (HttpContext http, AuthService auth) =>
        {
            await auth.LogoutAsync(http);
            return Results.NoContent();
        });

        app.MapPost("/api/auth/change-password", async (HttpContext http, AuthService auth, CancellationToken cancellationToken) =>
        {
            ChangePasswordRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<ChangePasswordRequest>(cancellationToken);
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { message = "بيانات كلمة المرور غير صالحة." });
            }

            var result = await auth.ChangePasswordAsync(http, request, cancellationToken);
            if (result is null)
            {
                return Results.Unauthorized();
            }

            return result.Succeeded
                ? Results.Ok(result.User)
                : Results.BadRequest(new { message = result.Message });
        });

        app.MapGet("/api/auth/me", async (HttpContext http, AuthService auth, CancellationToken cancellationToken) =>
        {
            var user = await auth.GetCurrentAsync(http, cancellationToken);
            return user is null ? Results.Unauthorized() : Results.Ok(user);
        });
    }

    public static void MapDeviceEndpoints(this WebApplication app)
    {
        app.MapGet("/api/device/current", async (HttpRequest request, DeviceService devices, CancellationToken cancellationToken) =>
        {
            try
            {
                var access = await devices.EvaluateAccessAsync(request, cancellationToken);
                if (access.Problem == DeviceAccessProblem.Revoked)
                {
                    return Results.Json(new { code = "Revoked" }, statusCode: StatusCodes.Status403Forbidden);
                }

                var profile = access.Problem == DeviceAccessProblem.None ? DeviceService.ToProfile(access.Device!) : null;
                return profile is null ? Results.Unauthorized() : Results.Ok(profile);
            }
            catch (Exception exception)
            {
                app.Logger.LogWarning(exception, "Current device lookup failed.");
                return Results.Json(new { message = "تعذر قراءة بيانات الجهاز." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        app.MapPost("/api/device/reset", async (HttpContext http, AuthService auth, DeviceService devices, CancellationToken cancellationToken) =>
        {
            var current = await auth.GetCurrentAsync(http, cancellationToken);
            if (current is null)
            {
                return Results.Unauthorized();
            }

            if (!current.Permissions.Contains(PermissionCodes.SystemAdminister, StringComparer.Ordinal))
            {
                return Results.Json(new { message = "إعادة التهيئة متاحة لمدير النظام فقط." }, statusCode: StatusCodes.Status403Forbidden);
            }

            DeviceResetRequest? body = null;
            if (http.Request.ContentLength is > 0)
            {
                body = await http.Request.ReadFromJsonAsync<DeviceResetRequest>(cancellationToken);
            }

            if (body?.DeleteExclusiveDatabase == true)
            {
                var refusal = ProtectedDatabasePolicy.RefuseDrop(
                    body.DatabaseName,
                    body.ExclusiveLocal,
                    body.BackupVerified,
                    body.UnsyncedFinancial);
                if (refusal is not null)
                {
                    var id = SetupLog.Write(http.RequestServices.GetRequiredService<IHostEnvironment>().ContentRootPath, http.RequestServices.GetRequiredService<IHostEnvironment>().EnvironmentName, "device-reset-drop", "refused", detail: refusal);
                    return Results.Json(new { message = refusal, diagnosticId = id }, statusCode: StatusCodes.Status409Conflict);
                }
            }

            if (body?.DeregisterCentral == true && body.PosNumber is int posNumber)
            {
                var registry = http.RequestServices.GetRequiredService<CentralPosRegistryService>();
                var revokedCentral = await registry.RevokeAsync(posNumber, cancellationToken);
                if (!revokedCentral)
                {
                    var id = SetupLog.Write(http.RequestServices.GetRequiredService<IHostEnvironment>().ContentRootPath, http.RequestServices.GetRequiredService<IHostEnvironment>().EnvironmentName, "device-reset-central", "pending");
                    return Results.Json(new { message = "تعذر إلغاء التسجيل في المركزية. بقيت إعدادات الجهاز حتى تكتمل التسوية.", diagnosticId = id }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }
            }

            var revoked = await devices.RevokeCurrentAsync(http.Request, cancellationToken);
            return revoked ? Results.Ok(new { status = "Reset" }) : Results.Unauthorized();
        });
        app.MapGet("/api/sync/status", (SyncStatusStore sync) => Results.Ok(sync.Current));
        app.MapPost("/api/sync/pos-replica", async (CentralPosReplicaSync replica, CancellationToken cancellationToken) =>
        {
            var count = await replica.PullAsync(cancellationToken);
            return Results.Ok(new { copied = count });
        });
        app.MapPost("/api/branches/sql-access", async (BranchSqlAccessRequest request, BranchSqlSecretStore secrets, CancellationToken cancellationToken) =>
        {
            if (request.BranchId == Guid.Empty || string.IsNullOrWhiteSpace(request.Password))
            {
                return Results.BadRequest(new { message = "بيانات اتصال الفرع غير مكتملة." });
            }

            await secrets.SaveAsync(request.BranchId, request.Password, cancellationToken);
            return Results.Ok(new { stored = true });
        });
        app.MapGet("/api/setup/existing-configuration", async (SqlConnectionFactory connections, CancellationToken cancellationToken) =>
        {
            var connectionString = connections.TryCreateBranchConnectionString();
            if (connectionString is null)
            {
                return Results.Json(new { message = "اتصال الفرع غير مهيأ." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(
                """
                SELECT TOP (1) b.BusinessID, b.BusinessName, b.Phone, b.Address, br.BranchID, br.BranchCode, br.BranchName
                FROM dbo.Businesses AS b
                INNER JOIN dbo.Branches AS br ON br.BusinessID = b.BusinessID
                ORDER BY br.CreatedAt;
                """,
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return Results.NotFound(new { message = "لا توجد منشأة محفوظة في هذه القاعدة." });
            }

            return Results.Ok(new
            {
                organizationId = reader.GetGuid(0),
                organizationName = reader.GetString(1),
                phone = reader.IsDBNull(2) ? null : reader.GetString(2),
                address = reader.IsDBNull(3) ? null : reader.GetString(3),
                branchId = reader.GetGuid(4),
                branchCode = reader.IsDBNull(5) ? null : reader.GetString(5),
                branchName = reader.GetString(6),
            });
        });
        app.MapGet("/api/pos/numbers/suggest", async (CentralPosRegistryService registry, SyncStatusStore sync, CancellationToken cancellationToken) =>
        {
            try
            {
                var number = await registry.SuggestAsync(cancellationToken);
                sync.Report("Success", "pos-suggest", null, null);
                return Results.Ok(new { posNumber = number });
            }
            catch (Exception exception)
            {
                var id = SetupLog.Fail(app.Environment.ContentRootPath, app.Environment.EnvironmentName, "pos-suggest", exception);
                sync.Report("Offline", "pos-suggest", "المركزية غير متاحة", id);
                return Results.Json(new { message = "المركزية غير متاحة. لا يُقترح رقم جديد.", diagnosticId = id }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });
        app.MapPost("/api/pos/numbers/reserve", async (PosReserveRequest request, CentralPosRegistryService registry, SyncStatusStore sync, CancellationToken cancellationToken) =>
        {
            sync.Report("InProgress", "pos-reserve", null, null);
            var result = await registry.ReserveAsync(request.PosNumber, request.BranchId, request.PosName, cancellationToken);
            if (!result.Ok)
            {
                sync.Report("Error", "pos-reserve", result.Message, null);
                return Results.Json(new { message = result.Message }, statusCode: StatusCodes.Status409Conflict);
            }

            sync.Report("Success", "pos-reserve", null, null);
            return Results.Ok(new { posNumber = result.Number });
        });

        app.MapPost("/api/device/heartbeat", async (HttpRequest request, DeviceService devices, CancellationToken cancellationToken) =>
        {
            try
            {
                var updated = await devices.HeartbeatAsync(request, cancellationToken);
                return updated ? Results.NoContent() : Results.Unauthorized();
            }
            catch (Exception exception)
            {
                app.Logger.LogWarning(exception, "Device heartbeat failed.");
                return Results.Json(new { message = "تعذر تحديث حالة الجهاز." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        app.MapSecondaryDeviceEndpoints();
    }

    private static async Task CreateBranchAsync(
        HttpContext http,
        SetupService setup,
        CancellationToken cancellationToken)
    {
        CreateBranchRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<CreateBranchRequest>(cancellationToken);
        }
        catch (JsonException)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { message = "بيانات الإعداد غير صالحة." }, cancellationToken);
            return;
        }

        if (!SetupAuthorization.TryAuthorize(
                http.Request,
                http.RequestServices.GetRequiredService<IConfiguration>(),
                http.RequestServices.GetRequiredService<IHostEnvironment>().ContentRootPath,
                InstallationState.IsConfigured(http.RequestServices.GetRequiredService<IHostEnvironment>().ContentRootPath),
                out var authStatus,
                out var authMessage))
        {
            http.Response.StatusCode = authStatus;
            await http.Response.WriteAsJsonAsync(new { message = authMessage }, cancellationToken);
            return;
        }

        if (!BranchSetupValidator.TryValidate(request, out var model, out var message))
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsJsonAsync(new { message }, cancellationToken);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status200OK;
        http.Response.ContentType = "application/x-ndjson; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";

        TopologyPlan? topology = null;
        if (!string.IsNullOrWhiteSpace(request?.OperatingMode))
        {
            try
            {
                topology = BuildTopology(request!);
            }
            catch (SetupRejectedException exception)
            {
                http.Response.StatusCode = StatusCodes.Status400BadRequest;
                await http.Response.WriteAsJsonAsync(new { message = exception.Message }, cancellationToken);
                return;
            }
        }

        await setup.RunAsync(model, async (setupEvent, token) =>
        {
            await http.Response.WriteAsync(JsonSerializer.Serialize(setupEvent, StreamJson) + "\n", token);
            await http.Response.Body.FlushAsync(token);
        }, cancellationToken, topology);
    }

    private static async Task<IResult> ListBranchDatabasesAsync(
        IOptions<SqlServerOptions> options,
        string? businessName,
        CancellationToken cancellationToken)
    {
        try
        {
            var databases = await new BranchDatabaseCatalog(options).ListCompatibleAsync(businessName, cancellationToken);
            return Results.Ok(new { databases });
        }
        catch (Exception exception)
        {
            return Results.BadRequest(new { message = SqlEndpointSettings.DescribeConnectionFailure(exception) });
        }
    }

    private static async Task<IResult> ListCentralAdminsAsync(HttpContext http, SqlProbeRequest? request, CancellationToken cancellationToken)
    {
        var result = await new CentralAdminLookup().ListAsync(
            request,
            cancellationToken,
            http.RequestServices.GetRequiredService<IHostEnvironment>(),
            http.RequestServices.GetRequiredService<IConfiguration>());
        return result.Ok
            ? Results.Ok(new { admins = result.Admins })
            : Results.BadRequest(new { message = result.Message });
    }

    private static async Task<IResult> VerifyCentralAdminAsync(HttpContext http, VerifyCentralAdminBody? body, CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return Results.BadRequest(new { message = "بيانات التحقق غير مكتملة." });
        }

        var passwords = http.RequestServices.GetRequiredService<PasswordService>();
        var probe = new SqlProbeRequest
        {
            Server = body.Server,
            InstanceName = body.InstanceName,
            Port = body.Port,
            DatabaseName = body.DatabaseName,
            AuthenticationType = body.AuthenticationType,
            Username = body.Username,
            Password = body.Password,
            Encrypt = body.Encrypt,
            TrustServerCertificate = body.TrustServerCertificate,
            ExpectedRole = "Central",
        };
        var result = await new CentralAdminLookup().VerifyAsync(
            probe,
            body.AdminUsername,
            body.AdminPassword,
            (hash, password) => passwords.Verify(new AppUser(), hash, password) != PasswordVerificationResult.Failed,
            cancellationToken,
            http.RequestServices.GetRequiredService<IHostEnvironment>(),
            http.RequestServices.GetRequiredService<IConfiguration>());
        return result.Ok && result.Admin is not null
            ? Results.Ok(new { admin = result.Admin })
            : Results.BadRequest(new { message = result.Message });
    }

    private static async Task<IResult> ListBranchStationsAsync(HttpContext http, SqlProbeRequest? request, CancellationToken cancellationToken)
    {
        var result = await new BranchStationLookup().ListAsync(
            request,
            cancellationToken,
            http.RequestServices.GetRequiredService<IHostEnvironment>(),
            http.RequestServices.GetRequiredService<IConfiguration>());
        return result.Ok
            ? Results.Ok(new { stations = result.Stations })
            : Results.BadRequest(new { message = result.Message });
    }

    private static async Task<IResult> ListCentralDatabasesAsync(IOptions<SqlServerOptions> options, CancellationToken cancellationToken)
    {
        try
        {
            var databases = await new CentralDatabaseCatalog(options).ListCompatibleAsync(cancellationToken);
            return Results.Ok(new { databases });
        }
        catch (Exception exception)
        {
            return Results.BadRequest(new { message = SqlEndpointSettings.DescribeConnectionFailure(exception) });
        }
    }

    private static async Task<IResult> TestConnectionAsync(
        HttpContext http,
        SqlProbeRequest? request,
        CancellationToken cancellationToken)
    {
        var configuration = http.RequestServices.GetRequiredService<IConfiguration>();
        var probe = new SetupConnectionProbe(
            http.RequestServices.GetRequiredService<IHostEnvironment>(),
            configuration: configuration);
        var result = await probe.ProbeAsync(request, cancellationToken);
        if (!result.Ok)
        {
            var message = result.Message ?? "failed";
            if (!message.Contains("رمز التشخيص:", StringComparison.Ordinal))
            {
                var environment = http.RequestServices.GetRequiredService<IHostEnvironment>();
                var id = SetupLog.Write(environment.ContentRootPath, environment.EnvironmentName, "connection-test", message);
                message = $"{message} رمز التشخيص: {id}";
            }

            return Results.BadRequest(new { message });
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> SuggestBranchCodeAsync(
        HttpContext http,
        BranchCodeRequest? request,
        SqlConnectionFactory connections,
        CancellationToken cancellationToken)
    {
        string? configured = null;
        try
        {
            configured = connections.TryCreateBranchConnectionString();
        }
        catch (Exception)
        {
            configured = null;
        }

        var advice = await new BranchCodeAdvisor().AdviseAsync(
            request,
            configured,
            cancellationToken,
            http.RequestServices.GetRequiredService<IHostEnvironment>(),
            http.RequestServices.GetRequiredService<IConfiguration>());
        return advice.Ok ? Results.Ok(advice) : Results.BadRequest(advice);
    }

    private static async Task<IResult> ListBranchesAsync(SqlConnectionFactory connections, CancellationToken cancellationToken)
    {
        var branches = new List<object>();
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            DECLARE @configured uniqueidentifier = TRY_CONVERT(uniqueidentifier, (
                SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'BranchID'));

            SELECT BranchID, BranchName, BranchCode
            FROM dbo.Branches
            WHERE IsActive = 1
              AND (
                    @configured IS NULL
                    OR NOT EXISTS (SELECT 1 FROM dbo.Branches AS bound WHERE bound.BranchID = @configured)
                    OR BranchID = @configured)
            ORDER BY BranchName;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            branches.Add(new
            {
                branchId = reader.GetGuid(0),
                branchName = reader.GetString(1),
                branchCode = reader.IsDBNull(2) ? null : reader.GetString(2),
                databaseName = connections.ConfiguredDatabase,
            });
        }

        return Results.Ok(new { databaseName = connections.ConfiguredDatabase, branches });
    }

    private static TopologyPlan BuildTopology(CreateBranchRequest request)
    {
        SqlEndpointSettings? central = null;
        if (!string.IsNullOrWhiteSpace(request.CentralDatabaseName))
        {
            central = CentralRoute.Bind(
                request.CentralLocation,
                request.CentralServer,
                request.CentralInstance,
                request.CentralPort,
                request.CentralDatabaseName,
                request.CentralAuthentication,
                request.CentralUsername,
                request.CentralPassword,
                request.CentralEncrypt ?? true,
                request.CentralTrustServerCertificate ?? true);
        }

        SqlEndpointSettings? remote = null;
        if (string.Equals(request.BranchRemoteChoice, "yes", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(request.BranchRemoteDatabaseName))
        {
            remote = new SqlEndpointSettings(
                Data.DatabaseRole.BranchRemote,
                SqlEndpointSettings.Remote,
                string.IsNullOrWhiteSpace(request.BranchRemoteServer) ? "localhost" : request.BranchRemoteServer.Trim(),
                null,
                request.BranchRemotePort,
                request.BranchRemoteDatabaseName.Trim(),
                string.Equals(request.BranchRemoteAuthentication, SqlEndpointSettings.SqlLogin, StringComparison.Ordinal) ? SqlEndpointSettings.SqlLogin : SqlEndpointSettings.Integrated,
                request.BranchRemoteUsername,
                request.BranchRemotePassword,
                true,
                true);
        }

        return new TopologyPlan(
            request.OperatingMode ?? "Standalone",
            string.Equals(request.CentralAction, "create", StringComparison.OrdinalIgnoreCase),
            string.Equals(request.LinkToCentral, "yes", StringComparison.OrdinalIgnoreCase),
            central,
            remote,
            BranchSetupValidator.ReusesCentralAdmin(request, out var centralAdminId) ? centralAdminId : null);
    }

    private static async Task<IResult> CreateAdminAsync(
        HttpContext http,
        SetupService setup,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        CreateAdminRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<CreateAdminRequest>(cancellationToken);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { message = "بيانات الإعداد غير صالحة." });
        }

        if (request is null)
        {
            return Results.BadRequest(new { message = "بيانات الإعداد غير صالحة." });
        }

        if (!SetupAuthorization.TryAuthorize(
                http.Request,
                http.RequestServices.GetRequiredService<IConfiguration>(),
                http.RequestServices.GetRequiredService<IHostEnvironment>().ContentRootPath,
                InstallationState.IsConfigured(http.RequestServices.GetRequiredService<IHostEnvironment>().ContentRootPath),
                out var authStatus,
                out var authMessage))
        {
            return Results.Json(new { message = authMessage }, statusCode: authStatus);
        }

        if (!BranchSetupValidator.TryValidateAdmin(
                request.Username,
                request.DisplayName,
                request.FullName,
                request.EmployeeNumber,
                request.Password,
                request.ConfirmPassword,
                out var admin,
                out var message))
        {
            return Results.BadRequest(new { message });
        }

        try
        {
            await setup.CreateMissingAdminAsync(http.Request, admin, cancellationToken);
            return Results.Ok(new { message = "تم إنشاء حساب مدير النظام." });
        }
        catch (SetupRejectedException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
        catch (Exception exception)
        {
            loggerFactory.CreateLogger("XtraPOS.Setup").LogError(exception, "First admin creation failed.");
            return Results.Json(
                new { message = "تعذر إنشاء حساب مدير النظام." },
                statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}

public sealed class VerifyCentralAdminBody
{
    public string? Server { get; set; }
    public string? InstanceName { get; set; }
    public int? Port { get; set; }
    public string? DatabaseName { get; set; }
    public string? AuthenticationType { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool? Encrypt { get; set; }
    public bool? TrustServerCertificate { get; set; }
    public string? AdminUsername { get; set; }
    public string? AdminPassword { get; set; }
}

public sealed class DeviceResetRequest
{
    public bool DeregisterCentral { get; set; }
    public bool DeleteExclusiveDatabase { get; set; }
    public bool ExclusiveLocal { get; set; }
    public bool BackupVerified { get; set; }
    public bool UnsyncedFinancial { get; set; }
    public string? DatabaseName { get; set; }
    public int? PosNumber { get; set; }
}

public sealed class BranchSqlAccessRequest
{
    public Guid BranchId { get; set; }
    public string Password { get; set; } = "";
}

public sealed class PosReserveRequest
{
    public int PosNumber { get; set; }
    public Guid BranchId { get; set; }
    public string PosName { get; set; } = "";
}

public sealed class CreateAdminRequest
{
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
    public string? FullName { get; set; }
    public string? EmployeeNumber { get; set; }
    public string? Password { get; set; }
    public string? ConfirmPassword { get; set; }
}
