using System.Globalization;
using XtraPOS.Api.Sales;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

/// <summary>
/// «أخطاء الموظفين»: للمشرف والمدير (صلاحية مراجعة الجرد). المدير (إدارة النظام) يرى كل الفروع، والمشرف فرعه.
/// </summary>
public static class EmployeeCashErrorEndpoints
{
    public static void MapEmployeeCashErrorEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/cash-errors");
        group.MapGet("", ListAsync);
        group.MapGet("/statement", StatementAsync);
        group.MapGet("/export", ExportAsync);
        group.MapPost("/actions", SaveActionAsync);
    }

    private static async Task<IResult> ListAsync(
        DateTime? from,
        DateTime? to,
        Guid? employeeId,
        Guid? branchId,
        Guid? stationId,
        string? type,
        string? status,
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        EmployeeCashErrorService errors,
        CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        var (start, end) = Period(from, to);
        return await RunAsync(() => errors.ListAsync(
            caller.Context!,
            caller.ManageAll,
            new CashErrorFilter(start, end, employeeId, branchId, stationId, type, status),
            cancellationToken));
    }

    private static async Task<IResult> ExportAsync(
        DateTime? from,
        DateTime? to,
        Guid? employeeId,
        Guid? branchId,
        Guid? stationId,
        string? type,
        string? status,
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        EmployeeCashErrorService errors,
        CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        var (start, end) = Period(from, to);
        var (items, summary, source) = await errors.ExportAsync(
            caller.Context!,
            caller.ManageAll,
            new CashErrorFilter(start, end, employeeId, branchId, stationId, type, status),
            cancellationToken);
        string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);
        var headers = new[] { "التاريخ", "رقم الموظف", "الموظف", "الفرع", "النقطة", "المبلغ", "النوع", "المبرر", "قرار المشرف", "الحالة", "سُوّي بواسطة", "تاريخ التسوية" };
        var rows = items.Select(item => (IReadOnlyList<string>)new[]
        {
            item.EffectiveAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            item.EmployeeNumber ?? "",
            item.EmployeeName,
            item.BranchName ?? "",
            item.StationNumber is int number ? $"{number} {item.StationName}" : "",
            Money(item.Amount),
            item.Kind == "Residual" ? "رصيد متبقٍ من مقاصة" : item.Amount < 0 ? "نقص" : "زيادة",
            item.Justification ?? "",
            item.DecisionCode switch { "KeepShortage" => "إبقاء النقص", "KeepSurplus" => "إبقاء الزيادة", "Waive" => "إعفاء الموظف", _ => "" },
            StatusText(item.Status),
            item.ClosedByName ?? "",
            item.ClosedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
        }).ToList();
        var meta = new List<string>
        {
            "أخطاء الموظفين",
            $"الفترة: {start:yyyy-MM-dd} — {end:yyyy-MM-dd}",
            $"المصدر: {(source == "central" ? "الإدارة المركزية" : "هذا الفرع")}",
            $"عدد الأخطاء: {summary.Count} — النقص {Money(summary.Shortage)} — الزيادة {Money(summary.Surplus)} — الصافي المستحق {Money(summary.OpenNet)}",
        };
        var bytes = SalesInquiryEndpoints.Workbook(meta, headers, rows);
        var fileName = $"أخطاء-الموظفين-{start:yyyyMMdd}-{end:yyyyMMdd}.xlsx";
        http.Response.Headers["X-Export-File-Name"] = Uri.EscapeDataString(fileName);
        return Results.File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", fileName);
    }

    private static string StatusText(string status) => status switch
    {
        EmployeeCashErrorRules.StatusOpen => "غير مسوّى",
        EmployeeCashErrorRules.StatusDeferred => "مؤجل",
        EmployeeCashErrorRules.StatusSettled => "مسوّى",
        EmployeeCashErrorRules.StatusOffset => "مُقاص",
        EmployeeCashErrorRules.StatusWaived => "معفى",
        _ => status,
    };

    private static async Task<IResult> StatementAsync(
        Guid employeeId,
        DateTime? from,
        DateTime? to,
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        EmployeeCashErrorService errors,
        CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        var (start, end) = Period(from, to);
        return await RunAsync(() => errors.StatementAsync(caller.Context!, caller.ManageAll, employeeId, start, end, cancellationToken));
    }

    private static async Task<IResult> SaveActionAsync(
        ActionBody? body,
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        EmployeeCashErrorService errors,
        CancellationToken cancellationToken)
    {
        var caller = await CallerAsync(http, auth, permissions, cancellationToken);
        if (caller.Result is not null) return caller.Result;
        if (body is null) return Results.Json(new { message = "الطلب فارغ." }, statusCode: StatusCodes.Status400BadRequest);
        return await RunAsync(() => errors.SaveActionAsync(
            caller.Context!,
            caller.ManageAll,
            new CashErrorActionRequest(body.Type, body.EmployeeId, body.ItemIds, body.Method, body.DeferTo, body.Note),
            cancellationToken));
    }

    /// <summary>الافتراض: الشهر الحالي. الفترة لا تتجاوز سنتين.</summary>
    private static (DateTime From, DateTime To) Period(DateTime? from, DateTime? to)
    {
        var today = DateTime.UtcNow.Date;
        var start = from?.Date ?? new DateTime(today.Year, today.Month, 1);
        var end = to?.Date ?? start.AddMonths(1).AddDays(-1);
        if (end < start) (start, end) = (end, start);
        if ((end - start).TotalDays > 731) start = end.AddDays(-731);
        return (start, end);
    }

    private static async Task<IResult> RunAsync(Func<Task<object>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (CashErrorException exception)
        {
            return Results.Json(new { message = exception.Message }, statusCode: exception.StatusCode);
        }
    }

    private static async Task<(IResult? Result, PosDeviceContext? Context, bool ManageAll)> CallerAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword)
        {
            return (Results.Unauthorized(), null, false);
        }

        var codes = await permissions.GetPermissionCodesAsync(current.UserId, cancellationToken);
        var manageAll = codes.Contains(PermissionCodes.SystemAdminister, StringComparer.Ordinal);
        if (!manageAll && !codes.Contains(PermissionCodes.CashDrawerReview, StringComparer.Ordinal))
        {
            return (Results.Forbid(), null, false);
        }

        var context = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        return context is null ? (Results.Unauthorized(), null, false) : (null, context, manageAll);
    }

    private sealed class ActionBody
    {
        public string? Type { get; set; }
        public Guid EmployeeId { get; set; }
        public List<Guid>? ItemIds { get; set; }
        public string? Method { get; set; }
        public DateTime? DeferTo { get; set; }
        public string? Note { get; set; }
    }
}
