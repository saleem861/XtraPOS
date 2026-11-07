namespace XtraPOS.Api.Membership;

public static class MembershipCalendar
{
    private static readonly TimeZoneInfo Zone = ResolveZone();

    public static DateOnly Today(DateTime? utcNow = null)
    {
        var utc = DateTime.SpecifyKind(utcNow ?? DateTime.UtcNow, DateTimeKind.Utc);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, Zone));
    }

    public static DateTimeOffset EndOfTodayUtc(DateTime? utcNow = null)
    {
        var nextLocal = Today(utcNow).AddDays(1).ToDateTime(TimeOnly.MinValue);
        var nextUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(nextLocal, DateTimeKind.Unspecified), Zone);
        return new DateTimeOffset(DateTime.SpecifyKind(nextUtc, DateTimeKind.Utc));
    }

    private static TimeZoneInfo ResolveZone()
    {
        foreach (var id in new[] { "Asia/Hebron", "Israel Standard Time" })
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
