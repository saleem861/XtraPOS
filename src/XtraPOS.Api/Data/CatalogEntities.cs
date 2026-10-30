namespace XtraPOS.Api.Data;

public sealed class Item
{
    public Guid ItemId { get; set; }
    public string ItemNumber { get; set; } = "";
    public string ItemName { get; set; } = "";
    public string ItemNameSearchStrict { get; set; } = "";
    public string ItemNameSearchLoose { get; set; } = "";
    public string? ItemType { get; set; }
    public string? Manufacturer { get; set; }
    public string? Color { get; set; }
    public string? Taste { get; set; }
    public string? Size { get; set; }
    public string? Packing { get; set; }
    public string? Classification { get; set; }
    public string? CountryOfOrigin { get; set; }
    public string? ProductType { get; set; }
    public Guid? ParentItemId { get; set; }
    public bool IsProhibited { get; set; }
    public bool IsPublishedOnline { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public Item? ParentItem { get; set; }
}

public sealed class ItemUnit
{
    public Guid ItemUnitId { get; set; }
    public Guid ItemId { get; set; }
    public decimal UnitsPerPackage { get; set; }
    public Guid? InternalUnitItemId { get; set; }
    public string? InternalUnitItemNumber { get; set; }
    public string? InternalUnitItemName { get; set; }
    public bool IsBaseUnit { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Item Item { get; set; } = null!;
    public Item? InternalUnitItem { get; set; }
}

public sealed class PriceType
{
    public Guid PriceTypeId { get; set; }
    public string PriceCode { get; set; } = "";
    public string PriceName { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class ItemPrice
{
    public Guid ItemPriceId { get; set; }
    public Guid ItemId { get; set; }
    public Guid PriceTypeId { get; set; }
    public Guid? BranchId { get; set; }
    public decimal Price { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public Item Item { get; set; } = null!;
    public PriceType PriceType { get; set; } = null!;
    public Branch? Branch { get; set; }
}

public sealed class ItemPriceHistory
{
    public Guid ItemPriceHistoryId { get; set; }
    public Guid ItemId { get; set; }
    public Guid PriceTypeId { get; set; }
    public Guid? BranchId { get; set; }
    public decimal? OldPrice { get; set; }
    public decimal NewPrice { get; set; }
    public DateTime ChangedAt { get; set; }
    public Guid? ChangedByUserId { get; set; }
    public string? ChangeSource { get; set; }
    public string? Reason { get; set; }
}

public sealed class ItemBranchInventory
{
    public Guid ItemBranchInventoryId { get; set; }
    public Guid BranchId { get; set; }
    public Guid ItemId { get; set; }
    public decimal? UnitCost { get; set; }
    public decimal? DailySalesRate { get; set; }
    public decimal? AvailabilityDays { get; set; }
    public DateTime? LastSaleAt { get; set; }
    public DateTime? LastRestockAt { get; set; }
    public bool IsAvailable { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public Branch Branch { get; set; } = null!;
    public Item Item { get; set; } = null!;
}

public sealed class StockMovementType
{
    public Guid MovementTypeId { get; set; }
    public string MovementCode { get; set; } = "";
    public string MovementName { get; set; } = "";
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class StockMovement
{
    public Guid StockMovementId { get; set; }
    public Guid BranchId { get; set; }

    /// <summary>
    /// Physical stock owner. Inventory movements post here, not against a derived package SKU.
    /// </summary>
    public Guid ItemId { get; set; }
    public Guid MovementTypeId { get; set; }
    public decimal QuantityDelta { get; set; }
    public decimal? UnitCost { get; set; }
    public DateTime OccurredAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedByUserId { get; set; }
    public string? ReferenceType { get; set; }
    public Guid? ReferenceId { get; set; }
    public Guid? MovementGroupId { get; set; }
    public string? Notes { get; set; }
    public string? SourceType { get; set; }
    public Guid? SourceDocumentId { get; set; }
    public Guid? SourceLineId { get; set; }
    public string? PostingMode { get; set; }
    public decimal? PreviousQuantity { get; set; }
    public decimal? ObservedQuantity { get; set; }
    public decimal? ResultingQuantity { get; set; }
    public decimal? ResultingUnitCost { get; set; }
    public StockMovementType MovementType { get; set; } = null!;
}

public sealed class BranchStockBalance
{
    public Guid BranchStockBalanceId { get; set; }
    public Guid BranchId { get; set; }

    /// <summary>
    /// Physical stock owner. Derived package SKUs do not get a balance row.
    /// </summary>
    public Guid ItemId { get; set; }
    public decimal? OnHandQuantity { get; set; }
    public bool IsQuantityInitialized { get; set; }
    public decimal? UnitCost { get; set; }
    public DateTime? LastRestockAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = [];
    public Branch Branch { get; set; } = null!;
    public Item Item { get; set; } = null!;
}
