namespace XtraPOS.Api.Customers;

public static class CustomerActions
{
    public const string Created = "CustomerCreated";
    public const string Updated = "CustomerUpdated";
    public const string Activated = "CustomerActivated";
    public const string Deactivated = "CustomerDeactivated";
}

public sealed class CustomerMasterException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed record CustomerListFilter(
    string? Search,
    bool? IsActive,
    Guid? ClassificationId,
    Guid? PriceTypeId,
    int Page,
    int PageSize);

public sealed record CustomerPriceTypeRecord(Guid PriceTypeId, string PriceCode, string PriceName);

public sealed record CustomerClassificationRecord(
    Guid CustomerClassificationId,
    Guid BusinessId,
    string? Code,
    string Name,
    Guid? DefaultPriceTypeId,
    decimal DefaultVariableValueDiscountPercent,
    bool IsDefault,
    bool IsActive);

public sealed record CustomerRecord(
    Guid CustomerId,
    Guid BusinessId,
    string? CustomerNumber,
    string Name,
    string? Phone,
    string? NormalizedPhone,
    string? SecondaryPhone,
    string? SecondaryNormalizedPhone,
    Guid? CustomerClassificationId,
    Guid? PriceTypeId,
    decimal VariableValueDiscountPercent,
    bool IsActive,
    string? Notes,
    string RowVersion,
    string? ClassificationName = null,
    string? PriceCode = null,
    string? WhatsAppPhoneE164 = null,
    string? PhoneVerificationStatus = null);

public sealed record CustomerDuplicateMatch(Guid CustomerId, string Name, string? CustomerNumber);

public sealed record CustomerDuplicateWarning(
    IReadOnlyList<string> NormalizedPhones,
    IReadOnlyList<CustomerDuplicateMatch> Customers);

public sealed record CustomerMutationResult(CustomerRecord Customer, CustomerDuplicateWarning? DuplicatePhoneWarning);

public sealed record CustomerPage(
    string Authority,
    int TotalCount,
    int Page,
    int PageSize,
    IReadOnlyList<CustomerRecord> Customers,
    string? SyncState = null);

public sealed record CustomerClassificationPage(string Authority, IReadOnlyList<CustomerClassificationRecord> Classifications);

public sealed class CustomerWriteRequest
{
    public string? CustomerNumber { get; set; }
    public string? Name { get; set; }
    public string? Phone { get; set; }
    public string? SecondaryPhone { get; set; }
    public Guid? CustomerClassificationId { get; set; }
    public Guid? PriceTypeId { get; set; }
    public decimal? VariableValueDiscountPercent { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public string? RowVersion { get; set; }
    public string? WhatsAppCountryCode { get; set; }
    public bool WhatsAppConsent { get; set; }
    public Guid? VerificationId { get; set; }
    public Guid? PasskeySessionId { get; set; }
    public List<CustomerSpecialPriceDraft>? SpecialPrices { get; set; }
}

public sealed class CustomerSpecialPriceDraft
{
    public string? ItemNumber { get; set; }
    public decimal MinimumQuantity { get; set; } = 1m;
    public decimal BundlePrice { get; set; }
}

public sealed record CustomerSyncClassification(
    Guid CustomerClassificationId,
    Guid BusinessId,
    string? Code,
    string Name,
    Guid? DefaultPriceTypeId,
    string? DefaultPriceCode,
    decimal DefaultVariableValueDiscountPercent,
    bool IsDefault,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record CustomerSyncPayload(
    Guid CustomerId,
    Guid BusinessId,
    string? CustomerNumber,
    string Name,
    string? Phone,
    string? NormalizedPhone,
    string? SecondaryPhone,
    string? SecondaryNormalizedPhone,
    Guid? CustomerClassificationId,
    Guid? PriceTypeId,
    string? PriceCode,
    decimal VariableValueDiscountPercent,
    bool IsActive,
    string? Notes,
    string? LegacySource,
    string? LegacyCustomerKey,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    CustomerSyncClassification? Classification,
    string? WhatsAppPhoneE164 = null,
    string? PhoneVerificationStatus = null,
    DateTime? PhoneVerifiedAt = null,
    string? PhoneVerifiedE164 = null);
