using XtraPOS.Api.Credit;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Endpoints;

public static class CreditCollectionEndpoints
{
    public static void MapCreditCollectionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/pos/credit-collections/enabled", (IConfiguration configuration) =>
            Results.Ok(new { enabled = configuration.GetValue("CreditCollections:Enabled", false) }));
        app.MapGet("/api/pos/credit-collections/customers/{customerId:guid}/summary", SummaryAsync);
        app.MapGet("/api/pos/credit-collections/customers/{customerId:guid}/receivables", ReceivablesAsync);
        app.MapPost("/api/pos/credit-collections/quote", QuoteAsync);
        app.MapPost("/api/pos/credit-collections", ConfirmAsync);
        app.MapGet("/api/pos/credit-collections/customers/{customerId:guid}/operations", OperationsAsync);
        app.MapGet("/api/pos/credit-collections/{operationId:guid}", StatusAsync);
    }

    private static async Task<IResult> SummaryAsync(Guid customerId, HttpContext http, AuthService auth, PermissionService permissions, SqlConnectionFactory connections, SyncEndpointStore centralStore, CreditCollectionService collections, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, PermissionCodes.CreditView, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        var central = await OpenCentralAsync(centralStore, cancellationToken);
        if (central is null) return Results.Json(new { centralReachable = false }, statusCode: StatusCodes.Status503ServiceUnavailable);
        await using (central)
        {
            var quote = await collections.QuoteAsync(central, customerId, 0.0001m, null, cancellationToken);
            return Results.Ok(new { customerId, outstanding = quote.Outstanding, collectible = quote.Collectible, centralReachable = true });
        }
    }

    private static async Task<IResult> ReceivablesAsync(Guid customerId, HttpContext http, AuthService auth, PermissionService permissions, SyncEndpointStore centralStore, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, PermissionCodes.CreditView, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        var settings = await centralStore.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (settings is null) return Results.Json(new { centralReachable = false }, statusCode: StatusCodes.Status503ServiceUnavailable);
        await using var central = new Microsoft.Data.SqlClient.SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        await central.OpenAsync(cancellationToken);
        var rows = new List<object>();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            """
            SELECT receivable.ReceivableID, receivable.SourceSaleID, receivable.SourceBranchID, receivable.SourceSaleNumber,
                   receivable.OriginalAmount, receivable.OutstandingAmount, receivable.DueDate, receivable.Status, receivable.CreatedAt,
                   branch.BranchName
            FROM dbo.CustomerReceivables AS receivable
            LEFT JOIN dbo.Branches AS branch ON branch.BranchID = receivable.SourceBranchID
            WHERE receivable.CustomerID = @customer AND receivable.OutstandingAmount > 0
            ORDER BY receivable.DueDate, receivable.ReceivableID;
            """,
            central);
        command.Parameters.AddWithValue("@customer", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new { receivableId = reader.GetGuid(0), sourceSaleId = reader.GetGuid(1), sourceBranchId = reader.GetGuid(2), sourceSaleNumber = reader.IsDBNull(3) ? null : (long?)reader.GetInt64(3), originalAmount = reader.GetDecimal(4), outstandingAmount = reader.GetDecimal(5), dueDate = reader.GetDateTime(6), status = reader.GetString(7), createdAt = reader.GetDateTime(8), sourceBranchName = reader.IsDBNull(9) ? null : reader.GetString(9) });
        }

        return Results.Ok(new { receivables = rows });
    }

    private static async Task<IResult> QuoteAsync(CollectionBody? request, HttpContext http, AuthService auth, PermissionService permissions, IConfiguration configuration, SyncEndpointStore centralStore, CreditCollectionService collections, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, PermissionCodes.CreditView, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        var origin = FinancialRequestGuard.RejectUntrusted(http, configuration);
        if (origin is not null) return origin;
        if (request?.CustomerId is not Guid customerId || request.Amount is not decimal amount)
        {
            return Results.BadRequest(new { code = "INVALID_COLLECTION_REQUEST" });
        }

        var central = await OpenCentralAsync(centralStore, cancellationToken);
        if (central is null) return Results.Json(new { centralReachable = false }, statusCode: StatusCodes.Status503ServiceUnavailable);
        await using (central)
        {
            return Results.Ok(await collections.QuoteAsync(central, customerId, amount, request.ReceivableId, cancellationToken));
        }
    }

    private static async Task<IResult> ConfirmAsync(CollectionBody? request, HttpContext http, AuthService auth, PermissionService permissions, IConfiguration configuration, SqlConnectionFactory connections, SyncEndpointStore centralStore, CreditCollectionService collections, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, PermissionCodes.CreditCollect, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        var origin = FinancialRequestGuard.RejectUntrusted(http, configuration);
        if (origin is not null) return origin;

        if (!configuration.GetValue("CreditCollections:Enabled", false))
        {
            return Results.Json(new { code = "COLLECTION_DISABLED" }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (request?.OperationId is not Guid operationId || request.CustomerId is not Guid customerId || request.Amount is not decimal amount)
        {
            return Results.BadRequest(new { code = "INVALID_COLLECTION_REQUEST" });
        }

        var branchConnection = connections.TryCreateBranchConnectionString();
        Microsoft.Data.SqlClient.SqlConnection? central;
        try
        {
            central = await OpenCentralAsync(centralStore, cancellationToken);
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            return Results.Json(new { centralReachable = false }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (branchConnection is null || central is null)
        {
            return Results.Json(new { centralReachable = false }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var context = actor.Context!;
        await using var branch = new Microsoft.Data.SqlClient.SqlConnection(branchConnection);
        await using (central)
        {
            await branch.OpenAsync(cancellationToken);
            var outcome = await collections.CollectAsync(central, branch, new CreditCollectionRequest(operationId, customerId, context.BranchId, context.DeviceId, context.UserId, request.CashSessionId ?? Guid.Empty, amount, request.PaymentMethod ?? "Cash", request.ReceivableId), true, false, false, cancellationToken);
            var body = new { operationId, state = outcome.ToString() };
            return outcome switch
            {
                CreditCollectionOutcome.Completed => Results.Ok(body),
                CreditCollectionOutcome.Refused or CreditCollectionOutcome.Unauthorized => Results.Json(body, statusCode: StatusCodes.Status403Forbidden),
                CreditCollectionOutcome.ReconciliationRequired => Results.Json(body, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(body, statusCode: StatusCodes.Status202Accepted),
            };
        }
    }

    private static async Task<IResult> OperationsAsync(Guid customerId, HttpContext http, AuthService auth, PermissionService permissions, SqlConnectionFactory connections, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, PermissionCodes.CreditView, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        var branchConnection = connections.TryCreateBranchConnectionString();
        if (branchConnection is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        await using var branch = new Microsoft.Data.SqlClient.SqlConnection(branchConnection);
        await branch.OpenAsync(cancellationToken);
        var rows = new List<object>();
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT CreditOperationID, Status FROM dbo.BranchCollectionOutbox WHERE CustomerID = @customer AND BranchID = @branch ORDER BY CreatedAt DESC;",
            branch);
        command.Parameters.AddWithValue("@customer", customerId);
        command.Parameters.AddWithValue("@branch", actor.Context!.BranchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(new { operationId = reader.GetGuid(0), state = reader.GetString(1) });
        return Results.Ok(new { operations = rows });
    }

    private static async Task<IResult> StatusAsync(Guid operationId, HttpContext http, AuthService auth, PermissionService permissions, SqlConnectionFactory connections, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, PermissionCodes.CreditView, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        var branchConnection = connections.TryCreateBranchConnectionString();
        if (branchConnection is null) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        await using var branch = new Microsoft.Data.SqlClient.SqlConnection(branchConnection);
        await branch.OpenAsync(cancellationToken);
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            "SELECT box.Status, collection.Amount, collection.CustomerID, collection.CreatedAt FROM dbo.BranchCollectionOutbox AS box INNER JOIN dbo.BranchDebtCollections AS collection ON collection.CollectionOperationID = box.CreditOperationID WHERE box.CreditOperationID = @operation AND box.BranchID = @branch;",
            branch);
        command.Parameters.AddWithValue("@operation", operationId);
        command.Parameters.AddWithValue("@branch", actor.Context!.BranchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return Results.NotFound();
        return Results.Ok(new { operationId, state = reader.GetString(0), amount = reader.GetDecimal(1), customerId = reader.GetGuid(2), collectedAt = reader.GetDateTime(3), centrallyConfirmed = reader.GetString(0) == "Completed", reconciliationRequired = reader.GetString(0) == "ReconciliationRequired" });
    }

    private static async Task<Microsoft.Data.SqlClient.SqlConnection?> OpenCentralAsync(SyncEndpointStore centralStore, CancellationToken cancellationToken)
    {
        var settings = await centralStore.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (settings is null) return null;
        var connection = new Microsoft.Data.SqlClient.SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<(PosDeviceContext? Context, IResult? Error)> ActorAsync(HttpContext http, AuthService auth, PermissionService permissions, string permission, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword) return (null, Results.Unauthorized());
        if (!await permissions.HasPermissionAsync(current.UserId, permission, cancellationToken)) return (null, Results.Forbid());
        var actor = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        return actor is null ? (null, Results.Unauthorized()) : (actor, null);
    }

    private sealed class CollectionBody
    {
        public Guid? OperationId { get; init; }
        public Guid? CustomerId { get; init; }
        public Guid? CashSessionId { get; init; }
        public Guid? ReceivableId { get; init; }
        public decimal? Amount { get; init; }
        public string? PaymentMethod { get; init; }
    }
}
