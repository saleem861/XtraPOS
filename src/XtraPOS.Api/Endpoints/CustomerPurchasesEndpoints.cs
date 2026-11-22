using System.Globalization;
using XtraPOS.Api.Customers;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

/// <summary>
/// «مشتريات الزبون» ونجوم الزبائن. القراءة لمن يبيع أو يرى الزبائن؛ المجاميع والأرباح لمن يرى الأرباح في مراجعة المبيعات.
/// تعديل حدود النجوم لمن يدير الزبائن أو يدير النظام.
/// </summary>
public static class CustomerPurchasesEndpoints
{
    public static void MapCustomerPurchasesEndpoints(this WebApplication app)
    {
        app.MapGet("/api/customers/{customerId:guid}/purchases", PurchasesAsync);
        app.MapGet("/api/customers/star-thresholds", ThresholdsAsync);
        app.MapPut("/api/customers/star-thresholds", SaveThresholdsAsync);
    }

    private static async Task<IResult> PurchasesAsync(
        Guid customerId,
        string? from,
        string? to,
        string? item,
        int? skip,
        int? take,
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CustomerPurchasesService purchases,
        CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, manage: false, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        DateOnly? start = Date(from);
        DateOnly? end = Date(to);
        if (start is not null && end is not null && start > end)
        {
            return Results.Json(new { message = "تاريخ البداية بعد تاريخ النهاية." }, statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            return Results.Ok(await purchases.PurchasesAsync(
                caller.Context!,
                SalesInquiryEndpoints.CanSeeEarns(caller.User!),
                new CustomerPurchaseQuery(customerId, start, end, item, skip ?? 0, take ?? CustomerPurchasesService.PageSize),
                cancellationToken));
        }
        catch (CustomerStarException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status404NotFound);
        }
    }

    private static async Task<IResult> ThresholdsAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CustomerPurchasesService purchases,
        CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, manage: false, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        var codes = await permissions.GetPermissionCodesAsync(caller.User!.UserId, cancellationToken);
        var canManage = codes.Contains(PermissionCodes.SystemAdminister) || codes.Contains(PermissionCodes.CustomersManage);
        return Results.Ok(new { thresholds = await purchases.ThresholdsAsync(caller.Context!, cancellationToken), canManage });
    }

    private static async Task<IResult> SaveThresholdsAsync(
        ThresholdsBody? body,
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CustomerPurchasesService purchases,
        CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, manage: true, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        if (body is null) return Results.Json(new { message = "الطلب فارغ." }, statusCode: StatusCodes.Status400BadRequest);
        try
        {
            var wanted = CustomerStarRules.Validate(body.WindowDays, body.OneStar, body.TwoStars, body.ThreeStars, body.FourStars, body.FiveStars);
            return Results.Ok(await purchases.SaveThresholdsAsync(caller.Context!, wanted, cancellationToken));
        }
        catch (CustomerStarException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    private static async Task<(IResult? Result, PosDeviceContext? Context, CurrentUserResponse? User)> CallerAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        bool manage,
        CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword)
        {
            return (Results.Unauthorized(), null, null);
        }

        var codes = await permissions.GetPermissionCodesAsync(current.UserId, cancellationToken);
        bool Has(string code) => codes.Contains(PermissionCodes.SystemAdminister) || codes.Contains(code);
        var allowed = manage
            ? Has(PermissionCodes.CustomersManage)
            : Has(PermissionCodes.PosSell) || Has(PermissionCodes.CustomersView);
        if (!allowed)
        {
            return (Results.Forbid(), null, null);
        }

        var context = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        return context is null ? (Results.Unauthorized(), null, null) : (null, context, current);
    }

    private sealed class ThresholdsBody
    {
        public int WindowDays { get; set; }
        public decimal OneStar { get; set; }
        public decimal TwoStars { get; set; }
        public decimal ThreeStars { get; set; }
        public decimal FourStars { get; set; }
        public decimal FiveStars { get; set; }
    }
}
