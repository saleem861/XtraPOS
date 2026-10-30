namespace XtraPOS.Api.Sales;

public static class SaleCodes
{
    public const string PriceChanged = "PRICE_CHANGED";
    public const string InvalidQuantity = "INVALID_QUANTITY";
    public const string EmptyCart = "EMPTY_CART";
    public const string DeviceNotFound = "DEVICE_NOT_FOUND";
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string AssortmentRuleNotSupported = "ASSORTMENT_RULE_NOT_SUPPORTED";
}

public static class SaleStatuses
{
    public const string Draft = "Draft";
    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
    public const string CreditPending = "CreditPending";
}

public class SaleValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed class AssortmentRuleException(Guid itemId, string itemNumber, string itemName)
    : SaleValidationException(
        SaleCodes.AssortmentRuleNotSupported,
        "هذا الصنف مرتبط بتشكيلة أحجام/ألوان ولم يتم تفعيل خصم مخزون التشكيلة بعد.")
{
    public Guid ItemId { get; } = itemId;
    public string ItemNumber { get; } = itemNumber;
    public string ItemName { get; } = itemName;
}

public sealed record SalePostActor(Guid StationId, Guid DeviceId, Guid CashierUserId);

public abstract record SaleLineInput;

public sealed record FixedSaleLineInput(
    Guid ItemId,
    decimal Quantity,
    decimal ExpectedUnitPrice,
    decimal ExpectedStockOwnerQuantity) : SaleLineInput;

public sealed record VariableSaleLineInput(
    decimal EnteredScaleValue,
    decimal ExpectedFinalSellingPrice,
    decimal ExpectedReferenceBasePrice,
    decimal ExpectedWeightKg,
    decimal ExpectedStockOwnerQuantity,
    decimal Quantity = 1m) : SaleLineInput;

public sealed record SalePostCommand(
    Guid CartId,
    Guid? CustomerId,
    SalePostActor Actor,
    IReadOnlyList<SaleLineInput> Lines);

public sealed record SalePriceChange(int LineNumber, string Field, decimal Expected, decimal Authoritative);

public abstract record SalePostResult;

public sealed record SaleDraftPosted(Guid SaleId, long SaleNumber, bool IdempotentReplay) : SalePostResult;

public sealed record SalePriceChanged(IReadOnlyList<SalePriceChange> Changes) : SalePostResult;
