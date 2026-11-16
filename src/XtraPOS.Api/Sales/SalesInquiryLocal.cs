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

    public static bool IsDeviceBranch(string? deviceBranch, string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || string.IsNullOrWhiteSpace(deviceBranch)) return true;
        return string.Equals(deviceBranch.Trim(), requested.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    public static string Page(string query) =>
        "SELECT page.* FROM (" + query + ") AS page WHERE page.RowNum > @skip AND page.RowNum <= @skip + @take ORDER BY page.RowNum";

    public static IReadOnlyList<string> DistinctTexts(IEnumerable<string> values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var value in values)
        {
            var text = value.Trim();
            if (text.Length == 0 || !seen.Add(text)) continue;
            result.Add(text);
        }

        return result;
    }

    public static string ExportFileName(string? branchName, string? branchCode)
    {
        var name = FileToken(branchName);
        var code = FileToken(branchCode);
        var label = code.Length == 0 ? name : name + "-" + code;
        if (label.Length == 0) label = "branch";
        return "مبيعات-" + label + ".xlsx";
    }

    public static string SqlFailure(int number, string? detail)
    {
        var text = number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(detail))
        {
            text += " " + detail.Replace('\r', ' ').Replace('\n', ' ').Trim();
        }

        return text.Length > 2000 ? text[..2000] : text;
    }

    private static string FileToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var chars = value.Trim().Select(ch => ch is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '-' : ch).ToArray();
        return new string(chars).Trim();
    }

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
