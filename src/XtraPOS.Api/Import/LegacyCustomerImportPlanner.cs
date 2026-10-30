using System.Globalization;
using XtraPOS.Api.Customers;

namespace XtraPOS.Api.Import;

public static class LegacyCustomerImportPlanner
{
    public const string LegacySourceName = "shop1";
    public const string NewGuidRequired = "NEW_GUID_REQUIRED";
    public const string ReuseExisting = "REUSE_EXISTING";

    public const string ReadyToImport = "READY_TO_IMPORT";
    public const string AlreadyImported = "ALREADY_IMPORTED";
    public const string SkippedWalkIn = "SKIPPED_WALK_IN";
    public const string SkippedTestCustomer = "SKIPPED_TEST_CUSTOMER";
    public const string MissingCustomerNumber = "MISSING_CUSTOMER_NUMBER";
    public const string SkippedInvalidName = "SKIPPED_INVALID_NAME";
    public const string SkippedPhoneManualReview = "SKIPPED_PHONE_MANUAL_REVIEW";
    public const string InvalidName = "INVALID_NAME";
    public const string NotesTooLong = "NOTES_TOO_LONG";
    public const string InvalidLegacyDiscount = "INVALID_LEGACY_DISCOUNT";
    public const string CostPriceReview = "COST_PRICE_REVIEW";
    public const string UnmappedPriceType = "UNMAPPED_PRICE_TYPE";
    public const string UnknownClassification = "UNKNOWN_CLASSIFICATION";
    public const string DuplicateLegacyKey = "DUPLICATE_LEGACY_KEY";
    public const string ImpossibleLegacyKey = "IMPOSSIBLE_LEGACY_KEY";

    public const string NormalPhone = "NORMAL_PHONE";
    public const string FormattedPhone = "FORMATTED_PHONE";
    public const string BlankPhone = "BLANK_PHONE";
    public const string NonNumericPhone = "NON_NUMERIC_PHONE";
    public const string SuspiciousPhone = "SUSPICIOUS_PHONE";

    public static readonly string[] PrimaryOutcomes =
    [
        ReadyToImport,
        AlreadyImported,
        SkippedWalkIn,
        SkippedTestCustomer,
        MissingCustomerNumber,
        SkippedInvalidName,
        SkippedPhoneManualReview,
        InvalidName,
        NotesTooLong,
        InvalidLegacyDiscount,
        CostPriceReview,
        UnmappedPriceType,
        UnknownClassification,
        DuplicateLegacyKey,
        ImpossibleLegacyKey,
    ];

    private const int MaxNameLength = 300;
    private const int MaxNotesLength = 1000;

    private static readonly Dictionary<string, string> ClassificationCodes = new(StringComparer.Ordinal)
    {
        ["عادي"] = "REGULAR",
        ["برونزي"] = "BRONZE",
        ["فضي"] = "SILVER",
        ["ذهبي"] = "GOLD",
        ["زبون مميز"] = "SPECIAL",
        ["محل تجاري"] = "COMMERCIAL",
        ["مستهلك كبير"] = "MAJOR_CONSUMER",
    };

    private static readonly Dictionary<string, string> LegacyPriceCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Price0"] = "Price0",
        ["Price1"] = "Price1",
        ["Price2"] = "Price2",
        ["Price3"] = "Price3",
        ["DealerPrice"] = "Dealer",
        ["MajorCustomerPrice"] = "MajorCustomer",
        ["SpecialCustomerPrice"] = "SpecialCustomer",
    };

    private static readonly Dictionary<string, string> ClassificationAliases = new(StringComparer.Ordinal)
    {
        ["SpecialCustomer"] = "زبون مميز",
        ["CofeeShop"] = "مستهلك كبير",
        ["Dealer"] = "محل تجاري",
    };

    private static readonly Dictionary<string, string> UnmatchedTypeGuidance = new(StringComparer.Ordinal)
    {
        ["normal"] = "CustomerType normal has no approved alias. It was not mapped to عادي.",
        ["test"] = "Review whether this row is test data. No classification was invented.",
    };

    public static bool TryConvertDiscount(double? legacy, out decimal percent)
    {
        if (legacy is null)
        {
            percent = 0m;
            return true;
        }

        if (double.IsNaN(legacy.Value) || double.IsInfinity(legacy.Value) || legacy.Value < -0.000001d || legacy.Value > 100.000001d)
        {
            percent = 0m;
            return false;
        }

        var text = legacy.Value.ToString("0.000000", CultureInfo.InvariantCulture);
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value < 0m || value > 100m)
        {
            percent = 0m;
            return false;
        }

        percent = value <= 1m
            ? decimal.Round(value * 100m, 4, MidpointRounding.AwayFromZero)
            : decimal.Round(value, 4, MidpointRounding.AwayFromZero);
        return percent >= 0m && percent <= 100m;
    }

    public static string ClassifyPhone(string? rawPhone)
    {
        var normalized = PhoneNormalizer.Normalize(rawPhone);
        if (normalized is null)
        {
            return BlankPhone;
        }

        if (normalized.Any(character => !char.IsAsciiDigit(character)))
        {
            return NonNumericPhone;
        }

        if (IsSuspiciousPhone(normalized))
        {
            return SuspiciousPhone;
        }

        if (!string.Equals(rawPhone!.Trim(), normalized, StringComparison.Ordinal))
        {
            return FormattedPhone;
        }

        return NormalPhone;
    }

    public static string FormatSourceDiscount(double? value)
    {
        if (value is null)
        {
            return "";
        }

        if (double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return "INVALID_NUMBER";
        }

        return value.Value.ToString("0.########", CultureInfo.InvariantCulture);
    }

    public static LegacyCustomerDryRunPlan Plan(LegacyCustomerImportRequest request)
    {
        var priceTypes = IndexPriceTypes(request.PriceTypes);
        var classifications = BuildClassifications(request.Classifications, priceTypes);
        var classificationByName = classifications
            .GroupBy(row => row.LegacyName, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        var duplicateClassificationNames = classifications
            .GroupBy(row => row.LegacyName, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        var imported = IndexImported(request.AlreadyImported);
        var duplicateKeys = request.Customers
            .GroupBy(customer => customer.CustomerId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();

        var decisions = request.Customers
            .Select(customer => Decide(
                customer,
                request.BusinessId,
                priceTypes,
                classificationByName,
                duplicateClassificationNames,
                imported,
                duplicateKeys))
            .ToList();

        var duplicateGroups = BuildDuplicatePhones(decisions);
        var duplicatePhoneSet = duplicateGroups
            .Select(group => group.NormalizedPhone)
            .ToHashSet(StringComparer.Ordinal);
        decisions = decisions
            .Select(decision => decision.NormalizedPhone is not null && duplicatePhoneSet.Contains(decision.NormalizedPhone)
                ? decision with { SecondaryWarnings = JoinWarning(decision.SecondaryWarnings, "DUPLICATE_NORMALIZED_PHONE") }
                : decision)
            .ToList();

        var outcomes = PrimaryOutcomes.ToDictionary(outcome => outcome, _ => 0, StringComparer.Ordinal);
        var phoneQuality = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [NormalPhone] = 0,
            [FormattedPhone] = 0,
            [BlankPhone] = 0,
            [NonNumericPhone] = 0,
            [SuspiciousPhone] = 0,
        };
        var customerTypes = new Dictionary<string, int>(StringComparer.Ordinal);
        var priceTypesSeen = new Dictionary<string, int>(StringComparer.Ordinal);
        var discounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var priceMapping = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["MAPPED"] = 0,
            ["NULL_PROPOSED_PRICE0"] = 0,
            ["COST"] = 0,
            ["COST_REPLACED"] = 0,
            ["UNMAPPED"] = 0,
        };
        var classificationMapping = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["EXACT_MATCH"] = 0,
            ["NULL_CLASSIFICATION"] = 0,
            ["UNKNOWN_CLASSIFICATION"] = 0,
        };

        foreach (var decision in decisions)
        {
            outcomes[decision.Outcome] = outcomes.GetValueOrDefault(decision.Outcome) + 1;
            phoneQuality[decision.PhoneQuality] = phoneQuality.GetValueOrDefault(decision.PhoneQuality) + 1;
            customerTypes[decision.CustomerTypeKey] = customerTypes.GetValueOrDefault(decision.CustomerTypeKey) + 1;
            priceTypesSeen[decision.PriceTypeKey] = priceTypesSeen.GetValueOrDefault(decision.PriceTypeKey) + 1;
            discounts[decision.DiscountGroupKey] = discounts.GetValueOrDefault(decision.DiscountGroupKey) + 1;
            priceMapping[decision.PriceMapping] = priceMapping.GetValueOrDefault(decision.PriceMapping) + 1;
            classificationMapping[decision.ClassificationMapping] = classificationMapping.GetValueOrDefault(decision.ClassificationMapping) + 1;
        }

        var outcomeTotal = outcomes.Values.Sum();
        var namedCustomers = decisions
            .Where(decision => !string.IsNullOrWhiteSpace(decision.Name))
            .GroupBy(decision => decision.Name!.Trim(), StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .ToList();
        var unexpectedDefaults = classifications
            .Where(row => row.LegacyIsDefault == true && !row.ProposedIsDefault)
            .Select(row => row.LegacyName)
            .ToList();
        var proposedDefault = classifications.SingleOrDefault(row => row.ProposedIsDefault);

        return new LegacyCustomerDryRunPlan
        {
            BusinessId = request.BusinessId,
            LegacySource = LegacySourceName,
            SourceCustomerCount = request.Customers.Count,
            SourceClassificationCount = request.Classifications.Count,
            DistinctLegacyCustomerIds = request.Customers.Select(customer => customer.CustomerId).Distinct().Count(),
            LegacyCustomerIdsUnique = duplicateKeys.Count == 0,
            Classifications = classifications,
            Decisions = decisions,
            Outcomes = outcomes,
            ReconciliationDifference = request.Customers.Count - outcomeTotal,
            PhoneQuality = phoneQuality,
            CustomerTypeCounts = customerTypes,
            PriceTypeCounts = priceTypesSeen,
            DiscountCounts = discounts,
            PriceMappingCounts = priceMapping,
            ClassificationMappingCounts = classificationMapping,
            CustomerTypeReconciliation = BuildReconciliation(decisions, decision => decision.CustomerTypeKey),
            PriceTypeReconciliation = BuildReconciliation(decisions, decision => decision.PriceTypeKey),
            DiscountReconciliation = BuildReconciliation(decisions, decision => decision.DiscountGroupKey),
            DuplicatePhoneGroups = duplicateGroups,
            DuplicatePhoneGroupCount = duplicateGroups.Count,
            CustomersInDuplicatePhoneGroups = duplicateGroups.Sum(group => group.CustomerCount),
            DuplicatePhoneGroupsSameName = duplicateGroups.Count(group => group.SameName),
            DuplicatePhoneGroupsDifferentName = duplicateGroups.Count(group => !group.SameName),
            DuplicatePhoneGroupsDifferentPriceType = duplicateGroups.Count(group => group.DifferentPriceType),
            DuplicatePhoneGroupsDifferentDiscount = duplicateGroups.Count(group => group.DifferentDiscount),
            DuplicateNameGroupCount = namedCustomers.Count,
            CustomersInDuplicateNameGroups = namedCustomers.Sum(group => group.Count()),
            BlankPhoneCount = phoneQuality.GetValueOrDefault(BlankPhone),
            CustomersWithNotes = request.Customers.Count(customer => !string.IsNullOrWhiteSpace(customer.Comments)),
            UnexpectedLegacyDefaultNames = unexpectedDefaults,
            ProposedDefaultClassificationCode = proposedDefault?.ProposedCode,
            DefaultClassificationRecommendation = BuildDefaultRecommendation(proposedDefault, unexpectedDefaults),
            DuplicateClassificationNames = duplicateClassificationNames.OrderBy(name => name, StringComparer.Ordinal).ToList(),
            UnknownCustomerTypeCounts = decisions
                .Where(decision => decision.ClassificationMapping == "UNKNOWN_CLASSIFICATION")
                .GroupBy(decision => decision.CustomerTypeKey, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal),
        };
    }

    private static bool IsSuspiciousPhone(string digits)
    {
        if (digits.Length is < 8 or > 12)
        {
            return true;
        }

        if (digits.Distinct().Count() == 1)
        {
            return true;
        }

        return digits.StartsWith("00", StringComparison.Ordinal);
    }

    private static Dictionary<string, CentralPriceTypeRef> IndexPriceTypes(IReadOnlyList<CentralPriceTypeRef> priceTypes)
    {
        var duplicate = priceTypes
            .GroupBy(price => price.Code.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"Central PriceType code '{duplicate.Key}' is duplicated.");
        }

        return priceTypes.ToDictionary(price => price.Code.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, Guid> IndexImported(IReadOnlyList<ExistingLegacyCustomer> imported)
    {
        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var row in imported)
        {
            result.TryAdd(row.LegacyCustomerKey, row.CustomerId);
        }

        return result;
    }

    private static List<LegacyClassificationProposal> BuildClassifications(
        IReadOnlyList<LegacyClassificationSource> sources,
        IReadOnlyDictionary<string, CentralPriceTypeRef> priceTypes)
    {
        var proposals = new List<LegacyClassificationProposal>(sources.Count);
        foreach (var source in sources)
        {
            var name = source.Name.Trim();
            ClassificationCodes.TryGetValue(name, out var code);
            var discountValid = TryConvertDiscount(source.Discount, out var percent);
            string? mappedCode = null;
            Guid? mappedId = null;
            string? priceReview = null;
            if (string.IsNullOrWhiteSpace(source.PriceType))
            {
                priceReview = "Classification has no legacy price type.";
            }
            else if (string.Equals(source.PriceType.Trim(), "Cost", StringComparison.OrdinalIgnoreCase))
            {
                priceReview = "Classification uses Cost. Do not create PriceType Cost.";
            }
            else if (LegacyPriceCodes.TryGetValue(source.PriceType.Trim(), out var targetCode))
            {
                mappedCode = targetCode;
                if (priceTypes.TryGetValue(targetCode, out var price))
                {
                    mappedId = price.PriceTypeId;
                }
                else
                {
                    priceReview = $"Mapped code {targetCode} is missing from current central PriceTypes.";
                }
            }
            else
            {
                priceReview = $"Legacy price type {source.PriceType.Trim()} has no approved code.";
            }

            var proposedDefault = code == "REGULAR"
                && mappedCode == "Price0"
                && mappedId is not null
                && discountValid;
            var review = new List<string>();
            if (code is null)
            {
                review.Add("No approved code for this classification name. None was invented.");
            }

            if (!discountValid)
            {
                review.Add("Legacy classification discount is outside 0..1 and was not converted.");
            }

            if (priceReview is not null)
            {
                review.Add(priceReview);
            }

            if (source.IsDefault == true && !proposedDefault)
            {
                review.Add("Legacy default flag is set. It was not copied.");
            }

            if (proposedDefault)
            {
                review.Add("Proposed as the only default for new customers.");
            }

            if (review.Count == 0)
            {
                review.Add("Proposed for review. Not inserted.");
            }

            proposals.Add(new LegacyClassificationProposal(
                name,
                code,
                name,
                string.IsNullOrWhiteSpace(source.PriceType) ? null : source.PriceType.Trim(),
                mappedCode,
                mappedId,
                FormatSourceDiscount(source.Discount),
                discountValid ? percent : null,
                source.IsDefault,
                proposedDefault,
                string.Join(" ", review)));
        }

        if (proposals.Count(row => row.ProposedIsDefault) > 1)
        {
            return proposals
                .Select(row => row with
                {
                    ProposedIsDefault = false,
                    ReviewNote = row.ReviewNote + " More than one default was proposed, so none was kept.",
                })
                .ToList();
        }

        return proposals;
    }

    private static LegacyCustomerDecision Decide(
        LegacyCustomerSource customer,
        Guid businessId,
        IReadOnlyDictionary<string, CentralPriceTypeRef> priceTypes,
        IReadOnlyDictionary<string, LegacyClassificationProposal> classificationByName,
        IReadOnlySet<string> duplicateClassificationNames,
        IReadOnlyDictionary<string, Guid> imported,
        IReadOnlySet<int> duplicateKeys)
    {
        var legacyKey = customer.CustomerId.ToString(CultureInfo.InvariantCulture);
        var normalizedPhone = PhoneNormalizer.Normalize(customer.Phone);
        var phoneQuality = ClassifyPhone(customer.Phone);
        var name = string.IsNullOrWhiteSpace(customer.Name) ? null : customer.Name.Trim();
        var customerType = string.IsNullOrWhiteSpace(customer.CustomerType) ? null : customer.CustomerType.Trim();
        if (customerType is not null && ClassificationAliases.TryGetValue(customerType, out var alias))
        {
            customerType = alias;
        }
        var legacyPrice = string.IsNullOrWhiteSpace(customer.PriceType) ? null : customer.PriceType.Trim();
        var discountValid = TryConvertDiscount(customer.Discount, out var percent);
        var notes = string.IsNullOrWhiteSpace(customer.Comments) ? null : customer.Comments.Trim();
        var warnings = new List<string>();

        LegacyClassificationProposal? classification = null;
        string classificationMapping;
        if (customerType is null)
        {
            classificationMapping = "NULL_CLASSIFICATION";
            warnings.Add("NULL_CLASSIFICATION");
        }
        else if (duplicateClassificationNames.Contains(customerType) || !classificationByName.TryGetValue(customerType, out classification))
        {
            classificationMapping = "UNKNOWN_CLASSIFICATION";
        }
        else
        {
            classificationMapping = "EXACT_MATCH";
            if (classification.ProposedCode is null)
            {
                warnings.Add("CLASSIFICATION_CODE_UNAPPROVED");
            }
        }

        var priceMapping = ClassifyPrice(legacyPrice, priceTypes, out var mappedPrice, out var priceExplanation);
        if (priceMapping == "NULL_PROPOSED_PRICE0")
        {
            warnings.Add("NULL_PRICE_TYPE_PROPOSED_PRICE0");
        }

        if (mappedPrice is { IsActive: false })
        {
            warnings.Add("INACTIVE_PRICE_TYPE");
        }

        string outcome;
        string explanation;
        string action;
        if (string.IsNullOrWhiteSpace(customer.Phone))
        {
            outcome = MissingCustomerNumber;
            explanation = "Missing CustomerNumber";
            action = "Do not invent a customer number.";
        }
        else if (PhoneNormalizer.IsWalkInPlaceholder(normalizedPhone))
        {
            outcome = SkippedWalkIn;
            explanation = "Legacy default/walk-in customer";
            action = "Do not create a customer row. POS walk-in stays customerId null.";
        }
        else if (string.Equals(customer.Phone.Trim(), "0599988877", StringComparison.Ordinal))
        {
            outcome = SkippedTestCustomer;
            explanation = "Legacy test customer";
            action = "Do not create a customer row.";
        }
        else if (customer.CustomerId <= 0)
        {
            outcome = ImpossibleLegacyKey;
            explanation = "Legacy CustomerID is not a positive identity.";
            action = "Correct the source key before any apply. Phone and name were not used as identity.";
        }
        else if (duplicateKeys.Contains(customer.CustomerId))
        {
            outcome = DuplicateLegacyKey;
            explanation = "Legacy CustomerID is duplicated. Import identity would not be unique.";
            action = "Resolve the duplicate CustomerID before any apply.";
        }
        else if (name is null)
        {
            outcome = InvalidName;
            explanation = "Missing CustomerName";
            action = "Supply a customer name before any apply.";
        }
        else if (name.Length > MaxNameLength)
        {
            outcome = InvalidName;
            explanation = "Name is longer than the Customer.Name column.";
            action = "Shorten the name before any apply. It was not truncated.";
        }
        else if (notes is not null && notes.Length > MaxNotesLength)
        {
            outcome = NotesTooLong;
            explanation = "Comments are longer than Customer.Notes. They were not truncated.";
            action = "Approve a shorter note before any apply.";
        }
        else if (!discountValid)
        {
            outcome = InvalidLegacyDiscount;
            explanation = $"Legacy discount {FormatSourceDiscount(customer.Discount)} is outside 0..100. It was not clamped.";
            action = "Approve an explicit percent or exclude this customer. Do not clamp the value.";
        }
        else if (priceMapping == "COST")
        {
            outcome = CostPriceReview;
            var classificationPrice = classification?.MappedDefaultPriceTypeCode;
            explanation = classificationPrice is null
                ? "Legacy PriceType Cost has no current PriceType. No Cost price type was proposed."
                : $"Legacy PriceType Cost has no current PriceType. Classification {classification!.LegacyName} uses {classificationPrice}. That price was not applied.";
            action = "Do not create PriceType Cost. Approve an existing PriceType before any apply.";
        }
        else if (priceMapping == "UNMAPPED")
        {
            outcome = UnmappedPriceType;
            explanation = priceExplanation;
            action = "Map this legacy price type to an existing PriceType code. Price0 was not used as a silent fallback.";
        }
        else if (classificationMapping == "UNKNOWN_CLASSIFICATION" || classificationMapping == "NULL_CLASSIFICATION")
        {
            outcome = UnknownClassification;
            explanation = classificationMapping == "NULL_CLASSIFICATION"
                ? "Legacy CustomerType is NULL"
                : $"CustomerType '{customerType}' is not an approved classification. No classification was invented.";
            action = UnmatchedTypeGuidance.TryGetValue(customer.CustomerType?.Trim() ?? "", out var guidance)
                ? guidance
                : "Approve an explicit classification mapping or correct the source text. No classification was invented.";
        }
        else if (customerType is not null && classification?.ProposedCode is null)
        {
            outcome = UnknownClassification;
            explanation = $"CustomerType '{customerType}' matches a classification row but has no approved code. None was invented.";
            action = "Approve a stable classification code before any apply.";
        }
        else if (imported.ContainsKey(legacyKey))
        {
            outcome = AlreadyImported;
            explanation = "BusinessID, LegacySource shop1, and LegacyCustomerKey already exist. The existing CustomerID would be reused.";
            action = "Do not create a second customer.";
        }
        else
        {
            outcome = ReadyToImport;
            explanation = "Eligible for a future central import. No row was written.";
            action = "Review with the classification and discount approvals. Do not apply in this step.";
        }

        if (outcome != InvalidLegacyDiscount && !discountValid)
        {
            warnings.Add(InvalidLegacyDiscount);
        }

        if (outcome != CostPriceReview && priceMapping == "COST")
        {
            warnings.Add(CostPriceReview);
            if (classification?.MappedDefaultPriceTypeCode is not null)
            {
                warnings.Add("CLASSIFICATION_PRICE_" + classification.MappedDefaultPriceTypeCode);
            }
        }

        if (outcome != UnknownClassification && classificationMapping == "UNKNOWN_CLASSIFICATION")
        {
            warnings.Add(UnknownClassification);
        }

        var assignment = outcome switch
        {
            AlreadyImported => ReuseExisting,
            ReadyToImport => NewGuidRequired,
            _ => "NOT_IMPORTED",
        };

        return new LegacyCustomerDecision(
            customer.CustomerId,
            legacyKey,
            businessId,
            customer.Phone,
            normalizedPhone,
            phoneQuality,
            name,
            customerType,
            customerType ?? "(null)",
            classification?.ProposedCode,
            legacyPrice,
            legacyPrice ?? "(null)",
            priceMapping == "COST" || priceMapping == "UNMAPPED" ? null : mappedPrice?.Code,
            priceMapping == "COST" || priceMapping == "UNMAPPED" ? null : mappedPrice?.PriceTypeId,
            priceMapping,
            classificationMapping,
            FormatSourceDiscount(customer.Discount),
            DiscountGroupKey(customer.Discount),
            discountValid ? percent : null,
            outcome,
            explanation,
            action,
            string.Join(" | ", warnings),
            assignment,
            outcome == AlreadyImported ? imported[legacyKey] : null);
    }

    private static string ClassifyPrice(
        string? legacyPrice,
        IReadOnlyDictionary<string, CentralPriceTypeRef> priceTypes,
        out CentralPriceTypeRef? mapped,
        out string explanation)
    {
        if (legacyPrice is null)
        {
            mapped = null;
            explanation = "Legacy PriceType is NULL";
            return "UNMAPPED";
        }

        if (string.Equals(legacyPrice, "Cost", StringComparison.OrdinalIgnoreCase))
        {
            mapped = null;
            explanation = "Legacy PriceType Cost is not a current PriceType.";
            return "COST";
        }

        if (!LegacyPriceCodes.TryGetValue(legacyPrice, out var code))
        {
            mapped = null;
            explanation = $"Legacy PriceType '{legacyPrice}' has no approved code. Price0 was not used.";
            return "UNMAPPED";
        }

        if (!priceTypes.TryGetValue(code, out var price))
        {
            mapped = null;
            explanation = $"Legacy PriceType '{legacyPrice}' maps to code {code}, which is missing from current central PriceTypes.";
            return "UNMAPPED";
        }

        mapped = price;
        explanation = $"Mapped to {code}.";
        return "MAPPED";
    }

    private static string DiscountGroupKey(double? legacy)
    {
        if (legacy is null)
        {
            return "NULL";
        }

        return TryConvertDiscount(legacy, out var percent)
            ? percent.ToString("0.0000", CultureInfo.InvariantCulture)
            : "INVALID";
    }

    private static List<DuplicatePhoneGroup> BuildDuplicatePhones(IReadOnlyList<LegacyCustomerDecision> decisions)
    {
        return decisions
            .Where(decision => decision.Outcome != SkippedWalkIn && decision.NormalizedPhone is not null)
            .GroupBy(decision => decision.NormalizedPhone!, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var names = group.Select(decision => decision.Name?.Trim() ?? "").Distinct(StringComparer.Ordinal).Count();
                var prices = group.Select(decision => decision.PriceTypeKey).Distinct(StringComparer.Ordinal).Count();
                var discounts = group.Select(decision => decision.DiscountGroupKey).Distinct(StringComparer.Ordinal).Count();
                return new DuplicatePhoneGroup(
                    group.Key,
                    group.Count(),
                    names == 1,
                    names,
                    prices > 1,
                    prices,
                    discounts > 1,
                    discounts,
                    string.Join(" ", group.Select(decision => decision.LegacyCustomerId).OrderBy(id => id)));
            })
            .ToList();
    }

    private static List<LegacyCountReconciliation> BuildReconciliation(
        IReadOnlyList<LegacyCustomerDecision> decisions,
        Func<LegacyCustomerDecision, string> key)
    {
        return decisions
            .GroupBy(key, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new LegacyCountReconciliation(
                group.Key,
                group.Count(),
                group.GroupBy(decision => decision.Outcome, StringComparer.Ordinal)
                    .OrderBy(outcome => outcome.Key, StringComparer.Ordinal)
                    .ToDictionary(outcome => outcome.Key, outcome => outcome.Count(), StringComparer.Ordinal)))
            .ToList();
    }

    private static string BuildDefaultRecommendation(
        LegacyClassificationProposal? proposedDefault,
        IReadOnlyList<string> unexpectedDefaults)
    {
        var legacy = unexpectedDefaults.Count == 0
            ? "No unexpected legacy default flag was found."
            : "Legacy IsItDefault is set on " + string.Join(", ", unexpectedDefaults) + ". That flag was not copied.";
        if (proposedDefault is null)
        {
            return "No classification was proposed as the default for new customers. Approve one before any apply. " + legacy;
        }

        var discount = proposedDefault.ProposedDefaultVariableValueDiscountPercent?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "unconverted";
        return $"Recommend {proposedDefault.Name} ({proposedDefault.ProposedCode}, {proposedDefault.MappedDefaultPriceTypeCode}, {discount}%) as the only default classification for new customers. {legacy}";
    }

    private static string JoinWarning(string existing, string warning)
    {
        return string.IsNullOrEmpty(existing) ? warning : existing + " | " + warning;
    }
}

public sealed record LegacyCustomerImportRequest(
    Guid BusinessId,
    IReadOnlyList<LegacyClassificationSource> Classifications,
    IReadOnlyList<LegacyCustomerSource> Customers,
    IReadOnlyList<CentralPriceTypeRef> PriceTypes,
    IReadOnlyList<ExistingLegacyCustomer> AlreadyImported);

public sealed record LegacyClassificationSource(string Name, double? Discount, string? PriceType, bool? IsDefault);

public sealed record LegacyCustomerSource(
    int CustomerId,
    string? Phone,
    string? Name,
    string? CustomerType,
    string? PriceType,
    double? Discount,
    string? Comments);

public sealed record CentralPriceTypeRef(string Code, Guid PriceTypeId, bool IsActive);

public sealed record ExistingLegacyCustomer(string LegacyCustomerKey, Guid CustomerId);

public sealed record LegacyClassificationProposal(
    string LegacyName,
    string? ProposedCode,
    string Name,
    string? LegacyPriceType,
    string? MappedDefaultPriceTypeCode,
    Guid? MappedPriceTypeId,
    string LegacyDiscount,
    decimal? ProposedDefaultVariableValueDiscountPercent,
    bool? LegacyIsDefault,
    bool ProposedIsDefault,
    string ReviewNote);

public sealed record LegacyCustomerDecision(
    int LegacyCustomerId,
    string LegacyCustomerKey,
    Guid BusinessId,
    string? RawPhone,
    string? NormalizedPhone,
    string PhoneQuality,
    string? Name,
    string? CustomerType,
    string CustomerTypeKey,
    string? ProposedClassificationCode,
    string? LegacyPriceType,
    string PriceTypeKey,
    string? ProposedPriceTypeCode,
    Guid? ProposedPriceTypeId,
    string PriceMapping,
    string ClassificationMapping,
    string LegacyDiscount,
    string DiscountGroupKey,
    decimal? ProposedVariableValueDiscountPercent,
    string Outcome,
    string Explanation,
    string RecommendedAction,
    string SecondaryWarnings,
    string CustomerIdAssignment,
    Guid? ExistingCustomerId);

public sealed record DuplicatePhoneGroup(
    string NormalizedPhone,
    int CustomerCount,
    bool SameName,
    int DistinctNames,
    bool DifferentPriceType,
    int DistinctPriceTypes,
    bool DifferentDiscount,
    int DistinctDiscounts,
    string LegacyCustomerIds);

public sealed record LegacyCountReconciliation(
    string Key,
    int Count,
    IReadOnlyDictionary<string, int> Outcomes);

public sealed class LegacyCustomerDryRunPlan
{
    public required Guid BusinessId { get; init; }

    public required string LegacySource { get; init; }

    public required int SourceCustomerCount { get; init; }

    public required int SourceClassificationCount { get; init; }

    public required int DistinctLegacyCustomerIds { get; init; }

    public required bool LegacyCustomerIdsUnique { get; init; }

    public required IReadOnlyList<LegacyClassificationProposal> Classifications { get; init; }

    public required IReadOnlyList<LegacyCustomerDecision> Decisions { get; init; }

    public required IReadOnlyDictionary<string, int> Outcomes { get; init; }

    public required int ReconciliationDifference { get; init; }

    public required IReadOnlyDictionary<string, int> PhoneQuality { get; init; }

    public required IReadOnlyDictionary<string, int> CustomerTypeCounts { get; init; }

    public required IReadOnlyDictionary<string, int> PriceTypeCounts { get; init; }

    public required IReadOnlyDictionary<string, int> DiscountCounts { get; init; }

    public required IReadOnlyDictionary<string, int> PriceMappingCounts { get; init; }

    public required IReadOnlyDictionary<string, int> ClassificationMappingCounts { get; init; }

    public required IReadOnlyList<LegacyCountReconciliation> CustomerTypeReconciliation { get; init; }

    public required IReadOnlyList<LegacyCountReconciliation> PriceTypeReconciliation { get; init; }

    public required IReadOnlyList<LegacyCountReconciliation> DiscountReconciliation { get; init; }

    public required IReadOnlyList<DuplicatePhoneGroup> DuplicatePhoneGroups { get; init; }

    public required int DuplicatePhoneGroupCount { get; init; }

    public required int CustomersInDuplicatePhoneGroups { get; init; }

    public required int DuplicatePhoneGroupsSameName { get; init; }

    public required int DuplicatePhoneGroupsDifferentName { get; init; }

    public required int DuplicatePhoneGroupsDifferentPriceType { get; init; }

    public required int DuplicatePhoneGroupsDifferentDiscount { get; init; }

    public required int DuplicateNameGroupCount { get; init; }

    public required int CustomersInDuplicateNameGroups { get; init; }

    public required int BlankPhoneCount { get; init; }

    public required int CustomersWithNotes { get; init; }

    public required IReadOnlyList<string> UnexpectedLegacyDefaultNames { get; init; }

    public required string? ProposedDefaultClassificationCode { get; init; }

    public required string DefaultClassificationRecommendation { get; init; }

    public required IReadOnlyList<string> DuplicateClassificationNames { get; init; }

    public required IReadOnlyDictionary<string, int> UnknownCustomerTypeCounts { get; init; }

    public int ReadyToImportCount => Outcomes.GetValueOrDefault(LegacyCustomerImportPlanner.ReadyToImport);

    public int AlreadyImportedCount => Outcomes.GetValueOrDefault(LegacyCustomerImportPlanner.AlreadyImported);

    public int SkippedWalkInCount => Outcomes.GetValueOrDefault(LegacyCustomerImportPlanner.SkippedWalkIn);
}
