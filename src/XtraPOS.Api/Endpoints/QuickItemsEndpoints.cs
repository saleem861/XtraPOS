using XtraPOS.Api.Catalog;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

/// <summary>
/// «الأصناف السريعة»: القائمة المعمول بها لشاشة البيع (لمن يبيع)، وشاشة الضبط للمشرف والمدير.
/// </summary>
public static class QuickItemsEndpoints
{
    public static void MapQuickItemsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/quick-items");
        group.MapGet("/current", CurrentAsync);
        group.MapGet("/manage", ManageAsync);
        group.MapGet("/suggest", SuggestAsync);
        group.MapPut("/manage", SaveAsync);
    }

    private static async Task<IResult> CurrentAsync(HttpContext http, AuthService auth, PermissionService permissions, QuickItemsService quick, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, manage: false, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        return await RunAsync(() => quick.CurrentAsync(caller.Context!, cancellationToken));
    }

    private static async Task<IResult> ManageAsync(Guid? branchId, HttpContext http, AuthService auth, PermissionService permissions, QuickItemsService quick, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, manage: true, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        return await RunAsync(() => quick.ManageAsync(caller.Context!, branchId, cancellationToken));
    }

    private static async Task<IResult> SuggestAsync(Guid? branchId, int? days, int? take, HttpContext http, AuthService auth, PermissionService permissions, QuickItemsService quick, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, manage: true, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        return await RunAsync(() => quick.SuggestAsync(caller.Context!, branchId, days ?? 30, take ?? 10, cancellationToken));
    }

    private static async Task<IResult> SaveAsync(SaveBody? body, HttpContext http, AuthService auth, PermissionService permissions, QuickItemsService quick, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, manage: true, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        if (body is null) return Results.Json(new { message = "الطلب فارغ." }, statusCode: StatusCodes.Status400BadRequest);
        return await RunAsync(() => quick.SaveAsync(caller.Context!, body.BranchId, body.ItemNumbers, cancellationToken));
    }

    private static async Task<IResult> RunAsync(Func<Task<object>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (QuickItemException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: exception.StatusCode);
        }
    }

    /// <summary>الضبط للمشرف والمدير: إدارة النظام أو تعديل الأصناف أو دور مشرف/مدير.</summary>
    private static async Task<(IResult? Result, PosDeviceContext? Context)> CallerAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        bool manage,
        CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword) return (Results.Unauthorized(), null);
        var codes = await permissions.GetPermissionCodesAsync(current.UserId, cancellationToken);
        bool Has(string code) => codes.Contains(PermissionCodes.SystemAdminister) || codes.Contains(code);
        var allowed = manage
            ? Has(PermissionCodes.ItemsEditMaster) || Has(PermissionCodes.ItemsEditBranchState)
              || current.Roles.Any(role => role.RoleCode is "Admin" or "Supervisor" or "Manager")
            : Has(PermissionCodes.PosSell) || Has(PermissionCodes.ItemsView);
        if (!allowed) return (Results.Forbid(), null);
        var context = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        return context is null ? (Results.Unauthorized(), null) : (null, context);
    }

    private sealed class SaveBody
    {
        public Guid? BranchId { get; set; }
        public List<string?>? ItemNumbers { get; set; }
    }
}
