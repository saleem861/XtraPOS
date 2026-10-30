using XtraPOS.Api.Credit;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Endpoints;

public static class CreditEndpoints
{
    public static void MapCreditEndpoints(this WebApplication app)
    {
        app.MapGet("/api/pos/credit-sales/enabled", (IConfiguration configuration) =>
            Results.Ok(new { enabled = configuration.GetValue("CreditSales:Enabled", false) }));
        app.MapPost("/api/pos/credit-sales", InitiateAsync);
        app.MapGet("/api/pos/credit-sales/{operationId:guid}", StatusAsync);
        app.MapGet("/api/pos/credit-sales", ListAsync);
    }

    private static async Task<IResult> InitiateAsync(
        CreditInitiateBody? request,
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        IConfiguration configuration,
        SqlConnectionFactory connections,
        SyncEndpointStore centralStore,
        CreditSaleCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, XtraPOS.Api.Security.PermissionCodes.CreditCreate, cancellationToken);
        if (actor.Error is not null)
        {
            return actor.Error;
        }

        var origin = FinancialRequestGuard.RejectUntrusted(http, configuration);
        if (origin is not null) return origin;

        if (!configuration.GetValue("CreditSales:Enabled", false))
        {
            return Results.Json(new { code = "CREDIT_DISABLED", message = "البيع بالدين غير مفعّل." }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (request?.OperationId is not Guid operationId || request.CustomerId is not Guid customerId || request.InvoiceTotal is not decimal invoice || request.ImmediatePayment is not decimal immediate)
        {
            return Results.BadRequest(new { code = "INVALID_CREDIT_REQUEST", message = "بيانات البيع بالدين غير مكتملة." });
        }

        var context = actor.Context!;
        var branchConnection = connections.TryCreateBranchConnectionString();
        var central = await centralStore.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (branchConnection is null || central is null)
        {
            return Results.Json(new { state = "Pending", message = "جهة الائتمان المركزية غير متاحة." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        await using var branch = new Microsoft.Data.SqlClient.SqlConnection(branchConnection);
        await using var centralConnection = new Microsoft.Data.SqlClient.SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await branch.OpenAsync(cancellationToken);
        await centralConnection.OpenAsync(cancellationToken);
        var sale = new CreditSaleRequest(
            operationId,
            request.SaleId ?? Guid.NewGuid(),
            request.CartId ?? Guid.NewGuid(),
            context.BusinessId,
            context.BranchId,
            context.StationId,
            context.DeviceId,
            request.CashSessionId ?? Guid.Empty,
            customerId,
            context.UserId,
            Guid.Empty,
            invoice,
            immediate,
            invoice - immediate,
            (request.Lines ?? []).Select(line => new CreditSaleLine(
                line.ItemId,
                line.Quantity,
                string.IsNullOrWhiteSpace(line.LineType) ? "Fixed" : line.LineType,
                line.ExpectedUnitPrice,
                line.PriceOverrideMode,
                line.ManualUnitPrice,
                line.ManualLineTotal,
                line.EnteredScaleValue,
                line.ExpectedFinalSellingPrice,
                line.ExpectedReferenceBasePrice,
                line.ExpectedWeightKg,
                line.ExpectedStockOwnerQuantity)).ToArray());
        var result = await coordinator.StageAsync(centralConnection, branch, sale, true, false, false, null, cancellationToken);
        return ToResult(result.Outcome, operationId);
    }

    private static async Task<IResult> StatusAsync(Guid operationId, HttpContext http, AuthService auth, PermissionService permissions, SqlConnectionFactory connections, CreditOutboxProcessor processor, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, XtraPOS.Api.Security.PermissionCodes.CreditView, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        var connectionString = connections.TryCreateBranchConnectionString();
        if (connectionString is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        await using var branch = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await branch.OpenAsync(cancellationToken);
        var status = await processor.ReadAsync(branch, actor.Context!.BranchId, operationId, cancellationToken);
        return status is null ? Results.NotFound() : Results.Ok(status);
    }

    private static async Task<IResult> ListAsync(HttpContext http, AuthService auth, PermissionService permissions, SqlConnectionFactory connections, CreditOutboxProcessor processor, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, XtraPOS.Api.Security.PermissionCodes.CreditView, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        var connectionString = connections.TryCreateBranchConnectionString();
        if (connectionString is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        await using var branch = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await branch.OpenAsync(cancellationToken);
        return Results.Ok(new { operations = await processor.ListUnresolvedAsync(branch, actor.Context!.BranchId, cancellationToken) });
    }

    private static IResult ToResult(CreditSaleOutcome outcome, Guid operationId)
    {
        var body = new { operationId, state = outcome.ToString() };
        return outcome switch
        {
            CreditSaleOutcome.Completed => Results.Ok(body),
            CreditSaleOutcome.Refused or CreditSaleOutcome.Unauthorized => Results.Json(body, statusCode: StatusCodes.Status403Forbidden),
            CreditSaleOutcome.ReconciliationRequired => Results.Json(body, statusCode: StatusCodes.Status409Conflict),
            _ => Results.Json(body, statusCode: StatusCodes.Status202Accepted),
        };
    }

    private static async Task<(PosDeviceContext? Context, IResult? Error)> ActorAsync(HttpContext http, AuthService auth, PermissionService permissions, string permission, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword)
        {
            return (null, Results.Unauthorized());
        }

        if (!await permissions.HasPermissionAsync(current.UserId, permission, cancellationToken))
        {
            return (null, Results.Forbid());
        }

        var actor = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        return actor is null ? (null, Results.Unauthorized()) : (actor, null);
    }

    private sealed class CreditInitiateBody
    {
        public Guid? OperationId { get; init; }
        public Guid? SaleId { get; init; }
        public Guid? CartId { get; init; }
        public Guid? CustomerId { get; init; }
        public Guid? CashSessionId { get; init; }
        public decimal? InvoiceTotal { get; init; }
        public decimal? ImmediatePayment { get; init; }
        public List<CreditLineBody>? Lines { get; init; }
    }

    private sealed class CreditLineBody
    {
        public Guid ItemId { get; init; }
        public decimal Quantity { get; init; }
        public string? LineType { get; init; }
        public decimal? ExpectedUnitPrice { get; init; }
        public string? PriceOverrideMode { get; init; }
        public decimal? ManualUnitPrice { get; init; }
        public decimal? ManualLineTotal { get; init; }
        public decimal? EnteredScaleValue { get; init; }
        public decimal? ExpectedFinalSellingPrice { get; init; }
        public decimal? ExpectedReferenceBasePrice { get; init; }
        public decimal? ExpectedWeightKg { get; init; }
        public decimal? ExpectedStockOwnerQuantity { get; init; }
    }
}
