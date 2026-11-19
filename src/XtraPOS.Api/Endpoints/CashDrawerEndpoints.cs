using XtraPOS.Api.Sales;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

public static class CashDrawerEndpoints
{
    public static void MapCashDrawerEndpoints(this WebApplication app)
    {
        app.MapGet("/api/cash-drawer/context", ContextAsync);
        app.MapPost("/api/cash-drawer/counts", SubmitAsync);
        app.MapGet("/api/cash-drawer/denominations", DenominationsAsync);
        app.MapPost("/api/cash-drawer/denominations", SaveDenominationAsync);
        app.MapGet("/api/cash-drawer/reviews", ReviewAsync);
        app.MapGet("/api/cash-drawer/reviews/{countId:guid}", DetailAsync);
        app.MapPost("/api/cash-drawer/reviews/{countId:guid}/decision", DecideAsync);
    }

    private static async Task<IResult> ContextAsync(HttpContext http, AuthService auth, PermissionService permissions, CashDrawerService drawer, CancellationToken cancellationToken)
    {
        var actor = await RequireAsync(http, auth, permissions, PermissionCodes.CashDrawerCount, cancellationToken);
        if (actor.Result is not null) return actor.Result;
        return Results.Ok(await drawer.ContextAsync(actor.Context!, cancellationToken));
    }

    private static async Task<IResult> SubmitAsync(CountBody? body, HttpContext http, AuthService auth, PermissionService permissions, CashDrawerService drawer, CancellationToken cancellationToken)
    {
        var actor = await RequireAsync(http, auth, permissions, PermissionCodes.CashDrawerCount, cancellationToken);
        if (actor.Result is not null) return actor.Result;
        try
        {
            var lines = (body?.Lines ?? []).Select(line => (line.DenominationId, line.Quantity)).ToArray();
            return Results.Ok(await drawer.SubmitAsync(actor.Context!, body?.ParentCountId, lines, body?.Justification, cancellationToken));
        }
        catch (CashDrawerException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: exception.StatusCode);
        }
    }

    private static async Task<IResult> DenominationsAsync(bool? activeOnly, HttpContext http, AuthService auth, PermissionService permissions, CashDrawerService drawer, CancellationToken cancellationToken)
    {
        var code = activeOnly == true ? PermissionCodes.CashDrawerCount : PermissionCodes.CashDrawerConfigure;
        var actor = await RequireAsync(http, auth, permissions, code, cancellationToken);
        if (actor.Result is not null) return actor.Result;
        return Results.Ok(await drawer.DenominationsAsync(activeOnly == true, cancellationToken));
    }

    private static async Task<IResult> SaveDenominationAsync(DenominationBody? body, HttpContext http, AuthService auth, PermissionService permissions, CashDrawerService drawer, CancellationToken cancellationToken)
    {
        var actor = await RequireAsync(http, auth, permissions, PermissionCodes.CashDrawerConfigure, cancellationToken);
        if (actor.Result is not null) return actor.Result;
        try
        {
            var saved = await drawer.SaveDenominationAsync(body?.DenominationId, body?.UnitValue ?? 0m, body?.DisplayName ?? "", body?.SortOrder ?? 0, body?.IsActive ?? true, cancellationToken);
            return Results.Ok(saved);
        }
        catch (CashDrawerException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: exception.StatusCode);
        }
    }

    private static async Task<IResult> ReviewAsync(
        Guid? branchId,
        Guid? stationId,
        Guid? userId,
        DateTime? dateFrom,
        DateTime? dateTo,
        string? status,
        string? decision,
        int? skip,
        int? take,
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CashDrawerService drawer,
        CancellationToken cancellationToken)
    {
        var actor = await RequireAsync(http, auth, permissions, PermissionCodes.CashDrawerReview, cancellationToken);
        if (actor.Result is not null) return actor.Result;
        return Results.Ok(await drawer.ReviewAsync(branchId, stationId, userId, dateFrom, dateTo, status, decision, skip ?? 0, take ?? 50, cancellationToken));
    }

    private static async Task<IResult> DetailAsync(Guid countId, HttpContext http, AuthService auth, PermissionService permissions, CashDrawerService drawer, CancellationToken cancellationToken)
    {
        var actor = await RequireAsync(http, auth, permissions, PermissionCodes.CashDrawerReview, cancellationToken);
        if (actor.Result is not null) return actor.Result;
        try
        {
            return Results.Ok(await drawer.DetailAsync(countId, cancellationToken));
        }
        catch (CashDrawerException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: exception.StatusCode);
        }
    }

    private static async Task<IResult> DecideAsync(Guid countId, DecisionBody? body, HttpContext http, AuthService auth, PermissionService permissions, CashDrawerService drawer, CancellationToken cancellationToken)
    {
        var actor = await RequireAsync(http, auth, permissions, PermissionCodes.CashDrawerReview, cancellationToken);
        if (actor.Result is not null) return actor.Result;
        try
        {
            return Results.Ok(await drawer.DecideAsync(actor.Context!, countId, body?.DecisionCode ?? "", body?.Note ?? "", cancellationToken));
        }
        catch (CashDrawerException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: exception.StatusCode);
        }
    }

    private static async Task<(IResult? Result, PosDeviceContext? Context)> RequireAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        string permission,
        CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword)
        {
            return (Results.Unauthorized(), null);
        }

        if (!await permissions.HasPermissionAsync(current.UserId, permission, cancellationToken))
        {
            return (Results.Forbid(), null);
        }

        var context = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        return context is null ? (Results.Unauthorized(), null) : (null, context);
    }

    private sealed class CountBody
    {
        public Guid? ParentCountId { get; set; }
        public string? Justification { get; set; }
        public List<CountLineBody> Lines { get; set; } = [];
    }

    private sealed class CountLineBody
    {
        public Guid DenominationId { get; set; }
        public int Quantity { get; set; }
    }

    private sealed class DenominationBody
    {
        public Guid? DenominationId { get; set; }
        public decimal UnitValue { get; set; }
        public string DisplayName { get; set; } = "";
        public int SortOrder { get; set; }
        public bool IsActive { get; set; } = true;
    }

    private sealed class DecisionBody
    {
        public string DecisionCode { get; set; } = "";
        public string Note { get; set; } = "";
    }
}
