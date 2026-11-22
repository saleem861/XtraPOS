namespace XtraPOS.Api.Sales;

/// <summary>
/// خطأ جرار أو رصيد متبقٍ من مقاصة، كما يظهر في شاشة «أخطاء الموظفين».
/// المبلغ بإشارة: النقص سالب والزيادة موجبة.
/// </summary>
public sealed record CashErrorItem(
    Guid ItemId,
    string Kind,
    Guid EmployeeId,
    string EmployeeName,
    Guid BranchId,
    string? BranchName,
    int? StationNumber,
    string? StationName,
    decimal Amount,
    DateTime OccurredAt,
    DateTime EffectiveAt,
    string? Justification,
    string? DecisionCode,
    string Status,
    Guid? ClosedBy,
    string? ClosedType,
    string? ClosedMethod,
    DateTime? ClosedAt,
    string? ClosedByName,
    DateTime? DeferredTo,
    int NoteCount,
    string? EmployeeNumber = null);

public sealed record CashErrorSummary(
    int Count,
    int OpenCount,
    decimal Shortage,
    decimal Surplus,
    decimal Waived,
    decimal Settled,
    decimal OpenNet);

public sealed record CashErrorEmployeeBalance(Guid EmployeeId, string EmployeeName, CashErrorSummary Summary, string? EmployeeNumber = null);

public sealed record CashErrorActionView(
    Guid ActionId,
    string Type,
    Guid EmployeeId,
    decimal Amount,
    string? Method,
    DateTime? DeferTo,
    string? Note,
    string? ActorName,
    DateTime CreatedAt,
    IReadOnlyList<Guid> Items);

public sealed record CashErrorActionRequest(
    string? Type,
    Guid EmployeeId,
    IReadOnlyList<Guid>? ItemIds,
    string? Method,
    DateTime? DeferTo,
    string? Note);

/// <summary>إجراء جاهز للحفظ بعد التحقق.</summary>
public sealed record CashErrorActionDraft(
    string Type,
    Guid EmployeeId,
    IReadOnlyList<(Guid ItemId, decimal Amount)> Items,
    decimal Amount,
    string? Method,
    DateTime? DeferTo,
    string? Note);

public sealed class CashErrorException(string message, int statusCode) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class EmployeeCashErrorRules
{
    public const string Offset = "Offset";
    public const string Settle = "Settle";
    public const string Defer = "Defer";
    public const string Note = "Note";

    public const string StatusOpen = "Open";
    public const string StatusDeferred = "Deferred";
    public const string StatusSettled = "Settled";
    public const string StatusOffset = "Offset";
    public const string StatusWaived = "Waived";

    public static readonly string[] Methods = ["Cash", "Payroll", "Other"];

    /// <summary>
    /// حالة البند: المعفى بقرار المشرف، ثم ما أغلقته مقاصة أو تسوية (الأقدم يفوز)، ثم المؤجل، وإلا مفتوح.
    /// </summary>
    public static string StatusOf(string? decisionCode, string? closedType, DateTime? deferredTo) =>
        decisionCode == CashDrawerRules.Waive ? StatusWaived
        : closedType == Offset ? StatusOffset
        : closedType == Settle ? StatusSettled
        : deferredTo is not null ? StatusDeferred
        : StatusOpen;

    public static bool IsOpen(string status) => status is StatusOpen or StatusDeferred;

    /// <summary>
    /// النقص والزيادة والمعفى تُحسب من أخطاء الجرد فقط؛ الرصيد المتبقي من مقاصة يدخل الصافي المفتوح فقط،
    /// لأن الأخطاء التي قوصت محسوبة أصلاً.
    /// </summary>
    public static CashErrorSummary Summarize(IEnumerable<CashErrorItem> items)
    {
        var count = 0;
        var open = 0;
        decimal shortage = 0, surplus = 0, waived = 0, settled = 0, openNet = 0;
        foreach (var item in items)
        {
            count++;
            var error = item.Kind == "Error";
            if (error && item.Amount < 0) shortage += item.Amount;
            if (error && item.Amount > 0) surplus += item.Amount;
            if (item.Status == StatusWaived) waived += item.Amount;
            if (item.Status == StatusSettled) settled += item.Amount;
            if (IsOpen(item.Status))
            {
                open++;
                openNet += item.Amount;
            }
        }

        return new CashErrorSummary(count, open, shortage, surplus, waived, settled, openNet);
    }

    public static IReadOnlyList<CashErrorEmployeeBalance> Balances(IEnumerable<CashErrorItem> items) =>
        items
            .GroupBy(item => item.EmployeeId)
            .Select(group => new CashErrorEmployeeBalance(group.Key, group.First().EmployeeName, Summarize(group), group.First().EmployeeNumber))
            .OrderBy(balance => balance.Summary.OpenNet)
            .ThenBy(balance => balance.EmployeeName, StringComparer.CurrentCulture)
            .ToArray();

    public static bool MatchesType(CashErrorItem item, string? type) =>
        type switch
        {
            CashDrawerRules.Shortage => item.Amount < 0,
            CashDrawerRules.Surplus => item.Amount > 0,
            _ => true,
        };

    public static bool MatchesStatus(CashErrorItem item, string? status) =>
        status switch
        {
            null or "" or "All" => true,
            "Unsettled" => IsOpen(item.Status),
            _ => item.Status == status,
        };

    public static DateTime FirstOfNextMonth(DateTime date) => new DateTime(date.Year, date.Month, 1).AddMonths(1);

    /// <summary>يتحقق من طلب الإجراء على البنود المختارة كما قُرئت من المركز أو الفرع.</summary>
    public static CashErrorActionDraft Validate(CashErrorActionRequest request, IReadOnlyDictionary<Guid, CashErrorItem> known)
    {
        var type = request.Type?.Trim();
        if (type is not (Offset or Settle or Defer or Note))
        {
            throw new CashErrorException("نوع الإجراء غير معروف.", StatusCodes.Status400BadRequest);
        }

        var ids = (request.ItemIds ?? []).Distinct().ToArray();
        if (ids.Length == 0)
        {
            throw new CashErrorException("اختر خطأً واحداً على الأقل.", StatusCodes.Status400BadRequest);
        }

        if (type == Offset && ids.Length < 2)
        {
            throw new CashErrorException("المقاصة تحتاج خطأين على الأقل.", StatusCodes.Status400BadRequest);
        }

        var items = new List<CashErrorItem>();
        foreach (var id in ids)
        {
            if (!known.TryGetValue(id, out var item))
            {
                throw new CashErrorException("بعض الأخطاء المختارة غير موجودة. حدّث القائمة.", StatusCodes.Status409Conflict);
            }

            items.Add(item);
        }

        if (request.EmployeeId == Guid.Empty || items.Any(item => item.EmployeeId != request.EmployeeId))
        {
            throw new CashErrorException("كل الأخطاء المختارة يجب أن تخص الموظف نفسه.", StatusCodes.Status400BadRequest);
        }

        if (type != Note && items.Any(item => !IsOpen(item.Status)))
        {
            throw new CashErrorException("بعض الأخطاء المختارة مسوّاة أو معفاة. اختر الأخطاء غير المسوّاة فقط.", StatusCodes.Status409Conflict);
        }

        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > 500 })
        {
            throw new CashErrorException("الملاحظة أطول من 500 حرف.", StatusCodes.Status400BadRequest);
        }

        if (type == Note && note is null)
        {
            throw new CashErrorException("اكتب الملاحظة.", StatusCodes.Status400BadRequest);
        }

        string? method = null;
        if (type == Settle)
        {
            method = Methods.FirstOrDefault(value => string.Equals(value, request.Method?.Trim(), StringComparison.OrdinalIgnoreCase));
            if (method is null)
            {
                throw new CashErrorException("اختر طريقة التسوية.", StatusCodes.Status400BadRequest);
            }
        }

        DateTime? deferTo = null;
        if (type == Defer)
        {
            var latest = items.Max(item => item.EffectiveAt).Date;
            var wanted = request.DeferTo?.Date ?? FirstOfNextMonth(latest);
            deferTo = new DateTime(wanted.Year, wanted.Month, 1);
            if (deferTo <= latest)
            {
                throw new CashErrorException("التأجيل يكون إلى شهر بعد تاريخ الخطأ.", StatusCodes.Status400BadRequest);
            }
        }

        var amount = type is Offset or Settle ? items.Sum(item => item.Amount) : 0m;
        return new CashErrorActionDraft(
            type,
            request.EmployeeId,
            items.Select(item => (item.ItemId, item.Amount)).ToArray(),
            amount,
            method,
            deferTo,
            note);
    }

    public static string Describe(CashErrorActionDraft draft, string employeeName) =>
        draft.Type switch
        {
            Offset => $"مقاصة أخطاء الجرار — {employeeName} ({draft.Items.Count} أخطاء، الصافي {draft.Amount:0.00})",
            Settle => $"تسوية أخطاء الجرار — {employeeName} ({draft.Amount:0.00})",
            Defer => $"تأجيل أخطاء الجرار — {employeeName} إلى {draft.DeferTo:yyyy-MM}",
            _ => $"ملاحظة على أخطاء الجرار — {employeeName}",
        };
}
