using XtraPOS.Api.Customers;
using XtraPOS.Api.Import;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class LegacyCustomerImportTests
{
    private static readonly Guid BusinessId = Guid.Parse("422CDA3A-498B-4EAE-9EDA-1DB23E399AE1");
    private static readonly Guid Price0Id = Guid.Parse("00000000-0000-0000-0000-000000000010");

    private static readonly CentralPriceTypeRef[] PriceTypes =
    [
        new("Price0", Price0Id, true),
        new("Price1", Guid.Parse("00000000-0000-0000-0000-000000000011"), true),
        new("Price2", Guid.Parse("00000000-0000-0000-0000-000000000012"), true),
        new("Price3", Guid.Parse("00000000-0000-0000-0000-000000000013"), true),
        new("Dealer", Guid.Parse("00000000-0000-0000-0000-000000000014"), true),
        new("MajorCustomer", Guid.Parse("00000000-0000-0000-0000-000000000015"), true),
        new("SpecialCustomer", Guid.Parse("00000000-0000-0000-0000-000000000016"), true),
    ];

    [Theory]
    [InlineData(0.22d, "22.0000")]
    [InlineData(0.318182d, "31.8182")]
    [InlineData(0d, "0.0000")]
    [InlineData(1d, "100.0000")]
    [InlineData(0.17999999999999999d, "18.0000")]
    [InlineData(0.31818180000000001d, "31.8182")]
    [InlineData(0.26262600000000003d, "26.2626")]
    [InlineData(0.10000000000000001d, "10.0000")]
    public void DiscountFraction_ConvertsToPercent(double legacy, string expected)
    {
        Assert.True(LegacyCustomerImportPlanner.TryConvertDiscount(legacy, out var percent));
        Assert.Equal(expected, percent.ToString("0.0000"));
    }

    [Fact]
    public void NullDiscount_BecomesZero()
    {
        Assert.True(LegacyCustomerImportPlanner.TryConvertDiscount(null, out var percent));
        Assert.Equal(0m, percent);
    }

    [Theory]
    [InlineData(-0.1d)]
    [InlineData(100.1d)]
    public void DiscountOutsideZeroToOneHundred_IsInvalidAndNotClamped(double legacy)
    {
        Assert.False(LegacyCustomerImportPlanner.TryConvertDiscount(legacy, out var percent));
        Assert.Equal(0m, percent);
    }

    [Fact]
    public void WalkInPhone_IsSkippedAndNotReady()
    {
        var plan = Plan(new LegacyCustomerSource(709465, "1", "عادي ", "عادي", "Price0", 0d, null));

        var decision = Assert.Single(plan.Decisions);
        Assert.Equal(LegacyCustomerImportPlanner.SkippedWalkIn, decision.Outcome);
        Assert.Equal("NOT_IMPORTED", decision.CustomerIdAssignment);
        Assert.Equal(0, plan.ReadyToImportCount);
        Assert.Equal(1, plan.SkippedWalkInCount);
        Assert.Equal(0, plan.ReconciliationDifference);
    }

    [Fact]
    public void LegacyKey_IsTheIdempotencyIdentity()
    {
        var existingId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var plan = Plan(
            [
                new LegacyCustomerSource(15, "0591234567", "أحمد", "عادي", "Price1", 0.22d, null),
                new LegacyCustomerSource(16, "0591234567", "أحمد", "عادي", "Price1", 0.22d, null),
            ],
            [new ExistingLegacyCustomer("15", existingId)]);

        var imported = plan.Decisions.Single(decision => decision.LegacyCustomerId == 15);
        var created = plan.Decisions.Single(decision => decision.LegacyCustomerId == 16);
        Assert.Equal(LegacyCustomerImportPlanner.AlreadyImported, imported.Outcome);
        Assert.Equal(existingId, imported.ExistingCustomerId);
        Assert.Equal(LegacyCustomerImportPlanner.ReuseExisting, imported.CustomerIdAssignment);
        Assert.Equal("15", imported.LegacyCustomerKey);
        Assert.Equal(LegacyCustomerImportPlanner.ReadyToImport, created.Outcome);
        Assert.Equal(LegacyCustomerImportPlanner.NewGuidRequired, created.CustomerIdAssignment);
        Assert.Null(created.ExistingCustomerId);
        Assert.Equal(2, plan.Decisions.Count);
        Assert.Equal(1, plan.DuplicatePhoneGroupCount);
        Assert.Equal(2, plan.CustomersInDuplicatePhoneGroups);
    }

    [Fact]
    public void Discount33_StaysThirtyThreePercent()
    {
        var plan = Plan(new LegacyCustomerSource(706599, "0593054038", "كرم", "مستهلك كبير", "MajorCustomerPrice", 33d, null));

        var decision = Assert.Single(plan.Decisions);
        Assert.Equal(LegacyCustomerImportPlanner.ReadyToImport, decision.Outcome);
        Assert.Equal(33m, decision.ProposedVariableValueDiscountPercent);
        Assert.DoesNotContain("3300", decision.Explanation);
    }

    [Fact]
    public void CostPrice_RequiresReviewAndDoesNotInventAPriceType()
    {
        var plan = Plan(new LegacyCustomerSource(704718, "00592116117", "حماده ناصر", "مستهلك كبير", "Cost", 0.262626d, null));

        var decision = Assert.Single(plan.Decisions);
        Assert.Equal(LegacyCustomerImportPlanner.CostPriceReview, decision.Outcome);
        Assert.Null(decision.ProposedPriceTypeCode);
        Assert.Null(decision.ProposedPriceTypeId);
        Assert.Contains("MajorCustomer", decision.Explanation);
        Assert.Equal(26.2626m, decision.ProposedVariableValueDiscountPercent);
    }

    [Fact]
    public void NullPriceType_ProposesPrice0()
    {
        var plan = Plan(new LegacyCustomerSource(40, "0592222222", "بدون سعر", "عادي", null, 0d, null));

        var decision = Assert.Single(plan.Decisions);
        Assert.Equal(LegacyCustomerImportPlanner.UnmappedPriceType, decision.Outcome);
        Assert.Null(decision.ProposedPriceTypeCode);
        Assert.Equal("Legacy PriceType is NULL", decision.Explanation);
    }

    [Fact]
    public void UnknownPriceType_IsAnException()
    {
        var plan = Plan(new LegacyCustomerSource(41, "0593333333", "سعر غريب", "عادي", "Wholesale", 0d, null));

        var decision = Assert.Single(plan.Decisions);
        Assert.Equal(LegacyCustomerImportPlanner.UnmappedPriceType, decision.Outcome);
        Assert.Null(decision.ProposedPriceTypeCode);
        Assert.Contains("Price0 was not used", decision.Explanation);
    }

    [Fact]
    public void PhoneNormalization_ReusesPhoneNormalizerAndKeepsLeadingZero()
    {
        const string raw = "059-123 4567";
        var plan = Plan(new LegacyCustomerSource(42, raw, "منسق", "عادي", "Price0", 0d, null));

        var decision = Assert.Single(plan.Decisions);
        Assert.Equal(PhoneNormalizer.Normalize(raw), decision.NormalizedPhone);
        Assert.Equal("0591234567", decision.NormalizedPhone);
        Assert.StartsWith("0", decision.NormalizedPhone);
        Assert.Equal(LegacyCustomerImportPlanner.FormattedPhone, decision.PhoneQuality);
        Assert.Equal(LegacyCustomerImportPlanner.ReadyToImport, decision.Outcome);
    }

    [Fact]
    public void ArabicDigits_AreNormalizedByPhoneNormalizer()
    {
        const string raw = "٠٥٩١٢٣٤٥٦٧";
        var plan = Plan(new LegacyCustomerSource(43, raw, "أرقام", "عادي", "Price0", 0d, null));

        var decision = Assert.Single(plan.Decisions);
        Assert.Equal(PhoneNormalizer.Normalize(raw), decision.NormalizedPhone);
        Assert.Equal("0591234567", decision.NormalizedPhone);
    }

    [Fact]
    public void DuplicatePhone_DoesNotMergeCustomers()
    {
        var plan = Plan(
            new LegacyCustomerSource(50, "0591234567", "أحمد", "عادي", "Price0", 0d, null),
            new LegacyCustomerSource(51, "059-1234567", "محمود", "ذهبي", "Price1", 0.22d, null));

        Assert.Equal(2, plan.ReadyToImportCount);
        Assert.Equal(2, plan.Decisions.Count);
        var group = Assert.Single(plan.DuplicatePhoneGroups);
        Assert.Equal("0591234567", group.NormalizedPhone);
        Assert.Equal(2, group.CustomerCount);
        Assert.False(group.SameName);
        Assert.True(group.DifferentPriceType);
        Assert.True(group.DifferentDiscount);
    }

    [Fact]
    public void BlankPhone_IsAllowed()
    {
        var plan = Plan(new LegacyCustomerSource(60, "   ", "بدون هاتف", "عادي", "Price0", null, null));

        var decision = Assert.Single(plan.Decisions);
        Assert.Equal(LegacyCustomerImportPlanner.MissingCustomerNumber, decision.Outcome);
        Assert.Null(decision.NormalizedPhone);
        Assert.Equal(LegacyCustomerImportPlanner.BlankPhone, decision.PhoneQuality);
        Assert.Equal(0m, decision.ProposedVariableValueDiscountPercent);
        Assert.Equal("60", decision.LegacyCustomerKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankName_IsRejected(string? name)
    {
        var plan = Plan(new LegacyCustomerSource(61, "0591234567", name, "عادي", "Price0", 0d, null));

        var decision = Assert.Single(plan.Decisions);
        Assert.Equal(LegacyCustomerImportPlanner.InvalidName, decision.Outcome);
        Assert.Equal(0, plan.ReadyToImportCount);
    }

    [Fact]
    public void ClassificationMapping_ProposesRegularAsTheOnlyDefault()
    {
        var plan = Plan(new LegacyCustomerSource(70, "0591234567", "زبون", "عادي", "Price0", 0.22d, null));

        var regular = plan.Classifications.Single(row => row.LegacyName == "عادي");
        var special = plan.Classifications.Single(row => row.LegacyName == "زبون مميز");
        var commercial = plan.Classifications.Single(row => row.LegacyName == "محل تجاري");
        var major = plan.Classifications.Single(row => row.LegacyName == "مستهلك كبير");
        Assert.Equal("REGULAR", regular.ProposedCode);
        Assert.True(regular.ProposedIsDefault);
        Assert.False(regular.LegacyIsDefault);
        Assert.Equal("Price0", regular.MappedDefaultPriceTypeCode);
        Assert.Equal(Price0Id, regular.MappedPriceTypeId);
        Assert.Equal("SPECIAL", special.ProposedCode);
        Assert.False(special.ProposedIsDefault);
        Assert.True(special.LegacyIsDefault);
        Assert.Equal(10.0000m, special.ProposedDefaultVariableValueDiscountPercent);
        Assert.Equal(31.8182m, commercial.ProposedDefaultVariableValueDiscountPercent);
        Assert.Equal(26.2626m, major.ProposedDefaultVariableValueDiscountPercent);
        Assert.Equal(["زبون مميز"], plan.UnexpectedLegacyDefaultNames);
        Assert.Equal("REGULAR", plan.ProposedDefaultClassificationCode);
        Assert.Contains("عادي", plan.DefaultClassificationRecommendation);
        Assert.Equal("REGULAR", Assert.Single(plan.Decisions).ProposedClassificationCode);
        Assert.Equal(22.0000m, Assert.Single(plan.Decisions).ProposedVariableValueDiscountPercent);
    }

    [Fact]
    public void UnknownCustomerType_IsNotInvented()
    {
        var plan = Plan(new LegacyCustomerSource(80, "0595555555", "خاص", "SpecialCustomer", "SpecialCustomerPrice", 0.1d, null));

        var decision = Assert.Single(plan.Decisions);
        Assert.Equal(LegacyCustomerImportPlanner.ReadyToImport, decision.Outcome);
        Assert.Equal("SPECIAL", decision.ProposedClassificationCode);
    }

    [Fact]
    public void NullCustomerType_CanStayReady()
    {
        var plan = Plan(new LegacyCustomerSource(81, "0596666666", "بلا تصنيف", null, "Price0", 0d, null));

        var decision = Assert.Single(plan.Decisions);
        Assert.Equal(LegacyCustomerImportPlanner.UnknownClassification, decision.Outcome);
        Assert.Equal("Legacy CustomerType is NULL", decision.Explanation);
    }

    [Fact]
    public void CostWithUnknownClassification_StaysInCostReview()
    {
        var plan = Plan(new LegacyCustomerSource(82, "0597777777", "مقهى", "CofeeShop", "Cost", 0.1d, null));

        var decision = Assert.Single(plan.Decisions);
        Assert.Equal(LegacyCustomerImportPlanner.CostPriceReview, decision.Outcome);
        Assert.Equal("MAJOR_CONSUMER", decision.ProposedClassificationCode);
    }

    [Fact]
    public void RepeatedDryRun_ProducesIdenticalCounts()
    {
        var customers = new[]
        {
            new LegacyCustomerSource(1, "1", "عادي", "عادي", "Price0", 0d, null),
            new LegacyCustomerSource(2, "0591234567", "أحمد", "عادي", "Price1", 0.22d, null),
            new LegacyCustomerSource(3, "0591234567", "محمود", "ذهبي", null, 0.318182d, null),
            new LegacyCustomerSource(4, null, "بدون", "عادي", "Price0", null, null),
            new LegacyCustomerSource(5, "0590000000", " ", "عادي", "Price0", 33d, null),
            new LegacyCustomerSource(6, "0591111111", "تكلفة", "محل تجاري", "Cost", 0.1d, null),
            new LegacyCustomerSource(7, "0592222222", "غريب", "test", "Wholesale", 0d, null),
        };

        var first = Plan(customers);
        var second = Plan(customers);

        Assert.Equal(0, first.ReconciliationDifference);
        Assert.Equal(customers.Length, first.Outcomes.Values.Sum());
        Assert.Equal(first.Outcomes.OrderBy(pair => pair.Key), second.Outcomes.OrderBy(pair => pair.Key));
        Assert.Equal(first.PhoneQuality.OrderBy(pair => pair.Key), second.PhoneQuality.OrderBy(pair => pair.Key));
        Assert.Equal(first.ReadyToImportCount, second.ReadyToImportCount);
        Assert.Equal(
            first.Decisions.Select(decision => (decision.LegacyCustomerId, decision.Outcome, decision.ProposedVariableValueDiscountPercent, decision.ProposedPriceTypeCode)),
            second.Decisions.Select(decision => (decision.LegacyCustomerId, decision.Outcome, decision.ProposedVariableValueDiscountPercent, decision.ProposedPriceTypeCode)));
    }

    [Fact]
    public void DryRunStatements_DoNotWrite()
    {
        Assert.NotEmpty(LegacyCustomerImportSql.Statements);
        foreach (var statement in LegacyCustomerImportSql.Statements)
        {
            var text = " " + statement.ToUpperInvariant().Replace('\n', ' ').Replace('\r', ' ') + " ";
            Assert.Contains("SELECT", text);
            Assert.DoesNotContain(" INSERT ", text);
            Assert.DoesNotContain(" UPDATE ", text);
            Assert.DoesNotContain(" DELETE ", text);
            Assert.DoesNotContain(" MERGE ", text);
            Assert.DoesNotContain(" ALTER ", text);
            Assert.DoesNotContain(" DROP ", text);
            Assert.DoesNotContain(" TRUNCATE ", text);
            Assert.DoesNotContain(" INTO ", text);
            Assert.DoesNotContain(" EXEC ", text);
        }
    }

    [Fact]
    public void ApplyMode_IsRefusedBeforeAnyConnection()
    {
        var message = LegacyCustomerImportCommand.RefuseBeforeConnection(
        [
            "--import-legacy-customers",
            "--source-database",
            "shop1",
            "--target-business",
            LegacyCustomerImportCommand.ApprovedBusinessIdText,
            "--dry-run",
            "--apply",
        ]);

        Assert.NotNull(message);
        Assert.Contains("confirm-database", message, StringComparison.Ordinal);
        Assert.Null(LegacyCustomerImportCommand.RefuseBeforeConnection(
        [
            "--import-legacy-customers",
            "--source-database",
            "shop1",
            "--target-business",
            LegacyCustomerImportCommand.ApprovedBusinessIdText,
            "--dry-run",
        ]));
    }

    private static LegacyCustomerDryRunPlan Plan(
        params LegacyCustomerSource[] customers)
    {
        return Plan(customers, []);
    }

    private static LegacyCustomerDryRunPlan Plan(
        IReadOnlyList<LegacyCustomerSource> customers,
        IReadOnlyList<ExistingLegacyCustomer> imported)
    {
        return LegacyCustomerImportPlanner.Plan(new LegacyCustomerImportRequest(
            BusinessId,
            StandardClassifications(),
            customers,
            PriceTypes,
            imported));
    }

    private static LegacyClassificationSource[] StandardClassifications()
    {
        return
        [
            new("عادي", 0d, "Price0", false),
            new("برونزي", 0.12d, "Price3", false),
            new("فضي", 0.17999999999999999d, "Price2", false),
            new("ذهبي", 0.22d, "Price1", false),
            new("زبون مميز", 0.10000000000000001d, "SpecialCustomerPrice", true),
            new("محل تجاري", 0.31818180000000001d, "DealerPrice", false),
            new("مستهلك كبير", 0.26262600000000003d, "MajorCustomerPrice", false),
        ];
    }
}
