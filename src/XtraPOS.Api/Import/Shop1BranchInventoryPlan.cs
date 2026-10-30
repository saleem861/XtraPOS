namespace XtraPOS.Api.Import;

public sealed record Shop1OperationalItem(
    string ItemNumber,
    string? ItemName,
    decimal? Quantity,
    decimal? Cost,
    decimal? ItemSpeed,
    DateTime? LastRestockAt,
    bool? IsAvailable,
    decimal? DealerPrice = null,
    decimal? MajorCustomerPrice = null,
    decimal? SpecialCustomerPrice = null,
    decimal? Price0 = null,
    decimal? Price1 = null,
    decimal? Price2 = null,
    decimal? Price3 = null);

public sealed record MasterItemMatch(
    string ItemNumber,
    bool IsActive,
    IReadOnlyDictionary<string, decimal> DefaultPrices);

public sealed record Shop1NegativeQuantity(string ItemNumber, string? ItemName, decimal Quantity);

public sealed record Shop1SalesRateRank(string ItemNumber, string? ItemName, decimal DailySalesRate);

public sealed record Shop1PriceOverrideCount(string PriceCode, int DifferentFromDefault, int MissingDefault);

public sealed record Shop1BranchProposal(
    int LegacyPosNumber,
    string BranchName,
    string BranchCode,
    string BranchType,
    int StationNumber,
    string StationName);

public static class Shop1BranchReadiness
{
    public const string Ready = "READY_FOR_SHOP1_BRANCH_COMMIT";
    public const string NotReady = "NOT_READY_FOR_SHOP1_BRANCH_COMMIT";
}

public sealed record Shop1BranchInventoryReport(
    int Shop1ItemCount,
    int MatchedItems,
    int MissingMasterItems,
    int DuplicateMatches,
    int PlannedItemBranchInventory,
    int PositiveQuantities,
    int ZeroQuantities,
    int NegativeQuantities,
    int NullQuantities,
    int FractionalQuantities,
    int PositiveCosts,
    int ZeroCosts,
    int NullCosts,
    int NegativeCosts,
    decimal? MinimumCost,
    decimal? MaximumCost,
    int ItemSpeedPositive,
    int ItemSpeedZero,
    int ItemSpeedNegative,
    int ItemSpeedNull,
    int Available,
    int Unavailable,
    int AvailableWithPositiveStock,
    int AvailableWithZeroStock,
    int UnavailableWithPositiveStock,
    int UnavailableWithZeroOrNegativeStock,
    int NullAvailability,
    int PlannedOpeningBalances,
    string ZeroQuantityRecommendation,
    int IdenticalBranchPricesSkipped,
    int NegativeBranchPricesSkipped,
    int RetailOverrides,
    int InactiveMasterItems,
    int RestockNull,
    int RestockFuture,
    int RestockLast90Days,
    int RestockLast91To180Days,
    int RestockLast181To365Days,
    int RestockOlderThan365Days,
    decimal SumPlannedOnHandQuantity,
    decimal SumPositiveQuantity,
    decimal SumNegativeQuantity,
    decimal? InventoryValue,
    IReadOnlyList<Shop1NegativeQuantity> NegativeQuantityItems,
    IReadOnlyList<Shop1SalesRateRank> TopDailySalesRates,
    IReadOnlyList<Shop1PriceOverrideCount> PriceOverrides,
    IReadOnlyList<string> InactiveMasterItemNumbers,
    IReadOnlyList<string> MissingMasterItemNumbers,
    Shop1BranchProposal ProposedBranch,
    IReadOnlyList<string> BlockingReasons,
    string Readiness);

public static class Shop1BranchInventoryPlan
{
    public static readonly string[] PriceCodes = ["Dealer", "MajorCustomer", "SpecialCustomer", "Price0", "Price1", "Price2", "Price3"];

    public const string ZeroMovementRecommendation =
        "Create ItemBranchInventory only. Do not create a zero OpeningBalance movement.";

    public static Shop1BranchInventoryReport Analyze(
        IReadOnlyList<Shop1OperationalItem> shopItems,
        IReadOnlyList<MasterItemMatch> masterItems,
        DateOnly today,
        Shop1BranchProposal proposedBranch)
    {
        var shopGroups = shopItems
            .GroupBy(item => item.ItemNumber.Trim(), StringComparer.Ordinal)
            .ToList();
        var masterGroups = masterItems
            .GroupBy(item => item.ItemNumber.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var blank = shopGroups.Count(group => group.Key.Length == 0);
        var shopDuplicates = shopGroups.Count(group => group.Key.Length > 0 && group.Count() > 1);
        var masterDuplicates = shopGroups.Count(group =>
            group.Key.Length > 0 && masterGroups.TryGetValue(group.Key, out var masters) && masters.Count > 1);
        var duplicateMatches = shopDuplicates + masterDuplicates;

        var matched = new List<(Shop1OperationalItem Shop, MasterItemMatch Master)>();
        var missing = new List<string>();
        var inactive = new List<string>();
        foreach (var group in shopGroups.Where(group => group.Key.Length > 0 && group.Count() == 1))
        {
            if (!masterGroups.TryGetValue(group.Key, out var masters))
            {
                missing.Add(group.Key);
                continue;
            }

            if (masters.Count != 1)
            {
                continue;
            }

            matched.Add((group.Single(), masters[0]));
            if (!masters[0].IsActive)
            {
                inactive.Add(group.Key);
            }
        }

        var quantities = shopItems.Select(item => item.Quantity).ToList();
        var positive = shopItems.Where(item => item.Quantity > 0).Select(item => item.Quantity!.Value).ToList();
        var negative = shopItems.Where(item => item.Quantity < 0).OrderBy(item => item.ItemNumber, StringComparer.Ordinal).ToList();
        var costs = shopItems.Where(item => item.Cost is not null).Select(item => item.Cost!.Value).ToList();
        var speeds = shopItems.Select(item => item.ItemSpeed).ToList();
        var plannedQuantities = matched.Where(pair => pair.Shop.Quantity is not null).Select(pair => pair.Shop.Quantity!.Value).ToList();
        var valued = matched.Where(pair => pair.Shop.Quantity is not null && pair.Shop.Cost is not null).ToList();
        var overrides = PriceCodes.Select(code => CountOverrides(matched, code)).ToList();
        var identical = matched.Sum(pair => PriceCodes.Count(code =>
        {
            var shopPrice = ReadPrice(pair.Shop, code);
            return shopPrice is >= 0 && pair.Master.DefaultPrices.TryGetValue(code, out var current) && current == shopPrice.Value;
        }));
        var negativePrices = matched.Sum(pair => PriceCodes.Count(code => ReadPrice(pair.Shop, code) < 0));
        var blocking = new List<string>();
        if (blank > 0)
        {
            blocking.Add($"{blank} Shop1 rows have no ItemNumber.");
        }

        if (shopDuplicates > 0)
        {
            blocking.Add($"{shopDuplicates} Shop1 ItemNumbers are duplicated.");
        }

        if (masterDuplicates > 0)
        {
            blocking.Add($"{masterDuplicates} Shop1 ItemNumbers match more than one master item.");
        }

        if (missing.Count > 0)
        {
            blocking.Add($"{missing.Count} Shop1 ItemNumbers are missing from the master catalog.");
        }

        if (inactive.Count > 0)
        {
            blocking.Add($"{inactive.Count} Shop1 items are inactive in the master catalog.");
        }

        var restock = shopItems.Select(item => item.LastRestockAt).ToList();
        return new Shop1BranchInventoryReport(
            shopItems.Count,
            matched.Count,
            missing.Count + blank,
            duplicateMatches,
            matched.Count,
            positive.Count,
            quantities.Count(value => value == 0),
            negative.Count,
            quantities.Count(value => value is null),
            quantities.Count(value => value is not null && value.Value != decimal.Truncate(value.Value)),
            costs.Count(value => value > 0),
            costs.Count(value => value == 0),
            shopItems.Count(item => item.Cost is null),
            costs.Count(value => value < 0),
            costs.Count == 0 ? null : costs.Min(),
            costs.Count == 0 ? null : costs.Max(),
            speeds.Count(value => value > 0),
            speeds.Count(value => value == 0),
            speeds.Count(value => value < 0),
            speeds.Count(value => value is null),
            shopItems.Count(item => item.IsAvailable == true),
            shopItems.Count(item => item.IsAvailable == false),
            shopItems.Count(item => item.IsAvailable == true && item.Quantity > 0),
            shopItems.Count(item => item.IsAvailable == true && item.Quantity == 0),
            shopItems.Count(item => item.IsAvailable == false && item.Quantity > 0),
            shopItems.Count(item => item.IsAvailable == false && item.Quantity <= 0),
            shopItems.Count(item => item.IsAvailable is null),
            matched.Count(pair => pair.Shop.Quantity is not null && pair.Shop.Quantity != 0),
            ZeroMovementRecommendation,
            identical,
            negativePrices,
            RetailOverrides: 0,
            inactive.Count,
            restock.Count(value => value is null),
            restock.Count(value => value is not null && DateOnly.FromDateTime(value.Value) > today),
            CountRestock(restock, today, 0, 90),
            CountRestock(restock, today, 91, 180),
            CountRestock(restock, today, 181, 365),
            restock.Count(value => value is not null && DateOnly.FromDateTime(value.Value) <= today.AddDays(-366)),
            plannedQuantities.Sum(),
            positive.Sum(),
            negative.Sum(item => item.Quantity!.Value),
            valued.Count == 0 ? null : valued.Sum(pair => pair.Shop.Quantity!.Value * pair.Shop.Cost!.Value),
            negative.Select(item => new Shop1NegativeQuantity(item.ItemNumber.Trim(), item.ItemName, item.Quantity!.Value)).ToList(),
            shopItems
                .Where(item => item.ItemSpeed is not null)
                .OrderByDescending(item => item.ItemSpeed)
                .ThenBy(item => item.ItemNumber, StringComparer.Ordinal)
                .Take(20)
                .Select(item => new Shop1SalesRateRank(item.ItemNumber.Trim(), item.ItemName, item.ItemSpeed!.Value))
                .ToList(),
            overrides,
            inactive.OrderBy(number => number, StringComparer.Ordinal).ToList(),
            missing.OrderBy(number => number, StringComparer.Ordinal).ToList(),
            proposedBranch,
            blocking,
            blocking.Count == 0 ? Shop1BranchReadiness.Ready : Shop1BranchReadiness.NotReady);
    }

    private static Shop1PriceOverrideCount CountOverrides(
        IReadOnlyList<(Shop1OperationalItem Shop, MasterItemMatch Master)> matched,
        string code)
    {
        var different = 0;
        var missingDefault = 0;
        foreach (var (shop, master) in matched)
        {
            var shopPrice = ReadPrice(shop, code);
            if (shopPrice is null or < 0)
            {
                continue;
            }

            if (!master.DefaultPrices.TryGetValue(code, out var current))
            {
                missingDefault++;
                continue;
            }

            if (current != shopPrice.Value)
            {
                different++;
            }
        }

        return new Shop1PriceOverrideCount(code, different, missingDefault);
    }

    private static decimal? ReadPrice(Shop1OperationalItem item, string code) => code switch
    {
        "Dealer" => item.DealerPrice,
        "MajorCustomer" => item.MajorCustomerPrice,
        "SpecialCustomer" => item.SpecialCustomerPrice,
        "Price0" => item.Price0,
        "Price1" => item.Price1,
        "Price2" => item.Price2,
        "Price3" => item.Price3,
        _ => null,
    };

    private static int CountRestock(IReadOnlyList<DateTime?> values, DateOnly today, int minimumAge, int maximumAge)
    {
        return values.Count(value =>
        {
            if (value is null)
            {
                return false;
            }

            var age = today.DayNumber - DateOnly.FromDateTime(value.Value).DayNumber;
            return age >= minimumAge && age <= maximumAge;
        });
    }
}
