using XtraPOS.Api.Catalog;
using XtraPOS.Api.Sales;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

public static class HeldSaleEndpoints
{
    public static void MapHeldSaleEndpoints(this WebApplication app)
    {
        app.MapGet("/api/pos/held-sales", ListAsync);
        app.MapGet("/api/pos/held-sales/{saleId:guid}", GetAsync);
        app.MapPost("/api/pos/held-sales", SaveAsync);
        app.MapPost("/api/pos/held-sales/{saleId:guid}/release", ReleaseAsync);
    }

    private static async Task<IResult> ListAsync(string? customer, DateOnly? from, DateOnly? to, HttpContext http, AuthService auth, PermissionService permissions, HeldSaleService sales, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        try
        {
            var rows = await sales.ListAsync(actor.Context!, customer, from, to, cancellationToken);
            return Results.Ok(new { sales = rows });
        }
        catch (HeldSaleException exception)
        {
            return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: exception.StatusCode);
        }
    }

    private static async Task<IResult> GetAsync(Guid saleId, HttpContext http, AuthService auth, PermissionService permissions, HeldSaleService sales, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        try
        {
            return Results.Ok(await sales.GetAsync(actor.Context!, saleId, cancellationToken));
        }
        catch (HeldSaleException exception)
        {
            return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: exception.StatusCode);
        }
    }

    private static async Task<IResult> SaveAsync(HeldSaveBody? request, HttpContext http, AuthService auth, PermissionService permissions, HeldSaleService sales, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        if (request?.CartId is not Guid cartId || cartId == Guid.Empty)
        {
            return Results.BadRequest(new { message = "معرّف السلة غير صالح." });
        }

        try
        {
            var saved = await sales.SaveAsync(actor.Context!, request.SaleId, cartId, request.CustomerId, request.RowVersion, request.Lines ?? [], cancellationToken);
            return Results.Ok(saved);
        }
        catch (HeldSaleException exception)
        {
            return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: exception.StatusCode);
        }
        catch (PricingException exception)
        {
            return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> ReleaseAsync(Guid saleId, HttpContext http, AuthService auth, PermissionService permissions, HeldSaleService sales, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        try
        {
            await sales.ReleaseAsync(actor.Context!, saleId, cancellationToken);
            return Results.Ok(new { released = true });
        }
        catch (HeldSaleException exception)
        {
            return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: exception.StatusCode);
        }
    }

    private static async Task<(PosDeviceContext? Context, IResult? Error)> ActorAsync(HttpContext http, AuthService auth, PermissionService permissions, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword)
        {
            return (null, Results.Unauthorized());
        }

        if (!await permissions.HasPermissionAsync(current.UserId, PermissionCodes.PosSell, cancellationToken))
        {
            return (null, Results.Forbid());
        }

        var actor = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        return actor is null ? (null, Results.Unauthorized()) : (actor, null);
    }

    private sealed class HeldSaveBody
    {
        public Guid? SaleId { get; init; }
        public Guid? CartId { get; init; }
        public Guid? CustomerId { get; init; }
        public string? RowVersion { get; init; }
        public List<HeldLineInput>? Lines { get; init; }
    }
}
