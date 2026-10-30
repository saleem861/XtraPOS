using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Devices;

public static class SecondaryDeviceEndpoints
{
    public static void MapSecondaryDeviceEndpoints(this WebApplication app)
    {
        app.MapPost("/api/devices/pairing-codes", IssuePairingCodeAsync);
        app.MapPost("/api/devices/pairing-codes/verify", VerifyPairingCodeAsync);
        app.MapGet("/api/devices/stations", ListStationsAsync);
        app.MapPost("/api/devices/stations", CreateStationAsync);
        app.MapPost("/api/devices/register", RegisterAsync);
    }

    private static async Task<IResult> IssuePairingCodeAsync(
        HttpContext http,
        AuthService auth,
        DevicePairingService pairing,
        BranchDbContext db,
        CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null)
        {
            return Results.Unauthorized();
        }

        if (!current.Permissions.Contains(PermissionCodes.SystemAdminister, StringComparer.Ordinal))
        {
            return Results.Json(new { message = "لا توجد صلاحية لربط جهاز." }, statusCode: StatusCodes.Status403Forbidden);
        }

        PairingIssueRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<PairingIssueRequest>(cancellationToken);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { message = DevicePairingService.InvalidCodeMessage });
        }

        if (request is null || request.BranchId == Guid.Empty)
        {
            return Results.BadRequest(new { message = DevicePairingService.InvalidCodeMessage });
        }

        var branchExists = await db.Branches.AnyAsync(branch => branch.BranchId == request.BranchId && branch.IsActive, cancellationToken);
        if (!branchExists)
        {
            return Results.BadRequest(new { message = DevicePairingService.InvalidCodeMessage });
        }

        var issued = pairing.Issue(request.BranchId, canCreateStation: true);
        return Results.Ok(new { code = issued.Code, expiresAt = issued.ExpiresAt });
    }

    private static async Task<IResult> VerifyPairingCodeAsync(
        HttpContext http,
        DevicePairingService pairing,
        CancellationToken cancellationToken)
    {
        PairingVerifyRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<PairingVerifyRequest>(cancellationToken);
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { message = DevicePairingService.InvalidCodeMessage });
        }

        try
        {
            var verified = pairing.Verify(request?.BranchId ?? Guid.Empty, request?.Code);
            return Results.Ok(new { pairingToken = verified.PairingToken, expiresAt = verified.ExpiresAt });
        }
        catch (PairingRejectedException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
    }

    private static async Task<IResult> ListStationsAsync(
        HttpRequest request,
        Guid branchId,
        SecondaryDeviceRegistration registration,
        CancellationToken cancellationToken)
    {
        try
        {
            var stations = await registration.ListStationsAsync(branchId, request.Headers["X-Pairing-Token"], cancellationToken);
            return Results.Ok(stations);
        }
        catch (PairingRejectedException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    private static async Task<IResult> CreateStationAsync(
        SecondaryStationRequest? request,
        SecondaryDeviceRegistration registration,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return Results.BadRequest(new { message = "بيانات نقطة البيع غير صالحة." });
        }

        try
        {
            var station = await registration.CreateStationAsync(request, cancellationToken);
            return Results.Ok(station);
        }
        catch (PairingForbiddenException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status403Forbidden);
        }
        catch (PairingRejectedException exception)
        {
            var status = string.IsNullOrWhiteSpace(request.PairingToken)
                ? StatusCodes.Status401Unauthorized
                : StatusCodes.Status400BadRequest;
            return Results.Json(new { message = exception.Message }, statusCode: status);
        }
    }

    private static async Task<IResult> RegisterAsync(
        SecondaryRegisterRequest? request,
        SecondaryDeviceRegistration registration,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PairingToken))
        {
            return Results.BadRequest(new { message = DevicePairingService.MissingTokenMessage });
        }

        try
        {
            var device = await registration.RegisterAsync(request, cancellationToken);
            return Results.Ok(device);
        }
        catch (PairingRejectedException exception)
        {
            return Results.BadRequest(new { message = exception.Message });
        }
    }
}

public sealed class PairingIssueRequest
{
    public Guid BranchId { get; set; }
}

public sealed class PairingVerifyRequest
{
    public Guid BranchId { get; set; }
    public string? Code { get; set; }
}
