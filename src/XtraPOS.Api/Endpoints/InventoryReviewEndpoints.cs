using XtraPOS.Api.Inventory;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

/// <summary>
/// المرحلة 2: «تقرير الجرد» وتدقيق المشرف (Inventory.Review)، و«معالجة الأصناف»:
/// التصنيف (Inventory.Classify) و«كمية صنف» (Inventory.CorrectQuantity). كلها على فرع الجهاز.
/// </summary>
public static class InventoryReviewEndpoints
{
    public const string ReviewPermission = "Inventory.Review";
    public const string ClassifyPermission = "Inventory.Classify";
    public const string CorrectQuantityPermission = "Inventory.CorrectQuantity";

    public static void MapInventoryReviewEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/inventory");
        group.MapGet("/report/branches", BranchesAsync);
        group.MapGet("/report/sessions", ReportAsync);
        group.MapGet("/report/sessions/{sessionId:guid}", SessionAsync);
        group.MapPost("/report/sessions/{sessionId:guid}/audits", StartAuditAsync);
        group.MapGet("/audits/{auditId:guid}", AuditAsync);
        group.MapPut("/audits/{auditId:guid}/count", AuditCountAsync);
        group.MapPost("/audits/{auditId:guid}/complete", CompleteAuditAsync);
        group.MapGet("/items/lookup", ItemAsync);
        group.MapPut("/items/{itemId:guid}/classification", ClassificationAsync);
        group.MapPost("/items/{itemId:guid}/quantity", QuantityAsync);
    }

    private static async Task<IResult> BranchesAsync(
        HttpContext http, AuthService auth, PermissionService permissions, InventoryReviewService review, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, [ReviewPermission], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        return await RunAsync(() => review.BranchesAsync(caller.Context!, cancellationToken));
    }

    private static async Task<IResult> ReportAsync(
        Guid? branchId, DateOnly? from, DateOnly? to, string? type, Guid? userId, string? status,
        HttpContext http, AuthService auth, PermissionService permissions, InventoryReviewService review, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, [ReviewPermission], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        return await RunAsync(() => review.ReportAsync(caller.Context!, branchId, from, to, type, userId, status, cancellationToken));
    }

    private static async Task<IResult> SessionAsync(
        Guid sessionId, Guid? branchId, HttpContext http, AuthService auth, PermissionService permissions, InventoryReviewService review, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, [ReviewPermission], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        return await RunAsync(() => review.SessionDetailAsync(caller.Context!, sessionId, branchId, cancellationToken));
    }

    private static async Task<IResult> StartAuditAsync(
        Guid sessionId, StartAuditBody? body, HttpContext http, AuthService auth, PermissionService permissions, InventoryReviewService review, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, [ReviewPermission], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        if (body is null) return Results.Json(new { message = "الطلب فارغ." }, statusCode: StatusCodes.Status400BadRequest);
        return await RunAsync(() => review.StartAuditAsync(caller.Context!, sessionId, body.Mode, body.SessionItemIds, cancellationToken));
    }

    private static async Task<IResult> AuditAsync(
        Guid auditId, HttpContext http, AuthService auth, PermissionService permissions, InventoryReviewService review, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, [ReviewPermission], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        return await RunAsync(() => review.AuditAsync(caller.Context!, auditId, cancellationToken));
    }

    private static async Task<IResult> AuditCountAsync(
        Guid auditId, AuditCountBody? body, HttpContext http, AuthService auth, PermissionService permissions, InventoryReviewService review, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, [ReviewPermission], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        if (body?.AuditItemId is not Guid line) return Results.Json(new { message = "يرجى اختيار الصنف." }, statusCode: StatusCodes.Status400BadRequest);
        return await RunAsync(() => review.SaveAuditCountAsync(caller.Context!, auditId, line, body.Quantity, cancellationToken));
    }

    private static async Task<IResult> CompleteAuditAsync(
        Guid auditId, HttpContext http, AuthService auth, PermissionService permissions, InventoryReviewService review, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, [ReviewPermission], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        return await RunAsync(() => review.CompleteAuditAsync(caller.Context!, auditId, cancellationToken));
    }

    private static async Task<IResult> ItemAsync(
        string? code, HttpContext http, AuthService auth, PermissionService permissions, InventoryReviewService review, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, [ClassifyPermission, CorrectQuantityPermission], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        return await RunAsync(() => review.ItemAsync(caller.Context!, code, cancellationToken));
    }

    private static async Task<IResult> ClassificationAsync(
        Guid itemId, ClassificationBody? body, HttpContext http, AuthService auth, PermissionService permissions, InventoryReviewService review, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, [ClassifyPermission], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        if (body is null) return Results.Json(new { message = "الطلب فارغ." }, statusCode: StatusCodes.Status400BadRequest);
        return await RunAsync(() => review.SaveClassificationAsync(caller.Context!, itemId, body.IsImportant, body.InventoryPage, body.SupplyDays, cancellationToken));
    }

    private static async Task<IResult> QuantityAsync(
        Guid itemId, QuantityBody? body, HttpContext http, AuthService auth, PermissionService permissions, InventoryReviewService review, CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, [CorrectQuantityPermission], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        if (body?.CorrectionId is not Guid correction) return Results.Json(new { message = "طلب التصحيح غير صالح." }, statusCode: StatusCodes.Status400BadRequest);
        return await RunAsync(() => review.CorrectQuantityAsync(caller.Context!, itemId, correction, body.Quantity, body.Reason, cancellationToken));
    }

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
            return Results.Json(new { message = "تعذر تصحيح الكمية. لم يتغير الرصيد." }, statusCode: StatusCodes.Status409Conflict);
        }
    }

    /// <summary>يكفي أن يملك المستخدم واحدة من الصلاحيات المطلوبة، أو System.Administer.</summary>
    private static async Task<(IResult? Result, PosDeviceContext? Context)> CallerAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        string[] anyOf,
        CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword) return (Results.Unauthorized(), null);
        var codes = await permissions.GetPermissionCodesAsync(current.UserId, cancellationToken);
        if (!codes.Contains(PermissionCodes.SystemAdminister) && !anyOf.Any(code => codes.Contains(code)))
        {
            return (Results.Json(new { message = "ليست لديك صلاحية لهذه الشاشة." }, statusCode: StatusCodes.Status403Forbidden), null);
        }

        var context = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        return context is null ? (Results.Unauthorized(), null) : (null, context);
    }

    private sealed class StartAuditBody
    {
        public string? Mode { get; set; }
        public List<Guid>? SessionItemIds { get; set; }
    }

    private sealed class AuditCountBody
    {
        public Guid? AuditItemId { get; set; }
        public decimal? Quantity { get; set; }
    }

    private sealed class ClassificationBody
    {
        public bool IsImportant { get; set; }
        public int? InventoryPage { get; set; }
        public int? SupplyDays { get; set; }
    }

    private sealed class QuantityBody
    {
        public Guid? CorrectionId { get; set; }
        public decimal? Quantity { get; set; }
        public string? Reason { get; set; }
    }
}
