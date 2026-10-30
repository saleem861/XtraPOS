namespace XtraPOS.Api.Import;

public static class LegacyCatalogs
{
    public const string Central = "saleemPOSDB";
    public const string Shop1 = "Shop1";
    public const string Shop2 = "Shop2";
    public const string Swstore = "swstore";
    public const string TrialBranch = "فرع التجربة";

    public static bool IsRealBranch(string source) =>
        string.Equals(source, Shop1, StringComparison.Ordinal)
        || string.Equals(source, Shop2, StringComparison.Ordinal)
        || string.Equals(source, Swstore, StringComparison.Ordinal);

    public static bool IsKnown(string source) =>
        string.Equals(source, Central, StringComparison.Ordinal) || IsRealBranch(source);
}

public static class LegacyImportCategories
{
    public const string BranchConsensusCorrection = "BranchConsensusCorrection";
    public const string MasterFieldConflict = "MasterFieldConflict";
    public const string MissingItemNameFallback = "MissingItemNameFallback";
    public const string BranchOnlyItem = "BranchOnlyItem";
    public const string CentralOnlyItem = "CentralOnlyItem";
    public const string OrphanInternalUnit = "OrphanInternalUnit";
    public const string OrphanParent = "OrphanParent";
    public const string InvalidNegativePrice = "InvalidNegativePrice";
    public const string SelfReferenceIgnored = "SelfReferenceIgnored";
    public const string UnitsPerPackageInvalid = "UnitsPerPackageInvalid";
}

public sealed record LegacyItemObservation(
    string Source,
    string ItemNumber,
    string? ItemName = null,
    string? ItemType = null,
    string? Manufacturer = null,
    string? Color = null,
    string? Taste = null,
    string? Size = null,
    string? Packing = null,
    string? Classification = null,
    string? CountryOfOrigin = null,
    string? ProductType = null,
    string? ParentItemNumber = null,
    decimal? UnitsPerPackage = null,
    string? InternalUnitItemNumber = null,
    string? InternalUnitItemName = null,
    decimal? DealerPrice = null,
    decimal? MajorCustomerPrice = null,
    decimal? SpecialCustomerPrice = null,
    decimal? Price0 = null,
    decimal? Price1 = null,
    decimal? Price2 = null,
    decimal? Price3 = null,
    bool? IsProhibited = null);

public sealed record UnitCandidate(
    decimal UnitsPerPackage,
    string InternalUnitItemNumber,
    string? InternalUnitItemName);

public sealed record PriceCandidate(string PriceCode, decimal Price, string? BranchCatalog);

public sealed record CanonicalItem(
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
    string? ParentItemNumber,
    bool ParentResolved,
    bool IsPublishedOnline,
    UnitCandidate? Unit,
    IReadOnlyList<PriceCandidate> Prices);

public sealed record LegacyImportIssue(
    string Category,
    string ItemNumber,
    string? Field,
    string? Detail,
    bool Unresolved = false);

public sealed record LegacyItemImportReport(
    int LegacyRowsRead,
    int CanonicalItems,
    int CentralOnlyItems,
    int BranchOnlyItems,
    int BranchConsensusCorrections,
    int MasterFieldConflicts,
    int MissingItemNameFallbacks,
    int ValidUnitLinks,
    int OrphanInternalUnits,
    int ValidParentLinks,
    int OrphanParents,
    int PricesToCreate,
    int InvalidNegativePrices,
    IReadOnlyList<CanonicalItem> Items,
    IReadOnlyList<LegacyImportIssue> Issues);

public static class LegacyOpeningBalancePolicy
{
    public static decimal QuantityDelta(decimal legacyOnHandQuantity) => legacyOnHandQuantity;
}

public static class LegacyItemCanonicalizer
{
    private static readonly string[] RealBranchOrder = [LegacyCatalogs.Shop1, LegacyCatalogs.Shop2, LegacyCatalogs.Swstore];

    private static readonly (string Field, string Code, Func<LegacyItemObservation, decimal?> Read)[] Prices =
    [
        ("DealerPrice", "Dealer", row => row.DealerPrice),
        ("MajorCustomerPrice", "MajorCustomer", row => row.MajorCustomerPrice),
        ("SpecialCustomerPrice", "SpecialCustomer", row => row.SpecialCustomerPrice),
        ("Price0", "Price0", row => row.Price0),
        ("Price1", "Price1", row => row.Price1),
        ("Price2", "Price2", row => row.Price2),
        ("Price3", "Price3", row => row.Price3),
    ];

    public static LegacyItemImportReport Plan(IReadOnlyList<LegacyItemObservation> rows)
    {
        var accepted = rows.Where(row => LegacyCatalogs.IsKnown(row.Source)).ToList();
        var groups = accepted
            .GroupBy(row => Text(row.ItemNumber), StringComparer.Ordinal)
            .Where(group => !string.IsNullOrEmpty(group.Key))
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();
        var numbers = new HashSet<string>(groups.Select(group => group.Key), StringComparer.Ordinal);
        var issues = new List<LegacyImportIssue>();
        var items = new List<CanonicalItem>(groups.Count);

        foreach (var group in groups)
        {
            var itemNumber = group.Key;
            var central = group.Where(row => string.Equals(row.Source, LegacyCatalogs.Central, StringComparison.Ordinal)).ToList();
            var branches = group.Where(row => LegacyCatalogs.IsRealBranch(row.Source)).ToList();
            if (central.Count > 0 && branches.Count == 0)
            {
                issues.Add(Issue(LegacyImportCategories.CentralOnlyItem, itemNumber, null, null));
            }

            if (central.Count == 0 && branches.Count > 0)
            {
                issues.Add(Issue(LegacyImportCategories.BranchOnlyItem, itemNumber, null, null));
            }

            var itemName = SelectText(itemNumber, "ItemName", central, branches, row => row.ItemName, issues);
            if (string.IsNullOrWhiteSpace(itemName))
            {
                itemName = itemNumber;
                issues.Add(Issue(LegacyImportCategories.MissingItemNameFallback, itemNumber, "ItemName", null));
            }

            var parentNumber = SelectText(itemNumber, "ParentItemNumber", central, branches, row => row.ParentItemNumber, issues);
            var parentResolved = false;
            if (string.IsNullOrWhiteSpace(parentNumber) || string.Equals(parentNumber, itemNumber, StringComparison.Ordinal))
            {
                parentNumber = null;
            }
            else if (numbers.Contains(parentNumber))
            {
                parentResolved = true;
            }
            else
            {
                issues.Add(Issue(LegacyImportCategories.OrphanParent, itemNumber, "ParentItemNumber", parentNumber));
                parentNumber = null;
            }

            items.Add(new CanonicalItem(
                itemNumber,
                itemName,
                SelectText(itemNumber, "ItemType", central, branches, row => row.ItemType, issues),
                SelectText(itemNumber, "Manufacturer", central, branches, row => row.Manufacturer, issues),
                SelectText(itemNumber, "Color", central, branches, row => row.Color, issues),
                SelectText(itemNumber, "Taste", central, branches, row => row.Taste, issues),
                SelectText(itemNumber, "Size", central, branches, row => row.Size, issues),
                SelectText(itemNumber, "Packing", central, branches, row => row.Packing, issues),
                SelectText(itemNumber, "Classification", central, branches, row => row.Classification, issues),
                SelectText(itemNumber, "CountryOfOrigin", central, branches, row => row.CountryOfOrigin, issues),
                SelectText(itemNumber, "ProductType", central, branches, row => row.ProductType, issues),
                parentNumber,
                parentResolved,
                IsPublishedOnline: false,
                SelectUnit(itemNumber, central, branches, numbers, issues),
                SelectPrices(itemNumber, central, branches, issues)));
        }

        return new LegacyItemImportReport(
            rows.Count,
            items.Count,
            Count(issues, LegacyImportCategories.CentralOnlyItem),
            Count(issues, LegacyImportCategories.BranchOnlyItem),
            Count(issues, LegacyImportCategories.BranchConsensusCorrection),
            Count(issues, LegacyImportCategories.MasterFieldConflict),
            Count(issues, LegacyImportCategories.MissingItemNameFallback),
            items.Count(item => item.Unit is not null),
            Count(issues, LegacyImportCategories.OrphanInternalUnit),
            items.Count(item => item.ParentResolved),
            Count(issues, LegacyImportCategories.OrphanParent),
            items.Sum(item => item.Prices.Count),
            Count(issues, LegacyImportCategories.InvalidNegativePrice),
            items,
            issues);
    }

    private static UnitCandidate? SelectUnit(
        string itemNumber,
        IReadOnlyList<LegacyItemObservation> central,
        IReadOnlyList<LegacyItemObservation> branches,
        HashSet<string> numbers,
        List<LegacyImportIssue> issues)
    {
        var chosen = SelectText(itemNumber, "InternalUnitItemNumber", central, branches, row => row.InternalUnitItemNumber, issues);
        if (string.IsNullOrWhiteSpace(chosen))
        {
            if (central.Concat(branches).Any(row => row.UnitsPerPackage is > 1))
            {
                issues.Add(Issue(LegacyImportCategories.UnitsPerPackageInvalid, itemNumber, "InternalUnitItemNumber", null));
            }

            return null;
        }

        if (string.Equals(chosen, itemNumber, StringComparison.Ordinal))
        {
            issues.Add(Issue(LegacyImportCategories.SelfReferenceIgnored, itemNumber, "InternalUnitItemNumber", null));
            return null;
        }

        if (!numbers.Contains(chosen))
        {
            issues.Add(Issue(LegacyImportCategories.OrphanInternalUnit, itemNumber, "InternalUnitItemNumber", chosen));
            return null;
        }

        var supporters = central.Concat(branches)
            .Where(row => string.Equals(Text(row.InternalUnitItemNumber), chosen, StringComparison.Ordinal))
            .ToList();
        var counts = supporters
            .Where(row => row.UnitsPerPackage is not null)
            .Select(row => row.UnitsPerPackage!.Value)
            .Distinct()
            .ToList();
        if (counts.Count != 1 || counts[0] <= 0)
        {
            issues.Add(Issue(LegacyImportCategories.UnitsPerPackageInvalid, itemNumber, "UnitsPerPackage", null));
            if (counts.Count > 1)
            {
                issues.Add(Issue(LegacyImportCategories.MasterFieldConflict, itemNumber, "UnitsPerPackage", null));
            }

            return null;
        }

        var names = DistinctText(supporters, row => row.InternalUnitItemName);
        if (names.Count > 1)
        {
            issues.Add(Issue(LegacyImportCategories.MasterFieldConflict, itemNumber, "InternalUnitItemName", null));
            return null;
        }

        return new UnitCandidate(counts[0], chosen, names.Count == 1 ? names[0] : null);
    }

    private static IReadOnlyList<PriceCandidate> SelectPrices(
        string itemNumber,
        IReadOnlyList<LegacyItemObservation> central,
        IReadOnlyList<LegacyItemObservation> branches,
        List<LegacyImportIssue> issues)
    {
        var prices = new List<PriceCandidate>();
        foreach (var price in Prices)
        {
            var centralPrice = central.Select(price.Read).FirstOrDefault(value => value is not null);
            decimal? defaultPrice = null;
            if (centralPrice is < 0)
            {
                issues.Add(Issue(LegacyImportCategories.InvalidNegativePrice, itemNumber, price.Code, LegacyCatalogs.Central));
            }
            else if (centralPrice is not null)
            {
                defaultPrice = centralPrice.Value;
                prices.Add(new PriceCandidate(price.Code, centralPrice.Value, null));
            }

            foreach (var branch in RealBranchOrder)
            {
                var branchPrice = branches
                    .Where(row => string.Equals(row.Source, branch, StringComparison.Ordinal))
                    .Select(price.Read)
                    .FirstOrDefault(value => value is not null);
                if (branchPrice is null)
                {
                    continue;
                }

                if (branchPrice.Value < 0)
                {
                    issues.Add(Issue(LegacyImportCategories.InvalidNegativePrice, itemNumber, price.Code, branch));
                    continue;
                }

                if (defaultPrice is null || branchPrice.Value != defaultPrice.Value)
                {
                    prices.Add(new PriceCandidate(price.Code, branchPrice.Value, branch));
                }
            }
        }

        return prices;
    }

    private static string? SelectText(
        string itemNumber,
        string field,
        IReadOnlyList<LegacyItemObservation> central,
        IReadOnlyList<LegacyItemObservation> branches,
        Func<LegacyItemObservation, string?> read,
        List<LegacyImportIssue> issues)
    {
        var branchValues = DistinctText(branches, read);
        var centralValues = DistinctText(central, read);
        var centralValue = centralValues.Count == 1 ? centralValues[0] : null;
        if (centralValues.Count > 1)
        {
            issues.Add(Issue(
                LegacyImportCategories.MasterFieldConflict,
                itemNumber,
                field,
                null,
                UnresolvedMaster(field)));
            centralValue = null;
        }

        if (branchValues.Count == 1)
        {
            if (centralValue is not null && !string.Equals(centralValue, branchValues[0], StringComparison.Ordinal))
            {
                issues.Add(Issue(LegacyImportCategories.BranchConsensusCorrection, itemNumber, field, centralValue));
            }

            return branchValues[0];
        }

        if (branchValues.Count > 1)
        {
            issues.Add(Issue(
                LegacyImportCategories.MasterFieldConflict,
                itemNumber,
                field,
                centralValue,
                unresolved: centralValue is null && UnresolvedMaster(field)));
            return centralValue;
        }

        return centralValue;
    }

    private static List<string> DistinctText(IEnumerable<LegacyItemObservation> rows, Func<LegacyItemObservation, string?> read)
    {
        var values = new List<string>();
        foreach (var row in rows)
        {
            var value = Text(read(row));
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (!values.Contains(value, StringComparer.Ordinal))
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static string Text(string? value) => value?.Trim() ?? "";

    private static readonly HashSet<string> BlockingMasterFields = new(StringComparer.Ordinal)
    {
        "ItemName",
        "ItemType",
        "Manufacturer",
        "Color",
        "Taste",
        "Size",
        "Packing",
        "Classification",
        "CountryOfOrigin",
        "ProductType",
    };

    private static bool UnresolvedMaster(string field) => BlockingMasterFields.Contains(field);

    private static LegacyImportIssue Issue(
        string category,
        string itemNumber,
        string? field,
        string? detail,
        bool unresolved = false) =>
        new(category, itemNumber, field, detail, unresolved);

    private static int Count(IReadOnlyList<LegacyImportIssue> issues, string category) =>
        issues.Count(issue => issue.Category == category);
}
