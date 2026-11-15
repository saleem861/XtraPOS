using XtraPOS.Api.Sales;
using Xunit;

namespace XtraPOS.Api.Tests;

public class SalesInquiryLocalTests
{
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
    public void NamedZoneFailureFallsBackToTheMachineClock()
    {
        Assert.True(SalesInquiryLocal.IsNamedZoneFailure(102, "Incorrect syntax near 'AT'."));
        Assert.True(SalesInquiryLocal.IsNamedZoneFailure(50000, "The time zone parameter is invalid."));
        Assert.False(SalesInquiryLocal.IsNamedZoneFailure(207, "Invalid column name 'PriceOverrideMode'."));
        Assert.Equal("قاعدة هذا الجهاز لا تحتوي جداول المبيعات المحدّثة.", SalesInquiryLocal.FailureMessage(208));
    }
}
