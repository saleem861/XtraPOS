using XtraPOS.Api.Sales;
using Xunit;

namespace XtraPOS.Api.Tests;

public class SalesInquiryLocalTests
{
    private static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone("Test-Plus-Two", TimeSpan.FromHours(2), "Test-Plus-Two", "Test-Plus-Two");

    [Fact]
    public void AnotherBranchIsNotThisDevice()
    {
        Assert.True(SalesInquiryLocal.IsDeviceBranch("Labtop", "Labtop"));
        Assert.True(SalesInquiryLocal.IsDeviceBranch("Labtop", "labtop"));
        Assert.True(SalesInquiryLocal.IsDeviceBranch("Labtop", null));
        Assert.False(SalesInquiryLocal.IsDeviceBranch("Labtop", "فرع آخر"));
    }

    [Fact]
    public void LocalDayIsComparedInUtc()
    {
        var (start, end) = SalesInquiryLocal.LocalDayBounds(new DateOnly(2026, 11, 15), new DateOnly(2026, 11, 15), PlusTwo);
        Assert.Equal(new DateTime(2026, 11, 14, 22, 0, 0, DateTimeKind.Utc), start);
        Assert.Equal(new DateTime(2026, 11, 15, 22, 0, 0, DateTimeKind.Utc), end);
        Assert.Equal(120, SalesInquiryLocal.LocalOffsetMinutes(new DateOnly(2026, 11, 15), PlusTwo));
        Assert.Equal("قاعدة هذا الجهاز لا تحتوي جداول المبيعات المحدّثة.", SalesInquiryLocal.FailureMessage(208, "Invalid column"));
        Assert.Contains("9820", SalesInquiryLocal.FailureMessage(9820), StringComparison.Ordinal);
        var page = SalesInquiryLocal.Page("SELECT 1 AS RowNum FROM dbo.Sales");
        Assert.Contains("RowNum", page, StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", page, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListsAndExportNamesStayDistinct()
    {
        Assert.Equal(["ألبان", "مخبوزات"], SalesInquiryLocal.DistinctTexts([" ألبان ", "ألبان", "مخبوزات", ""]));
        Assert.Equal("مبيعات-Labtop-12.xlsx", SalesInquiryLocal.ExportFileName("Labtop", "12"));
        Assert.Equal("مبيعات-Labtop.xlsx", SalesInquiryLocal.ExportFileName("Labtop", null));
        Assert.Equal("547 The INSERT statement conflicted.", SalesInquiryLocal.SqlFailure(547, "The INSERT statement conflicted."));
        Assert.Equal(["أرجيلة,زجاج", "ألبان"], SalesInquiryLocal.SplitClasses("أرجيلة,زجاج\u001eألبان"));
        var cost = SalesInquiryLocal.CostSql(true, true, true, true, "(itemInventory.UnitCost * line.Quantity)");
        Assert.Contains("LineCostSnapshot", cost, StringComparison.Ordinal);
        Assert.Contains("StockOwnerQuantity", cost, StringComparison.Ordinal);
        Assert.Contains("itemInventory.UnitCost", cost, StringComparison.Ordinal);
        var sold = SaleLineEconomics.FromOwnerUnit(10m, 5m, 50m, 4m, 5m);
        Assert.Equal(4m, sold.UnitCost);
        Assert.Equal(6m, sold.UnitProfit);
        Assert.Equal(20m, sold.QuantityCost);
        Assert.Equal(30m, sold.QuantityProfit);
        var from = """
            FROM dbo.SaleItems AS line
            WHERE sale.Status = N'Completed'
            """;
        var joined = SalesInquiryLocal.WithJoins(from, "        LEFT JOIN dbo.BranchStockBalances AS ownerBalance ON ownerBalance.ItemID = line.ItemID\n        LEFT JOIN dbo.ItemBranchInventory AS ownerInventory ON ownerInventory.ItemID = line.ItemID\n");
        Assert.True(joined.IndexOf("ownerBalance", StringComparison.Ordinal) < joined.IndexOf("WHERE", StringComparison.Ordinal));
        Assert.Contains("ownerInventory", joined, StringComparison.Ordinal);
    }
}
