namespace XtraPOS.Api.Sales;

public readonly record struct SaleLineMoney(decimal? UnitCost, decimal? UnitProfit, decimal? QuantityCost, decimal? QuantityProfit)
{
    public static SaleLineMoney Empty { get; } = new(null, null, null, null);
}

public static class SaleLineEconomics
{
    public static SaleLineMoney FromOwnerUnit(decimal unitPrice, decimal quantity, decimal lineTotal, decimal? ownerUnitCost, decimal ownerQuantity)
    {
        if (ownerUnitCost is null)
        {
            return SaleLineMoney.Empty;
        }

        var quantityCost = decimal.Round(ownerUnitCost.Value * ownerQuantity, 4, MidpointRounding.AwayFromZero);
        return FromQuantityCost(unitPrice, quantity, lineTotal, quantityCost);
    }

    public static SaleLineMoney FromQuantityCost(decimal unitPrice, decimal quantity, decimal lineTotal, decimal? quantityCost)
    {
        if (quantityCost is null || quantity == 0m)
        {
            return SaleLineMoney.Empty;
        }

        var unitCost = decimal.Round(quantityCost.Value / quantity, 4, MidpointRounding.AwayFromZero);
        return new SaleLineMoney(
            unitCost,
            decimal.Round(unitPrice - unitCost, 4, MidpointRounding.AwayFromZero),
            quantityCost,
            decimal.Round(lineTotal - quantityCost.Value, 4, MidpointRounding.AwayFromZero));
    }
}
