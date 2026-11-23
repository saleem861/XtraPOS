namespace XtraPOS.Api.Inventory;

public sealed class InventoryException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class InventoryTypes
{
    public const string Important = "Important";
    public const string Periodic = "Periodic";
    public const string Manual = "Manual";

    public static string? Normalize(string? value)
    {
        var text = value?.Trim() ?? "";
        if (text.Equals(Important, StringComparison.OrdinalIgnoreCase)) return Important;
        if (text.Equals(Periodic, StringComparison.OrdinalIgnoreCase)) return Periodic;
        if (text.Equals(Manual, StringComparison.OrdinalIgnoreCase)) return Manual;
        return null;
    }

    public static string Label(string type) => type switch
    {
        Important => "جرد الأصناف المهمة",
        Periodic => "الجرد الدوري",
        _ => "جرد يدوي",
    };
}

public static class InventorySessionStatuses
{
    public const string Open = "Open";
    public const string Completed = "Completed";
    public const string Discarded = "Discarded";
}

/// <summary>
/// Rules carried over unchanged from XtraPOS Inventory (inventory.sharbelfashion.com), adapted to XtraPOS stock.
/// </summary>
public static class InventoryCountRules
{
    public const int PeriodicPageCount = 12;
    public static readonly TimeOnly PeriodCutoff = new(16, 0);
    public const decimal MaxCountedQuantity = 1_000_000m;

    public const string PeriodBeforeCutoff = "AM";
    public const string PeriodAfterCutoff = "PM";

    /// <summary>One schedule page before 16:00 and another after it.</summary>
    public static string PeriodType(TimeOnly localTime) =>
        localTime < PeriodCutoff ? PeriodBeforeCutoff : PeriodAfterCutoff;

    /// <summary>Distinct pages, sorted. Periodic needs at least one page between 1 and 12; other types ignore pages.</summary>
    public static int[] NormalizePages(string type, IEnumerable<int>? pages)
    {
        if (type != InventoryTypes.Periodic) return [];
        var distinct = (pages ?? []).Distinct().Order().ToArray();
        if (distinct.Length == 0)
        {
            throw new InventoryException("يرجى اختيار صفحة واحدة على الأقل.");
        }

        if (distinct.Any(page => page < 1 || page > PeriodicPageCount))
        {
            throw new InventoryException("صفحة الجرد غير صالحة.");
        }

        return distinct;
    }

    public static bool SamePages(IEnumerable<int> left, IEnumerable<int> right) =>
        left.Distinct().Order().SequenceEqual(right.Distinct().Order());

    /// <summary>Zero is allowed, a negative or absurd quantity is refused.</summary>
    public static decimal ValidateCountedQuantity(decimal? quantity)
    {
        if (quantity is null)
        {
            throw new InventoryException("يرجى إدخال الكمية المعدودة.");
        }

        if (quantity < 0)
        {
            throw new InventoryException("لا يمكن أن تكون الكمية المعدودة سالبة.");
        }

        if (quantity > MaxCountedQuantity)
        {
            throw new InventoryException("الكمية المعدودة غير صالحة.");
        }

        return decimal.Round(quantity.Value, 4, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Posting a finished count. A known balance moves by (counted − quantity at count time), so sales made while
    /// counting are kept. An unknown balance (never initialized) is set to the counted quantity.
    /// </summary>
    public static CountPosting PlanPosting(decimal counted, decimal? systemQuantityAtCount, bool systemQuantityKnown) =>
        systemQuantityKnown && systemQuantityAtCount is decimal known
            ? new CountPosting(Absolute: false, Delta: counted - known)
            : new CountPosting(Absolute: true, Delta: null);

    /// <summary>
    /// Blind count: a counter without Inventory.Review never sees the expected quantity or the difference,
    /// not even after saving. Supervisors and admins see both.
    /// </summary>
    public static (decimal? Expected, decimal? Difference) Visible(
        bool canReview,
        bool isCounted,
        decimal? counted,
        decimal? systemQuantityAtCount,
        bool systemQuantityKnown)
    {
        if (!canReview || !isCounted || !systemQuantityKnown || systemQuantityAtCount is null)
        {
            return (null, null);
        }

        return (systemQuantityAtCount, counted is decimal value ? value - systemQuantityAtCount.Value : null);
    }
}

public sealed record CountPosting(bool Absolute, decimal? Delta);

public static class InventoryCalendar
{
    private static readonly TimeZoneInfo Zone = ResolveZone();

    public static DateTime LocalNow(DateTime? utcNow = null) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utcNow ?? DateTime.UtcNow, DateTimeKind.Utc), Zone);

    public static DateOnly Today(DateTime? utcNow = null) => DateOnly.FromDateTime(LocalNow(utcNow));

    private static TimeZoneInfo ResolveZone()
    {
        foreach (var id in new[] { "Asia/Hebron", "West Bank Standard Time", "Israel Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return TimeZoneInfo.CreateCustomTimeZone("HebronFallback", TimeSpan.FromHours(2), "Hebron", "Hebron");
    }
}
