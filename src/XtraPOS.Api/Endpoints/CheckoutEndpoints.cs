using XtraPOS.Api.Catalog;
using XtraPOS.Api.Sales;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

public static class CheckoutEndpoints
{
    public static void MapCheckoutEndpoints(this WebApplication app)
    {
        app.MapPost("/api/pos/checkout", CheckoutAsync);
    }

    private static async Task<IResult> CheckoutAsync(
        CheckoutBody? request,
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CheckoutService checkout,
        CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword)
        {
            return Results.Unauthorized();
        }

        if (!await permissions.HasPermissionAsync(current.UserId, PermissionCodes.PosSell, cancellationToken))
        {
            return Results.Forbid();
        }

        var actor = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        if (actor is null || request?.CartId is not Guid cartId || cartId == Guid.Empty)
        {
            return Results.Unauthorized();
        }

        try
        {
            var result = await checkout.CheckoutAsync(
                actor,
                cartId,
                request.CustomerId,
                request.Lines ?? [],
                request.Payments ?? [],
                cancellationToken);
            return result switch
            {
                CheckoutSucceeded succeeded => Results.Ok(succeeded.Sale),
                CheckoutPriceRejected changed => Results.Json(
                    new { code = CheckoutCodes.PriceChanged, message = "تغير سعر صنف أو أكثر", changes = changed.Changes },
                    statusCode: StatusCodes.Status409Conflict),
                _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
            };
        }
        catch (CheckoutException exception)
        {
            return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: exception.StatusCode);
        }
        catch (AssortmentRuleException exception)
        {
            return Results.Json(
                new { code = exception.Code, message = exception.Message, itemId = exception.ItemId, itemNumber = exception.ItemNumber, itemName = exception.ItemName },
                statusCode: StatusCodes.Status409Conflict);
        }
        catch (PricingException exception)
        {
            var status = exception.Code is PricingCodes.PriceNotFound or PricingCodes.ItemNotFound or PricingCodes.CustomerNotFound
                ? StatusCodes.Status404NotFound
                : StatusCodes.Status409Conflict;
            return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: status);
        }
        catch (SalePaymentException exception)
        {
            return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: exception.StatusCode);
        }
    }

    private sealed class CheckoutBody
    {
        public Guid? CartId { get; set; }
        public Guid? CustomerId { get; set; }
        public List<CheckoutLine>? Lines { get; set; }
        public List<CheckoutPaymentInput>? Payments { get; set; }
    }
}
