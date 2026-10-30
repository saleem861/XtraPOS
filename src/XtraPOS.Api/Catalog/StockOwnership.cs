namespace XtraPOS.Api.Catalog;

public sealed class StockOwnershipException(string message) : InvalidOperationException(message);

public sealed record StockUnitLink(Guid ItemId, Guid InternalUnitItemId, decimal UnitsPerPackage);

public sealed record StockOwnerResolution(Guid SellableItemId, Guid StockOwnerItemId, decimal BaseQuantityFactor)
{
    public bool OwnsPhysicalStock => SellableItemId == StockOwnerItemId;

    public decimal? DerivedQuantity(decimal? ownerOnHandQuantity) =>
        ownerOnHandQuantity is null ? null : ownerOnHandQuantity.Value / BaseQuantityFactor;
}

public sealed record PhysicalQuantity(decimal? OnHandQuantity, bool IsQuantityInitialized)
{
    public static PhysicalQuantity Pending { get; } = new(null, false);

    public static PhysicalQuantity Known(decimal onHandQuantity) => new(onHandQuantity, true);

    public bool IsKnownZero => IsQuantityInitialized && OnHandQuantity == 0m;
}

public sealed record StockMovementPlan(
    Guid SellableItemId,
    decimal SellableQuantity,
    Guid StockOwnerItemId,
    decimal QuantityDelta);

public static class StockOwnership
{
    public static StockOwnerResolution Resolve(Guid sellableItemId, IReadOnlyList<StockUnitLink> links)
    {
        var next = SingleLinks(links);
        var factor = 1m;
        var current = sellableItemId;
        var seen = new HashSet<Guid>();
        while (true)
        {
            if (!seen.Add(current))
            {
                throw new StockOwnershipException($"Stock ownership for {sellableItemId} is circular.");
            }

            if (!next.TryGetValue(current, out var link) || link.InternalUnitItemId == current)
            {
                return new StockOwnerResolution(sellableItemId, current, factor);
            }

            factor *= link.UnitsPerPackage;
            current = link.InternalUnitItemId;
        }
    }

    public static StockMovementPlan PlanSale(StockOwnerResolution resolution, decimal soldQuantity) =>
        new(
            resolution.SellableItemId,
            soldQuantity,
            resolution.StockOwnerItemId,
            -(soldQuantity * resolution.BaseQuantityFactor));

    public static StockMovementPlan PlanReceipt(StockOwnerResolution resolution, decimal purchasedQuantity) =>
        new(
            resolution.SellableItemId,
            purchasedQuantity,
            resolution.StockOwnerItemId,
            purchasedQuantity * resolution.BaseQuantityFactor);

    public static IReadOnlyList<Guid> SelectPhysicalStockItems(
        IEnumerable<Guid> itemIds,
        IReadOnlyList<StockUnitLink> links)
    {
        return itemIds
            .Select(itemId => Resolve(itemId, links))
            .Where(resolution => resolution.OwnsPhysicalStock)
            .Select(resolution => resolution.StockOwnerItemId)
            .Distinct()
            .ToList();
    }

    public static decimal PhysicalInventoryValue(
        IEnumerable<(Guid ItemId, decimal? OnHandQuantity, decimal? UnitCost)> balances,
        IReadOnlyList<StockUnitLink> links)
    {
        var owners = SelectPhysicalStockItems(balances.Select(balance => balance.ItemId), links).ToHashSet();
        return balances
            .Where(balance => owners.Contains(balance.ItemId) && balance.OnHandQuantity is not null && balance.UnitCost is not null)
            .Sum(balance => balance.OnHandQuantity!.Value * balance.UnitCost!.Value);
    }

    private static Dictionary<Guid, StockUnitLink> SingleLinks(IReadOnlyList<StockUnitLink> links)
    {
        var result = new Dictionary<Guid, StockUnitLink>();
        foreach (var group in links.GroupBy(link => link.ItemId))
        {
            var outward = group.Where(link => link.InternalUnitItemId != link.ItemId).ToList();
            if (outward.Count == 0)
            {
                continue;
            }

            if (outward.Select(link => link.InternalUnitItemId).Distinct().Count() != 1
                || outward.Select(link => link.UnitsPerPackage).Distinct().Count() != 1)
            {
                throw new StockOwnershipException($"Item {group.Key} has more than one stock-owner link.");
            }

            var chosen = outward[0];
            if (chosen.UnitsPerPackage <= 0)
            {
                throw new StockOwnershipException($"Item {group.Key} has an invalid stock conversion factor.");
            }

            result[group.Key] = chosen;
        }

        return result;
    }
}
