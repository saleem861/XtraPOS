using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class CentralFailureMessageTests
{
    [Theory]
    [InlineData(262)]
    [InlineData(15247)]
    public void PermissionDenied_IsNotReportedAsUnreachable(int number)
    {
        var message = SqlEndpointSettings.DescribeSqlFailure(number);
        Assert.Equal("تم الاتصال بالخادم، لكن حساب SQL لا يملك صلاحية إنشاء قاعدة بيانات.", message);
        Assert.DoesNotContain("تعذر الوصول إلى خادم البيانات", message);
    }

    [Fact]
    public void ExistingPhysicalFile_DoesNotExposeTheServerPath()
    {
        var message = SqlEndpointSettings.DescribeSqlFailure(5170);
        Assert.Equal("اسم قاعدة البيانات متاح، لكن توجد ملفات قاعدة بيانات قديمة بنفس الاسم على الخادم.", message);
        Assert.DoesNotContain(".mdf", message);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10060)]
    public void NetworkFailure_StaysUnreachable(int number)
    {
        Assert.Equal("تعذر الوصول إلى خادم البيانات.", SqlEndpointSettings.DescribeSqlFailure(number));
    }
}
