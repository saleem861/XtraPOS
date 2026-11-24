using XtraPOS.Api.Inventory;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

/// <summary>
/// «الجرد» من شاشة البيع. كل نقطة تتحقق من الصلاحية على الخادم:
/// Inventory.Count للجرد، وInventory.Review لرؤية الكمية المفترضة والفارق (الجرد أعمى لمن لا يملكها).
/// </summary>
public static class InventoryEndpoints
{
    public const string CountPermission = "Inventory.Count";
    public const string ReviewPermission = "Inventory.Review";

    public static void MapInventoryEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/inventory");
        group.MapGet("/configuration", ConfigurationAsync);
        group.MapGet("/sessions/open", OpenAsync);
        group.MapPost("/sessions", StartAsync);
        group.MapGet("/sessions/{sessionId:guid}/items", ItemsAsync);
        group.MapGet("/sessions/{sessionId:guid}/lookup", LookupAsync);
        group.MapPut("/sessions/{sessionId:guid}/count", CountAsync);
        group.MapPost("/sessions/{sessionId:guid}/finalize", FinalizeAsync);
    }

    private static async Task<IResult> ConfigurationAsync(HttpContext http, AuthService auth, PermissionService permissions, InventoryCountService inventory, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        return await RunAsync(() => inventory.ConfigurationAsync(cancellationToken));
    }

    private static async Task<IResult> OpenAsync(string? type, string? pages, Guid? customInventoryId, HttpContext http, AuthService auth, PermissionService permissions, InventoryCountService inventory, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        return await RunAsync(() => inventory.FindOpenAsync(caller.Context!, type, ParsePages(pages), customInventoryId, cancellationToken));
    }

    private static async Task<IResult> StartAsync(StartBody? body, HttpContext http, AuthService auth, PermissionService permissions, InventoryCountService inventory, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        if (body is null) return Results.Json(new { message = "الطلب فارغ." }, statusCode: StatusCodes.Status400BadRequest);
        return await RunAsync(() => inventory.StartAsync(caller.Context!, body.InventoryType, body.Pages, body.CustomInventoryId, body.ReplaceOpen, cancellationToken));
    }

    private static async Task<IResult> ItemsAsync(Guid sessionId, HttpContext http, AuthService auth, PermissionService permissions, InventoryCountService inventory, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        return await RunAsync(() => inventory.ItemsAsync(caller.Context!, sessionId, caller.CanReview, cancellationToken));
    }

    private static async Task<IResult> LookupAsync(Guid sessionId, string? code, HttpContext http, AuthService auth, PermissionService permissions, InventoryCountService inventory, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        return await RunAsync(() => inventory.LookupAsync(caller.Context!, sessionId, code, cancellationToken));
    }

    private static async Task<IResult> CountAsync(Guid sessionId, CountBody? body, HttpContext http, AuthService auth, PermissionService permissions, InventoryCountService inventory, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        if (body?.ItemId is not Guid itemId) return Results.Json(new { message = "يرجى اختيار الصنف." }, statusCode: StatusCodes.Status400BadRequest);
        return await RunAsync(() => inventory.SaveCountAsync(caller.Context!, sessionId, itemId, body.Quantity, caller.CanReview, cancellationToken));
    }

    private static async Task<IResult> FinalizeAsync(Guid sessionId, HttpContext http, AuthService auth, PermissionService permissions, InventoryCountService inventory, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        return await RunAsync(() => inventory.FinalizeAsync(caller.Context!, sessionId, cancellationToken));
    }

    public static IReadOnlyList<int> ParsePages(string? pages) =>
        string.IsNullOrWhiteSpace(pages)
            ? []
            : pages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => int.TryParse(part, out var page) ? page : -1)
                .ToArray();

    private static async Task<IResult> RunAsync(Func<Task<object>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (InventoryException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: exception.StatusCode);
        }
        catch (XtraPOS.Api.Catalog.StockPostingException)
        {
            return Results.Json(new { message = "تعذر تثبيت الجرد. لم يتغير أي رصيد." }, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<(IResult? Result, PosDeviceContext? Context, bool CanReview)> CallerAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword) return (Results.Unauthorized(), null, false);
        var codes = await permissions.GetPermissionCodesAsync(current.UserId, cancellationToken);
        bool Has(string code) => codes.Contains(PermissionCodes.SystemAdminister) || codes.Contains(code);
        if (!Has(CountPermission))
        {
            return (Results.Json(new { message = "ليست لديك صلاحية الجرد." }, statusCode: StatusCodes.Status403Forbidden), null, false);
        }

        var context = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        return context is null ? (Results.Unauthorized(), null, false) : (null, context, Has(ReviewPermission));
    }

    private sealed class StartBody
    {
        public string? InventoryType { get; set; }
        public List<int>? Pages { get; set; }
        public Guid? CustomInventoryId { get; set; }
        public bool ReplaceOpen { get; set; }
    }

    private sealed class CountBody
    {
        public Guid? ItemId { get; set; }
        public decimal? Quantity { get; set; }
    }
}
