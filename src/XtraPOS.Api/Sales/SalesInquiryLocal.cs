namespace XtraPOS.Api.Sales;

public static class SalesInquiryLocal
{
    public const string NamedLocalSale = "CONVERT(datetime2, (sale.CompletedAt AT TIME ZONE 'UTC') AT TIME ZONE 'Israel Standard Time')";
    public const string MachineLocalSale = "DATEADD(minute, DATEDIFF(minute, SYSUTCDATETIME(), SYSDATETIME()), sale.CompletedAt)";

    public static bool IsCurrentBranch(IReadOnlyList<string> localBranches, string? requested)
    {
        if (localBranches.Count <= 1 || string.IsNullOrWhiteSpace(requested)) return true;
        return localBranches.Any(name => string.Equals(name.Trim(), requested.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsNamedZoneFailure(int number, string? message)
    {
        if (number is 102 or 241 or 7772 or 9826) return true;
        return message?.Contains("time zone", StringComparison.OrdinalIgnoreCase) == true;
    }

    public static string FailureMessage(int number) => number switch
    {
        207 or 208 => "قاعدة هذا الجهاز لا تحتوي جداول المبيعات المحدّثة.",
        _ => "تعذر قراءة المبيعات من قاعدة هذا الجهاز.",
    };
}
