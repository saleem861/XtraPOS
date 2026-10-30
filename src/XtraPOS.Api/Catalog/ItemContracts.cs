using System.Text.Json.Serialization;

namespace XtraPOS.Api.Catalog;

public sealed record ItemRecord(
    Guid ItemId,
    string ItemNumber,
    string ItemName,
    string? ItemType,
    string? Classification,
    string? ProductType,
    Guid? ParentItemId,
    bool IsProhibited,
    bool IsPublishedOnline,
    bool IsActive);

public sealed record ItemUnitRecord(
    Guid ItemUnitId,
    Guid ItemId,
    decimal UnitsPerPackage,
    Guid? InternalUnitItemId,
    string? InternalUnitItemNumber,
    string? InternalUnitItemName,
    bool IsBaseUnit,
    bool IsActive);

public sealed record ItemPriceRecord(
    Guid ItemPriceId,
    Guid ItemId,
    string PriceCode,
    string PriceName,
    Guid? BranchId,
    decimal Price,
    bool IsActive);

public sealed record EffectiveItemPrice(
    Guid ItemId,
    string PriceCode,
    Guid? BranchId,
    decimal Price,
    bool IsBranchOverride);

public sealed record BranchInventoryRecord(
    Guid ItemBranchInventoryId,
    Guid BranchId,
    Guid ItemId,
    decimal? OnHandQuantity,
    bool IsQuantityInitialized,
    decimal? UnitCost,
    decimal? DailySalesRate,
    decimal? AvailabilityDays,
    DateTime? LastSaleAt,
    DateTime? LastRestockAt,
    bool IsAvailable,
    bool IsActive);

public enum ItemActivityFilter
{
    Active,
    Inactive,
    All,
}

public enum ItemSort
{
    Number,
    Name,
}

public static class ItemQuantityState
{
    public const string BranchPending = "BranchPending";
    public const string NotCounted = "NotCounted";
    public const string Known = "Known";
}

public sealed record ItemListItem(
    Guid ItemId,
    string ItemNumber,
    string ItemName,
    string? ItemType,
    string? Manufacturer,
    string? Classification,
    bool IsActive,
    bool IsProhibited,
    decimal? ResolvedPrice = null);

public sealed record ItemPageResult(
    IReadOnlyList<ItemListItem> Items,
    int TotalCount,
    int Page,
    int PageSize);

public sealed record ItemDetailView(
    Guid ItemId,
    string ItemNumber,
    string ItemName,
    string? ItemType,
    string? Manufacturer,
    string? Color,
    string? Taste,
    string? Size,
    string? Packing,
    string? Classification,
    string? CountryOfOrigin,
    string? ProductType,
    bool IsActive,
    bool IsProhibited,
    bool IsPublishedOnline,
    string? ParentItemNumber,
    string? ParentItemName);

public sealed record ItemPriceView(
    string PriceCode,
    string PriceName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? Price,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? DefaultPrice = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? BranchPrice = null);

public sealed record ItemUnitView(
    decimal UnitsPerPackage,
    string? InternalUnitItemNumber,
    string? RecordedUnitName,
    string? CurrentItemName);

public sealed record ItemUnitsView(
    IReadOnlyList<ItemUnitView> Units,
    bool OwnsPhysicalStock,
    string StockOwnerItemNumber,
    string StockOwnerItemName,
    decimal BaseQuantityFactor);

public sealed record ItemInventoryView(
    bool BranchReady,
    bool IsDerived,
    string? StockOwnerItemNumber,
    string? StockOwnerItemName,
    decimal BaseQuantityFactor,
    string QuantityState,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] decimal? OnHandQuantity);

public sealed record ItemMovementView(
    DateTime OccurredAt,
    string MovementName,
    decimal QuantityDelta,
    string? Notes,
    string? ReferenceType);

public sealed record StockMovementRecord(
    Guid StockMovementId,
    Guid BranchId,
    Guid ItemId,
    string MovementCode,
    decimal QuantityDelta,
    decimal? UnitCost,
    DateTime OccurredAt,
    string? ReferenceType,
    Guid? ReferenceId,
    Guid? MovementGroupId,
    string? Notes);
