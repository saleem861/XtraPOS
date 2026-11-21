using System.Text.Json;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Devices;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

/// <summary>
/// القيم الأولية لنقطة البيع.
/// GET  /api/device-settings/current                 أي مستخدم مسجّل على هذه النقطة (لتطبيق الإعدادات)
/// PUT  /api/device-settings/current                 Devices.ConfigureCurrent (المدير والمشرف)
/// GET  /api/device-settings/current/history         Devices.ConfigureCurrent: سجل الترحيلات لهذه النقطة
/// GET  /api/device-settings/central                 Devices.ManageAll (موظف المركز)
/// PUT  /api/device-settings/central/{deviceId}      Devices.ManageAll
/// GET  /api/device-settings/central/{deviceId}/history  Devices.ManageAll: سجل التدقيق المركزي (قبل/بعد)
/// </summary>
public static class DeviceSettingsEndpoints
{
    public static void MapDeviceSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/device-settings");
        group.MapGet("/current", GetCurrentAsync);
        group.MapPut("/current", SaveCurrentAsync);
        group.MapGet("/current/history", CurrentHistoryAsync);
        group.MapGet("/central", ListCentralAsync);
        group.MapPut("/central/{deviceId:guid}", SaveCentralAsync);
        group.MapGet("/central/{deviceId:guid}/history", CentralHistoryAsync);
    }

    private static async Task<IResult> GetCurrentAsync(
        HttpContext http,
        AuthService auth,
        PosDeviceSettingsService settings,
        CancellationToken cancellationToken)
    {
        var device = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        if (device is null)
        {
            return Results.Unauthorized();
        }

        return await RunAsync(() => settings.GetCurrentAsync(device, cancellationToken));
    }

    private static async Task<IResult> SaveCurrentAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        PosDeviceSettingsService settings,
        SettingsBody? body,
        CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        var device = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword || device is null)
        {
            return Results.Unauthorized();
        }

        if (!await permissions.HasPermissionAsync(device.UserId, PermissionCodes.DevicesConfigureCurrent, cancellationToken))
        {
            return Results.Json(new { message = "ضبط القيم الأولية متاح للمدير والمشرف فقط." }, statusCode: StatusCodes.Status403Forbidden);
        }

        return await RunAsync(() => settings.SaveCurrentAsync(device, body?.Values, cancellationToken));
    }

    private static async Task<IResult> CurrentHistoryAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        PosDeviceSettingsService settings,
        CancellationToken cancellationToken)
    {
        var device = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        if (device is null)
        {
            return Results.Unauthorized();
        }

        if (!await permissions.HasPermissionAsync(device.UserId, PermissionCodes.DevicesConfigureCurrent, cancellationToken))
        {
            return Results.Json(new { message = "سجل الترحيلات متاح للمدير والمشرف فقط." }, statusCode: StatusCodes.Status403Forbidden);
        }

        return await RunAsync(() => settings.GetCurrentHistoryAsync(device, cancellationToken));
    }

    private static async Task<IResult> CentralHistoryAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        PosDeviceSettingsService settings,
        Guid deviceId,
        CancellationToken cancellationToken)
    {
        var caller = await RequireManageAllAsync(http, auth, permissions, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        return await RunAsync(() => settings.GetCentralHistoryAsync(deviceId, cancellationToken));
    }

    private static async Task<IResult> ListCentralAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        PosDeviceSettingsService settings,
        CancellationToken cancellationToken)
    {
        var caller = await RequireManageAllAsync(http, auth, permissions, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        return await RunAsync(() => settings.ListCentralAsync(cancellationToken));
    }

    private static async Task<IResult> SaveCentralAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        PosDeviceSettingsService settings,
        Guid deviceId,
        SettingsBody? body,
        CancellationToken cancellationToken)
    {
        var caller = await RequireManageAllAsync(http, auth, permissions, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        return await RunAsync(() => settings.SaveCentralAsync(caller.UserId, caller.DisplayName, deviceId, body?.Values, cancellationToken));
    }

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

    private static async Task<(IResult? Result, Guid UserId, string? DisplayName)> RequireManageAllAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword)
        {
            return (Results.Unauthorized(), Guid.Empty, null);
        }

        if (!await permissions.HasPermissionAsync(current.UserId, PermissionCodes.DevicesManageAll, cancellationToken))
        {
            return (Results.Json(new { message = "إعدادات نقاط الفروع متاحة لمسؤول النظام فقط." }, statusCode: StatusCodes.Status403Forbidden), Guid.Empty, null);
        }

        return (null, current.UserId, current.DisplayName);
    }

    private sealed class SettingsBody
    {
        public Dictionary<string, JsonElement>? Values { get; init; }
    }
}
