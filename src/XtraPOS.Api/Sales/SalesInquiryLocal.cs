namespace XtraPOS.Api.Sales;

public static class SalesInquiryLocal
{
    public static (DateTime StartUtc, DateTime EndUtc) LocalDayBounds(DateOnly from, DateOnly to, TimeZoneInfo zone)
    {
        var start = DateTime.SpecifyKind(from.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        var end = DateTime.SpecifyKind(to.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return (TimeZoneInfo.ConvertTimeToUtc(start, zone), TimeZoneInfo.ConvertTimeToUtc(end, zone));
    }

    public static int LocalOffsetMinutes(DateOnly day, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(new TimeOnly(12, 0)), DateTimeKind.Unspecified);
        return (int)zone.GetUtcOffset(local).TotalMinutes;
    }

    public static bool IsCurrentBranch(IReadOnlyList<string> localBranches, string? requested)
    {
        if (localBranches.Count <= 1 || string.IsNullOrWhiteSpace(requested)) return true;
        return localBranches.Any(name => string.Equals(name.Trim(), requested.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static string Page(string query) =>
        "SELECT page.* FROM (" + query + ") AS page WHERE page.RowNum > @skip AND page.RowNum <= @skip + @take ORDER BY page.RowNum";

    public static string FailureMessage(int number, string? detail = null)
    {
        if (number is 207 or 208) return "قاعدة هذا الجهاز لا تحتوي جداول المبيعات المحدّثة.";
        var hint = "";
        if (!string.IsNullOrWhiteSpace(detail))
        {
            hint = " " + detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (hint.Length > 140) hint = hint[..140];
        }

        return "تعذر قراءة المبيعات من قاعدة هذا الجهاز (" + number.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")." + hint;
    }
}
