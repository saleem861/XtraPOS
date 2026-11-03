using Xunit;

namespace XtraPOS.Api.Tests;

public class CustomerReferenceDataTests
{
    [Fact]
    public void CentralReferenceDataInsertsMissingTypesWithoutCustomers()
    {
        var sql = Read("XtraPOS.Api.Data.Central.Migrations.016_CustomerReferenceData.sql");

        Assert.Contains("INSERT INTO dbo.PriceTypes", sql, StringComparison.Ordinal);
        Assert.Contains("N'عادي'", sql, StringComparison.Ordinal);
        Assert.Contains("N'محل تجاري'", sql, StringComparison.Ordinal);
        Assert.Contains("N'مستهلك كبير'", sql, StringComparison.Ordinal);
        Assert.Contains("existing.Name = seed.Name", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO dbo.Customers", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void CentralRetrySeedsStagingBusinessThenMissingTypes()
    {
        var sql = Read("XtraPOS.Api.Data.Central.Migrations.017_CustomerReferenceDataRetry.sql");

        Assert.Contains("DB_NAME() = N'XtraPOS_Central_Test_Staging'", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO dbo.Businesses", sql, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO dbo.PriceTypes", sql, StringComparison.Ordinal);
        Assert.Contains("N'عادي'", sql, StringComparison.Ordinal);
        Assert.Contains("existing.Name = seed.Name", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO dbo.Customers", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("XtraPOS_Central'", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void StagingReferenceAttachesTypesToSmokerWorld()
    {
        var sql = Read("XtraPOS.Api.Data.Central.Migrations.018_StagingSmokerWorldReference.sql");

        Assert.Contains("DB_NAME() = N'XtraPOS_Central_Test_Staging'", sql, StringComparison.Ordinal);
        Assert.Contains("422cda3a-498b-4eae-9eda-1db23e399ae1", sql, StringComparison.Ordinal);
        Assert.Contains("N'Smoker World'", sql, StringComparison.Ordinal);
        Assert.Contains("N'عادي'", sql, StringComparison.Ordinal);
        Assert.Contains("11111111-1111-4111-8111-111111111111", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO dbo.Customers", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BranchRetrySeedsPriceCodesThenMissingTypes()
    {
        var sql = Read("XtraPOS.Api.Data.Migrations.035_CustomerReferenceDataRetry.sql");

        Assert.Contains("INSERT INTO dbo.PriceTypes", sql, StringComparison.Ordinal);
        Assert.Contains("N'محل تجاري'", sql, StringComparison.Ordinal);
        Assert.Contains("existing.Name = seed.Name", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO dbo.Customers", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO dbo.Businesses", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BranchReferenceDataInsertsMissingTypesWithoutCustomers()
    {
        var sql = Read("XtraPOS.Api.Data.Migrations.034_CustomerReferenceData.sql");

        Assert.Contains("N'عادي'", sql, StringComparison.Ordinal);
        Assert.Contains("existing.Name = seed.Name", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO dbo.Customers", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO dbo.PriceTypes", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingBusinessReferenceDoesNotCreateABusiness()
    {
        var sql = Read("XtraPOS.Api.Data.Central.Migrations.021_CustomerReferenceForExistingBusiness.sql");
        Assert.Contains("INSERT INTO dbo.PriceTypes", sql, StringComparison.Ordinal);
        Assert.Contains("N'عادي'", sql, StringComparison.Ordinal);
        Assert.Contains("N'مستهلك كبير'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO dbo.Businesses", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT INTO dbo.Customers", sql, StringComparison.Ordinal);
    }

    private static string Read(string name)
    {
        using var stream = typeof(XtraPOS.Api.Data.Central.CentralMigrationRunner).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
