using XtraPOS.Api.Inventory;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

/// <summary>
/// المراحل 4 و5 و6 من وحدة الجرد والنواقص. كلها على المركز وتتحقق من الصلاحية على الخادم:
/// Shortages.Process (معالجة النواقص)، Shortages.FollowUp (المتابعة والتجار)، Shortages.Approve (اعتماد الحذف)،
/// Inventory.Define (تحديد جرد)، Shortages.Audit (طلبات التدقيق)، Shortages.Efficiency (الكفاءة والرد على الطلبات)،
/// Shortages.Report (تقرير معالجة النواقص).
/// </summary>
public static class ShortageEndpoints
{
    private const string Process = "Shortages.Process";
    private const string FollowUp = "Shortages.FollowUp";
    private const string Approve = "Shortages.Approve";
    private const string Define = "Inventory.Define";
    private const string Audit = "Shortages.Audit";
    private const string Efficiency = "Shortages.Efficiency";
    private const string Report = "Shortages.Report";
    private const string Count = "Inventory.Count";

    public static void MapShortageEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/shortages");

        group.MapGet("/options", (HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, [Process], token, caller => service.OptionsAsync(caller.Context, token)));
        group.MapGet("/manual-counters", (DateOnly? from, DateOnly? to, string? branchIds, HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, [Process], token, caller => service.ManualCountersAsync(
                caller.Context,
                from,
                to,
                (branchIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(part => Guid.TryParse(part, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty).ToList(),
                token)));
        group.MapPost("/runs", (ShortageService.StartRequest? body, HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, [Process], token, caller => service.StartAsync(caller.Context, body ?? new(), token)));
        group.MapGet("/runs/{runId:guid}", (Guid runId, HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, [Process, Report], token, caller => service.RunAsync(caller.Context, runId, token)));
        group.MapPut("/runs/{runId:guid}/items/{itemId:guid}", (Guid runId, Guid itemId, DecisionBody? body, HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, [Process], token, caller => service.SaveDecisionAsync(
                caller.Context,
                runId,
                itemId,
                body is null || body.Clear ? null : new ShortageDecision(body.PurchaseQuantity, body.WarehouseQuantity, body.BalanceQuantity, body.Delete, body.AlreadySupplied, body.Note, body.SubstituteItemNumber),
                token)));
        group.MapPost("/runs/{runId:guid}/finalize", (Guid runId, HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, [Process], token, caller => service.FinalizeAsync(caller.Context, runId, token)));
        group.MapPost("/runs/{runId:guid}/discard", (Guid runId, HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, [Process], token, caller => service.DiscardAsync(caller.Context, runId, token)));
        group.MapGet("/items/{itemId:guid}/branches", (Guid itemId, HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, [Process, FollowUp, "Inventory.CorrectQuantity", "Inventory.Classify"], token, caller => service.ItemBranchesAsync(caller.Context, itemId, token)));
        group.MapPost("/items/{itemId:guid}/corrections", (Guid itemId, CorrectionBody? body, HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, ["Inventory.CorrectQuantity"], token, caller => service.CorrectAsync(
                caller.Context, body?.BranchId ?? Guid.Empty, itemId, body?.RequestId ?? Guid.Empty, body?.Quantity, body?.Reason, token, body?.ExpectedQuantity)));

        group.MapGet("/tasks/open-count", (HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, [FollowUp], token, caller => service.OpenTaskCountAsync(caller.Context, token)));
        group.MapGet("/tasks", (string? tab, DateOnly? from, DateOnly? to, HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, [FollowUp, Approve], token, caller => service.TasksAsync(caller.Context, tab, from, to, token)));
        group.MapPost("/tasks/actions", (ShortageService.TaskActionRequest? body, HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, [FollowUp, Approve], token, caller => service.TaskActionAsync(caller.Context, body ?? new(), caller.Has(Approve), token)));
        group.MapGet("/suppliers", (HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, [FollowUp], token, caller => service.SuppliersAsync(caller.Context, token)));
        group.MapPost("/suppliers", (ShortageService.SupplierRequest? body, HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, [FollowUp], token, caller => service.SaveSupplierAsync(caller.Context, body ?? new(), token)));
        group.MapGet("/supply-requests", (string? status, HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, [FollowUp], token, caller => service.SupplyRequestsAsync(caller.Context, status, token)));
        group.MapPost("/supply-requests/{requestId:guid}/actions", (Guid requestId, SupplyActionBody? body, HttpContext http, AuthService auth, PermissionService permissions, ShortageService service, CancellationToken token) =>
            Run(http, auth, permissions, [FollowUp], token, caller => service.SupplyRequestActionAsync(caller.Context, requestId, body?.Action, body?.SupplierId, token)));

        group.MapGet("/custom-inventories", (HttpContext http, AuthService auth, PermissionService permissions, ShortageAdminService service, CancellationToken token) =>
            Run(http, auth, permissions, [Define], token, caller => service.CustomInventoriesAsync(caller.Context, token)));
        group.MapPost("/custom-inventories", (ShortageAdminService.CustomInventoryRequest? body, HttpContext http, AuthService auth, PermissionService permissions, ShortageAdminService service, CancellationToken token) =>
            Run(http, auth, permissions, [Define], token, caller => service.SaveCustomInventoryAsync(caller.Context, body ?? new(), token)));
        group.MapGet("/custom-inventories/items", (string? q, HttpContext http, AuthService auth, PermissionService permissions, ShortageAdminService service, CancellationToken token) =>
            Run(http, auth, permissions, [Define, "Inventory.CorrectQuantity", "Inventory.Classify"], token, caller => service.SearchItemsAsync(caller.Context, q, token)));
        group.MapPost("/custom-inventories/{customInventoryId:guid}/active", (Guid customInventoryId, ActiveBody? body, HttpContext http, AuthService auth, PermissionService permissions, ShortageAdminService service, CancellationToken token) =>
            Run(http, auth, permissions, [Define], token, caller => service.SetCustomInventoryActiveAsync(caller.Context, customInventoryId, body?.IsActive ?? false, token)));
        group.MapPost("/custom-inventories/{customInventoryId:guid}/delete", (Guid customInventoryId, HttpContext http, AuthService auth, PermissionService permissions, ShortageAdminService service, CancellationToken token) =>
            Run(http, auth, permissions, [Define], token, caller => service.DeleteCustomInventoryAsync(caller.Context, customInventoryId, token)));

        group.MapGet("/audits/candidates", (DateOnly? date, HttpContext http, AuthService auth, PermissionService permissions, ShortageAdminService service, CancellationToken token) =>
            Run(http, auth, permissions, [Audit], token, caller => service.AuditCandidatesAsync(caller.Context, date, token)));
        group.MapGet("/audits/options", (Guid? branchId, HttpContext http, AuthService auth, PermissionService permissions, ShortageAdminService service, CancellationToken token) =>
            Run(http, auth, permissions, [Audit], token, caller => service.AuditOptionsAsync(caller.Context, branchId, token)));
        group.MapPost("/audits", (ShortageAdminService.AuditRequestBody? body, HttpContext http, AuthService auth, PermissionService permissions, ShortageAdminService service, CancellationToken token) =>
            Run(http, auth, permissions, [Audit], token, caller => service.CreateAuditRequestAsync(caller.Context, body ?? new(), token)));
        group.MapGet("/audits", (DateOnly? from, DateOnly? to, HttpContext http, AuthService auth, PermissionService permissions, ShortageAdminService service, CancellationToken token) =>
            Run(http, auth, permissions, [Audit], token, caller => service.AuditRequestsAsync(caller.Context, false, from, to, token)));
        group.MapPost("/audits/{requestId:guid}/cancel", (Guid requestId, HttpContext http, AuthService auth, PermissionService permissions, ShortageAdminService service, CancellationToken token) =>
            Run(http, auth, permissions, [Audit], token, caller => service.CancelAuditAsync(caller.Context, requestId, token)));
        group.MapGet("/audits/mine", (DateOnly? from, DateOnly? to, HttpContext http, AuthService auth, PermissionService permissions, ShortageAdminService service, CancellationToken token) =>
            Run(http, auth, permissions, [Efficiency, Count], token, caller => service.AuditRequestsAsync(caller.Context, true, from, to, token)));
        group.MapPost("/audits/{requestId:guid}/answer", (Guid requestId, AnswerBody? body, HttpContext http, AuthService auth, PermissionService permissions, ShortageAdminService service, CancellationToken token) =>
            Run(http, auth, permissions, [Efficiency, Count], token, caller => service.AnswerAuditAsync(caller.Context, requestId, body?.Items, token)));

        group.MapGet("/efficiency", (DateOnly? from, DateOnly? to, HttpContext http, AuthService auth, PermissionService permissions, ShortageAdminService service, CancellationToken token) =>
            Run(http, auth, permissions, [Efficiency], token, caller => service.EfficiencyAsync(
                caller.Context, !(caller.Has(Audit) || caller.Has(Report)), from, to, token)));
        group.MapGet("/report", (DateOnly? from, DateOnly? to, HttpContext http, AuthService auth, PermissionService permissions, ShortageAdminService service, CancellationToken token) =>
            Run(http, auth, permissions, [Report], token, caller => service.ProcessingReportAsync(caller.Context, from, to, token)));
    }

    private sealed record Caller(PosDeviceContext Context, IReadOnlyCollection<string> Codes)
    {
        public bool Has(string code) => Codes.Contains(PermissionCodes.SystemAdminister) || Codes.Contains(code);
    }

    /// <summary>يكفي أن يملك المستخدم واحدة من الصلاحيات المطلوبة، أو System.Administer.</summary>
    private static async Task<IResult> Run(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        string[] anyOf,
        CancellationToken cancellationToken,
        Func<Caller, Task<object>> action)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword) return Results.Unauthorized();
        var codes = await permissions.GetPermissionCodesAsync(current.UserId, cancellationToken);
        var caller = new Caller(null!, codes.ToList());
        if (!anyOf.Any(caller.Has))
        {
            return Results.Json(new { message = "ليست لديك صلاحية لهذه الشاشة." }, statusCode: StatusCodes.Status403Forbidden);
        }

        var context = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        if (context is null) return Results.Unauthorized();
        try
        {
            return Results.Ok(await action(caller with { Context = context }));
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

    private sealed class DecisionBody
    {
        public decimal? PurchaseQuantity { get; set; }
        public decimal? WarehouseQuantity { get; set; }
        public decimal? BalanceQuantity { get; set; }
        public bool Delete { get; set; }
        public bool AlreadySupplied { get; set; }
        public string? Note { get; set; }
        public string? SubstituteItemNumber { get; set; }
        public bool Clear { get; set; }
    }

    private sealed class ActiveBody
    {
        public bool IsActive { get; set; }
    }

    private sealed class CorrectionBody
    {
        public Guid? BranchId { get; set; }
        public Guid? RequestId { get; set; }
        public decimal? Quantity { get; set; }
        public decimal? ExpectedQuantity { get; set; }
        public string? Reason { get; set; }
    }

    private sealed class SupplyActionBody
    {
        public string? Action { get; set; }
        public Guid? SupplierId { get; set; }
    }

    private sealed class AnswerBody
    {
        public List<ShortageAdminService.AuditAnswer>? Items { get; set; }
    }
}
