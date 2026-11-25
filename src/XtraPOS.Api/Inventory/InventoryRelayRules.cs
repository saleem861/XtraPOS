namespace XtraPOS.Api.Inventory;

/// <summary>قواعد المرحلة 3 بلا قاعدة بيانات: سرعة البيع، والنسخة الكاملة، وحداثة نسخة الفرع على المركز.</summary>
public static class InventoryRelayRules
{
    public const int SalesRateWindowDays = 30;

    /// <summary>كل إرسال يعيد آخر دقيقتين، فلا يضيع رصيد تغيّر في نفس الثانية.</summary>
    public const int OverlapMinutes = 2;

    /// <summary>
    /// كانت تُعدّ بعدها نسخة الفرع على المركز قديمة. منذ قرار 25 نوفمبر المركز هو المرجع للكمية،
    /// فلا تُنتظر نسخة الفرع ولا يظهر تنبيه القِدم في الجرد والنواقص.
    /// </summary>
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

    /// <summary>
    /// تصحيح طلبه المركز لفرع آخر (قرار المالك): الفارق = المعدودة − المفترضة، ويُضاف إلى رصيد الفرع الحالي.
    /// مثال: مفترض 7، معدود 5، بيع 1 قبل المزامنة (الرصيد 6) → 6 + (−2) = 4.
    /// بلا فارق معروف (طلب قديم أو رصيد غير مهيأ في المركز) أو رصيد الفرع غير مهيأ: تُكتب المعدودة كما هي.
    /// </summary>
    public static CorrectionPlan PlanCorrection(decimal counted, decimal? delta, bool branchInitialized) =>
        delta is decimal difference && branchInitialized
            ? new CorrectionPlan(true, difference)
            : new CorrectionPlan(false, counted);

    /// <summary>ByDifference: Value هو الفارق المضاف. وإلا Value هو الرصيد الذي يُكتب.</summary>
    public readonly record struct CorrectionPlan(bool ByDifference, decimal Value);

    /// <summary>
    /// قرار المالك (25 نوفمبر 2026): في النظام المركزي يُثبَّت الجرد والتصحيح على المركز أولاً بالفارق.
    /// المفترضة = ما رآه الموظف (رصيد المركز لحظة العد)، أو رصيد المركز الآن إن لم يُعرف.
    /// الفارق = المعدودة − المفترضة، ويُضاف إلى رصيد المركز الحالي، فلا يضيع بيع وصل بين العد والتثبيت.
    /// مثال: مفترض 7، معدود 5، بيع 1 وصل المركز قبل التثبيت (المركز 6) → 6 + (−2) = 4.
    /// رصيد المركز غير معروف: تُكتب المعدودة كما هي ولا يوجد فارق (والفرع يكتبها كما هي أيضاً).
    /// </summary>
    public static CentralPlan PlanCentral(decimal counted, decimal? expected, decimal? centralQuantity)
    {
        if (centralQuantity is decimal current)
        {
            var delta = counted - (expected ?? current);
            return new CentralPlan(delta, current + delta);
        }

        return new CentralPlan(null, counted);
    }

    /// <summary>Delta: الفارق المضاف (null = كُتبت المعدودة كما هي). Resulting: رصيد المركز بعد التثبيت.</summary>
    public readonly record struct CentralPlan(decimal? Delta, decimal Resulting);

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
