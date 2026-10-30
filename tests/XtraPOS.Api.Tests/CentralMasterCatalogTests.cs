using XtraPOS.Api.Import;
using XtraPOS.Api.Security;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class CentralMasterCatalogTests
{
    [Fact]
    public void Catalog_ExcludesTheTechnicalRow_AndKeepsNonNumericNumbers()
    {
        var plan = CentralMasterCatalog.Plan(
        [
            Item(LegacyCatalogs.Shop1, "G1535", "رمز"),
            Item(LegacyCatalogs.Shop1, "ؤ62198011005", "باركود"),
            Item(LegacyCatalogs.Shop1, CentralMasterCatalog.TechnicalItemNumber, "اختبار"),
            Item(LegacyCatalogs.Shop2, CentralMasterCatalog.TechnicalItemNumber, "اختبار"),
            Item(LegacyCatalogs.Central, "   ", "فارغ"),
        ],
        []);

        Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "G1535", "ؤ62198011005" }, plan.Items.Select(item => item.ItemNumber).ToHashSet(StringComparer.Ordinal));
        Assert.Equal(2, plan.TechnicalRowsExcluded);
        Assert.Equal(1, plan.TechnicalIdentitiesExcluded);
        Assert.Equal(1, plan.BlankItemRows);
        Assert.Equal(2, plan.ActiveItems);
        Assert.Equal(0, plan.InactiveItems);
        Assert.Equal("saleemPOSDB then Shop1 then Shop2 then swstore", CentralMasterCatalog.PrecedenceText);
    }

    [Fact]
    public void DescriptivePrecedence_UsesSaleemPosThenTheShops()
    {
        var plan = CentralMasterCatalog.Plan(
        [
            Item(LegacyCatalogs.Central, "1087", "من المركز"),
            Item(LegacyCatalogs.Shop1, "1087", "من الفرع"),
            Item(LegacyCatalogs.Shop1, "1300", "من المتجر", unitNumber: "1360", factor: 4, unitName: "معسل فرنسي  وقية"),
            Item(LegacyCatalogs.Swstore, "1360", "وقية", unitNumber: "1360", factor: 1, unitName: "نص كيلو معسل سلطان فرنسي"),
            Item(LegacyCatalogs.Central, "900", "قديم فقط"),
        ],
        []);

        Assert.Equal("من المركز", plan.Items.Single(item => item.ItemNumber == "1087").ItemName);
        Assert.Equal("saleemPOSDB", plan.Items.Single(item => item.ItemNumber == "1087").DescriptiveSource);
        Assert.Equal("من المتجر", plan.Items.Single(item => item.ItemNumber == "1300").ItemName);
        Assert.False(plan.Items.Single(item => item.ItemNumber == "900").IsActive);
        Assert.Contains(plan.Units, unit => unit.ItemNumber == "1300" && unit.InternalUnitItemNumber == "1360" && unit.UnitsPerPackage == 4 && unit.InternalUnitItemName == "معسل فرنسي  وقية");
        Assert.DoesNotContain(plan.Units, unit => unit.ItemNumber == "1360");
        Assert.Equal(1, plan.SelfLinks);
    }

    [Fact]
    public void UnitsAndPrices_SkipOrphansSelfLinksAndNegativeMasterPrices()
    {
        var plan = CentralMasterCatalog.Plan(
        [
            Item(LegacyCatalogs.Central, "1", "أساس", dealer: 0, major: -2),
            Item(LegacyCatalogs.Shop1, "1", "أساس", dealer: 9),
            Item(LegacyCatalogs.Shop1, "2", "علبة", unitNumber: "1", factor: 4, unitName: "الاسم المخزن"),
            Item(LegacyCatalogs.Shop1, "3", "يتيم", unitNumber: "999", factor: 2, unitName: "غير موجود"),
            Item(LegacyCatalogs.Shop1, "4", "دورة", unitNumber: "5", factor: 2),
            Item(LegacyCatalogs.Shop1, "5", "عكس", unitNumber: "4", factor: 3),
        ],
        []);

        Assert.Contains(plan.Prices, price => price.ItemNumber == "1" && price.PriceCode == "Dealer" && price.Price == 0);
        Assert.DoesNotContain(plan.Prices, price => price.Price < 0);
        Assert.Equal(1, plan.NegativeMasterPricesExcluded);
        Assert.Equal(1, plan.ZeroPrices);
        Assert.Equal(1, plan.ShopPricesIgnored);
        Assert.Contains(plan.Units, unit => unit.ItemNumber == "2" && unit.InternalUnitItemName == "الاسم المخزن");
        Assert.Equal(1, plan.OrphanLinksSkipped);
        Assert.Equal(1, plan.CycleLinksSkipped);
        Assert.DoesNotContain(plan.Items, item => item.ItemNumber == "999");
    }

    [Fact]
    public void DuplicateRows_KeepTheMoreCompleteRow()
    {
        var sparse = Item(LegacyCatalogs.Central, "72900434", "نفس الاسم");
        var complete = Item(LegacyCatalogs.Central, "72900434", "نفس الاسم", origin: "فرنسا", dealer: 5);
        var plan = CentralMasterCatalog.Plan([sparse, complete], []);
        Assert.Equal("فرنسا", plan.Items.Single().CountryOfOrigin);
        Assert.Contains(plan.DuplicateChoices, choice => choice.ItemNumber == "72900434" && choice.Reason.Contains("more complete", StringComparison.Ordinal));
    }

    [Fact]
    public void Users_MapTheThreeRoles_AndKeepEndedEmployees()
    {
        var plan = CentralMasterCatalog.Plan(
            [],
            [
                Person("905017083", "سليم جيوسي", "سليم أحمد سليم جيوسي", null),
                Person("402120067", "طارق دعاس", "طارق نزار يوسف دعاس", null),
                Person("421600008", "عبدالله ريان", "عبدالله فريد عبدالله ريان", null),
                Person("100", "موظف", "موظف كامل", null),
                Person("200", "", "اسم كامل", new DateTime(2020, 1, 1)),
            ]);

        Assert.Equal(RoleCodes.Admin, plan.Users.Single(user => user.EmployeeNumber == "905017083").RoleCode);
        Assert.Equal(RoleCodes.Supervisor, plan.Users.Single(user => user.EmployeeNumber == "402120067").RoleCode);
        Assert.Equal(RoleCodes.Supervisor, plan.Users.Single(user => user.EmployeeNumber == "421600008").RoleCode);
        Assert.Equal(RoleCodes.User, plan.Users.Single(user => user.EmployeeNumber == "100").RoleCode);
        var ended = plan.Users.Single(user => user.EmployeeNumber == "200");
        Assert.False(ended.IsActive);
        Assert.Equal("اسم كامل", ended.DisplayName);
        Assert.Equal(4, plan.ActiveUsers);
        Assert.Equal(1, plan.InactiveUsers);
        Assert.DoesNotContain(plan.Anomalies, anomaly => anomaly.Contains("was not found", StringComparison.Ordinal));
    }

    private static LegacyItemObservation Item(
        string source,
        string number,
        string name,
        string? unitNumber = null,
        decimal? factor = null,
        string? unitName = null,
        string? origin = null,
        decimal? dealer = null,
        decimal? major = null) =>
        new(source, number, name, CountryOfOrigin: origin, UnitsPerPackage: factor, InternalUnitItemNumber: unitNumber, InternalUnitItemName: unitName, DealerPrice: dealer, MajorCustomerPrice: major);

    private static LegacyEmployeeRecord Person(string id, string shortName, string fullName, DateTime? end) =>
        new(id, shortName, fullName, null, null, null, null, null, null, null, null, end);
}
