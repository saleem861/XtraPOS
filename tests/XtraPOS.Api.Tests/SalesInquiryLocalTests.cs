using XtraPOS.Api.Sales;
using Xunit;

namespace XtraPOS.Api.Tests;

public class SalesInquiryLocalTests
{
    private static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone("Test-Plus-Two", TimeSpan.FromHours(2), "Test-Plus-Two", "Test-Plus-Two");

    [Fact]
    public void OneLocalBranchIsAlwaysTheCurrentDatabase()
    {
        Assert.True(SalesInquiryLocal.IsCurrentBranch(["Labtop"], "Labtop"));
        Assert.True(SalesInquiryLocal.IsCurrentBranch(["Labtop"], "labtop"));
        Assert.True(SalesInquiryLocal.IsCurrentBranch(["Labtop"], "فرع آخر"));
        Assert.False(SalesInquiryLocal.IsCurrentBranch(["Labtop", "مركز"], "مركز غير موجود"));
        Assert.True(SalesInquiryLocal.IsCurrentBranch(["Labtop", "مركز"], "مركز"));
    }

    [Fact]
    public void LocalDayIsComparedInUtc()
    {
        var (start, end) = SalesInquiryLocal.LocalDayBounds(new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 15), PlusTwo);
        Assert.Equal(new DateTime(2026, 11, 14, 22, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 11, 15, 22, 0, 0, DateTimeKind.Utc), end);
        Assert.Equal(120, SalesInquiryLocal.LocalOffsetMinutes(new DateOnly(2026, 11, 15), PlusTwo));
        Assert.Equal("قاعدة هذا الجهاز لا تحتوي جداول المبيعات المحدّثة.", SalesInquiryLocal.FailureMessage(208));
        Assert.Contains("9820", SalesInquiryLocal.FailureMessage(9820), StringComparison.Ordinal);
    }
}
