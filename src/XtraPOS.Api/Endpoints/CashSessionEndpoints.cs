using XtraPOS.Api.Sales;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

public static class CashSessionEndpoints
{
    public static void MapCashSessionEndpoints(this WebApplication app)
    {
        app.MapGet("/api/pos/cash-session/current", CurrentAsync);
        app.MapPost("/api/pos/cash-session/open", OpenAsync);
        app.MapPost("/api/pos/cash-session/close", CloseAsync);
    }

    private static async Task<IResult> CurrentAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CashSessionService sessions,
        CancellationToken cancellationToken)
    {
        var actor = await RequireAsync(http, auth, permissions, cancellationToken);
        if (actor.Result is not null)
        {
            return actor.Result;
        }

        var current = await sessions.GetCurrentAsync(actor.Context!, cancellationToken);
        var expectedCashPreview = current is null
            ? null
            : await sessions.PreviewExpectedCashAsync(actor.Context!, cancellationToken);
        return Results.Ok(new { session = current, expectedCashPreview });
    }

    private static async Task<IResult> OpenAsync(
        OpenCashSessionBody? request,
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CashSessionService sessions,
        CancellationToken cancellationToken)
    {
        var actor = await RequireAsync(http, auth, permissions, cancellationToken);
        if (actor.Result is not null)
        {
            return actor.Result;
        }

        try
        {
            var opened = await sessions.OpenAsync(actor.Context!, request?.OpeningCashAmount ?? -1m, cancellationToken);
            if (opened is CashSessionAlreadyOpen)
            {
                return Results.Json(
                    new { code = CashSessionCodes.AlreadyOpen, message = "يوجد صندوق مفتوح لهذا الجهاز.", session = opened.Session },
                    statusCode: StatusCodes.Status409Conflict);
            }

            return Results.Ok(new { session = opened.Session });
        }
        catch (CashSessionException exception)
        {
            return Fail(exception);
        }
    }

    private static async Task<IResult> CloseAsync(
        CloseCashSessionBody? request,
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CashSessionService sessions,
        CancellationToken cancellationToken)
    {
        var actor = await RequireAsync(http, auth, permissions, cancellationToken);
        if (actor.Result is not null)
        {
            return actor.Result;
        }

        try
        {
            var closed = await sessions.CloseAsync(actor.Context!, request?.CountedCashAmount ?? -1m, cancellationToken);
            return Results.Ok(new { session = closed });
        }
        catch (CashSessionException exception)
        {
            return Fail(exception);
        }
    }

    private static IResult Fail(CashSessionException exception) =>
        Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: exception.StatusCode);

    private static async Task<(IResult? Result, PosDeviceContext? Context)> RequireAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword)
        {
            return (Results.Unauthorized(), null);
        }

        if (!await permissions.HasPermissionAsync(current.UserId, PermissionCodes.PosCashSession, cancellationToken))
        {
            return (Results.Forbid(), null);
        }

        var context = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        return context is null
            ? (Results.Unauthorized(), null)
            : (null, context);
    }

    private sealed class OpenCashSessionBody
    {
        public decimal? OpeningCashAmount { get; set; }
    }

    private sealed class CloseCashSessionBody
    {
        public decimal? CountedCashAmount { get; set; }
    }
}
