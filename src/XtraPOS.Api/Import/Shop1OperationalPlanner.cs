using System.Globalization;

namespace XtraPOS.Api.Import;

public sealed record Shop1OperationalRow(
    string ItemNumber,
    string? ItemName,
    decimal? Quantity,
    bool QuantityUnparsed,
    decimal? Cost,
    bool CostUnparsed,
    decimal? Dealer,
    decimal? MajorCustomer,
    decimal? SpecialCustomer,
    decimal? Price0,
    decimal? Price1,
    decimal? Price2,
    decimal? Price3,
    DateTime? LastProvided);

public sealed record BranchCatalogItem(string ItemNumber, string ItemName);

public sealed record BranchCatalogUnit(string ItemNumber, string TargetItemNumber, decimal Factor);

public sealed record BranchCatalogPrice(string ItemNumber, string PriceCode, decimal Price);

public sealed record OwnershipResult(
    string ItemNumber,
    string? OwnerItemNumber,
    decimal Factor,
    int Hops,
    string State,
    string Path);

public sealed record FamilyQuantityResult(
    string OwnerItemNumber,
    string Classification,
    decimal? OwnerQuantity,
    int PackageCount,
    string Reason);

public sealed record ItemOperationalSample(
    string ItemNumber,
    string ItemName,
    bool InShop1,
    decimal? ShopQuantity,
    decimal? ShopCost,
    string PriceText,
    string? OwnerItemNumber,
    decimal Factor,
    decimal? DerivedQuantity,
    string OwnershipState,
    string QuantityClassification);

public sealed record Shop1InventoryDecision(string ItemNumber, bool IsAvailable, decimal? UnitCost);

public sealed record Shop1StockDecision(string OwnerItemNumber, string Classification, decimal? OnHandQuantity);

public sealed record Shop1PriceDecision(string ItemNumber, string PriceCode, decimal Price);

public sealed record Shop1OperationalPlan(
    int ShopRows,
    int DistinctShopItemNumbers,
    int BlankItemRows,
    int TechnicalRows,
    int DuplicateItemNumbers,
    int ConflictingDuplicateItemNumbers,
    int MatchedItems,
    int ShopOnlyItems,
    int CatalogAbsentFromShop,
    int Available,
    int Unavailable,
    int InventoryRows,
    int PositiveCosts,
    int ZeroCosts,
    int NegativeCosts,
    int NullCosts,
    int UnparsedCosts,
    int ProposedUnitCosts,
    int CostFamilyAnomalies,
    int StockOwners,
    int DerivedPackages,
    int MultiHopItems,
    int UnresolvedItems,
    int CycleItems,
    int TrustedOwners,
    int AmbiguousOwners,
    int InvalidOwners,
    int ProposedInitializedBalances,
    int ProposedUninitializedBalances,
    int ProposedOpeningMovements,
    IReadOnlyDictionary<string, int> OverridesByPriceType,
    IReadOnlyDictionary<string, int> ZeroOverridesByPriceType,
    int NegativePricesExcluded,
    int IdenticalPricesSkipped,
    int LastProvidedPresent,
    int LastProvidedMissing,
    int LastProvidedFuture,
    IReadOnlyList<ItemOperationalSample> Samples,
    IReadOnlyList<ItemOperationalSample> ExtraFamilies,
    IReadOnlyList<Shop1InventoryDecision> InventoryDecisions,
    IReadOnlyList<Shop1StockDecision> StockDecisions,
    IReadOnlyList<Shop1PriceDecision> PriceDecisions,
    IReadOnlyList<string> Notes)
{
    public const string Trusted = "TRUSTED";
    public const string Ambiguous = "AMBIGUOUS";
    public const string Invalid = "INVALID";
    public const string Unresolved = "UNRESOLVED";
}

public static class Shop1OperationalPlanner
{
    public const decimal Tolerance = 0.0001m;
    public static readonly string[] PriceCodes = ["Dealer", "MajorCustomer", "SpecialCustomer", "Price0", "Price1", "Price2", "Price3"];

    public static Shop1OperationalPlan Plan(
        IReadOnlyList<Shop1OperationalRow> shopRows,
        IReadOnlyList<BranchCatalogItem> catalog,
        IReadOnlyList<BranchCatalogUnit> units,
        IReadOnlyList<BranchCatalogPrice> defaultPrices,
        IReadOnlyList<string> sampleItemNumbers,
        DateTime utcNow)
    {
        var notes = new List<string>();
        var blank = shopRows.Count(row => string.IsNullOrWhiteSpace(row.ItemNumber));
        var technical = shopRows.Count(row => string.Equals(Text(row.ItemNumber), CentralMasterCatalog.TechnicalItemNumber, StringComparison.Ordinal));
        var shopGroups = shopRows
            .Select(row => row with { ItemNumber = Text(row.ItemNumber) })
            .Where(row => row.ItemNumber.Length > 0 && !string.Equals(row.ItemNumber, CentralMasterCatalog.TechnicalItemNumber, StringComparison.Ordinal))
            .GroupBy(row => row.ItemNumber, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var duplicateNumbers = shopGroups.Count(pair => pair.Value.Count > 1);
        var conflictingDuplicates = shopGroups.Count(pair => pair.Value.Count > 1 && !RowsAgree(pair.Value));
        var catalogByNumber = catalog
            .GroupBy(item => Text(item.ItemNumber), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var matched = catalogByNumber.Keys.Count(number => shopGroups.ContainsKey(number));
        var shopOnly = shopGroups.Keys.Count(number => !catalogByNumber.ContainsKey(number));
        var absent = catalogByNumber.Count - matched;

        var ownership = ResolveOwnership(catalogByNumber.Keys, units);
        var resolved = ownership.Values.Where(item => item.OwnerItemNumber is not null).ToList();
        var owners = resolved.Select(item => item.OwnerItemNumber!).Distinct(StringComparer.Ordinal).OrderBy(item => item, StringComparer.Ordinal).ToList();
        var packages = resolved.Where(item => item.Hops > 0).ToList();
        var byOwner = packages.GroupBy(item => item.OwnerItemNumber!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);

        var classes = new Dictionary<string, FamilyQuantityResult>(StringComparer.Ordinal);
        foreach (var owner in owners)
        {
            classes[owner] = ClassifyFamily(owner, byOwner.GetValueOrDefault(owner) ?? [], shopGroups);
        }

        var trusted = classes.Values.Count(item => item.Classification == Shop1OperationalPlan.Trusted);
        var ambiguous = classes.Values.Count(item => item.Classification == Shop1OperationalPlan.Ambiguous);
        var invalid = classes.Values.Count(item => item.Classification == Shop1OperationalPlan.Invalid);
        var reasonCounts = classes.Values
            .GroupBy(item => item.Reason, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => group.Key + "=" + group.Count());

        var costRows = shopRows
            .Select(row => row with { ItemNumber = Text(row.ItemNumber) })
            .Where(row => row.ItemNumber.Length > 0 && !string.Equals(row.ItemNumber, CentralMasterCatalog.TechnicalItemNumber, StringComparison.Ordinal))
            .ToList();
        var positiveCosts = costRows.Count(item => !item.CostUnparsed && item.Cost > 0);
        var zeroCosts = costRows.Count(item => !item.CostUnparsed && item.Cost == 0);
        var negativeCosts = costRows.Count(item => !item.CostUnparsed && item.Cost < 0);
        var nullCosts = costRows.Count(item => !item.CostUnparsed && item.Cost is null);
        var unparsedCosts = costRows.Count(item => item.CostUnparsed);
        var proposedCosts = catalogByNumber.Keys.Count(number =>
            shopGroups.TryGetValue(number, out var rows)
            && rows.All(row => !row.CostUnparsed)
            && rows.Select(row => row.Cost).Distinct().Count() == 1
            && rows[0].Cost >= 0);
        var (costAnomalies, costExamples) = CostAnomalies(owners, byOwner, shopGroups);

        var defaults = defaultPrices
            .GroupBy(price => (Text(price.ItemNumber), price.PriceCode))
            .ToDictionary(group => group.Key, group => group.First().Price);
        var overrides = PriceCodes.ToDictionary(code => code, _ => 0, StringComparer.Ordinal);
        var zeroOverrides = PriceCodes.ToDictionary(code => code, _ => 0, StringComparer.Ordinal);
        var priceDecisions = new List<Shop1PriceDecision>();
        var negativePrices = 0;
        var identicalPrices = 0;
        foreach (var number in catalogByNumber.Keys)
        {
            if (!shopGroups.TryGetValue(number, out var rows))
            {
                continue;
            }

            var collapsed = Collapse(rows);
            foreach (var code in PriceCodes)
            {
                var shopPrice = Price(collapsed, code);
                if (shopPrice is null)
                {
                    continue;
                }

                if (shopPrice < 0)
                {
                    negativePrices++;
                    continue;
                }

                defaults.TryGetValue((number, code), out var master);
                var hasMaster = defaults.ContainsKey((number, code));
                if (hasMaster && Near(shopPrice.Value, master))
                {
                    identicalPrices++;
                    continue;
                }

                overrides[code]++;
                priceDecisions.Add(new Shop1PriceDecision(number, code, shopPrice.Value));
                if (shopPrice == 0)
                {
                    zeroOverrides[code]++;
                }
            }
        }

        var provided = shopGroups.Values.Select(rows => Collapse(rows).LastProvided).ToList();
        var names = catalogByNumber.ToDictionary(pair => pair.Key, pair => pair.Value.ItemName, StringComparer.Ordinal);
        var samples = sampleItemNumbers.Select(number => Describe(Text(number), names, ownership, classes, shopGroups)).ToList();
        var extraOwners = owners
            .Where(owner => byOwner.ContainsKey(owner) && !sampleItemNumbers.Contains(owner, StringComparer.Ordinal))
            .OrderBy(owner => classes[owner].Classification == Shop1OperationalPlan.Trusted && classes[owner].OwnerQuantity > 0 ? 0 : 1)
            .ThenBy(owner => owner, StringComparer.Ordinal)
            .Take(5)
            .ToList();
        var extras = extraOwners
            .SelectMany(owner => new[] { Describe(owner, names, ownership, classes, shopGroups) }
                .Concat(byOwner[owner].Select(package => Describe(package.ItemNumber, names, ownership, classes, shopGroups))))
            .ToList();
        var multiHop = resolved
            .Where(item => item.Hops > 1)
            .Select(item => item.Path)
            .ToList();

        var inventoryDecisions = catalogByNumber.Keys
            .OrderBy(number => number, StringComparer.Ordinal)
            .Select(number =>
            {
                if (!shopGroups.TryGetValue(number, out var rows))
                {
                    return new Shop1InventoryDecision(number, false, null);
                }

                decimal? cost = null;
                if (rows.All(row => !row.CostUnparsed)
                    && rows.Select(row => row.Cost).Distinct().Count() == 1
                    && rows[0].Cost >= 0)
                {
                    cost = rows[0].Cost;
                }

                return new Shop1InventoryDecision(number, true, cost);
            })
            .ToList();
        var stockDecisions = owners
            .Select(owner =>
            {
                var family = classes[owner];
                var trusted = family.Classification == Shop1OperationalPlan.Trusted;
                return new Shop1StockDecision(owner, family.Classification, trusted ? family.OwnerQuantity : null);
            })
            .ToList();
        notes.Add("TRUSTED: owner quantity is present and zero or positive, and every package quantity is missing or equal to the owner quantity, the owner quantity divided by the accumulated factor, or the owner quantity multiplied by that factor.");
        notes.Add("AMBIGUOUS: the owner quantity is missing, a duplicate quantity disagrees, or a package quantity is a different physical amount. The balance stays uninitialized.");
        notes.Add("INVALID: any quantity in the family is negative or the quantity text cannot be read. The balance stays uninitialized.");
        notes.Add("An explicit Shop1 zero that differs from the Central default is a branch price override. A zero that matches the default is not an override. Negative Shop1 prices are excluded.");
        notes.Add("ItemBranchInventory.DailySalesRate is currently NOT NULL with default 0. A future import must make that column nullable before inserting rows, so an uninitialized rate is not stored as zero.");
        notes.Add("LastRestockAt and LastSaleAt stay empty. ItemLastProvidedDate is a supply date, not a proven restock or sale timestamp.");
        notes.Add("Quantity reasons: " + string.Join(", ", reasonCounts));
        notes.Add("Multi-hop: " + (multiHop.Count == 0 ? "(none)" : string.Join("; ", multiHop)));
        notes.AddRange(costExamples);

        return new Shop1OperationalPlan(
            shopRows.Count,
            shopGroups.Count,
            blank,
            technical,
            duplicateNumbers,
            conflictingDuplicates,
            matched,
            shopOnly,
            absent,
            matched,
            absent,
            catalogByNumber.Count,
            positiveCosts,
            zeroCosts,
            negativeCosts,
            nullCosts,
            unparsedCosts,
            proposedCosts,
            costAnomalies,
            owners.Count,
            packages.Count,
            resolved.Count(item => item.Hops > 1),
            ownership.Values.Count(item => item.OwnerItemNumber is null),
            ownership.Values.Count(item => item.State == "Cycle"),
            trusted,
            ambiguous,
            invalid,
            trusted,
            ambiguous + invalid,
            trusted,
            overrides,
            zeroOverrides,
            negativePrices,
            identicalPrices,
            provided.Count(date => date is not null),
            provided.Count(date => date is null),
            provided.Count(date => date is not null && date > utcNow),
            samples,
            extras,
            inventoryDecisions,
            stockDecisions,
            priceDecisions,
            notes);
    }

    private static ItemOperationalSample Describe(
        string itemNumber,
        IReadOnlyDictionary<string, string> names,
        IReadOnlyDictionary<string, OwnershipResult> ownership,
        IReadOnlyDictionary<string, FamilyQuantityResult> classes,
        IReadOnlyDictionary<string, List<Shop1OperationalRow>> shopGroups)
    {
        ownership.TryGetValue(itemNumber, out var owned);
        var owner = owned?.OwnerItemNumber;
        var classification = owner is not null && classes.TryGetValue(owner, out var family)
            ? family.Classification
            : Shop1OperationalPlan.Unresolved;
        decimal? ownerQuantity = owner is not null && classes.TryGetValue(owner, out var quantityFamily)
            ? quantityFamily.OwnerQuantity
            : null;
        var factor = owned?.Factor ?? 0;
        decimal? derived = classification == Shop1OperationalPlan.Trusted && ownerQuantity is not null && factor > 0
            ? decimal.Round(ownerQuantity.Value / factor, 4)
            : null;
        shopGroups.TryGetValue(itemNumber, out var rows);
        var collapsed = rows is null ? null : Collapse(rows);
        names.TryGetValue(itemNumber, out var name);
        return new ItemOperationalSample(
            itemNumber,
            name ?? collapsed?.ItemName ?? "",
            rows is not null,
            collapsed?.Quantity,
            collapsed?.Cost,
            collapsed is null ? "" : FormatPrices(collapsed),
            owner,
            factor,
            derived,
            owned?.State ?? "Missing",
            classification);
    }

    private static FamilyQuantityResult ClassifyFamily(
        string owner,
        IReadOnlyList<OwnershipResult> packages,
        IReadOnlyDictionary<string, List<Shop1OperationalRow>> shopGroups)
    {
        var ownerRow = shopGroups.TryGetValue(owner, out var ownerRows) ? Collapse(ownerRows) : null;
        var packageRows = packages.Select(package => (
            package.Factor,
            package.ItemNumber,
            Row: shopGroups.TryGetValue(package.ItemNumber, out var rows) ? Collapse(rows) : null)).ToList();
        var quantities = packageRows.Select(item => item.Row?.Quantity).Append(ownerRow?.Quantity);
        var unparsed = (ownerRow?.QuantityUnparsed ?? false) || packageRows.Any(item => item.Row?.QuantityUnparsed == true);
        if (unparsed)
        {
            return Family(owner, Shop1OperationalPlan.Invalid, "InvalidUnparsed", ownerRow?.Quantity, packages.Count);
        }

        if (quantities.Any(quantity => quantity < 0))
        {
            var echo = ownerRow?.Quantity is < 0
                && packageRows.Count > 0
                && packageRows.All(package => package.Row?.Quantity is not { } packageQuantity
                    || (packageQuantity < 0 && Consistent(decimal.Abs(ownerRow.Quantity.Value), package.Factor, decimal.Abs(packageQuantity))));
            return Family(owner, Shop1OperationalPlan.Invalid, echo ? "InvalidNegativeConsistent" : "InvalidNegative", ownerRow?.Quantity, packages.Count);
        }

        if (ownerRow?.Quantity is not { } ownerQuantity)
        {
            return Family(owner, Shop1OperationalPlan.Ambiguous, "AmbiguousMissingOwner", ownerRow?.Quantity, packages.Count);
        }

        if (QuantityConflict(owner, shopGroups))
        {
            return Family(owner, Shop1OperationalPlan.Ambiguous, "AmbiguousDuplicate", ownerQuantity, packages.Count);
        }

        var sawZeroPackage = false;
        var sawOtherDisagreement = false;
        foreach (var package in packageRows)
        {
            if (package.Row?.Quantity is not { } packageQuantity)
            {
                continue;
            }

            if (QuantityConflict(package.ItemNumber, shopGroups) || !Consistent(ownerQuantity, package.Factor, packageQuantity))
            {
                if (packageQuantity == 0 && ownerQuantity > 0)
                {
                    sawZeroPackage = true;
                }
                else
                {
                    sawOtherDisagreement = true;
                }
            }
        }

        if (sawOtherDisagreement)
        {
            return Family(owner, Shop1OperationalPlan.Ambiguous, "AmbiguousInconsistent", ownerQuantity, packages.Count);
        }

        if (sawZeroPackage)
        {
            return Family(owner, Shop1OperationalPlan.Ambiguous, "AmbiguousPackageZero", ownerQuantity, packages.Count);
        }

        return Family(owner, Shop1OperationalPlan.Trusted, "Trusted", ownerQuantity, packages.Count);
    }

    private static FamilyQuantityResult Family(string owner, string classification, string reason, decimal? quantity, int packages) =>
        new(owner, classification, quantity, packages, reason);

    private static bool QuantityConflict(string itemNumber, IReadOnlyDictionary<string, List<Shop1OperationalRow>> shopGroups)
    {
        return shopGroups.TryGetValue(itemNumber, out var rows)
            && rows.Count > 1
            && rows.Select(row => row.Quantity).Distinct().Count() > 1;
    }

    private static bool Consistent(decimal ownerQuantity, decimal factor, decimal packageQuantity)
    {
        if (factor <= 0)
        {
            return false;
        }

        return Near(packageQuantity, ownerQuantity)
            || Near(packageQuantity, ownerQuantity / factor)
            || Near(packageQuantity, ownerQuantity * factor);
    }

    private static (int Count, List<string> Examples) CostAnomalies(
        IReadOnlyList<string> owners,
        IReadOnlyDictionary<string, List<OwnershipResult>> packages,
        IReadOnlyDictionary<string, List<Shop1OperationalRow>> shopGroups)
    {
        var anomalies = 0;
        var roundingFamilies = 0;
        var roundingExamples = new List<string>();
        var materialExamples = new List<string>();
        foreach (var owner in owners)
        {
            if (!shopGroups.TryGetValue(owner, out var ownerRows) || Collapse(ownerRows).Cost is not { } ownerCost || ownerCost <= 0)
            {
                continue;
            }

            if (!packages.TryGetValue(owner, out var family))
            {
                continue;
            }

            var inconsistent = new List<string>();
            var rounding = true;
            foreach (var package in family)
            {
                if (!shopGroups.TryGetValue(package.ItemNumber, out var packageRows)
                    || Collapse(packageRows).Cost is not { } packageCost
                    || packageCost <= 0)
                {
                    continue;
                }

                if (Consistent(ownerCost, package.Factor, packageCost))
                {
                    continue;
                }

                var nearest = NearestIdentityGap(ownerCost, package.Factor, packageCost);
                if (nearest > 0.01m)
                {
                    rounding = false;
                }

                inconsistent.Add(package.ItemNumber + " factor=" + package.Factor.ToString(CultureInfo.InvariantCulture) + " cost=" + packageCost.ToString(CultureInfo.InvariantCulture) + " gap=" + nearest.ToString(CultureInfo.InvariantCulture));
            }

            if (inconsistent.Count == 0)
            {
                continue;
            }

            anomalies++;
            if (rounding)
            {
                roundingFamilies++;
            }

            if (rounding && roundingExamples.Count < 3)
            {
                roundingExamples.Add("CostRounding owner=" + owner + " cost=" + ownerCost.ToString(CultureInfo.InvariantCulture) + " | " + string.Join(", ", inconsistent));
            }
            else if (!rounding && materialExamples.Count < 5)
            {
                materialExamples.Add("CostMaterial owner=" + owner + " cost=" + ownerCost.ToString(CultureInfo.InvariantCulture) + " | " + string.Join(", ", inconsistent));
            }
        }

        var examples = new List<string> { "CostRoundingFamilies=" + roundingFamilies + " CostMaterialFamilies=" + (anomalies - roundingFamilies) };
        examples.AddRange(materialExamples);
        examples.AddRange(roundingExamples);
        return (anomalies, examples);
    }

    private static decimal NearestIdentityGap(decimal ownerQuantity, decimal factor, decimal packageQuantity)
    {
        if (factor <= 0)
        {
            return decimal.MaxValue;
        }

        return new[]
        {
            decimal.Abs(packageQuantity - ownerQuantity),
            decimal.Abs(packageQuantity - (ownerQuantity / factor)),
            decimal.Abs(packageQuantity - (ownerQuantity * factor)),
        }.Min();
    }

    private static Dictionary<string, OwnershipResult> ResolveOwnership(
        IEnumerable<string> itemNumbers,
        IReadOnlyList<BranchCatalogUnit> units)
    {
        var numbers = itemNumbers.ToHashSet(StringComparer.Ordinal);
        var edges = units
            .Select(unit => unit with { ItemNumber = Text(unit.ItemNumber), TargetItemNumber = Text(unit.TargetItemNumber) })
            .Where(unit => unit.ItemNumber.Length > 0 && !string.Equals(unit.ItemNumber, unit.TargetItemNumber, StringComparison.Ordinal))
            .GroupBy(unit => (unit.ItemNumber, unit.TargetItemNumber, unit.Factor))
            .Select(group => group.First())
            .GroupBy(unit => unit.ItemNumber, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.Ordinal);
        var results = new Dictionary<string, OwnershipResult>(StringComparer.Ordinal);
        foreach (var itemNumber in numbers.OrderBy(item => item, StringComparer.Ordinal))
        {
            results[itemNumber] = Walk(itemNumber, numbers, edges);
        }

        return results;
    }

    private static OwnershipResult Walk(
        string start,
        HashSet<string> numbers,
        IReadOnlyDictionary<string, List<BranchCatalogUnit>> edges)
    {
        var factor = 1m;
        var current = start;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hops = 0;
        var path = new List<string> { start };
        while (edges.TryGetValue(current, out var links))
        {
            if (links.Count != 1 || links[0].Factor <= 0)
            {
                return new OwnershipResult(start, null, 0, hops, links.Count == 1 ? "InvalidFactor" : "MultipleUnits", start);
            }

            if (!seen.Add(current) || hops > numbers.Count)
            {
                return new OwnershipResult(start, null, 0, hops, "Cycle", start);
            }

            var link = links[0];
            if (!numbers.Contains(link.TargetItemNumber))
            {
                return new OwnershipResult(start, null, 0, hops, "MissingTarget", start);
            }

            factor *= link.Factor;
            current = link.TargetItemNumber;
            hops++;
            path.Add("x" + link.Factor.ToString(CultureInfo.InvariantCulture) + "->" + current);
        }

        return new OwnershipResult(start, current, factor, hops, hops == 0 ? "Owner" : "Package", string.Join(" ", path));
    }

    private static Shop1OperationalRow Collapse(IReadOnlyList<Shop1OperationalRow> rows) => rows[0];

    private static bool RowsAgree(IReadOnlyList<Shop1OperationalRow> rows)
    {
        var first = rows[0];
        return rows.All(row => row.Quantity == first.Quantity
            && row.Cost == first.Cost
            && row.Dealer == first.Dealer
            && row.MajorCustomer == first.MajorCustomer
            && row.SpecialCustomer == first.SpecialCustomer
            && row.Price0 == first.Price0
            && row.Price1 == first.Price1
            && row.Price2 == first.Price2
            && row.Price3 == first.Price3);
    }

    private static decimal? Price(Shop1OperationalRow row, string code) => code switch
    {
        "Dealer" => row.Dealer,
        "MajorCustomer" => row.MajorCustomer,
        "SpecialCustomer" => row.SpecialCustomer,
        "Price0" => row.Price0,
        "Price1" => row.Price1,
        "Price2" => row.Price2,
        "Price3" => row.Price3,
        _ => null,
    };

    private static string FormatPrices(Shop1OperationalRow row) =>
        string.Join(", ", PriceCodes.Select(code => code + "=" + (Price(row, code)?.ToString() ?? "")));

    private static bool Near(decimal left, decimal right) => decimal.Abs(left - right) <= Tolerance;

    private static string Text(string? value) => (value ?? "").Trim();
}
