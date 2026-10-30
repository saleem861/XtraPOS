using XtraPOS.Api.Credit;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Endpoints;

public static class CreditReversalEndpoints
{
    public static void MapCreditReversalEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/pos/credit-reversals");
        group.MapGet("/enabled", (IConfiguration configuration) => Results.Ok(new { enabled = configuration.GetValue("CreditReversals:Enabled", false) }));
        group.MapGet("/collections/{collectionId:guid}", EligibilityAsync);
        group.MapPost("/", ConfirmAsync);
        group.MapGet("/{operationId:guid}", StatusAsync);
    }

    private static async Task<IResult> EligibilityAsync(Guid collectionId, HttpContext http, AuthService auth, PermissionService permissions, SqlConnectionFactory connections, SyncEndpointStore centralStore, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        var branchConnection = connections.TryCreateBranchConnectionString();
        if (branchConnection is null) return Results.NotFound();
        await using var branch = new Microsoft.Data.SqlClient.SqlConnection(branchConnection);
        await branch.OpenAsync(cancellationToken);
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            """
            SELECT collection.Amount, collection.CreatedAt, cashier.DisplayName,
                   CASE WHEN reversal.ReversalOperationID IS NULL THEN N'Eligible' ELSE reversal.Status END,
                   reversal.ReversalOperationID, reversal.CashEffect, reversal.ReasonCode
            FROM dbo.BranchDebtCollections AS collection
            INNER JOIN dbo.Users AS cashier ON cashier.UserID = collection.CashierUserID
            LEFT JOIN dbo.BranchCollectionReversals AS reversal ON reversal.CollectionOperationID = collection.CollectionOperationID
            WHERE collection.CollectionOperationID = @collection
              AND collection.BranchID = @branch
              AND collection.Status = N'Completed';
            """,
            branch);
        command.Parameters.AddWithValue("@collection", collectionId);
        command.Parameters.AddWithValue("@branch", actor.Context!.BranchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return Results.NotFound();
        var amount = reader.GetDecimal(0);
        var collectedAt = reader.GetDateTime(1);
        var cashierName = reader.GetString(2);
        var state = reader.GetString(3);
        var reversalId = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4);
        var cashEffect = reader.IsDBNull(5) ? null : reader.GetString(5);
        var reasonCode = reader.IsDBNull(6) ? null : reader.GetString(6);
        await reader.DisposeAsync();
        decimal? outstanding = null;
        var lines = new List<object>();
        try
        {
            var central = await OpenCentralAsync(centralStore, cancellationToken);
            if (central is null) return Results.Json(new { centralReachable = false }, statusCode: StatusCodes.Status503ServiceUnavailable);
            await using (central)
            {
                await using var balances = new Microsoft.Data.SqlClient.SqlCommand(
                    """
                    SELECT COALESCE(SUM(receivable.OutstandingAmount), 0)
                    FROM dbo.CustomerReceivables AS receivable
                    INNER JOIN dbo.CreditCollectionIntents AS intent ON intent.CustomerID = receivable.CustomerID
                    WHERE intent.CollectionOperationID = @collection;

                    SELECT allocation.Amount, receivable.SourceSaleNumber, branch.BranchName
                    FROM dbo.CreditPaymentAllocations AS allocation
                    INNER JOIN dbo.CustomerCreditLedger AS ledger ON ledger.LedgerEntryID = allocation.LedgerEntryID
                    INNER JOIN dbo.CustomerReceivables AS receivable ON receivable.ReceivableID = allocation.ReceivableID
                    LEFT JOIN dbo.Branches AS branch ON branch.BranchID = receivable.SourceBranchID
                    WHERE ledger.IdempotencyKey = @key;
                    """,
                    central);
                balances.Parameters.AddWithValue("@collection", collectionId);
                balances.Parameters.AddWithValue("@key", "collection:" + collectionId.ToString("N"));
                await using var rows = await balances.ExecuteReaderAsync(cancellationToken);
                if (await rows.ReadAsync(cancellationToken)) outstanding = rows.GetDecimal(0);
                await rows.NextResultAsync(cancellationToken);
                while (await rows.ReadAsync(cancellationToken))
                {
                    lines.Add(new { amount = rows.GetDecimal(0), sourceSaleNumber = rows.IsDBNull(1) ? (long?)null : rows.GetInt64(1), sourceBranchName = rows.IsDBNull(2) ? null : rows.GetString(2) });
                }
            }
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            return Results.Json(new { centralReachable = false }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Ok(new
        {
            collectionId,
            amount,
            collectedAt,
            cashierName,
            state,
            reversalOperationId = reversalId,
            cashEffect,
            reasonCode,
            outstanding,
            expectedOutstanding = state == "Completed" ? outstanding : outstanding + amount,
            fullAmountOnly = true,
            allocations = lines,
        });
    }

    private static async Task<IResult> ConfirmAsync(ReversalBody? request, HttpContext http, AuthService auth, PermissionService permissions, IConfiguration configuration, SqlConnectionFactory connections, SyncEndpointStore centralStore, CreditReversalService reversals, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        var origin = FinancialRequestGuard.RejectUntrusted(http, configuration);
        if (origin is not null) return origin;

        if (!configuration.GetValue("CreditReversals:Enabled", false))
        {
            return Results.Json(new { code = "REVERSAL_DISABLED" }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (request?.OperationId is not Guid operationId || request.CollectionOperationId is not Guid collectionId || string.IsNullOrWhiteSpace(request.ReasonCode) || string.IsNullOrWhiteSpace(request.Notes))
        {
            return Results.BadRequest(new { code = "INVALID_REVERSAL_REQUEST" });
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
            var outcome = await reversals.ReverseAsync(central, branch, new CreditReversalRequest(operationId, collectionId, request.CustomerId ?? Guid.Empty, context.BranchId, context.DeviceId, context.UserId, request.CashSessionId ?? Guid.Empty, request.ReasonCode, request.Notes.Trim()), true, false, cancellationToken);
            return outcome switch
            {
                CreditReversalOutcome.Completed => Results.Ok(new { operationId, state = outcome.ToString() }),
                CreditReversalOutcome.PendingCentral => Results.Json(new { operationId, state = outcome.ToString() }, statusCode: StatusCodes.Status202Accepted),
                CreditReversalOutcome.Unauthorized => Results.Forbid(),
                CreditReversalOutcome.CentralUnavailable => Results.Json(new { centralReachable = false }, statusCode: StatusCodes.Status503ServiceUnavailable),
                CreditReversalOutcome.ReconciliationRequired => Results.Json(new { operationId, state = outcome.ToString() }, statusCode: StatusCodes.Status409Conflict),
                _ => Results.Json(new { operationId, state = outcome.ToString() }, statusCode: StatusCodes.Status422UnprocessableEntity),
            };
        }
    }

    private static async Task<IResult> StatusAsync(Guid operationId, HttpContext http, AuthService auth, PermissionService permissions, SqlConnectionFactory connections, CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, cancellationToken);
        if (actor.Error is not null) return actor.Error;
        var branchConnection = connections.TryCreateBranchConnectionString();
        if (branchConnection is null) return Results.NotFound();
        await using var branch = new Microsoft.Data.SqlClient.SqlConnection(branchConnection);
        await branch.OpenAsync(cancellationToken);
        await using var command = new Microsoft.Data.SqlClient.SqlCommand(
            """
            SELECT Status, Amount, ReasonCode, CashEffect, CollectionOperationID
            FROM dbo.BranchCollectionReversals
            WHERE ReversalOperationID = @operation AND BranchID = @branch;
            """,
            branch);
        command.Parameters.AddWithValue("@operation", operationId);
        command.Parameters.AddWithValue("@branch", actor.Context!.BranchId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return Results.NotFound();
        return Results.Ok(new { operationId, state = reader.GetString(0), amount = reader.GetDecimal(1), reasonCode = reader.GetString(2), cashEffect = reader.GetString(3), collectionOperationId = reader.GetGuid(4) });
    }

    private static async Task<Microsoft.Data.SqlClient.SqlConnection?> OpenCentralAsync(SyncEndpointStore centralStore, CancellationToken cancellationToken)
    {
        var settings = await centralStore.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (settings is null) return null;
        var connection = new Microsoft.Data.SqlClient.SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<(PosDeviceContext? Context, IResult? Error)> ActorAsync(HttpContext http, AuthService auth, PermissionService permissions, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword) return (null, Results.Unauthorized());
        if (!await permissions.HasPermissionAsync(current.UserId, PermissionCodes.CreditReverse, cancellationToken)) return (null, Results.Forbid());
        var actor = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        return actor is null ? (null, Results.Unauthorized()) : (actor, null);
    }

    private sealed class ReversalBody
    {
        public Guid? OperationId { get; init; }
        public Guid? CollectionOperationId { get; init; }
        public Guid? CustomerId { get; init; }
        public Guid? CashSessionId { get; init; }
        public string? ReasonCode { get; init; }
        public string? Notes { get; init; }
    }
}
