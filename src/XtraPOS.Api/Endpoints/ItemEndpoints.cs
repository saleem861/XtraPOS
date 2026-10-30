using XtraPOS.Api.Catalog;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

public static class ItemEndpoints
{
    public static void MapItemEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/items");
        group.MapGet("", ListAsync);
        group.MapGet("/{id:guid}", DetailAsync);
        group.MapGet("/{id:guid}/prices", PricesAsync);
        group.MapGet("/{id:guid}/units", UnitsAsync);
        group.MapGet("/{id:guid}/inventory", InventoryAsync);
        group.MapGet("/{id:guid}/stock-movements", MovementsAsync);
        group.MapGet("/{id:guid}/edit-form", EditFormAsync);
        group.MapPut("/{id:guid}", UpdateMasterAsync);
        group.MapPut("/{id:guid}/units", UpdateUnitsAsync);
        group.MapPut("/{id:guid}/prices/default", UpdateDefaultPriceAsync);
        group.MapPut("/{id:guid}/prices/branch", UpdateBranchPriceAsync);
        group.MapPut("/{id:guid}/branch-state", UpdateBranchStateAsync);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http,
        AuthService auth,
        ItemCatalogService catalog,
        PricingResolver pricing,
        string? search,
        string? isActive,
        int? page,
        int? pageSize,
        string? sort,
        bool? resolvePrice,
        Guid? customerId,
        CancellationToken cancellationToken)
    {
        var caller = await RequireViewAsync(http, auth, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        var result = await catalog.SearchPageAsync(
            search,
            ParseActivity(isActive),
            page ?? 1,
            pageSize is 25 or 50 or 100 ? pageSize.Value : 50,
            ParseSort(sort),
            cancellationToken);
        if (resolvePrice == true)
        {
            var priced = new List<ItemListItem>(result.Items.Count);
            foreach (var item in result.Items)
            {
                decimal? resolved = null;
                try
                {
                    var price = await pricing.ResolveFixedPriceAsync(item.ItemId, customerId, cancellationToken);
                    resolved = price.ResolvedPrice;
                }
                catch (PricingException)
                {
                    resolved = null;
                }

                priced.Add(item with { ResolvedPrice = resolved });
            }

            result = result with { Items = priced };
        }

        return Results.Ok(result);
    }

    private static async Task<IResult> DetailAsync(
        HttpContext http,
        Guid id,
        AuthService auth,
        ItemCatalogService catalog,
        CancellationToken cancellationToken)
    {
        var caller = await RequireViewAsync(http, auth, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        var item = await catalog.GetItemDetailAsync(id, cancellationToken);
        return item is null ? Results.NotFound() : Results.Ok(item);
    }

    private static async Task<IResult> PricesAsync(
        HttpContext http,
        Guid id,
        AuthService auth,
        ItemCatalogService catalog,
        CancellationToken cancellationToken)
    {
        var caller = await RequireViewAsync(http, auth, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        var prices = await catalog.GetDefaultPriceViewsAsync(id, caller.Caller!.BranchId, cancellationToken);
        return prices is null ? Results.NotFound() : Results.Ok(prices);
    }

    private static async Task<IResult> UnitsAsync(
        HttpContext http,
        Guid id,
        AuthService auth,
        ItemCatalogService catalog,
        CancellationToken cancellationToken)
    {
        var caller = await RequireViewAsync(http, auth, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        var units = await catalog.GetUnitViewsAsync(id, cancellationToken);
        return units is null ? Results.NotFound() : Results.Ok(units);
    }

    private static async Task<IResult> InventoryAsync(
        HttpContext http,
        Guid id,
        AuthService auth,
        ItemCatalogService catalog,
        CancellationToken cancellationToken)
    {
        var caller = await RequireViewAsync(http, auth, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        var inventory = await catalog.GetInventoryViewAsync(caller.Caller!.BranchId, id, cancellationToken);
        return inventory is null ? Results.NotFound() : Results.Ok(inventory);
    }

    private static async Task<IResult> MovementsAsync(
        HttpContext http,
        Guid id,
        AuthService auth,
        ItemCatalogService catalog,
        CancellationToken cancellationToken)
    {
        var caller = await RequireViewAsync(http, auth, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        var movements = await catalog.GetMovementViewsAsync(caller.Caller!.BranchId, id, cancellationToken);
        return movements is null ? Results.NotFound() : Results.Ok(movements);
    }

    private static async Task<IResult> EditFormAsync(
        HttpContext http,
        Guid id,
        AuthService auth,
        ItemEditService edits,
        CancellationToken cancellationToken)
    {
        var caller = await RequireViewAsync(http, auth, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        var form = await edits.GetEditFormAsync(caller.Caller!, id, cancellationToken);
        return form is null ? Results.NotFound() : Results.Ok(form);
    }

    private static async Task<IResult> UpdateMasterAsync(
        HttpContext http,
        Guid id,
        UpdateItemMasterRequest? request,
        AuthService auth,
        ItemEditService edits,
        CancellationToken cancellationToken)
    {
        var caller = await RequireViewAsync(http, auth, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        return await ExecuteAsync(() => edits.UpdateMasterAsync(caller.Caller!, id, request ?? new UpdateItemMasterRequest(), cancellationToken));
    }

    private static async Task<IResult> UpdateUnitsAsync(
        HttpContext http,
        Guid id,
        UpdateItemUnitsRequest? request,
        AuthService auth,
        ItemEditService edits,
        CancellationToken cancellationToken)
    {
        var caller = await RequireViewAsync(http, auth, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        return await ExecuteAsync(() => edits.UpdateUnitsAsync(caller.Caller!, id, request ?? new UpdateItemUnitsRequest(), cancellationToken));
    }

    private static async Task<IResult> UpdateDefaultPriceAsync(
        HttpContext http,
        Guid id,
        UpdateDefaultPriceRequest? request,
        AuthService auth,
        ItemEditService edits,
        CancellationToken cancellationToken)
    {
        var caller = await RequireViewAsync(http, auth, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        return await ExecuteAsync(() => edits.UpdateDefaultPriceAsync(caller.Caller!, id, request ?? new UpdateDefaultPriceRequest(), cancellationToken));
    }

    private static async Task<IResult> UpdateBranchPriceAsync(
        HttpContext http,
        Guid id,
        UpdateBranchPriceRequest? request,
        AuthService auth,
        ItemEditService edits,
        CancellationToken cancellationToken)
    {
        var caller = await RequireViewAsync(http, auth, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        return await ExecuteAsync(async () =>
        {
            await edits.UpdateBranchPriceAsync(caller.Caller!, id, request ?? new UpdateBranchPriceRequest(), cancellationToken);
            return Results.Ok(new { saved = true });
        });
    }

    private static async Task<IResult> UpdateBranchStateAsync(
        HttpContext http,
        Guid id,
        UpdateBranchStateRequest? request,
        AuthService auth,
        ItemEditService edits,
        CancellationToken cancellationToken)
    {
        var caller = await RequireViewAsync(http, auth, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        return await ExecuteAsync(async () =>
        {
            await edits.UpdateBranchStateAsync(caller.Caller!, id, request ?? new UpdateBranchStateRequest(), cancellationToken);
            return Results.Ok(new { saved = true });
        });
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<ItemWriteResponse>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (ItemEditException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: exception.StatusCode);
        }
    }

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ItemEditException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: exception.StatusCode);
        }
    }

    private static async Task<(IResult? Result, CatalogCaller? Caller)> RequireViewAsync(
        HttpContext http,
        AuthService auth,
        CancellationToken cancellationToken)
    {
        var caller = await auth.GetCatalogCallerAsync(http, cancellationToken);
        if (caller is null)
        {
            return (Results.Unauthorized(), null);
        }

        if (!caller.CanViewItems)
        {
            return (Results.Forbid(), null);
        }

        return (null, caller);
    }

    private static ItemActivityFilter ParseActivity(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "false" or "0" or "inactive" => ItemActivityFilter.Inactive,
        "all" => ItemActivityFilter.All,
        _ => ItemActivityFilter.Active,
    };

    private static ItemSort ParseSort(string? value) =>
        string.Equals(value?.Trim(), "name", StringComparison.OrdinalIgnoreCase)
            ? ItemSort.Name
            : ItemSort.Number;
}
