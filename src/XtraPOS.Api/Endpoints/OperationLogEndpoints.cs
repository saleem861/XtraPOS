using XtraPOS.Api.Data;
using XtraPOS.Api.Operations;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

public static class OperationLogEndpoints
{
    public static void MapOperationLogEndpoints(this WebApplication app)
    {
        app.MapGet("/api/operations/log", ListAsync);
        app.MapPost("/api/operations/log/{operationId:guid}/retry", RetryAsync);
        app.MapDelete("/api/operations/log/{operationId:guid}", DeleteAsync);
    }

    private static async Task<IResult> ListAsync(DateTime? from, DateTime? to, string? destination, HttpContext http, AuthService auth, PermissionService permissions, SqlConnectionFactory connections, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, auth, permissions, cancellationToken) is { } error)
        {
            return error;
        }

        var connectionString = connections.TryCreateBranchConnectionString();
        if (connectionString is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await using var branch = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await branch.OpenAsync(cancellationToken);
        var end = to?.Date.AddDays(1);
        return Results.Ok(await BranchOperationLog.ListAsync(branch, from?.Date, end, destination, cancellationToken));
    }

    private static async Task<IResult> RetryAsync(Guid operationId, HttpContext http, AuthService auth, PermissionService permissions, SqlConnectionFactory connections, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, auth, permissions, cancellationToken) is { } error)
        {
            return error;
        }

        var connectionString = connections.TryCreateBranchConnectionString();
        if (connectionString is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await using var branch = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await branch.OpenAsync(cancellationToken);
        return await BranchOperationLog.RetryAsync(branch, operationId, cancellationToken)
            ? Results.Ok(new { retried = true })
            : Results.NotFound(new { message = "لا يمكن إعادة محاولة هذا الأمر." });
    }

    private static async Task<IResult> DeleteAsync(Guid operationId, HttpContext http, AuthService auth, PermissionService permissions, SqlConnectionFactory connections, CancellationToken cancellationToken)
    {
        if (await AdminAsync(http, auth, permissions, cancellationToken) is { } error)
        {
            return error;
        }

        var connectionString = connections.TryCreateBranchConnectionString();
        if (connectionString is null)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        await using var branch = new Microsoft.Data.SqlClient.SqlConnection(connectionString);
        await branch.OpenAsync(cancellationToken);
        return await BranchOperationLog.RemoveAsync(branch, operationId, cancellationToken)
            ? Results.Ok(new { deleted = true })
            : Results.NotFound(new { message = "الأمر غير موجود في السجل." });
    }

    private static async Task<IResult?> AdminAsync(HttpContext http, AuthService auth, PermissionService permissions, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword)
        {
            return Results.Unauthorized();
        }

        if (!await permissions.HasPermissionAsync(current.UserId, PermissionCodes.SystemAdminister, cancellationToken))
        {
            return Results.Forbid();
        }

        return await auth.GetPosDeviceContextAsync(http, cancellationToken) is null ? Results.Unauthorized() : null;
    }
}
