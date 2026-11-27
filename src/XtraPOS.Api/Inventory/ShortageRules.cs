namespace XtraPOS.Api.Inventory;

public static class ShortageTypes
{
    public static string? Normalize(string? value) => InventoryTypes.Normalize(value);

    public static string Label(string type) => type switch
    {
        InventoryTypes.Important => "نواقص الأصناف المهمة",
        InventoryTypes.Periodic => "نواقص الجرد الدوري",
        InventoryTypes.Custom => "نواقص الجرد المخصص",
        _ => "نواقص الجرد اليدوي",
    };
}

public static class ShortageTaskTypes
{
    public const string Purchase = "Purchase";
    public const string Warehouse = "Warehouse";
    public const string Balance = "Balance";
    public const string Delete = "Delete";
}

/// <summary>رصيد فرع وسرعته لصنف واحد كما وصلا للمركز.</summary>
public sealed record ShortageBranchInput(Guid BranchId, decimal? Quantity, decimal? DailySalesRate);

public sealed record ShortageBranchResult(Guid BranchId, decimal? Quantity, decimal? DailySalesRate, decimal Target, decimal Need, decimal Surplus);

public sealed record ShortageItemResult(
    IReadOnlyList<ShortageBranchResult> Branches,
    decimal TotalNeed,
    decimal TotalSurplus,
    decimal Required,
    decimal Balance,
    bool MissingRate,
    bool MissingQuantity)
{
    public bool IsShort => TotalNeed > 0;

    /// <summary>بلا سرعة بيع في كل الفروع المختارة (لم يُبع أو لم تُحسب سرعته)، فهدفه صفر ولا يظهر في النواقص.</summary>
    public bool NoSales => Branches.All(branch => (branch.DailySalesRate ?? 0m) <= 0m);
}

/// <summary>
/// لماذا خرجت المعالجة بهذا العدد (ملاحظة المالك 27 نوفمبر: «جردت ولم يظهر أي صنف»):
/// الأصناف في النطاق، والناقصة منها، وما لا سرعة بيع له، وما رصيده يغطي الهدف.
/// </summary>
public sealed record ShortageScopeSummary(int ScopeItems, int ShortItems, int NoSalesItems, int CoveredItems)
{
    public static ShortageScopeSummary From(IEnumerable<ShortageItemResult> results)
    {
        int scope = 0, shortItems = 0, noSales = 0, covered = 0;
        foreach (var result in results)
        {
            scope++;
            if (result.IsShort) shortItems++;
            else if (result.NoSales) noSales++;
            else covered++;
        }

        return new ShortageScopeSummary(scope, shortItems, noSales, covered);
    }
}

/// <summary>قرار المعالج لصنف واحد: كميات للشراء والمخزن والموازنة، أو حذف، أو «تم التزويد».</summary>
/// <param name="Substitute">رقم الصنف البديل، مع طلب الحذف فقط (اختياري).</param>
public sealed record ShortageDecision(decimal? Purchase, decimal? Warehouse, decimal? Balance, bool Delete, bool AlreadySupplied, string? Note, string? Substitute = null);

/// <summary>
/// معادلات معالجة النواقص كما في XtraPOS Inventory، لكل فرع أولاً:
/// الهدف = سقف(السرعة × أيام التوفير)، الاحتياج = max(الهدف − الرصيد، 0)، الفائض = max(الرصيد − الهدف، 0).
/// ثم المطلوب شراؤه = max(مجموع الاحتياج − مجموع الفائض، 0)، والموازنة = min(مجموع الاحتياج، مجموع الفائض).
/// سرعة غير محسوبة أو رصيد غير معروف يُحسبان صفراً مع علامة تنبيه على الصنف، لا يُخفيان.
/// </summary>
public static class ShortageRules
{
    /// <summary>نص بحث آمن داخل LIKE مع ESCAPE N'!': يهرب ! و% و_ و[ فتُطابق حرفياً.</summary>
    public static string EscapeLike(string text) =>
        text.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_").Replace("[", "![");

    public const int MinSupplyDays = 1;
    public const int MaxSupplyDays = 365;
    public const int FollowUpDefaultDays = 15;
    public const int OverduePurchaseDays = 3;
    public const int AuditMinItems = 1;
    public const int AuditMaxItems = 10;
    public const int AuditValidDays = 7;

    public static int RequireSupplyDays(int? days) =>
        days is >= MinSupplyDays and <= MaxSupplyDays
            ? days.Value
            : throw new InventoryException("أيام التوفير يجب أن تكون من 1 إلى 365.");

    public static ShortageItemResult Calculate(IEnumerable<ShortageBranchInput> branches, int supplyDays)
    {
        var results = new List<ShortageBranchResult>();
        var missingRate = false;
        var missingQuantity = false;
        foreach (var branch in branches)
        {
            missingRate |= branch.DailySalesRate is null;
            missingQuantity |= branch.Quantity is null;
            var rate = Math.Max(branch.DailySalesRate ?? 0m, 0m);
            var quantity = branch.Quantity ?? 0m;
            var target = Math.Ceiling(rate * supplyDays);
            results.Add(new ShortageBranchResult(
                branch.BranchId,
                branch.Quantity,
                branch.DailySalesRate,
                target,
                Math.Max(target - quantity, 0m),
                Math.Max(quantity - target, 0m)));
        }

        var need = results.Sum(row => row.Need);
        var surplus = results.Sum(row => row.Surplus);
        return new ShortageItemResult(results, need, surplus, Math.Max(need - surplus, 0m), Math.Min(need, surplus), missingRate, missingQuantity);
    }

    public static ShortageDecision ValidateDecision(ShortageDecision decision)
    {
        static decimal? Quantity(decimal? value, string label)
        {
            if (value is null or 0m) return null;
            if (value < 0 || value > 1_000_000m) throw new InventoryException($"كمية {label} غير صالحة.");
            return Math.Round(value.Value, 4, MidpointRounding.AwayFromZero);
        }

        var purchase = Quantity(decision.Purchase, "الشراء");
        var warehouse = Quantity(decision.Warehouse, "المخزن");
        var balance = Quantity(decision.Balance, "الموازنة");
        var note = string.IsNullOrWhiteSpace(decision.Note) ? null : decision.Note.Trim();
        if (note?.Length > 500) throw new InventoryException("الملاحظة أطول من المسموح (500 حرف).");
        if (decision.Delete && (purchase is not null || warehouse is not null || balance is not null))
        {
            throw new InventoryException("طلب الحذف لا يُجمع مع شراء أو مخزن أو موازنة.");
        }

        if (!decision.Delete && !decision.AlreadySupplied && purchase is null && warehouse is null && balance is null)
        {
            throw new InventoryException("اختر إجراءً للصنف: شراء أو مخزن أو موازنة أو حذف أو «تم التزويد».");
        }

        var substitute = string.IsNullOrWhiteSpace(decision.Substitute) ? null : decision.Substitute.Trim();
        if (substitute is not null && !decision.Delete) throw new InventoryException("الصنف البديل يُحدَّد مع طلب الحذف فقط.");
        if (substitute?.Length > 100) throw new InventoryException("رقم الصنف البديل أطول من المسموح.");
        return new ShortageDecision(purchase, warehouse, balance, decision.Delete, decision.AlreadySupplied && !decision.Delete, note, substitute);
    }

    /// <summary>المهام التي يولّدها التثبيت النهائي من قرار صنف واحد.</summary>
    public static IReadOnlyList<(string Type, decimal? Quantity)> TasksFor(ShortageDecision decision)
    {
        var tasks = new List<(string, decimal?)>();
        if (decision.Delete) tasks.Add((ShortageTaskTypes.Delete, null));
        if (decision.Purchase is decimal purchase) tasks.Add((ShortageTaskTypes.Purchase, purchase));
        if (decision.Warehouse is decimal warehouse) tasks.Add((ShortageTaskTypes.Warehouse, warehouse));
        if (decision.Balance is decimal balance) tasks.Add((ShortageTaskTypes.Balance, balance));
        return tasks;
    }

    /// <summary>تحويل جزء من الشراء إلى المخزن: يُنقص المهام الأقدم أولاً، وما يصل صفراً يُلغى.</summary>
    public static IReadOnlyList<(Guid TaskId, decimal Remaining)> ReducePurchases(IEnumerable<(Guid TaskId, decimal Quantity)> openOldestFirst, decimal moved)
    {
        if (moved <= 0) throw new InventoryException("أدخل كمية أكبر من صفر.");
        var result = new List<(Guid, decimal)>();
        var left = moved;
        foreach (var (taskId, quantity) in openOldestFirst)
        {
            var take = Math.Min(quantity, left);
            left -= take;
            result.Add((taskId, quantity - take));
            if (left <= 0) break;
        }

        if (left > 0) throw new InventoryException("الكمية أكبر من المطلوب شراؤه لهذا الصنف.");
        return result;
    }

    /// <summary>درجة طلب تأكيد الاستلام من 10: نسبة الأصناف التي طابق فيها المستلَم المتوقع.</summary>
    public static decimal AuditScore(IReadOnlyCollection<(decimal Expected, decimal Received)> items)
    {
        if (items.Count == 0) return 0m;
        var matched = items.Count(item => item.Expected == item.Received);
        return Math.Round(matched * 10m / items.Count, 1, MidpointRounding.AwayFromZero);
    }

    /// <summary>درجة تدقيق نعم/لا من 10 (كما في النظام القديم): نسبة الأصناف التي أكّد المستلم استلامها.</summary>
    public static decimal AuditYesNoScore(int received, int total) =>
        total <= 0 ? 0m : Math.Round(received * 10m / total, 1, MidpointRounding.AwayFromZero);

    /// <summary>أنواع المعالجة التي يُدقَّق عليها (الحذف لا يُسلَّم للفرع).</summary>
    public static readonly string[] AuditTaskTypes = [ShortageTaskTypes.Purchase, ShortageTaskTypes.Warehouse, ShortageTaskTypes.Balance];

    /// <summary>
    /// قاعدة النظام القديم: من صنف إلى 10 أصناف، وصنف واحد على الأقل من كل نوع معالجة متاح لهذا الموظف في هذا اليوم.
    /// </summary>
    public static void RequireAuditSelection(IReadOnlyCollection<string> chosenTypes, IReadOnlyCollection<string> availableTypes)
    {
        if (chosenTypes.Count is < AuditMinItems or > AuditMaxItems)
        {
            throw new InventoryException("طلب التدقيق من صنف واحد إلى 10 أصناف.");
        }

        if (availableTypes.Any(type => !chosenTypes.Contains(type)))
        {
            throw new InventoryException("يجب اختيار صنف واحد على الأقل من كل نوع معالجة متاح.");
        }
    }

    public static (DateOnly From, DateOnly To) Range(DateOnly? from, DateOnly? to, DateOnly today, int defaultDays)
    {
        var end = to ?? today;
        var start = from ?? end.AddDays(-(defaultDays - 1));
        if (start > end) throw new InventoryException("تاريخ البداية بعد تاريخ النهاية.");
        if (end.DayNumber - start.DayNumber + 1 > 366) throw new InventoryException("أقصى فترة سنة واحدة.");
        return (start, end);
    }
}
