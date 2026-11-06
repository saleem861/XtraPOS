using XtraPOS.Api.Credit;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Endpoints;

public static class CreditHistoryEndpoints
{
    public static void MapCreditHistoryEndpoints(this WebApplication app)
    {
        app.MapGet("/api/pos/credit-history", HistoryAsync);
        app.MapGet("/api/pos/credit-documents", DocumentAsync);
        app.MapGet("/api/pos/credit-sync/reconciliation", ReconcileAsync);
        app.MapPost("/api/pos/credit-debt-reversals", ReverseDebtAsync);
    }

    private static async Task<IResult> HistoryAsync(
        Guid? customerId,
        int? skip,
        int? take,
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        SqlConnectionFactory connections,
        SyncEndpointStore centralStore,
        CreditHistoryService history,
        CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, PermissionCodes.CreditView, cancellationToken);
        if (actor.Error is not null)
        {
            return actor.Error;
        }

        if (customerId is not Guid customer)
        {
            return Results.BadRequest(new { code = "INVALID_CREDIT_HISTORY", message = "الزبون غير محدد." });
        }

        var branchConnection = connections.TryCreateBranchConnectionString();
        if (branchConnection is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await using var branch = new Microsoft.Data.SqlClient.SqlConnection(branchConnection);
        await branch.OpenAsync(cancellationToken);
        Microsoft.Data.SqlClient.SqlConnection? central = null;
        try
        {
            var settings = await centralStore.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
            if (settings is not null)
            {
                central = new Microsoft.Data.SqlClient.SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
                await central.OpenAsync(cancellationToken);
            }
        }
        catch (Exception)
        {
            if (central is not null)
            {
                await central.DisposeAsync();
            }

            central = null;
        }

        await using var centralConnection = central;
        var page = await history.ReadAsync(central, branch, actor.Context!.BusinessId, customer, Math.Max(0, skip ?? 0), Math.Clamp(take ?? 50, 1, 100), cancellationToken);
        return Results.Ok(page);
    }

    private static async Task<IResult> DocumentAsync(
        Guid? saleId,
        string? operationId,
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        SqlConnectionFactory connections,
        SyncEndpointStore centralStore,
        CreditHistoryService history,
        CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, PermissionCodes.CreditView, cancellationToken);
        if (actor.Error is not null)
        {
            return actor.Error;
        }

        var branchConnection = connections.TryCreateBranchConnectionString();
        if (branchConnection is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await using var branch = new Microsoft.Data.SqlClient.SqlConnection(branchConnection);
        await branch.OpenAsync(cancellationToken);
        Microsoft.Data.SqlClient.SqlConnection? central = null;
        try
        {
            var settings = await centralStore.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
            if (settings is not null)
            {
                central = new Microsoft.Data.SqlClient.SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
                await central.OpenAsync(cancellationToken);
            }
        }
        catch (Exception)
        {
            if (central is not null)
            {
                await central.DisposeAsync();
            }

            central = null;
        }

        await using var centralConnection = central;
        var document = await history.DocumentAsync(central, branch, actor.Context!.BusinessId, saleId, operationId, cancellationToken);
        return document is null
            ? Results.NotFound(new { message = "تفاصيل الحركة غير متاحة." })
            : Results.Text(document, "application/json");
    }

    private static async Task<IResult> ReconcileAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        SqlConnectionFactory connections,
        SyncEndpointStore centralStore,
        CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, PermissionCodes.CreditAudit, cancellationToken);
        if (actor.Error is not null)
        {
            return actor.Error;
        }

        var branchConnection = connections.TryCreateBranchConnectionString();
        var settings = await centralStore.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (branchConnection is null || settings is null)
        {
            return Results.Json(new { stale = true, message = "جهة الائتمان المركزية غير متاحة." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        await using var branch = new Microsoft.Data.SqlClient.SqlConnection(branchConnection);
        await using var central = new Microsoft.Data.SqlClient.SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        await branch.OpenAsync(cancellationToken);
        await central.OpenAsync(cancellationToken);
        var result = await FinancialInboxSync.PullAsync(central, branch, cancellationToken);
        return Results.Ok(new
        {
            applied = result.Applied,
            checkpoint = result.Checkpoint,
            centralCount = result.CentralCount,
            inboxCount = result.InboxCount,
            matched = result.CentralCount == result.InboxCount,
        });
    }

    private static async Task<IResult> ReverseDebtAsync(
        DebtReversalBody? request,
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        IConfiguration configuration,
        SqlConnectionFactory connections,
        SyncEndpointStore centralStore,
        CreditDebtReversalService reversals,
        CancellationToken cancellationToken)
    {
        var actor = await ActorAsync(http, auth, permissions, PermissionCodes.CreditReverse, cancellationToken);
        if (actor.Error is not null)
        {
            return actor.Error;
        }

        var origin = FinancialRequestGuard.RejectUntrusted(http, configuration);
        if (origin is not null)
        {
            return origin;
        }

        if (request?.OperationId is not Guid operationId || request.SaleId is not Guid saleId || string.IsNullOrWhiteSpace(request.Reason))
        {
            return Results.BadRequest(new { code = "INVALID_DEBT_REVERSAL", message = "بيانات عكس الدين غير مكتملة." });
        }

        var branchConnection = connections.TryCreateBranchConnectionString();
        var settings = await centralStore.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (branchConnection is null || settings is null)
        {
            return Results.Json(new { state = "CentralUnavailable", message = "جهة الائتمان المركزية غير متاحة. لم يُعكس الدين." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        await using var branch = new Microsoft.Data.SqlClient.SqlConnection(branchConnection);
        await using var central = new Microsoft.Data.SqlClient.SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        try
        {
            await branch.OpenAsync(cancellationToken);
            await central.OpenAsync(cancellationToken);
        }
        catch (Exception)
        {
            return Results.Json(new { state = "CentralUnavailable", message = "جهة الائتمان المركزية غير متاحة. لم يُعكس الدين." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var result = await reversals.ReverseAsync(central, branch, operationId, saleId, actor.Context!.UserId, actor.Context.BusinessId, request.Reason.Trim(), true, cancellationToken);
        return result.Outcome switch
        {
            CreditDebtReversalOutcome.Completed => Results.Ok(new { state = "Completed", ledgerEntryId = result.LedgerEntryId }),
            CreditDebtReversalOutcome.Unauthorized => Results.Forbid(),
            CreditDebtReversalOutcome.AlreadyReversed => Results.Json(new { state = "AlreadyReversed", message = "تم عكس هذا الدين من قبل." }, statusCode: StatusCodes.Status409Conflict),
            CreditDebtReversalOutcome.HasPayments => Results.Json(new { state = "HasPayments", message = "لا يمكن عكس دين وصلته تسديدات. استخدم مسار التصحيح المعتمد." }, statusCode: StatusCodes.Status409Conflict),
            CreditDebtReversalOutcome.NotFound => Results.NotFound(new { state = "NotFound", message = "الدين المؤكد غير موجود." }),
            _ => Results.Json(new { state = result.Outcome.ToString(), message = "تم رفض عكس الدين." }, statusCode: StatusCodes.Status403Forbidden),
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

    private sealed class DebtReversalBody
    {
        public Guid? OperationId { get; init; }
        public Guid? SaleId { get; init; }
        public string? Reason { get; init; }
    }
}
