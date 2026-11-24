namespace XtraPOS.Api.Inventory;

/// <summary>قواعد المرحلة 3 بلا قاعدة بيانات: سرعة البيع، والنسخة الكاملة، وحداثة نسخة الفرع على المركز.</summary>
public static class InventoryRelayRules
{
    public const int SalesRateWindowDays = 30;

    /// <summary>كل إرسال يعيد آخر دقيقتين، فلا يضيع رصيد تغيّر في نفس الثانية.</summary>
    public const int OverlapMinutes = 2;

    /// <summary>بعدها تُعدّ نسخة الفرع على المركز قديمة، ويظهر تنبيه قبل معالجة النواقص.</summary>
    public const int StaleMinutes = 30;

    /// <summary>عدد أيام القسمة: 30، أو منذ أول بيع إن كان أحدث. يوم واحد على الأقل.</summary>
    public static decimal SalesRateDays(DateTime firstSaleUtc, DateTime nowUtc)
    {
        var since = (decimal)(nowUtc - firstSaleUtc).TotalDays;
        return Math.Clamp(since, 1m, SalesRateWindowDays);
    }

    /// <summary>السرعة لكل يوم بوحدة الصنف. صافي سالب (مرتجعات أكثر من المبيعات) = صفر.</summary>
    public static decimal DailyRate(decimal soldOwnerUnits, decimal days, decimal baseQuantityFactor)
    {
        if (days <= 0 || baseQuantityFactor <= 0) return 0m;
        var rate = Math.Max(soldOwnerUnits, 0m) / days / baseQuantityFactor;
        return Math.Round(rate, 6, MidpointRounding.AwayFromZero);
    }

    /// <summary>نسخة كاملة أول مرة، ثم مرة في كل يوم جديد بتوقيت Asia/Hebron.</summary>
    public static bool NeedsFullPush(DateTime? lastFullPushUtc, DateOnly today) =>
        lastFullPushUtc is not DateTime last || InventoryCalendar.Today(DateTime.SpecifyKind(last, DateTimeKind.Utc)) < today;

    public static bool IsStale(DateTime? lastPushUtc, DateTime nowUtc) =>
        lastPushUtc is not DateTime last || (nowUtc - DateTime.SpecifyKind(last, DateTimeKind.Utc)).TotalMinutes > StaleMinutes;

    /// <summary>صنف مالك مخزون: ليس عبوة لوحدة أخرى، وليس صنفاً أماً لتشكيلة.</summary>
    public static string StockOwnerSql(string alias) =>
        $"""
        NOT EXISTS (SELECT 1 FROM dbo.ItemUnits AS unit
                    WHERE unit.ItemID = {alias}.ItemID AND unit.IsActive = 1
                      AND unit.InternalUnitItemID IS NOT NULL AND unit.InternalUnitItemID <> {alias}.ItemID)
        AND NOT EXISTS (SELECT 1 FROM dbo.Items AS child WHERE child.ParentItemID = {alias}.ItemID)
        """;
}
