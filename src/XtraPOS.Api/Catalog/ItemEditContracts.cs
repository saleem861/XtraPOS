namespace XtraPOS.Api.Catalog;

public sealed class UpdateItemMasterRequest
{
    public string? ItemNumber { get; set; }
    public string? ItemName { get; set; }
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
    public bool IsActive { get; set; }
    public bool IsProhibited { get; set; }
    public bool IsPublishedOnline { get; set; }
    public string? RowVersion { get; set; }
}

public sealed class ItemUnitWrite
{
    public Guid? ItemUnitId { get; set; }
    public decimal UnitsPerPackage { get; set; }
    public Guid? InternalUnitItemId { get; set; }
    public string? InternalUnitItemNumber { get; set; }
    public string? InternalUnitItemName { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed class UpdateItemUnitsRequest
{
    public string? RowVersion { get; set; }
    public List<ItemUnitWrite>? Units { get; set; }
}

public sealed class UpdateDefaultPriceRequest
{
    public string? PriceCode { get; set; }
    public decimal Price { get; set; }
    public string? RowVersion { get; set; }
}

public sealed class UpdateBranchPriceRequest
{
    public string? PriceCode { get; set; }
    public decimal? Price { get; set; }
}

public sealed class UpdateBranchStateRequest
{
    public decimal? UnitCost { get; set; }
    public bool IsAvailable { get; set; }
    public bool IsActive { get; set; } = true;
}

public sealed record ItemWriteResponse(string RowVersion, long? ChangeVersion);

public sealed record ItemEditForm(
    string Topology,
    bool CentralReachable,
    bool MasterReadOnly,
    string? MasterReadOnlyMessage,
    bool CanEditMaster,
    bool CanEditBranchState,
    bool CanEditBranchPrice,
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
    Guid? ParentItemId,
    bool IsActive,
    bool IsProhibited,
    bool IsPublishedOnline,
    string? RowVersion,
    decimal? UnitCost,
    bool IsAvailable,
    bool BranchActive,
    IReadOnlyList<ItemEditPrice> Prices);

public sealed record ItemEditPrice(
    string PriceCode,
    string PriceName,
    decimal? DefaultPrice,
    decimal? BranchPrice,
    string? DefaultRowVersion);

public sealed class ItemEditException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
