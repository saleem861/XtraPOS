using Microsoft.Data.SqlClient;
using XtraPOS.Api.Devices;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

/// <summary>
/// القيم الأولية لنقاط البيع.
/// GET /api/device-settings/current          أي مستخدم مسجّل على هذه النقطة: إعدادات النقطة الحالية (تقرأها نقطة البيع)
/// GET /api/device-settings/points           Devices.ConfigureCurrent: النقطة الحالية، وكل النقاط في كل الفروع لمن يملك Devices.ManageAll
/// GET /api/device-settings/history?deviceId= Devices.ConfigureCurrent: عمليات الإعدادات في سجل الترحيل لهذه النقطة
/// PUT /api/device-settings                  Devices.ConfigureCurrent للنقطة الحالية، و Devices.ManageAll لنقطة أخرى أو لجميع النقاط
/// </summary>
public static class DeviceSettingsEndpoints
{
    public static void MapDeviceSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/device-settings");
        group.MapGet("/current", GetCurrentAsync);
        group.MapGet("/points", ListPointsAsync);
        group.MapGet("/history", HistoryAsync);
        group.MapPut("", SaveAsync);
    }

    private static async Task<IResult> GetCurrentAsync(
        HttpContext http,
        AuthService auth,
        PosDeviceSettingsService settings,
        CancellationToken cancellationToken)
    {
        var device = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        return device is null
            ? Results.Unauthorized()
            : await RunAsync(() => settings.GetCurrentAsync(device, cancellationToken));
    }

    private static async Task<IResult> ListPointsAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        PosDeviceSettingsService settings,
        CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, cancellationToken);
        return caller.Denied ?? await RunAsync(() => settings.ListPointsAsync(caller.Device!, caller.CanManageAll, cancellationToken));
    }

    private static async Task<IResult> HistoryAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        PosDeviceSettingsService settings,
        Guid? deviceId,
        CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, cancellationToken);
        if (caller.Denied is not null)
        {
            return caller.Denied;
        }

        var target = deviceId ?? caller.Device!.DeviceId;
        if (target != caller.Device!.DeviceId && !caller.CanManageAll)
        {
            return Forbidden("سجل نقطة أخرى متاح للمدير فقط.");
        }

        return await RunAsync(() => settings.HistoryAsync(target, cancellationToken));
    }

    private static async Task<IResult> SaveAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        PosDeviceSettingsService settings,
        DeviceSettingsSaveRequest? body,
        CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, cancellationToken);
        if (caller.Denied is not null)
        {
            return caller.Denied;
        }

        if (body is null)
        {
            return Results.Json(new { message = "لا توجد قيم للحفظ." }, statusCode: StatusCodes.Status400BadRequest);
        }

        return await RunAsync(() => settings.SaveAsync(caller.Device!, body, caller.CanManageAll, cancellationToken));
    }

    private sealed record Caller(IResult? Denied, PosDeviceContext? Device, bool CanManageAll);

    private static async Task<Caller> CallerAsync(HttpContext http, AuthService auth, PermissionService permissions, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        var device = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword || device is null)
        {
            return new Caller(Results.Unauthorized(), null, false);
        }

        var admin = await permissions.HasPermissionAsync(device.UserId, PermissionCodes.SystemAdminister, cancellationToken);
        var manageAll = admin || await permissions.HasPermissionAsync(device.UserId, PermissionCodes.DevicesManageAll, cancellationToken);
        var configure = manageAll || await permissions.HasPermissionAsync(device.UserId, PermissionCodes.DevicesConfigureCurrent, cancellationToken);
        return configure
            ? new Caller(null, device, manageAll)
            : new Caller(Forbidden("ضبط القيم الأولية متاح للمدير والمشرف فقط."), null, false);
    }

    private static IResult Forbidden(string message) =>
        Results.Json(new { message }, statusCode: StatusCodes.Status403Forbidden);

    private static async Task<IResult> RunAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (PosDeviceSettingsException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: exception.StatusCode);
        }
        catch (SqlException)
        {
            return Results.Json(
                new { message = "تعذر الوصول إلى قاعدة البيانات. حاول مرة أخرى." },
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
