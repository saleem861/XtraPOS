namespace XtraPOS.Api.Import;

public static class LegacyDryRunSeverity
{
    public const string AutoResolved = "AUTO_RESOLVED";
    public const string ReviewRecommended = "REVIEW_RECOMMENDED";
    public const string Blocking = "BLOCKING";
}

public static class LegacyMasterCommitReadiness
{
    public const string Ready = "READY_FOR_MASTER_COMMIT";
    public const string NotReady = "NOT_READY_FOR_MASTER_COMMIT";
}

public sealed record SourceCatalogStats(string Source, int Rows, int DistinctItemNumbers, int DuplicateItemNumbers, int BlankItemNumbers);

public sealed record PriceCodeCount(string PriceCode, int Count);

public sealed record BranchPriceOverrideCount(string LegacyBranch, string PriceCode, int Count);

public sealed record DryRunSample(string ItemNumber, string CanonicalItemName, string Reason, IReadOnlyList<SourceFieldValue> Sources);

public sealed record SourceFieldValue(string Source, string? Value);

public sealed record OrphanParentDetail(string ItemNumber, string ParentItemNumber, string? ProductType, string CanonicalItemName);

public sealed record NameCorrectionDetail(string ItemNumber, string CanonicalItemName, string CentralItemName);

public sealed record LegacyMasterDryRunReport(
    IReadOnlyList<SourceCatalogStats> LegacyRowsReadBySource,
    int CanonicalItems,
    int CentralOnlyItems,
    int BranchOnlyItems,
    int Shop1OnlyItems,
    int Shop2OnlyItems,
    int SwstoreOnlyItems,
    int MultiBranchWithoutCentral,
    int AllSourcesItems,
    int BranchConsensusCorrections,
    int ItemNameBranchConsensusCorrections,
    int MasterFieldConflicts,
    int ItemNameMasterFieldConflicts,
    int ExactNameAgreement,
    int MissingItemNameFallbacks,
    int ValidUnitLinks,
    int SelfUnitReferencesIgnored,
    int OrphanInternalUnits,
    int UnitsPerPackageInvalid,
    int MultiLevelDirectLinks,
    int CircularPackageRelations,
    int ValidParentLinks,
    int OrphanParents,
    IReadOnlyList<PriceCodeCount> DefaultPricesToCreate,
    int InvalidNegativePrices,
    IReadOnlyList<BranchPriceOverrideCount> PotentialBranchPriceOverrides,
    int PlannedRetailPrices,
    int PlannedBranchPriceInserts,
    int PlannedItemBranchInventory,
    int PlannedOpeningBalances,
    int PlannedIsActive,
    int PlannedPublishedOnline,
    int BlockingIssues,
    int ReviewRecommendedIssues,
    int AutoResolvedIssues,
    string ProhibitedRecommendation,
    int ProhibitedTrueCentral,
    int ProhibitedTrueShop1,
    int ProhibitedTrueShop2,
    int ProhibitedTrueSwstore,
    int ProhibitedDisagreements,
    IReadOnlyDictionary<string, int> ProductTypes,
    string Readiness,
    IReadOnlyList<NameCorrectionDetail> NameCorrections,
    IReadOnlyList<OrphanParentDetail> OrphanParentDetails,
    IReadOnlyList<DryRunSample> NegativePrices,
    IReadOnlyList<DryRunSample> BranchConsensusSamples,
    IReadOnlyList<DryRunSample> MasterConflictSamples,
    IReadOnlyList<DryRunSample> OrphanUnitSamples,
    IReadOnlyList<DryRunSample> BranchOnlySamples,
    IReadOnlyList<LegacyImportIssue> BlockingDetails);

public static class LegacyMasterCatalogDryRun
{
    private static readonly string[] Sources =
    [
        LegacyCatalogs.Central,
        LegacyCatalogs.Shop1,
        LegacyCatalogs.Shop2,
        LegacyCatalogs.Swstore,
    ];

    public static LegacyMasterDryRunReport Analyze(IReadOnlyList<LegacyItemObservation> rows)
    {
        var known = rows.Where(row => LegacyCatalogs.IsKnown(row.Source)).ToList();
        var plan = LegacyItemCanonicalizer.Plan(known);
        var bySource = known.GroupBy(row => row.Source, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToList());
        var numbersBySource = bySource.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Select(row => row.ItemNumber.Trim()).Where(number => number.Length > 0).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);
        var stats = Sources.Select(source =>
        {
            var sourceRows = bySource.GetValueOrDefault(source) ?? [];
            var blank = sourceRows.Count(row => string.IsNullOrWhiteSpace(row.ItemNumber));
            var duplicates = sourceRows
                .Where(row => !string.IsNullOrWhiteSpace(row.ItemNumber))
                .GroupBy(row => row.ItemNumber.Trim(), StringComparer.Ordinal)
                .Count(group => group.Count() > 1);
            return new SourceCatalogStats(
                source,
                sourceRows.Count,
                numbersBySource.GetValueOrDefault(source)?.Count ?? 0,
                duplicates,
                blank);
        }).ToList();

        var central = numbersBySource.GetValueOrDefault(LegacyCatalogs.Central) ?? new HashSet<string>(StringComparer.Ordinal);
        var shop1 = numbersBySource.GetValueOrDefault(LegacyCatalogs.Shop1) ?? new HashSet<string>(StringComparer.Ordinal);
        var shop2 = numbersBySource.GetValueOrDefault(LegacyCatalogs.Shop2) ?? new HashSet<string>(StringComparer.Ordinal);
        var swstore = numbersBySource.GetValueOrDefault(LegacyCatalogs.Swstore) ?? new HashSet<string>(StringComparer.Ordinal);
        var shop1Only = shop1.Count(number => !central.Contains(number) && !shop2.Contains(number) && !swstore.Contains(number));
        var shop2Only = shop2.Count(number => !central.Contains(number) && !shop1.Contains(number) && !swstore.Contains(number));
        var swstoreOnly = swstore.Count(number => !central.Contains(number) && !shop1.Contains(number) && !shop2.Contains(number));
        var multiWithoutCentral = shop1.Union(shop2, StringComparer.Ordinal).Union(swstore, StringComparer.Ordinal)
            .Count(number => !central.Contains(number) && SourceCount(number, shop1, shop2, swstore) >= 2);
        var allSources = central.Count(number => shop1.Contains(number) && shop2.Contains(number) && swstore.Contains(number));

        var itemsByNumber = plan.Items.ToDictionary(item => item.ItemNumber, StringComparer.Ordinal);
        var observationsByNumber = known
            .Where(row => !string.IsNullOrWhiteSpace(row.ItemNumber))
            .GroupBy(row => row.ItemNumber.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<LegacyItemObservation>)group.ToList(), StringComparer.Ordinal);

        var nameCorrections = plan.Issues
            .Where(issue => issue.Category == LegacyImportCategories.BranchConsensusCorrection && issue.Field == "ItemName")
            .Select(issue => new NameCorrectionDetail(
                issue.ItemNumber,
                itemsByNumber[issue.ItemNumber].ItemName,
                issue.Detail ?? ""))
            .OrderBy(correction => correction.ItemNumber, StringComparer.Ordinal)
            .ToList();
        var exactNames = observationsByNumber.Count(pair => NamesAgree(pair.Value));
        var defaultPrices = plan.Items
            .SelectMany(item => item.Prices)
            .Where(price => price.BranchCatalog is null)
            .GroupBy(price => price.PriceCode, StringComparer.Ordinal)
            .Select(group => new PriceCodeCount(group.Key, group.Count()))
            .OrderBy(price => price.PriceCode, StringComparer.Ordinal)
            .ToList();
        var overrides = plan.Items
            .SelectMany(item => item.Prices)
            .Where(price => price.BranchCatalog is not null)
            .GroupBy(price => (price.BranchCatalog!, price.PriceCode))
            .Select(group => new BranchPriceOverrideCount(group.Key.Item1, group.Key.Item2, group.Count()))
            .OrderBy(price => price.LegacyBranch, StringComparer.Ordinal)
            .ThenBy(price => price.PriceCode, StringComparer.Ordinal)
            .ToList();
        var retail = plan.Items.SelectMany(item => item.Prices).Count(price => price.PriceCode == "Retail");
        var links = plan.Items
            .Where(item => item.Unit is not null)
            .ToDictionary(item => item.ItemNumber, item => item.Unit!.InternalUnitItemNumber, StringComparer.Ordinal);
        var multiLevel = links.Count(pair => links.ContainsKey(pair.Value));
        var circular = CountCircularItems(links);
        var prohibited = AnalyzeProhibited(known);
        var productTypes = plan.Items
            .GroupBy(item => string.IsNullOrWhiteSpace(item.ProductType) ? "(null)" : item.ProductType.Trim(), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var orphanParents = plan.Issues
            .Where(issue => issue.Category == LegacyImportCategories.OrphanParent)
            .Select(issue => new OrphanParentDetail(
                issue.ItemNumber,
                issue.Detail ?? "",
                itemsByNumber[issue.ItemNumber].ProductType,
                itemsByNumber[issue.ItemNumber].ItemName))
            .OrderBy(parent => parent.ItemNumber, StringComparer.Ordinal)
            .ToList();
        var negative = plan.Issues
            .Where(issue => issue.Category == LegacyImportCategories.InvalidNegativePrice)
            .OrderBy(issue => issue.ItemNumber, StringComparer.Ordinal)
            .ThenBy(issue => issue.Field, StringComparer.Ordinal)
            .Select(issue => Sample(issue, itemsByNumber, observationsByNumber, PriceField(issue.Field)))
            .ToList();
        var blocking = plan.Issues.Where(issue => issue.Unresolved).OrderBy(issue => issue.ItemNumber, StringComparer.Ordinal).ToList();
        var blankNumbers = stats.Sum(source => source.BlankItemNumbers);
        var blockingCount = blocking.Count + blankNumbers;
        var review = plan.Issues.Count(issue => Severity(issue) == LegacyDryRunSeverity.ReviewRecommended);
        var auto = plan.Issues.Count(issue => Severity(issue) == LegacyDryRunSeverity.AutoResolved);
        var readiness = blockingCount == 0 && retail == 0
            ? LegacyMasterCommitReadiness.Ready
            : LegacyMasterCommitReadiness.NotReady;

        return new LegacyMasterDryRunReport(
            stats,
            plan.CanonicalItems,
            plan.CentralOnlyItems,
            plan.BranchOnlyItems,
            shop1Only,
            shop2Only,
            swstoreOnly,
            multiWithoutCentral,
            allSources,
            plan.BranchConsensusCorrections,
            nameCorrections.Count,
            plan.MasterFieldConflicts,
            plan.Issues.Count(issue => issue.Category == LegacyImportCategories.MasterFieldConflict && issue.Field == "ItemName"),
            exactNames,
            plan.MissingItemNameFallbacks,
            plan.ValidUnitLinks,
            plan.Issues.Count(issue => issue.Category == LegacyImportCategories.SelfReferenceIgnored),
            plan.OrphanInternalUnits,
            plan.Issues.Count(issue => issue.Category == LegacyImportCategories.UnitsPerPackageInvalid),
            multiLevel,
            circular,
            plan.ValidParentLinks,
            plan.OrphanParents,
            defaultPrices,
            plan.InvalidNegativePrices,
            overrides,
            retail,
            PlannedBranchPriceInserts: 0,
            PlannedItemBranchInventory: 0,
            PlannedOpeningBalances: 0,
            PlannedIsActive: plan.Items.Count,
            PlannedPublishedOnline: plan.Items.Count(item => item.IsPublishedOnline),
            blockingCount,
            review,
            auto,
            prohibited.Recommendation,
            prohibited.CentralTrue,
            prohibited.Shop1True,
            prohibited.Shop2True,
            prohibited.SwstoreTrue,
            prohibited.Disagreements,
            productTypes,
            readiness,
            nameCorrections,
            orphanParents,
            negative,
            Samples(plan, itemsByNumber, observationsByNumber, LegacyImportCategories.BranchConsensusCorrection, "ItemName", row => row.ItemName),
            Samples(plan, itemsByNumber, observationsByNumber, LegacyImportCategories.MasterFieldConflict, "ItemName", row => row.ItemName),
            Samples(plan, itemsByNumber, observationsByNumber, LegacyImportCategories.OrphanInternalUnit, "InternalUnitItemNumber", row => row.InternalUnitItemNumber),
            BranchOnlySamples(plan, observationsByNumber),
            blocking);
    }

    private static int SourceCount(string number, HashSet<string> shop1, HashSet<string> shop2, HashSet<string> swstore) =>
        (shop1.Contains(number) ? 1 : 0) + (shop2.Contains(number) ? 1 : 0) + (swstore.Contains(number) ? 1 : 0);

    private static bool NamesAgree(IReadOnlyList<LegacyItemObservation> rows)
    {
        var names = rows
            .Select(row => row.ItemName?.Trim())
            .Where(name => !string.IsNullOrEmpty(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var contributors = rows.Select(row => row.Source).Distinct(StringComparer.Ordinal).Count(source =>
            rows.Any(row => row.Source == source && !string.IsNullOrWhiteSpace(row.ItemName)));
        return names.Count == 1 && contributors >= 2;
    }

    private static int CountCircularItems(IReadOnlyDictionary<string, string> links)
    {
        var circular = new HashSet<string>(StringComparer.Ordinal);
        foreach (var start in links.Keys)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            var path = new List<string>();
            var current = start;
            while (links.TryGetValue(current, out var next))
            {
                if (seen.TryGetValue(current, out var at))
                {
                    for (var index = at; index < path.Count; index++)
                    {
                        circular.Add(path[index]);
                    }

                    break;
                }

                seen[current] = path.Count;
                path.Add(current);
                current = next;
            }
        }

        return circular.Count;
    }

    private static (string Recommendation, int CentralTrue, int Shop1True, int Shop2True, int SwstoreTrue, int Disagreements) AnalyzeProhibited(
        IReadOnlyList<LegacyItemObservation> rows)
    {
        int CountTrue(string source) => rows.Count(row => row.Source == source && row.IsProhibited == true);
        var disagreements = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.ItemNumber) && row.IsProhibited is not null)
            .GroupBy(row => row.ItemNumber.Trim(), StringComparer.Ordinal)
            .Count(group => group.Select(row => row.IsProhibited!.Value).Distinct().Count() > 1);
        var recommendation = disagreements == 0
            ? "IsProhibited is consistent across every source that contains the item. Plan it as a master restriction."
            : "Do not import IsProhibited. Sources disagree, so the master commit should keep the new-system default of not prohibited.";
        return (recommendation, CountTrue(LegacyCatalogs.Central), CountTrue(LegacyCatalogs.Shop1), CountTrue(LegacyCatalogs.Shop2), CountTrue(LegacyCatalogs.Swstore), disagreements);
    }

    private static string Severity(LegacyImportIssue issue)
    {
        if (issue.Unresolved)
        {
            return LegacyDryRunSeverity.Blocking;
        }

        return issue.Category switch
        {
            LegacyImportCategories.BranchConsensusCorrection => LegacyDryRunSeverity.AutoResolved,
            LegacyImportCategories.SelfReferenceIgnored => LegacyDryRunSeverity.AutoResolved,
            LegacyImportCategories.InvalidNegativePrice => LegacyDryRunSeverity.ReviewRecommended,
            LegacyImportCategories.OrphanInternalUnit => LegacyDryRunSeverity.ReviewRecommended,
            LegacyImportCategories.OrphanParent => LegacyDryRunSeverity.ReviewRecommended,
            LegacyImportCategories.UnitsPerPackageInvalid => LegacyDryRunSeverity.ReviewRecommended,
            LegacyImportCategories.MissingItemNameFallback => LegacyDryRunSeverity.ReviewRecommended,
            LegacyImportCategories.MasterFieldConflict => LegacyDryRunSeverity.ReviewRecommended,
            _ => "",
        };
    }

    private static List<DryRunSample> Samples(
        LegacyItemImportReport plan,
        IReadOnlyDictionary<string, CanonicalItem> items,
        IReadOnlyDictionary<string, IReadOnlyList<LegacyItemObservation>> observations,
        string category,
        string field,
        Func<LegacyItemObservation, string?> read)
    {
        return plan.Issues
            .Where(issue => issue.Category == category && issue.Field == field)
            .OrderBy(issue => issue.ItemNumber, StringComparer.Ordinal)
            .Take(10)
            .Select(issue => Sample(issue, items, observations, read))
            .ToList();
    }

    private static DryRunSample Sample(
        LegacyImportIssue issue,
        IReadOnlyDictionary<string, CanonicalItem> items,
        IReadOnlyDictionary<string, IReadOnlyList<LegacyItemObservation>> observations,
        Func<LegacyItemObservation, string?> read)
    {
        var sources = observations.GetValueOrDefault(issue.ItemNumber)?
            .Select(row => new SourceFieldValue(row.Source, read(row)))
            .ToList() ?? [];
        return new DryRunSample(
            issue.ItemNumber,
            items.TryGetValue(issue.ItemNumber, out var item) ? item.ItemName : issue.ItemNumber,
            issue.Unresolved
                ? $"{LegacyDryRunSeverity.Blocking}:{issue.Field}"
                : $"{issue.Category}:{issue.Field}:{issue.Detail}",
            sources);
    }

    private static Func<LegacyItemObservation, string?> PriceField(string? priceCode) => priceCode switch
    {
        "Dealer" => row => row.DealerPrice?.ToString(),
        "MajorCustomer" => row => row.MajorCustomerPrice?.ToString(),
        "SpecialCustomer" => row => row.SpecialCustomerPrice?.ToString(),
        "Price0" => row => row.Price0?.ToString(),
        "Price1" => row => row.Price1?.ToString(),
        "Price2" => row => row.Price2?.ToString(),
        "Price3" => row => row.Price3?.ToString(),
        _ => _ => null,
    };

    private static List<DryRunSample> BranchOnlySamples(
        LegacyItemImportReport plan,
        IReadOnlyDictionary<string, IReadOnlyList<LegacyItemObservation>> observations)
    {
        return plan.Issues
            .Where(issue => issue.Category == LegacyImportCategories.BranchOnlyItem)
            .OrderBy(issue => issue.ItemNumber, StringComparer.Ordinal)
            .Take(10)
            .Select(issue =>
            {
                var sources = observations[issue.ItemNumber]
                    .Select(row => new SourceFieldValue(row.Source, row.ItemName))
                    .ToList();
                var name = plan.Items.Single(item => item.ItemNumber == issue.ItemNumber).ItemName;
                return new DryRunSample(issue.ItemNumber, name, LegacyImportCategories.BranchOnlyItem, sources);
            })
            .ToList();
    }
}
