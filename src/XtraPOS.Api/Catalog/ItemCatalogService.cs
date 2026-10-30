using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Catalog;

public sealed class ItemCatalogService(BranchDbContext db)
{
    public async Task<ItemRecord?> GetItemByIdAsync(Guid itemId, CancellationToken cancellationToken)
    {
        return await db.Items
            .AsNoTracking()
            .Where(item => item.ItemId == itemId)
            .Select(ItemProjection)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<ItemRecord?> GetItemByNumberAsync(string itemNumber, CancellationToken cancellationToken)
    {
        var number = itemNumber.Trim();
        return await db.Items
            .AsNoTracking()
            .Where(item => item.ItemNumber == number)
            .Select(ItemProjection)
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ItemRecord>> SearchItemsAsync(string? term, CancellationToken cancellationToken)
    {
        var query = db.Items.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(term))
        {
            var text = term.Trim();
            query = query.Where(item => item.ItemNumber.Contains(text) || item.ItemName.Contains(text));
        }

        return await query
            .OrderBy(item => item.ItemNumber)
            .Take(50)
            .Select(ItemProjection)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ItemUnitRecord>> GetItemUnitsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        return await db.ItemUnits
            .AsNoTracking()
            .Where(unit => unit.ItemId == itemId)
            .OrderBy(unit => unit.InternalUnitItemNumber)
            .Select(unit => new ItemUnitRecord(
                unit.ItemUnitId,
                unit.ItemId,
                unit.UnitsPerPackage,
                unit.InternalUnitItemId,
                unit.InternalUnitItemNumber,
                unit.InternalUnitItemName,
                unit.IsBaseUnit,
                unit.IsActive))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ItemPriceRecord>> GetItemPricesAsync(Guid itemId, CancellationToken cancellationToken)
    {
        return await db.ItemPrices
            .AsNoTracking()
            .Where(price => price.ItemId == itemId)
            .OrderBy(price => price.PriceType.SortOrder)
            .ThenBy(price => price.BranchId)
            .Select(price => new ItemPriceRecord(
                price.ItemPriceId,
                price.ItemId,
                price.PriceType.PriceCode,
                price.PriceType.PriceName,
                price.BranchId,
                price.Price,
                price.IsActive))
            .ToListAsync(cancellationToken);
    }

    public async Task<EffectiveItemPrice?> GetEffectiveItemPriceAsync(
        Guid itemId,
        string priceCode,
        Guid? branchId,
        CancellationToken cancellationToken)
    {
        var code = priceCode.Trim();
        var prices = await db.ItemPrices
            .AsNoTracking()
            .Where(price => price.ItemId == itemId && price.IsActive && price.PriceType.PriceCode == code && price.PriceType.IsActive)
            .Where(price => price.BranchId == null || price.BranchId == branchId)
            .Select(price => new { price.BranchId, price.Price })
            .ToListAsync(cancellationToken);

        if (branchId is Guid branch)
        {
            var branchPrice = prices.SingleOrDefault(price => price.BranchId == branch);
            if (branchPrice is not null)
            {
                return new EffectiveItemPrice(itemId, code, branch, branchPrice.Price, true);
            }
        }

        var master = prices.SingleOrDefault(price => price.BranchId is null);
        return master is null
            ? null
            : new EffectiveItemPrice(itemId, code, null, master.Price, false);
    }

    public async Task<BranchInventoryRecord?> GetBranchInventoryAsync(
        Guid branchId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        var row = await db.ItemBranchInventory
            .AsNoTracking()
            .Where(inventory => inventory.BranchId == branchId && inventory.ItemId == itemId)
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var links = await db.ItemUnits
            .AsNoTracking()
            .Where(unit => unit.IsActive && unit.InternalUnitItemId != null && unit.UnitsPerPackage > 0)
            .Select(unit => new StockUnitLink(unit.ItemId, unit.InternalUnitItemId!.Value, unit.UnitsPerPackage))
            .ToListAsync(cancellationToken);
        var resolution = StockOwnership.Resolve(itemId, links);
        var balance = await db.BranchStockBalances
            .AsNoTracking()
            .SingleOrDefaultAsync(
                stock => stock.BranchId == branchId && stock.ItemId == resolution.StockOwnerItemId,
                cancellationToken);
        var quantity = balance is { IsQuantityInitialized: true }
            ? resolution.DerivedQuantity(balance.OnHandQuantity)
            : null;
        return new BranchInventoryRecord(
            row.ItemBranchInventoryId,
            row.BranchId,
            row.ItemId,
            quantity,
            balance?.IsQuantityInitialized ?? false,
            row.UnitCost,
            row.DailySalesRate,
            row.AvailabilityDays,
            row.LastSaleAt,
            row.LastRestockAt,
            row.IsAvailable,
            row.IsActive);
    }

    public async Task<IReadOnlyList<StockMovementRecord>> GetStockMovementHistoryAsync(
        Guid branchId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        return await db.StockMovements
            .AsNoTracking()
            .Where(movement => movement.BranchId == branchId && movement.ItemId == itemId)
            .OrderBy(movement => movement.OccurredAt)
            .ThenBy(movement => movement.StockMovementId)
            .Select(movement => new StockMovementRecord(
                movement.StockMovementId,
                movement.BranchId,
                movement.ItemId,
                movement.MovementType.MovementCode,
                movement.QuantityDelta,
                movement.UnitCost,
                movement.OccurredAt,
                movement.ReferenceType,
                movement.ReferenceId,
                movement.MovementGroupId,
                movement.Notes))
            .ToListAsync(cancellationToken);
    }

    public async Task<ItemPageResult> SearchPageAsync(
        string? search,
        ItemActivityFilter activity,
        int page,
        int pageSize,
        ItemSort sort,
        CancellationToken cancellationToken)
    {
        page = page < 1 ? 1 : page;
        if (pageSize is < 1 or > 100)
        {
            pageSize = 50;
        }

        var query = db.Items.AsNoTracking();
        query = activity switch
        {
            ItemActivityFilter.Inactive => query.Where(item => !item.IsActive),
            ItemActivityFilter.All => query,
            _ => query.Where(item => item.IsActive),
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var text = search.Trim();
            var strict = ArabicSearchNormalizer.NormalizeStrict(text);
            var loose = ArabicSearchNormalizer.NormalizeLoose(text);
            var digits = ArabicSearchNormalizer.NormalizeDigits(text);
            query = query.Where(item =>
                item.ItemNumber == text
                || item.ItemNumber.Contains(text)
                || (digits != text && (item.ItemNumber == digits || item.ItemNumber.Contains(digits)))
                || (strict != "" && (item.ItemNameSearchStrict == strict
                    || item.ItemNameSearchStrict.StartsWith(strict)
                    || item.ItemNameSearchStrict.Contains(strict)
                    || item.ItemNameSearchLoose == loose
                    || item.ItemNameSearchLoose.StartsWith(loose)
                    || item.ItemNameSearchLoose.Contains(loose))));
            query = query.OrderBy(item => item.ItemNumber == text || item.ItemNumber == digits ? 0
                : item.ItemName == text ? 1
                : item.ItemNameSearchStrict == strict ? 2
                : item.ItemNameSearchStrict.StartsWith(strict) ? 3
                : item.ItemNameSearchStrict.Contains(strict) ? 4
                : item.ItemNameSearchLoose == loose || item.ItemNameSearchLoose.StartsWith(loose) ? 5
                : 6)
                .ThenBy(item => sort == ItemSort.Name ? item.ItemName : item.ItemNumber)
                .ThenBy(item => item.ItemNumber);
        }
        else
        {
            query = sort == ItemSort.Name
                ? query.OrderBy(item => item.ItemName).ThenBy(item => item.ItemNumber)
                : query.OrderBy(item => item.ItemNumber);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(item => new ItemListItem(
                item.ItemId,
                item.ItemNumber,
                item.ItemName,
                item.ItemType,
                item.Manufacturer,
                item.Classification,
                item.IsActive,
                item.IsProhibited,
                null))
            .ToListAsync(cancellationToken);

        return new ItemPageResult(items, totalCount, page, pageSize);
    }

    public async Task<ItemDetailView?> GetItemDetailAsync(Guid itemId, CancellationToken cancellationToken)
    {
        return await (
            from item in db.Items.AsNoTracking()
            where item.ItemId == itemId
            join parent in db.Items.AsNoTracking() on item.ParentItemId equals parent.ItemId into parents
            from parent in parents.DefaultIfEmpty()
            select new ItemDetailView(
                item.ItemId,
                item.ItemNumber,
                item.ItemName,
                item.ItemType,
                item.Manufacturer,
                item.Color,
                item.Taste,
                item.Size,
                item.Packing,
                item.Classification,
                item.CountryOfOrigin,
                item.ProductType,
                item.IsActive,
                item.IsProhibited,
                item.IsPublishedOnline,
                parent == null ? null : parent.ItemNumber,
                parent == null ? null : parent.ItemName))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ItemPriceView>?> GetDefaultPriceViewsAsync(
        Guid itemId,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        if (!await db.Items.AsNoTracking().AnyAsync(item => item.ItemId == itemId, cancellationToken))
        {
            return null;
        }

        var prices = await db.ItemPrices
            .AsNoTracking()
            .Where(price => price.ItemId == itemId && price.IsActive && (price.BranchId == null || price.BranchId == branchId))
            .Select(price => new { price.PriceTypeId, price.BranchId, price.Price })
            .ToListAsync(cancellationToken);

        var types = await db.PriceTypes
            .AsNoTracking()
            .Where(type => type.IsActive)
            .OrderBy(type => type.SortOrder)
            .Select(type => new { type.PriceTypeId, type.PriceCode, type.PriceName })
            .ToListAsync(cancellationToken);

        return types
            .Select(type =>
            {
                var master = prices.SingleOrDefault(price => price.PriceTypeId == type.PriceTypeId && price.BranchId is null);
                var branch = prices.SingleOrDefault(price => price.PriceTypeId == type.PriceTypeId && price.BranchId == branchId);
                decimal? defaultPrice = master is null ? null : master.Price;
                decimal? branchPrice = branch is null ? null : branch.Price;
                return new ItemPriceView(type.PriceCode, type.PriceName, defaultPrice, defaultPrice, branchPrice);
            })
            .ToList();
    }

    public async Task<ItemUnitsView?> GetUnitViewsAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var item = await db.Items
            .AsNoTracking()
            .Where(candidate => candidate.ItemId == itemId)
            .Select(candidate => new { candidate.ItemId })
            .SingleOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            return null;
        }

        var links = await ActiveLinksAsync(cancellationToken);
        var resolution = StockOwnership.Resolve(itemId, links);
        var owner = await OwnerNameAsync(resolution.StockOwnerItemId, cancellationToken);
        var units = await (
            from unit in db.ItemUnits.AsNoTracking()
            where unit.ItemId == itemId && unit.IsActive && unit.InternalUnitItemId != null && unit.InternalUnitItemId != unit.ItemId
            join target in db.Items.AsNoTracking() on unit.InternalUnitItemId equals target.ItemId into targets
            from target in targets.DefaultIfEmpty()
            orderby unit.InternalUnitItemNumber
            select new ItemUnitView(
                unit.UnitsPerPackage,
                unit.InternalUnitItemNumber,
                unit.InternalUnitItemName,
                target == null ? null : target.ItemName))
            .ToListAsync(cancellationToken);

        return new ItemUnitsView(
            units,
            resolution.OwnsPhysicalStock,
            owner.Number,
            owner.Name,
            resolution.BaseQuantityFactor);
    }

    public async Task<ItemInventoryView?> GetInventoryViewAsync(
        Guid branchId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        if (!await db.Items.AsNoTracking().AnyAsync(item => item.ItemId == itemId, cancellationToken))
        {
            return null;
        }

        var links = await ActiveLinksAsync(cancellationToken);
        var resolution = StockOwnership.Resolve(itemId, links);
        var owner = await OwnerNameAsync(resolution.StockOwnerItemId, cancellationToken);
        var branchReady = await db.BranchStockBalances
            .AsNoTracking()
            .AnyAsync(balance => balance.BranchId == branchId, cancellationToken);
        var balance = await db.BranchStockBalances
            .AsNoTracking()
            .SingleOrDefaultAsync(
                stock => stock.BranchId == branchId && stock.ItemId == resolution.StockOwnerItemId,
                cancellationToken);

        string state;
        decimal? quantity;
        if (!branchReady)
        {
            state = ItemQuantityState.BranchPending;
            quantity = null;
        }
        else if (balance is not { IsQuantityInitialized: true })
        {
            state = ItemQuantityState.NotCounted;
            quantity = null;
        }
        else
        {
            state = ItemQuantityState.Known;
            quantity = resolution.DerivedQuantity(balance.OnHandQuantity);
        }

        return new ItemInventoryView(
            branchReady,
            !resolution.OwnsPhysicalStock,
            resolution.OwnsPhysicalStock ? null : owner.Number,
            resolution.OwnsPhysicalStock ? null : owner.Name,
            resolution.BaseQuantityFactor,
            state,
            quantity);
    }

    public async Task<IReadOnlyList<ItemMovementView>?> GetMovementViewsAsync(
        Guid branchId,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        if (!await db.Items.AsNoTracking().AnyAsync(item => item.ItemId == itemId, cancellationToken))
        {
            return null;
        }

        var links = await ActiveLinksAsync(cancellationToken);
        var resolution = StockOwnership.Resolve(itemId, links);
        return await db.StockMovements
            .AsNoTracking()
            .Where(movement => movement.BranchId == branchId && movement.ItemId == resolution.StockOwnerItemId)
            .OrderByDescending(movement => movement.OccurredAt)
            .ThenByDescending(movement => movement.StockMovementId)
            .Take(100)
            .Select(movement => new ItemMovementView(
                movement.OccurredAt,
                movement.MovementType.MovementName,
                movement.QuantityDelta,
                movement.Notes,
                movement.ReferenceType))
            .ToListAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<StockUnitLink>> ActiveLinksAsync(CancellationToken cancellationToken)
    {
        return await db.ItemUnits
            .AsNoTracking()
            .Where(unit => unit.IsActive && unit.InternalUnitItemId != null && unit.UnitsPerPackage > 0)
            .Select(unit => new StockUnitLink(unit.ItemId, unit.InternalUnitItemId!.Value, unit.UnitsPerPackage))
            .ToListAsync(cancellationToken);
    }

    private async Task<(string Number, string Name)> OwnerNameAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var owner = await db.Items
            .AsNoTracking()
            .Where(item => item.ItemId == itemId)
            .Select(item => new { item.ItemNumber, item.ItemName })
            .SingleAsync(cancellationToken);
        return (owner.ItemNumber, owner.ItemName);
    }

    private static readonly Expression<Func<Item, ItemRecord>> ItemProjection = item =>
        new ItemRecord(
            item.ItemId,
            item.ItemNumber,
            item.ItemName,
            item.ItemType,
            item.Classification,
            item.ProductType,
            item.ParentItemId,
            item.IsProhibited,
            item.IsPublishedOnline,
            item.IsActive);
}
