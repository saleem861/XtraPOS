namespace XtraPOS.Api.Inventory;

public static class InventoryAuditModes
{
    public const string All = "All";
    public const string Differences = "Differences";
    public const string Manual = "Manual";

    public static string? Normalize(string? value) => value?.Trim() switch
    {
        All => All,
        Differences => Differences,
        Manual => Manual,
        _ => null,
    };

    public static string Label(string mode) => mode switch
    {
        All => "كل الأصناف المعدودة",
        Differences => "الفروقات فقط",
        Manual => "اختيار يدوي",
        _ => mode,
    };
}

/// <summary>A counted line of a completed session, as the audit needs it.</summary>
public sealed record AuditCandidate(Guid SessionItemId, decimal? CountedQuantity, decimal? SystemQuantity, bool SystemKnown, bool IsCounted);

/// <summary>
/// المرحلة 2: تقرير الجرد، تدقيق المشرف الأعمى، تغيير التصنيف، و«كمية صنف».
/// قواعد بلا قاعدة بيانات حتى تُختبر وحدها.
/// </summary>
public static class InventoryReviewRules
{
    public const int MaxReasonLength = 500;
    public const int MinReasonLength = 3;
    public const int MaxSupplyDays = 365;
    public const int MaxReportDays = 92;

    /// <summary>
    /// الأسطر التي يدققها المشرف. التدقيق على الأصناف المعدودة فقط.
    /// «الفروقات» = المعدود يختلف عن كمية النظام لحظة العد، أو كمية النظام لم تكن معروفة.
    /// </summary>
    public static IReadOnlyList<Guid> SelectAuditLines(string mode, IEnumerable<AuditCandidate> lines, IEnumerable<Guid>? picked)
    {
        var counted = lines.Where(line => line.IsCounted && line.CountedQuantity is not null).ToList();
        IEnumerable<AuditCandidate> chosen = mode switch
        {
            InventoryAuditModes.All => counted,
            InventoryAuditModes.Differences => counted.Where(line => !line.SystemKnown || line.SystemQuantity != line.CountedQuantity),
            InventoryAuditModes.Manual => counted.Where(line => (picked ?? []).Contains(line.SessionItemId)),
            _ => throw new InventoryException("نوع التدقيق غير صالح."),
        };

        var result = chosen.Select(line => line.SessionItemId).Distinct().ToList();
        if (result.Count == 0)
        {
            throw new InventoryException(mode switch
            {
                InventoryAuditModes.Differences => "لا توجد فروقات في هذا الجرد للتدقيق.",
                InventoryAuditModes.Manual => "يرجى اختيار صنف واحد على الأقل من الأصناف المعدودة.",
                _ => "لا توجد أصناف معدودة في هذا الجرد للتدقيق.",
            });
        }

        return result;
    }

    /// <summary>الفارق بين عدّ المشرف وعدّ الموظف. موجب = وجد المشرف أكثر.</summary>
    public static decimal? AuditDifference(decimal? auditQuantity, decimal? employeeQuantity) =>
        auditQuantity is decimal audit && employeeQuantity is decimal employee ? audit - employee : null;

    public static string RequireReason(string? reason)
    {
        var text = reason?.Trim() ?? "";
        if (text.Length < MinReasonLength) throw new InventoryException("يرجى كتابة سبب التصحيح.");
        if (text.Length > MaxReasonLength) throw new InventoryException("سبب التصحيح أطول من المسموح (500 حرف).");
        return text;
    }

    /// <summary>الصفحة 0 تعني «مهم» في التطبيق القديم، فتُحفظ مهماً بلا صفحة.</summary>
    public static (bool IsImportant, byte? Page, int? SupplyDays) NormalizeClassification(bool isImportant, int? page, int? supplyDays)
    {
        if (page is < 0 or > InventoryCountRules.PeriodicPageCount)
        {
            throw new InventoryException("صفحة الجرد يجب أن تكون من 1 إلى 12، أو بلا صفحة.");
        }

        if (supplyDays is < 1 or > MaxSupplyDays)
        {
            throw new InventoryException("أيام التزويد يجب أن تكون من 1 إلى 365، أو فارغة.");
        }

        if (page == 0) return (true, null, supplyDays);
        return (isImportant, page is null ? null : (byte)page.Value, supplyDays);
    }

    /// <summary>فترة التقرير بتوقيت Asia/Hebron. الافتراضي آخر 7 أيام، والحد 92 يوماً.</summary>
    public static (DateOnly From, DateOnly To) ReportRange(DateOnly? from, DateOnly? to, DateOnly today)
    {
        var end = to ?? today;
        var start = from ?? end.AddDays(-6);
        if (start > end) throw new InventoryException("تاريخ البداية بعد تاريخ النهاية.");
        if (end.DayNumber - start.DayNumber + 1 > MaxReportDays)
        {
            throw new InventoryException("أقصى فترة للتقرير 92 يوماً.");
        }

        return (start, end);
    }
}
