namespace XtraPOS.Api.Catalog;

/// <summary>
/// Package speed is derived from the internal unit. It is not stored on the item
/// and it is not calculated by a SQL trigger.
/// PackageDailySalesRate = InternalUnitDailySalesRate / UnitsPerPackage.
/// </summary>
public static class PackageSalesRate
{
    public static decimal PackageDailySalesRate(decimal internalUnitDailySalesRate, decimal unitsPerPackage)
    {
        if (unitsPerPackage <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(unitsPerPackage), "Units per package must be greater than zero.");
        }

        return internalUnitDailySalesRate / unitsPerPackage;
    }
}
