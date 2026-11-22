namespace XtraPOS.Api.Customers;

/// <summary>حدود النجوم: أقل مجموع مشتريات خلال آخر <see cref="WindowDays"/> يوماً لنجمة واحدة حتى خمس نجوم.</summary>
public sealed record CustomerStarThresholds(
    int WindowDays,
    decimal OneStar,
    decimal TwoStars,
    decimal ThreeStars,
    decimal FourStars,
    decimal FiveStars,
    bool IsDefault,
    DateTime? UpdatedAt,
    string? UpdatedByName)
{
    public IReadOnlyList<decimal> Steps => [OneStar, TwoStars, ThreeStars, FourStars, FiveStars];
}

/// <summary>نجوم الزبون الآن، واتجاهه مقارنة بالفترة السابقة بالطول نفسه، وما يلزمه للنجمة التالية.</summary>
public sealed record CustomerStarStanding(
    int Stars,
    int WindowDays,
    decimal WindowTotal,
    decimal PreviousTotal,
    string Trend,
    decimal? ChangePercent,
    decimal? NextStarAt,
    decimal? NeededForNext,
    bool ThresholdsAreDefault);

public sealed class CustomerStarException(string message) : Exception(message);

public static class CustomerStarRules
{
    public const string TrendUp = "Up";
    public const string TrendDown = "Down";
    public const string TrendSteady = "Steady";
    public const string TrendNew = "New";
    public const string TrendInactive = "Inactive";

    /// <summary>فرق أقل من 10٪ يُعدّ ثباتاً.</summary>
    public const decimal SteadyBand = 10m;

    /// <summary>القيم الافتراضية إلى أن يحدد المدير حدوداً: 90 يوماً.</summary>
    public static CustomerStarThresholds Default { get; } = new(90, 100m, 500m, 1500m, 3000m, 6000m, true, null, null);

    public static int StarsFor(decimal total, CustomerStarThresholds thresholds)
    {
        var stars = 0;
        foreach (var step in thresholds.Steps)
        {
            if (total >= step) stars++;
        }

        return stars;
    }

    public static CustomerStarStanding Standing(decimal windowTotal, decimal previousTotal, CustomerStarThresholds thresholds)
    {
        var stars = StarsFor(windowTotal, thresholds);
        decimal? next = stars < 5 ? thresholds.Steps[stars] : null;
        decimal? needed = next is null ? null : Math.Max(0m, next.Value - windowTotal);
        string trend;
        decimal? change = null;
        if (windowTotal <= 0m && previousTotal <= 0m)
        {
            trend = TrendInactive;
        }
        else if (previousTotal <= 0m)
        {
            trend = TrendNew;
        }
        else
        {
            change = decimal.Round((windowTotal - previousTotal) / previousTotal * 100m, 1, MidpointRounding.AwayFromZero);
            trend = change >= SteadyBand ? TrendUp : change <= -SteadyBand ? TrendDown : TrendSteady;
        }

        return new CustomerStarStanding(stars, thresholds.WindowDays, windowTotal, previousTotal, trend, change, next, needed, thresholds.IsDefault);
    }

    public static CustomerStarThresholds Validate(int windowDays, decimal one, decimal two, decimal three, decimal four, decimal five)
    {
        if (windowDays is < 7 or > 730)
        {
            throw new CustomerStarException("مدة الاحتساب بين 7 و730 يوماً.");
        }

        decimal[] steps = [one, two, three, four, five];
        if (steps[0] <= 0m)
        {
            throw new CustomerStarException("حد النجمة الأولى أكبر من صفر.");
        }

        for (var index = 1; index < steps.Length; index++)
        {
            if (steps[index] <= steps[index - 1])
            {
                throw new CustomerStarException($"حد {index + 1} نجوم يجب أن يكون أكبر من حد {index} {(index == 1 ? "نجمة" : "نجوم")}.");
            }
        }

        return new CustomerStarThresholds(windowDays, one, two, three, four, five, false, null, null);
    }
}
