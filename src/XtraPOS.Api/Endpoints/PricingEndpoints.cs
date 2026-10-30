using XtraPOS.Api.Catalog;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

public static class PricingEndpoints
{
    public static void MapPricingEndpoints(this WebApplication app)
    {
        app.MapGet("/api/pricing/items/{itemId:guid}/resolve", ResolveAsync);
        app.MapPost("/api/pricing/variable-value/resolve", ResolveVariableAsync);
    }

    private static async Task<IResult> ResolveAsync(
        Guid itemId,
        Guid? customerId,
        decimal? quantity,
        bool? applySpecial,
        HttpContext http,
        AuthService auth,
        PricingResolver pricing,
        CancellationToken cancellationToken)
    {
        var caller = await auth.GetCatalogCallerAsync(http, cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        if (!caller.CanViewItems)
        {
            return Results.Forbid();
        }

        try
        {
            return Results.Ok(await pricing.ResolveFixedPriceAsync(itemId, customerId, cancellationToken, quantity ?? 1m, applySpecial ?? true));
        }
        catch (PricingException exception)
        {
            var status = exception.Code is PricingCodes.CustomerInactive or PricingCodes.ItemNotSellable
                ? StatusCodes.Status409Conflict
                : StatusCodes.Status404NotFound;
            return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: status);
        }
    }

    private static async Task<IResult> ResolveVariableAsync(
        VariableValueRequest? request,
        HttpContext http,
        AuthService auth,
        VariableValuePricingResolver pricing,
        CancellationToken cancellationToken)
    {
        var caller = await auth.GetCatalogCallerAsync(http, cancellationToken);
        if (caller is null)
        {
            return Results.Unauthorized();
        }

        if (!caller.CanViewItems)
        {
            return Results.Forbid();
        }

        try
        {
            return Results.Ok(await pricing.ResolveAsync(request?.EnteredScaleValue ?? 0m, request?.CustomerId, cancellationToken));
        }
        catch (PricingException exception)
        {
            var status = exception.Code switch
            {
                PricingCodes.CustomerInactive or VariableValueCodes.ReferenceItemNotSellable => StatusCodes.Status409Conflict,
                VariableValueCodes.InvalidEnteredScaleValue or VariableValueCodes.BasePriceInvalid or VariableValueCodes.InvalidUnitConversion => StatusCodes.Status400BadRequest,
                _ => StatusCodes.Status404NotFound,
            };
            return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: status);
        }
    }
}

public sealed class VariableValueRequest
{
    public decimal? EnteredScaleValue { get; set; }
    public Guid? CustomerId { get; set; }
}
