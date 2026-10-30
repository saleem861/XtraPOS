namespace XtraPOS.Api.Import;

public sealed record ApprovedClassificationSpec(
    string Code,
    string Name,
    string PriceTypeCode,
    decimal DefaultVariableValueDiscountPercent,
    bool IsDefault);

public sealed record ApprovedSourceRow(
    string Phone,
    string Name,
    string CustomerType,
    string PriceType,
    string Discount);

public static class LegacyCustomerApprovedCatalog
{
    public const int ApprovedSourceCount = 5159;
    public const int ApprovedClassificationCount = 7;
    public const int ApprovedImportableCount = 5137;
    public const string ConfirmationToken = "LEGACY_CUSTOMERS_5137";
    public const int ApprovedDiscountOverrideCustomerId = 706599;
    public const decimal ApprovedDiscountOverridePercent = 26.2626m;
    public const int SkippedTestCustomerId = 709435;
    public const int SkippedWalkInCustomerId = 709465;
    public const int SkippedPhoneReviewCustomerId = 704706;

    public static readonly IReadOnlyList<ApprovedClassificationSpec> Classifications =
    [
        new("REGULAR", "عادي", "Price0", 0.0000m, true),
        new("BRONZE", "برونزي", "Price3", 12.0000m, false),
        new("SILVER", "فضي", "Price2", 18.0000m, false),
        new("GOLD", "ذهبي", "Price1", 22.0000m, false),
        new("SPECIAL", "زبون مميز", "SpecialCustomer", 10.0000m, false),
        new("COMMERCIAL", "محل تجاري", "Dealer", 31.8182m, false),
        new("MAJOR_CONSUMER", "مستهلك كبير", "MajorCustomer", 26.2626m, false),
        new("COFFEE_SHOP", "مقهى", "MajorCustomer", 26.2626m, false),
    ];

    public static readonly IReadOnlyDictionary<string, string> CustomerTypeCodes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["عادي"] = "REGULAR",
        ["برونزي"] = "BRONZE",
        ["فضي"] = "SILVER",
        ["ذهبي"] = "GOLD",
        ["زبون مميز"] = "SPECIAL",
        ["محل تجاري"] = "COMMERCIAL",
        ["مستهلك كبير"] = "MAJOR_CONSUMER",
        ["SpecialCustomer"] = "SPECIAL",
        ["Dealer"] = "COMMERCIAL",
        ["dealer"] = "COMMERCIAL",
        ["normal"] = "REGULAR",
        ["CofeeShop"] = "COFFEE_SHOP",
    };

    public static readonly IReadOnlyDictionary<int, string> CostPriceReplacements = new Dictionary<int, string>
    {
        [704718] = "MajorCustomer",
        [707897] = "MajorCustomer",
        [708760] = "Dealer",
        [709605] = "MajorCustomer",
    };

    public static readonly IReadOnlySet<int> SkippedInvalidNameIds = new HashSet<int>
    {
        704705, 704707, 705002, 705798, 705862, 705899, 706330, 706655, 707398,
        707421, 707633, 707634, 708361, 708376, 708714, 708841, 709578, 709752, 709769,
    };

    public static readonly IReadOnlySet<int> SwappedFieldCustomerIds = new HashSet<int>
    {
        709858, 709859, 709860, 709861, 709862, 709863,
    };

    public static readonly IReadOnlyDictionary<int, ApprovedSourceRow> SwappedLegacyFields = new Dictionary<int, ApprovedSourceRow>
    {
        [709858] = new("احمد طعمه", "0599136918", "زبون مميز", "SpecialCustomerPrice", "0.1"),
        [709859] = new("دكان الصدوق", "0594450024", "محل تجاري", "DealerPrice", "0.3181818"),
        [709860] = new("سوبر ماركت الطيبات", "2020", "محل تجاري", "DealerPrice", "0.3181818"),
        [709861] = new("كفتيريا ونادي ارتاح", "0592011951", "مستهلك كبير", "MajorCustomerPrice", "0.262626"),
        [709862] = new("محمد غياب", "0569753302", "زبون مميز", "SpecialCustomerPrice", "0.1"),
        [709863] = new("معتز ياسين", "0569512223", "زبون مميز", "SpecialCustomerPrice", "0.1"),
    };

    public static readonly IReadOnlyDictionary<string, int> ExpectedCustomerTypeCounts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["عادي"] = 8,
        ["برونزي"] = 121,
        ["فضي"] = 203,
        ["ذهبي"] = 253,
        ["زبون مميز"] = 684,
        ["محل تجاري"] = 243,
        ["مستهلك كبير"] = 302,
        ["SpecialCustomer"] = 2337,
        ["CofeeShop"] = 653,
        ["Dealer"] = 316,
        ["normal"] = 2,
        ["dealer"] = 1,
        ["test"] = 1,
        [""] = 35,
    };
}
