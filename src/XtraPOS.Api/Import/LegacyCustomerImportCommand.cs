using System.Data;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Import;

public static class LegacyCustomerImportCommand
{
    public const string ApprovedBusinessIdText = "422CDA3A-498B-4EAE-9EDA-1DB23E399AE1";
    public const string SourceDatabaseName = "shop1";
    public const string BranchTwoDatabaseName = "XtraPOS_Branch_02";

    public static readonly Guid ApprovedBusinessId = Guid.Parse(ApprovedBusinessIdText);

    public static bool IsRequested(IReadOnlyList<string> args)
    {
        return args.Any(arg => string.Equals(arg, "--import-legacy-customers", StringComparison.Ordinal));
    }

    public static string? RefuseBeforeConnection(IReadOnlyList<string> args)
    {
        if (!IsRequested(args))
        {
            return null;
        }

        if (args.Any(arg => string.Equals(arg, "--apply", StringComparison.Ordinal)))
        {
            var confirmed = ReadOption(args, "--confirm-database");
            if (!string.Equals(confirmed, CentralDatabase.Name, StringComparison.Ordinal))
            {
                return "Legacy customer apply requires --confirm-database XtraPOS_Central. Nothing was written.";
            }
        }
        else if (args.Any(arg => string.Equals(arg, "--commit", StringComparison.Ordinal)))
        {
            return "Legacy customer apply is not enabled. No customer or classification was written.";
        }
        else if (!args.Any(arg => string.Equals(arg, "--dry-run", StringComparison.Ordinal)))
        {
            return "Legacy customer import requires --dry-run or --apply. Nothing was written.";
        }

        var source = ReadOption(args, "--source-database");
        if (!string.Equals(source, SourceDatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            return "Legacy customer import requires --source-database shop1. Nothing was written.";
        }

        var business = ReadOption(args, "--target-business");
        if (!Guid.TryParse(business, out var businessId) || businessId != ApprovedBusinessId)
        {
            return "Legacy customer import requires --target-business 422CDA3A-498B-4EAE-9EDA-1DB23E399AE1. Nothing was written.";
        }

        return null;
    }

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        IServiceProvider services,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var refusal = RefuseBeforeConnection(args);
        if (refusal is not null)
        {
            output.WriteLine(refusal);
            return 1;
        }

        var options = services.GetRequiredService<IOptions<SqlServerOptions>>().Value;
        var connections = services.GetRequiredService<SqlConnectionFactory>();
        if (!string.Equals(connections.ConfiguredDatabase, LinkedBranchSetupCommand.DatabaseName, StringComparison.Ordinal))
        {
            output.WriteLine("Legacy customer dry run requires the configured branch database XtraPOS_Branch_01. Nothing was written.");
            return 2;
        }

        if (!IsLocalServer(connections.ConfiguredServer))
        {
            output.WriteLine("Legacy customer dry run reads shop1 only from the local SQL Server. Nothing was written.");
            return 2;
        }

        var central = await ReadCentralRouteAsync(services, cancellationToken);
        if (central is null)
        {
            output.WriteLine("Legacy customer dry run could not read the saved central route. Nothing was written.");
            return 2;
        }

        if (!string.Equals(central.DatabaseName, CentralDatabase.Name, StringComparison.Ordinal))
        {
            output.WriteLine("The saved central route is not database XtraPOS_Central. A database name alone was not accepted. Nothing was written.");
            return 2;
        }

        var centralConnection = ReadOnlyConnection(central.BuildConnectionString(CentralDatabase.Name));
        var branchConnection = ReadOnlyConnection(connections.CreateBranchConnectionString());
        var branchTwoConnection = LocalCatalogConnection(options, BranchTwoDatabaseName);
        var shopConnection = LocalCatalogConnection(options, SourceDatabaseName);

        DryRunFingerprints before;
        try
        {
            before = await ReadFingerprintsAsync(centralConnection, branchConnection, branchTwoConnection, shopConnection, cancellationToken);
        }
        catch (Exception exception)
        {
            output.WriteLine("Legacy customer dry run could not read the before fingerprints. Nothing was written.");
            output.WriteLine(SafeMessage(exception));
            return 2;
        }

        try
        {
            if (!string.Equals(before.Central.DatabaseName, CentralDatabase.Name, StringComparison.Ordinal)
                || !string.Equals(before.Branch.DatabaseName, LinkedBranchSetupCommand.DatabaseName, StringComparison.Ordinal)
                || !string.Equals(before.BranchTwo.DatabaseName, BranchTwoDatabaseName, StringComparison.Ordinal)
                || !string.Equals(before.Shop1.DatabaseName, SourceDatabaseName, StringComparison.OrdinalIgnoreCase))
            {
                output.WriteLine("A connected database name did not match the approved dry-run route. Nothing was written.");
                await WriteAfterFailureAsync(output, before, centralConnection, branchConnection, branchTwoConnection, shopConnection, cancellationToken);
                return 2;
            }

            var source = await ReadSourceAsync(centralConnection, shopConnection, ApprovedBusinessId, cancellationToken);
            if (source.Customers.Count != before.Shop1.Customers
                || source.Classifications.Count != before.Shop1.Classifications)
            {
                throw new InvalidOperationException("The shop1 read count did not match the shop1 fingerprint.");
            }
            var plan = LegacyCustomerImportPlanner.Plan(new LegacyCustomerImportRequest(
                ApprovedBusinessId,
                source.Classifications,
                source.Customers,
                source.PriceTypes,
                source.AlreadyImported));
            if (!CountsReconcile(plan))
            {
                output.WriteLine("Legacy customer dry run reconciliation failed. Nothing was written.");
                await WriteAfterFailureAsync(output, before, centralConnection, branchConnection, branchTwoConnection, shopConnection, cancellationToken);
                return 2;
            }

            var directory = ArtifactDirectory(services);
            Directory.CreateDirectory(directory);
            var after = await ReadFingerprintsAsync(centralConnection, branchConnection, branchTwoConnection, shopConnection, cancellationToken);
            var unchanged = before == after;
            WriteReports(directory, source, plan, before, after, unchanged);
            WriteConsoleSummary(output, source, plan, before, after, unchanged, directory);
            if (args.Any(arg => string.Equals(arg, "--apply", StringComparison.Ordinal)))
            {
                var writable = new SqlConnectionStringBuilder(central.BuildConnectionString(CentralDatabase.Name))
                {
                    ApplicationIntent = ApplicationIntent.ReadWrite,
                    ApplicationName = "XtraPOS.LegacyCustomerImport",
                }.ConnectionString;
                var applied = await LegacyCustomerApply.ExecuteAsync(writable, plan, source.Customers, output, cancellationToken);
                output.WriteLine($"Created={applied.Created} Updated={applied.Updated} Skipped={applied.Skipped} Conflict={applied.Conflict} Failed={applied.Failed}");
                return applied.Failed == 0 && applied.Created + applied.Updated + applied.Skipped + applied.Conflict == plan.SourceCustomerCount ? 0 : 2;
            }

            output.WriteLine(unchanged
                ? "LEGACY_CUSTOMER_IMPORT_DRY_RUN_READY"
                : "LEGACY_CUSTOMER_IMPORT_DRY_RUN_NEEDS_REVIEW");
            return unchanged ? 0 : 2;
        }
        catch (Exception exception)
        {
            output.WriteLine("Legacy customer dry run failed. No customer import was applied.");
            output.WriteLine(SafeMessage(exception));
            await WriteAfterFailureAsync(output, before, centralConnection, branchConnection, branchTwoConnection, shopConnection, cancellationToken);
            return 2;
        }
    }

    private static bool CountsReconcile(LegacyCustomerDryRunPlan plan)
    {
        return plan.ReconciliationDifference == 0
            && plan.Outcomes.Values.Sum() == plan.SourceCustomerCount
            && plan.PhoneQuality.Values.Sum() == plan.SourceCustomerCount
            && plan.PriceMappingCounts.Values.Sum() == plan.SourceCustomerCount
            && plan.ClassificationMappingCounts.Values.Sum() == plan.SourceCustomerCount
            && plan.CustomerTypeCounts.Values.Sum() == plan.SourceCustomerCount
            && plan.PriceTypeCounts.Values.Sum() == plan.SourceCustomerCount
            && plan.DiscountCounts.Values.Sum() == plan.SourceCustomerCount
            && plan.CustomerTypeReconciliation.Sum(row => row.Count) == plan.SourceCustomerCount
            && plan.PriceTypeReconciliation.Sum(row => row.Count) == plan.SourceCustomerCount
            && plan.DiscountReconciliation.Sum(row => row.Count) == plan.SourceCustomerCount;
    }

    private static async Task<SqlEndpointSettings?> ReadCentralRouteAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        var stored = await services.GetRequiredService<CentralConnectionFileStore>().ReadAsync(cancellationToken);
        if (stored is not null)
        {
            return stored;
        }

        var connections = services.GetRequiredService<SqlConnectionFactory>();
        if (!string.Equals(connections.ConfiguredDatabase, LinkedBranchSetupCommand.DatabaseName, StringComparison.Ordinal))
        {
            return null;
        }

        using var scope = services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<SyncEndpointStore>()
            .ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
    }

    private static async Task<DryRunSource> ReadSourceAsync(
        string centralConnection,
        string shopConnection,
        Guid businessId,
        CancellationToken cancellationToken)
    {
        await using var central = await OpenAsync(centralConnection, cancellationToken);
        var businessName = await ReadBusinessNameAsync(central, businessId, cancellationToken);
        if (!string.Equals(businessName, "Smoker World", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The target business on the saved central route is not Smoker World.");
        }

        var priceTypes = await ReadPriceTypesAsync(central, cancellationToken);
        var imported = await ReadImportedAsync(central, businessId, cancellationToken);
        await using var shop = await OpenAsync(shopConnection, cancellationToken);
        var classifications = await ReadClassificationsAsync(shop, cancellationToken);
        var customers = await ReadCustomersAsync(shop, cancellationToken);
        DebtOverlap debt;
        try
        {
            debt = await ReadDebtOverlapAsync(shop, cancellationToken);
        }
        catch (SqlException)
        {
            debt = new DebtOverlap(null, null, null);
        }

        return new DryRunSource(businessName, priceTypes, imported, classifications, customers, debt);
    }

    private static async Task<string> ReadBusinessNameAsync(SqlConnection connection, Guid businessId, CancellationToken cancellationToken)
    {
        await using var command = Command(LegacyCustomerImportSql.BusinessName, connection);
        command.Parameters.Add("@businessId", SqlDbType.UniqueIdentifier).Value = businessId;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            throw new InvalidOperationException("The target business was not found on the saved central route.");
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private static async Task<List<CentralPriceTypeRef>> ReadPriceTypesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<CentralPriceTypeRef>();
        await using var command = Command(LegacyCustomerImportSql.PriceTypes, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
            {
                continue;
            }

            rows.Add(new CentralPriceTypeRef(reader.GetString(0).Trim(), reader.GetGuid(1), !reader.IsDBNull(2) && reader.GetBoolean(2)));
        }

        return rows;
    }

    private static async Task<List<ExistingLegacyCustomer>> ReadImportedAsync(
        SqlConnection connection,
        Guid businessId,
        CancellationToken cancellationToken)
    {
        var rows = new List<ExistingLegacyCustomer>();
        await using var command = Command(LegacyCustomerImportSql.ImportedCustomers, connection);
        command.Parameters.Add("@businessId", SqlDbType.UniqueIdentifier).Value = businessId;
        command.Parameters.Add("@source", SqlDbType.NVarChar, 64).Value = LegacyCustomerImportPlanner.LegacySourceName;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new ExistingLegacyCustomer(reader.GetString(0), reader.GetGuid(1)));
        }

        var duplicate = rows.GroupBy(row => row.LegacyCustomerKey, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException("Central already has a duplicated shop1 legacy customer key.");
        }

        return rows;
    }

    private static async Task<List<LegacyClassificationSource>> ReadClassificationsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<LegacyClassificationSource>();
        await using var command = Command(LegacyCustomerImportSql.Classifications, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LegacyClassificationSource(
                reader.IsDBNull(0) ? "" : reader.GetString(0),
                ReadDouble(reader, 1),
                ReadString(reader, 2),
                reader.IsDBNull(3) ? null : reader.GetBoolean(3)));
        }

        return rows;
    }

    private static async Task<List<LegacyCustomerSource>> ReadCustomersAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var rows = new List<LegacyCustomerSource>();
        await using var command = Command(LegacyCustomerImportSql.Customers, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new LegacyCustomerSource(
                reader.GetInt32(0),
                ReadString(reader, 1),
                ReadString(reader, 2),
                ReadString(reader, 3),
                ReadString(reader, 4),
                ReadDouble(reader, 5),
                ReadString(reader, 6)));
        }

        return rows;
    }

    private static async Task<DebtOverlap> ReadDebtOverlapAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        return new DebtOverlap(
            await ReadNullableIntAsync(connection, LegacyCustomerImportSql.DebtCustomerOverlap, cancellationToken),
            await ReadNullableIntAsync(connection, LegacyCustomerImportSql.CreditLimitRows, cancellationToken),
            await ReadNullableIntAsync(connection, LegacyCustomerImportSql.CreditLimitCustomerOverlap, cancellationToken));
    }

    private static async Task<int?> ReadNullableIntAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = Command(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<DryRunFingerprints> ReadFingerprintsAsync(
        string centralConnection,
        string branchConnection,
        string branchTwoConnection,
        string shopConnection,
        CancellationToken cancellationToken)
    {
        await using var central = await OpenAsync(centralConnection, cancellationToken);
        await using var branch = await OpenAsync(branchConnection, cancellationToken);
        await using var branchTwo = await OpenAsync(branchTwoConnection, cancellationToken);
        await using var shop = await OpenAsync(shopConnection, cancellationToken);
        return new DryRunFingerprints(
            await ReadCentralFingerprintAsync(central, cancellationToken),
            await ReadBranchFingerprintAsync(branch, cancellationToken),
            await ReadBranchFingerprintAsync(branchTwo, cancellationToken),
            await ReadShopFingerprintAsync(shop, cancellationToken));
    }

    private static async Task<CentralFingerprint> ReadCentralFingerprintAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = Command(LegacyCustomerImportSql.CentralFingerprint, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Central fingerprint returned no row.");
        }

        return new CentralFingerprint(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3), reader.GetInt32(4));
    }

    private static async Task<BranchFingerprint> ReadBranchFingerprintAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = Command(LegacyCustomerImportSql.BranchFingerprint, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Branch fingerprint returned no row.");
        }

        return new BranchFingerprint(
            reader.GetString(0),
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.GetInt32(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9));
    }

    private static async Task<Shop1Fingerprint> ReadShopFingerprintAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = Command(LegacyCustomerImportSql.ShopFingerprint, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Shop1 fingerprint returned no row.");
        }

        return new Shop1Fingerprint(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2));
    }

    private static void WriteReports(
        string directory,
        DryRunSource source,
        LegacyCustomerDryRunPlan plan,
        DryRunFingerprints before,
        DryRunFingerprints after,
        bool unchanged)
    {
        var summary = BuildSummary(source, plan, before, after, unchanged);
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        File.WriteAllText(Path.Combine(directory, "summary.json"), json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        WriteClassifications(Path.Combine(directory, "classification-mapping.csv"), plan);
        WriteReady(Path.Combine(directory, "ready-customers.csv"), plan);
        WriteExceptions(Path.Combine(directory, "exceptions.csv"), plan);
        WriteDuplicatePhones(Path.Combine(directory, "duplicate-phones.csv"), plan);
        WritePhoneQuality(Path.Combine(directory, "phone-quality.csv"), plan);
    }

    private static object BuildSummary(
        DryRunSource source,
        LegacyCustomerDryRunPlan plan,
        DryRunFingerprints before,
        DryRunFingerprints after,
        bool unchanged)
    {
        return new
        {
            mode = "dry-run",
            writes = 0,
            applyEnabled = false,
            customerIdPreview = "NEW_GUID_REQUIRED is non-binding. No customer GUID was stored.",
            legacySource = LegacyCustomerImportPlanner.LegacySourceName,
            targetBusinessId = ApprovedBusinessId,
            businessName = source.BusinessName,
            centralDatabase = CentralDatabase.Name,
            sourceCustomerCount = plan.SourceCustomerCount,
            sourceClassificationCount = plan.SourceClassificationCount,
            distinctLegacyCustomerIds = plan.DistinctLegacyCustomerIds,
            legacyCustomerIdsUnique = plan.LegacyCustomerIdsUnique,
            reconciliationDifference = plan.ReconciliationDifference,
            outcomes = plan.Outcomes,
            priceMapping = plan.PriceMappingCounts,
            classificationMapping = plan.ClassificationMappingCounts,
            phoneQuality = plan.PhoneQuality,
            blankPhoneCount = plan.BlankPhoneCount,
            duplicateNameGroupCount = plan.DuplicateNameGroupCount,
            customersInDuplicateNameGroups = plan.CustomersInDuplicateNameGroups,
            duplicatePhoneGroupCount = plan.DuplicatePhoneGroupCount,
            customersInDuplicatePhoneGroups = plan.CustomersInDuplicatePhoneGroups,
            duplicatePhoneGroupsSameName = plan.DuplicatePhoneGroupsSameName,
            duplicatePhoneGroupsDifferentName = plan.DuplicatePhoneGroupsDifferentName,
            duplicatePhoneGroupsDifferentPriceType = plan.DuplicatePhoneGroupsDifferentPriceType,
            duplicatePhoneGroupsDifferentDiscount = plan.DuplicatePhoneGroupsDifferentDiscount,
            customersWithNotes = plan.CustomersWithNotes,
            proposedDefaultClassificationCode = plan.ProposedDefaultClassificationCode,
            defaultClassificationRecommendation = plan.DefaultClassificationRecommendation,
            unexpectedLegacyDefaultNames = plan.UnexpectedLegacyDefaultNames,
            duplicateClassificationNames = plan.DuplicateClassificationNames,
            centralPriceTypes = source.PriceTypes.Select(price => new { price.Code, priceTypeId = price.PriceTypeId, price.IsActive }),
            classifications = plan.Classifications,
            unknownCustomerTypeCounts = plan.UnknownCustomerTypeCounts,
            customerTypeCounts = plan.CustomerTypeCounts,
            priceTypeCounts = plan.PriceTypeCounts,
            discountCounts = plan.DiscountCounts,
            customerTypeReconciliation = plan.CustomerTypeReconciliation,
            priceTypeReconciliation = plan.PriceTypeReconciliation,
            discountReconciliation = plan.DiscountReconciliation,
            walkIn = ReviewRows(plan, LegacyCustomerImportPlanner.SkippedWalkIn),
            invalidNames = ReviewRows(plan, LegacyCustomerImportPlanner.InvalidName),
            invalidDiscounts = ReviewRows(plan, LegacyCustomerImportPlanner.InvalidLegacyDiscount),
            costPriceReview = ReviewRows(plan, LegacyCustomerImportPlanner.CostPriceReview),
            unmappedPriceTypes = ReviewRows(plan, LegacyCustomerImportPlanner.UnmappedPriceType),
            alreadyImported = plan.AlreadyImportedCount,
            debtCustomerOverlap = source.Debt.DebtCustomers,
            creditLimitRows = source.Debt.CreditLimitRows,
            creditLimitCustomerOverlap = source.Debt.CreditLimitCustomerOverlap,
            debtImported = false,
            fingerprintsUnchanged = unchanged,
            before,
            after,
            approvalsRequiredBeforeApply = Approvals(plan),
        };
    }

    private static IReadOnlyList<object> ReviewRows(LegacyCustomerDryRunPlan plan, string outcome)
    {
        return plan.Decisions
            .Where(decision => decision.Outcome == outcome)
            .OrderBy(decision => decision.LegacyCustomerId)
            .Select(decision => (object)new
            {
                decision.LegacyCustomerId,
                decision.Name,
                phone = decision.RawPhone,
                decision.CustomerType,
                decision.LegacyPriceType,
                discount = decision.LegacyDiscount,
                decision.ProposedClassificationCode,
                decision.Explanation,
                decision.RecommendedAction,
                decision.SecondaryWarnings,
            })
            .ToList();
    }

    private static IReadOnlyList<string> Approvals(LegacyCustomerDryRunPlan plan)
    {
        var approvals = new List<string>
        {
            "Do not apply this import until these review items are approved.",
            plan.DefaultClassificationRecommendation,
            "Duplicate normalized phones are information only. No customers were merged.",
            "Legacy phone 1 stays out of Customer Master. Anonymous sales use CustomerID null.",
        };
        if (plan.UnknownCustomerTypeCounts.Count > 0)
        {
            approvals.Add("CustomerType values that are not classification names stay out of the ready set. Approve an explicit mapping before apply.");
        }

        if (plan.Outcomes.GetValueOrDefault(LegacyCustomerImportPlanner.CostPriceReview) > 0)
        {
            approvals.Add("Cost customers need an approved existing PriceType. Do not create PriceType Cost.");
        }

        if (plan.Outcomes.GetValueOrDefault(LegacyCustomerImportPlanner.InvalidLegacyDiscount) > 0)
        {
            approvals.Add("Discounts outside 0..1 were not converted and must not be clamped.");
        }

        if (plan.Outcomes.GetValueOrDefault(LegacyCustomerImportPlanner.InvalidName) > 0)
        {
            approvals.Add("Blank names were not invented from phone numbers.");
        }

        return approvals;
    }

    private static void WriteClassifications(string path, LegacyCustomerDryRunPlan plan)
    {
        var lines = new List<string>
        {
            "LegacyName,ProposedCode,Name,LegacyPriceType,MappedDefaultPriceTypeCode,MappedPriceTypeId,LegacyDiscount,ProposedDefaultVariableValueDiscountPercent,LegacyIsDefault,ProposedIsDefault,ReviewNote",
        };
        lines.AddRange(plan.Classifications.Select(row => string.Join(",",
            Csv(row.LegacyName),
            Csv(row.ProposedCode),
            Csv(row.Name),
            Csv(row.LegacyPriceType),
            Csv(row.MappedDefaultPriceTypeCode),
            Csv(row.MappedPriceTypeId?.ToString("D", CultureInfo.InvariantCulture)),
            Csv(row.LegacyDiscount),
            Csv(row.ProposedDefaultVariableValueDiscountPercent?.ToString("0.0000", CultureInfo.InvariantCulture)),
            Csv(row.LegacyIsDefault?.ToString()),
            Csv(row.ProposedIsDefault ? "true" : "false"),
            Csv(row.ReviewNote))));
        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WriteReady(string path, LegacyCustomerDryRunPlan plan)
    {
        var lines = new List<string>
        {
            "LegacyCustomerID,LegacyCustomerKey,LegacyPhone,NormalizedPhone,PhoneQuality,Name,LegacyCustomerType,ProposedClassificationCode,LegacyPriceType,ProposedPriceTypeCode,LegacyDiscount,ProposedVariableValueDiscountPercent,SecondaryWarnings,CustomerIdAssignment,ImportStatus",
        };
        lines.AddRange(plan.Decisions
            .Where(decision => decision.Outcome == LegacyCustomerImportPlanner.ReadyToImport)
            .OrderBy(decision => decision.LegacyCustomerId)
            .Select(decision => string.Join(",",
                decision.LegacyCustomerId.ToString(CultureInfo.InvariantCulture),
                Csv(decision.LegacyCustomerKey),
                Csv(decision.RawPhone),
                Csv(decision.NormalizedPhone),
                Csv(decision.PhoneQuality),
                Csv(decision.Name),
                Csv(decision.CustomerType),
                Csv(decision.ProposedClassificationCode),
                Csv(decision.LegacyPriceType),
                Csv(decision.ProposedPriceTypeCode),
                Csv(decision.LegacyDiscount),
                Csv(decision.ProposedVariableValueDiscountPercent?.ToString("0.0000", CultureInfo.InvariantCulture)),
                Csv(decision.SecondaryWarnings),
                Csv(decision.CustomerIdAssignment),
                Csv(decision.Outcome))));
        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WriteExceptions(string path, LegacyCustomerDryRunPlan plan)
    {
        var lines = new List<string>
        {
            "LegacyCustomerID,Name,Phone,CustomerType,PriceType,Discount,ExceptionCode,Explanation,RecommendedAction,SecondaryWarnings",
        };
        lines.AddRange(plan.Decisions
            .Where(decision => decision.Outcome is not (LegacyCustomerImportPlanner.ReadyToImport or LegacyCustomerImportPlanner.AlreadyImported))
            .OrderBy(decision => decision.Outcome, StringComparer.Ordinal)
            .ThenBy(decision => decision.LegacyCustomerId)
            .Select(decision => string.Join(",",
                decision.LegacyCustomerId.ToString(CultureInfo.InvariantCulture),
                Csv(decision.Name),
                Csv(decision.RawPhone),
                Csv(decision.CustomerType),
                Csv(decision.LegacyPriceType),
                Csv(decision.LegacyDiscount),
                Csv(decision.Outcome),
                Csv(decision.Explanation),
                Csv(decision.RecommendedAction),
                Csv(decision.SecondaryWarnings))));
        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WriteDuplicatePhones(string path, LegacyCustomerDryRunPlan plan)
    {
        var lines = new List<string>
        {
            "NormalizedPhone,CustomerCount,SameName,DistinctNames,DifferentPriceType,DistinctPriceTypes,DifferentDiscount,DistinctDiscounts,LegacyCustomerIds",
        };
        lines.AddRange(plan.DuplicatePhoneGroups.Select(group => string.Join(",",
            Csv(group.NormalizedPhone),
            group.CustomerCount.ToString(CultureInfo.InvariantCulture),
            group.SameName ? "true" : "false",
            group.DistinctNames.ToString(CultureInfo.InvariantCulture),
            group.DifferentPriceType ? "true" : "false",
            group.DistinctPriceTypes.ToString(CultureInfo.InvariantCulture),
            group.DifferentDiscount ? "true" : "false",
            group.DistinctDiscounts.ToString(CultureInfo.InvariantCulture),
            Csv(group.LegacyCustomerIds))));
        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WritePhoneQuality(string path, LegacyCustomerDryRunPlan plan)
    {
        var lines = new List<string>
        {
            "LegacyCustomerID,Name,RawPhone,NormalizedPhone,PhoneQuality,PrimaryOutcome",
        };
        lines.AddRange(plan.Decisions
            .Where(decision => decision.PhoneQuality != LegacyCustomerImportPlanner.NormalPhone)
            .OrderBy(decision => decision.PhoneQuality, StringComparer.Ordinal)
            .ThenBy(decision => decision.LegacyCustomerId)
            .Select(decision => string.Join(",",
                decision.LegacyCustomerId.ToString(CultureInfo.InvariantCulture),
                Csv(decision.Name),
                Csv(decision.RawPhone),
                Csv(decision.NormalizedPhone),
                Csv(decision.PhoneQuality),
                Csv(decision.Outcome))));
        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static void WriteConsoleSummary(
        TextWriter output,
        DryRunSource source,
        LegacyCustomerDryRunPlan plan,
        DryRunFingerprints before,
        DryRunFingerprints after,
        bool unchanged,
        string directory)
    {
        output.WriteLine("Legacy customer import dry run. No customer or classification was written.");
        output.WriteLine($"Business={source.BusinessName} BusinessID={ApprovedBusinessIdText} Central={CentralDatabase.Name} Source={SourceDatabaseName}");
        output.WriteLine($"SourceCustomers={plan.SourceCustomerCount} Classifications={plan.SourceClassificationCount} ReconciliationDifference={plan.ReconciliationDifference}");
        foreach (var outcome in LegacyCustomerImportPlanner.PrimaryOutcomes)
        {
            output.WriteLine($"{outcome}={plan.Outcomes.GetValueOrDefault(outcome)}");
        }

        output.WriteLine($"PriceMapped={plan.PriceMappingCounts.GetValueOrDefault("MAPPED")} NullPriceProposedPrice0={plan.PriceMappingCounts.GetValueOrDefault("NULL_PROPOSED_PRICE0")} Cost={plan.PriceMappingCounts.GetValueOrDefault("COST")} UnmappedPrice={plan.PriceMappingCounts.GetValueOrDefault("UNMAPPED")}");
        output.WriteLine($"ExactClassification={plan.ClassificationMappingCounts.GetValueOrDefault("EXACT_MATCH")} NullClassification={plan.ClassificationMappingCounts.GetValueOrDefault("NULL_CLASSIFICATION")} UnknownClassification={plan.ClassificationMappingCounts.GetValueOrDefault("UNKNOWN_CLASSIFICATION")}");
        output.WriteLine($"NormalPhone={plan.PhoneQuality.GetValueOrDefault(LegacyCustomerImportPlanner.NormalPhone)} FormattedPhone={plan.PhoneQuality.GetValueOrDefault(LegacyCustomerImportPlanner.FormattedPhone)} BlankPhone={plan.PhoneQuality.GetValueOrDefault(LegacyCustomerImportPlanner.BlankPhone)} NonNumericPhone={plan.PhoneQuality.GetValueOrDefault(LegacyCustomerImportPlanner.NonNumericPhone)} SuspiciousPhone={plan.PhoneQuality.GetValueOrDefault(LegacyCustomerImportPlanner.SuspiciousPhone)}");
        output.WriteLine($"DuplicatePhoneGroups={plan.DuplicatePhoneGroupCount} CustomersInGroups={plan.CustomersInDuplicatePhoneGroups} SameName={plan.DuplicatePhoneGroupsSameName} DifferentName={plan.DuplicatePhoneGroupsDifferentName} DifferentPrice={plan.DuplicatePhoneGroupsDifferentPriceType} DifferentDiscount={plan.DuplicatePhoneGroupsDifferentDiscount}");
        output.WriteLine($"DuplicateNameGroups={plan.DuplicateNameGroupCount} CustomersInDuplicateNames={plan.CustomersInDuplicateNameGroups}");
        output.WriteLine(plan.DefaultClassificationRecommendation);
        output.WriteLine($"DebtCustomerOverlap={FormatOptional(source.Debt.DebtCustomers)} CreditLimitRows={FormatOptional(source.Debt.CreditLimitRows)} CreditLimitCustomerOverlap={FormatOptional(source.Debt.CreditLimitCustomerOverlap)} DebtImported=false");
        output.WriteLine($"FingerprintsUnchanged={unchanged}");
        WriteFingerprint(output, "CentralBefore", before.Central);
        WriteFingerprint(output, "CentralAfter", after.Central);
        WriteFingerprint(output, "Branch01Before", before.Branch);
        WriteFingerprint(output, "Branch01After", after.Branch);
        WriteFingerprint(output, "Branch02Before", before.BranchTwo);
        WriteFingerprint(output, "Branch02After", after.BranchTwo);
        output.WriteLine($"Shop1Before customers={before.Shop1.Customers} classifications={before.Shop1.Classifications}");
        output.WriteLine($"Shop1After customers={after.Shop1.Customers} classifications={after.Shop1.Classifications}");
        output.WriteLine($"ReportDirectory={directory}");
    }

    private static void WriteFingerprint(TextWriter output, string label, CentralFingerprint fingerprint)
    {
        output.WriteLine($"{label} database={fingerprint.DatabaseName} customers={fingerprint.Customers} classifications={fingerprint.CustomerClassifications} permissions={fingerprint.Permissions} migrations={fingerprint.Migrations}");
    }

    private static void WriteFingerprint(TextWriter output, string label, BranchFingerprint fingerprint)
    {
        output.WriteLine($"{label} database={fingerprint.DatabaseName} customers={fingerprint.Customers} classifications={fingerprint.CustomerClassifications} permissions={fingerprint.Permissions} migrations={fingerprint.Migrations} items={fingerprint.Items} itemPrices={fingerprint.ItemPrices} branchPriceOverrides={fingerprint.BranchPriceOverrides} balances={fingerprint.BranchStockBalances} movements={fingerprint.StockMovements}");
    }

    private static async Task WriteAfterFailureAsync(
        TextWriter output,
        DryRunFingerprints before,
        string centralConnection,
        string branchConnection,
        string branchTwoConnection,
        string shopConnection,
        CancellationToken cancellationToken)
    {
        try
        {
            var after = await ReadFingerprintsAsync(centralConnection, branchConnection, branchTwoConnection, shopConnection, cancellationToken);
            output.WriteLine($"FingerprintsUnchanged={before == after}");
            WriteFingerprint(output, "CentralAfter", after.Central);
            WriteFingerprint(output, "Branch01After", after.Branch);
            WriteFingerprint(output, "Branch02After", after.BranchTwo);
            output.WriteLine($"Shop1After customers={after.Shop1.Customers} classifications={after.Shop1.Classifications}");
        }
        catch (Exception exception)
        {
            output.WriteLine("The after fingerprint could not be read.");
            output.WriteLine(SafeMessage(exception));
        }

        output.WriteLine("LEGACY_CUSTOMER_IMPORT_DRY_RUN_NEEDS_REVIEW");
    }

    private static string ArtifactDirectory(IServiceProvider services)
    {
        var contentRoot = services.GetRequiredService<IHostEnvironment>().ContentRootPath;
        var directory = new DirectoryInfo(contentRoot);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XtraPOS.sln")))
            {
                return Path.Combine(directory.FullName, "artifacts", "customer-import");
            }

            directory = directory.Parent;
        }

        return Path.Combine(contentRoot, "artifacts", "customer-import");
    }

    private static SqlCommand Command(string sql, SqlConnection connection)
    {
        return new SqlCommand(sql, connection) { CommandTimeout = 180 };
    }

    private static async Task<SqlConnection> OpenAsync(string connectionString, CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static string ReadOnlyConnection(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            ApplicationIntent = ApplicationIntent.ReadOnly,
            ApplicationName = "XtraPOS.LegacyCustomerDryRun",
            ConnectTimeout = Math.Max(new SqlConnectionStringBuilder(connectionString).ConnectTimeout, 30),
        };
        return builder.ConnectionString;
    }

    private static string LocalCatalogConnection(SqlServerOptions options, string catalog)
    {
        if (catalog is not (SourceDatabaseName or BranchTwoDatabaseName))
        {
            throw new InvalidOperationException("The dry run refused to open a catalog outside shop1 and XtraPOS_Branch_02.");
        }

        var server = options.Server.Trim();
        if (!IsLocalServer(server))
        {
            throw new InvalidOperationException("Legacy shop1 can be read only from the local SQL Server.");
        }

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = catalog,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = Math.Clamp(options.ConnectTimeoutSeconds, 1, 120),
            ApplicationName = "XtraPOS.LegacyCustomerDryRun",
            ApplicationIntent = ApplicationIntent.ReadOnly,
            IntegratedSecurity = string.IsNullOrWhiteSpace(options.User),
        };
        if (!builder.IntegratedSecurity)
        {
            builder.UserID = options.User;
            builder.Password = options.Password;
        }

        return builder.ConnectionString;
    }

    private static bool IsLocalServer(string server)
    {
        return server.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || server.Equals(".", StringComparison.Ordinal)
            || server.Equals("(local)", StringComparison.OrdinalIgnoreCase)
            || server.Equals("127.0.0.1", StringComparison.Ordinal);
    }

    private static string? ReadString(SqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static double? ReadDouble(SqlDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
    }

    private static string FormatOptional(int? value)
    {
        return value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
    }

    private static string? ReadOption(IReadOnlyList<string> args, string name)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], name, StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    private static string Csv(string? value)
    {
        var text = value ?? "";
        if (text.Contains('"') || text.Contains(',') || text.Contains('\n') || text.Contains('\r'))
        {
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        return text;
    }

    private static string SafeMessage(Exception exception)
    {
        var message = exception.Message;
        var passwordAt = message.IndexOf("Password=", StringComparison.OrdinalIgnoreCase);
        if (passwordAt >= 0)
        {
            message = message[..passwordAt] + "Password=(omitted)";
        }

        var pwdAt = message.IndexOf("pwd=", StringComparison.OrdinalIgnoreCase);
        if (pwdAt >= 0)
        {
            message = message[..pwdAt] + "pwd=(omitted)";
        }

        return message;
    }
}

public static class LegacyCustomerImportSql
{
    public const string DatabaseName = "SELECT DB_NAME()";

    public const string BusinessName = """
        SELECT BusinessName
        FROM dbo.Businesses
        WHERE BusinessID = @businessId
        """;

    public const string PriceTypes = """
        SELECT PriceCode, PriceTypeID, IsActive
        FROM dbo.PriceTypes
        """;

    public const string ImportedCustomers = """
        SELECT LegacyCustomerKey, CustomerID
        FROM dbo.Customers
        WHERE BusinessID = @businessId
          AND LegacySource COLLATE DATABASE_DEFAULT = @source COLLATE DATABASE_DEFAULT
          AND LegacyCustomerKey IS NOT NULL
        """;

    public const string Classifications = """
        SELECT CustomerType, Discount, PriceType, IsItDefault
        FROM dbo.CustomersClassifications
        ORDER BY Serial
        """;

    public const string Customers = """
        SELECT CustomerID, CustomerPhoneNumber, CustomerName, CustomerType, PriceType, Discount, Comments
        FROM dbo.Customers
        ORDER BY CustomerID
        """;

    public const string CentralFingerprint = """
        SELECT
            DB_NAME(),
            CAST(ISNULL((SELECT SUM(p.rows) FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id INNER JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE s.name = N'dbo' AND t.name = N'Customers'), -1) AS int),
            CAST(ISNULL((SELECT SUM(p.rows) FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id INNER JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE s.name = N'dbo' AND t.name = N'CustomerClassifications'), -1) AS int),
            CAST(ISNULL((SELECT SUM(p.rows) FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id INNER JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE s.name = N'dbo' AND t.name = N'Permissions'), -1) AS int),
            CAST(ISNULL((SELECT SUM(p.rows) FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id INNER JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE s.name = N'dbo' AND t.name = N'DatabaseSchemaHistory'), -1) AS int)
        """;

    public const string BranchFingerprint = """
        SELECT
            DB_NAME(),
            CAST(ISNULL((SELECT SUM(p.rows) FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id INNER JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE s.name = N'dbo' AND t.name = N'Customers'), -1) AS int),
            CAST(ISNULL((SELECT SUM(p.rows) FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id INNER JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE s.name = N'dbo' AND t.name = N'CustomerClassifications'), -1) AS int),
            CAST(ISNULL((SELECT SUM(p.rows) FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id INNER JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE s.name = N'dbo' AND t.name = N'Permissions'), -1) AS int),
            CAST(ISNULL((SELECT SUM(p.rows) FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id INNER JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE s.name = N'dbo' AND t.name = N'DatabaseSchemaHistory'), -1) AS int),
            CAST(ISNULL((SELECT SUM(p.rows) FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id INNER JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE s.name = N'dbo' AND t.name = N'Items'), -1) AS int),
            CAST(ISNULL((SELECT SUM(p.rows) FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id INNER JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE s.name = N'dbo' AND t.name = N'ItemPrices'), -1) AS int),
            CASE WHEN COL_LENGTH(N'dbo.ItemPrices', N'BranchID') IS NULL THEN -1 ELSE (SELECT COUNT(*) FROM dbo.ItemPrices WHERE BranchID IS NOT NULL) END,
            CAST(ISNULL((SELECT SUM(p.rows) FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id INNER JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE s.name = N'dbo' AND t.name = N'BranchStockBalances'), -1) AS int),
            CAST(ISNULL((SELECT SUM(p.rows) FROM sys.tables AS t INNER JOIN sys.schemas AS s ON s.schema_id = t.schema_id INNER JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1) WHERE s.name = N'dbo' AND t.name = N'StockMovements'), -1) AS int)
        """;

    public const string ShopFingerprint = """
        SELECT
            DB_NAME(),
            (SELECT COUNT(*) FROM dbo.Customers),
            (SELECT COUNT(*) FROM dbo.CustomersClassifications)
        """;

    public const string DebtCustomerOverlap = """
        SELECT
            CASE
                WHEN OBJECT_ID(N'dbo.Debts', N'U') IS NULL OR COL_LENGTH(N'dbo.Debts', N'CustomerPhoneNumber') IS NULL THEN CAST(NULL AS int)
                ELSE (
                    SELECT COUNT(DISTINCT c.CustomerID)
                    FROM dbo.Customers AS c
                    WHERE EXISTS (
                        SELECT 1
                        FROM dbo.Debts AS d
                        WHERE d.CustomerPhoneNumber COLLATE DATABASE_DEFAULT = c.CustomerPhoneNumber COLLATE DATABASE_DEFAULT
                    )
                )
            END
        """;

    public const string CreditLimitRows = """
        SELECT
            CASE
                WHEN OBJECT_ID(N'dbo.CustomersAllowedToDebt', N'U') IS NULL THEN CAST(NULL AS int)
                ELSE (SELECT COUNT(*) FROM dbo.CustomersAllowedToDebt)
            END
        """;

    public const string CreditLimitCustomerOverlap = """
        SELECT
            CASE
                WHEN OBJECT_ID(N'dbo.CustomersAllowedToDebt', N'U') IS NULL
                    OR COL_LENGTH(N'dbo.CustomersAllowedToDebt', N'CustomerPhoneNumber') IS NULL THEN CAST(NULL AS int)
                ELSE (
                    SELECT COUNT(DISTINCT c.CustomerID)
                    FROM dbo.Customers AS c
                    WHERE EXISTS (
                        SELECT 1
                        FROM dbo.CustomersAllowedToDebt AS d
                        WHERE d.CustomerPhoneNumber COLLATE DATABASE_DEFAULT = c.CustomerPhoneNumber COLLATE DATABASE_DEFAULT
                    )
                )
            END
        """;

    public static IReadOnlyList<string> Statements { get; } =
    [
        DatabaseName,
        BusinessName,
        PriceTypes,
        ImportedCustomers,
        Classifications,
        Customers,
        CentralFingerprint,
        BranchFingerprint,
        ShopFingerprint,
        DebtCustomerOverlap,
        CreditLimitRows,
        CreditLimitCustomerOverlap,
    ];
}

public sealed record CentralFingerprint(
    string DatabaseName,
    int Customers,
    int CustomerClassifications,
    int Permissions,
    int Migrations);

public sealed record BranchFingerprint(
    string DatabaseName,
    int Customers,
    int CustomerClassifications,
    int Permissions,
    int Migrations,
    int Items,
    int ItemPrices,
    int BranchPriceOverrides,
    int BranchStockBalances,
    int StockMovements);

public sealed record Shop1Fingerprint(string DatabaseName, int Customers, int Classifications);

public sealed record DryRunFingerprints(
    CentralFingerprint Central,
    BranchFingerprint Branch,
    BranchFingerprint BranchTwo,
    Shop1Fingerprint Shop1);

internal sealed record DebtOverlap(int? DebtCustomers, int? CreditLimitRows, int? CreditLimitCustomerOverlap);

internal sealed record DryRunSource(
    string BusinessName,
    IReadOnlyList<CentralPriceTypeRef> PriceTypes,
    IReadOnlyList<ExistingLegacyCustomer> AlreadyImported,
    IReadOnlyList<LegacyClassificationSource> Classifications,
    IReadOnlyList<LegacyCustomerSource> Customers,
    DebtOverlap Debt);
