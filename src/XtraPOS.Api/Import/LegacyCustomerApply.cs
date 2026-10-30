using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;

namespace XtraPOS.Api.Import;

public static class LegacyCustomerApply
{
    public readonly record struct ApplyCounts(int Created, int Updated, int Skipped, int Conflict, int Failed);

    public static async Task<ApplyCounts> ExecuteAsync(
        string connectionString,
        LegacyCustomerDryRunPlan plan,
        IReadOnlyList<LegacyCustomerSource> customers,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var comments = customers.ToDictionary(customer => customer.CustomerId, customer => string.IsNullOrWhiteSpace(customer.Comments) ? null : customer.Comments.Trim());
        var eligible = plan.Decisions.Where(decision => decision.Outcome is LegacyCustomerImportPlanner.ReadyToImport or LegacyCustomerImportPlanner.AlreadyImported).ToList();
        var skipped = plan.Decisions.Count(decision => decision.Outcome is LegacyCustomerImportPlanner.SkippedWalkIn or LegacyCustomerImportPlanner.SkippedTestCustomer);
        var conflict = plan.Decisions.Count - eligible.Count - skipped;
        foreach (var group in plan.Decisions.Where(decision => decision.Outcome is not (LegacyCustomerImportPlanner.ReadyToImport or LegacyCustomerImportPlanner.AlreadyImported)).GroupBy(decision => decision.Outcome).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            output.WriteLine($"{group.Key}={group.Count()}");
            foreach (var decision in group.Where(decision => decision.Outcome is LegacyCustomerImportPlanner.CostPriceReview or LegacyCustomerImportPlanner.UnmappedPriceType or LegacyCustomerImportPlanner.MissingCustomerNumber or LegacyCustomerImportPlanner.InvalidName).Take(25))
            {
                output.WriteLine($"  {decision.LegacyCustomerId}|{decision.RawPhone}|{decision.Name}|{decision.CustomerTypeKey}|{decision.PriceTypeKey}|{decision.Explanation}");
            }
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureClassificationsAsync(connection, transaction, plan, output, cancellationToken);
        var table = BuildTable(eligible, comments);
        await using (var create = new SqlCommand("""
            CREATE TABLE #LegacyCustomerImport (
                LegacyCustomerKey nvarchar(64) NOT NULL PRIMARY KEY,
                CustomerNumber nvarchar(50) NOT NULL,
                Name nvarchar(300) NOT NULL,
                Phone nvarchar(50) NULL,
                NormalizedPhone nvarchar(50) NULL,
                ClassificationCode nvarchar(50) NOT NULL,
                PriceTypeID uniqueidentifier NOT NULL,
                Discount decimal(7, 4) NOT NULL,
                Notes nvarchar(1000) NULL,
                NameSearchStrict nvarchar(300) NULL,
                NameSearchLoose nvarchar(300) NULL
            );
            """, connection, transaction) { CommandTimeout = 180 })
        {
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = "#LegacyCustomerImport", BulkCopyTimeout = 180 })
        {
            foreach (DataColumn column in table.Columns)
            {
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulk.WriteToServerAsync(table, cancellationToken);
        }

        int numberConflicts;
        await using (var conflicts = new SqlCommand("""
            SELECT COUNT(*)
            FROM #LegacyCustomerImport AS incoming
            INNER JOIN dbo.Customers AS existing
                ON existing.BusinessID = @business
               AND existing.CustomerNumber COLLATE DATABASE_DEFAULT = incoming.CustomerNumber COLLATE DATABASE_DEFAULT
            WHERE existing.LegacySource IS NULL
               OR existing.LegacySource COLLATE DATABASE_DEFAULT <> N'shop1'
               OR existing.LegacyCustomerKey IS NULL
               OR existing.LegacyCustomerKey COLLATE DATABASE_DEFAULT <> incoming.LegacyCustomerKey COLLATE DATABASE_DEFAULT;
            """, connection, transaction))
        {
            conflicts.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = LegacyCustomerImportCommand.ApprovedBusinessId;
            numberConflicts = (int)(await conflicts.ExecuteScalarAsync(cancellationToken) ?? 0);
        }

        if (numberConflicts > 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            output.WriteLine($"TARGET_CUSTOMER_NUMBER_CONFLICT={numberConflicts}");
            return new ApplyCounts(0, 0, skipped, conflict + numberConflicts, numberConflicts);
        }

        int created;
        int updated;
        await using (var merge = new SqlCommand("""
            DECLARE @actions TABLE (ActionName nvarchar(10) NOT NULL);
            MERGE dbo.Customers AS target
            USING (
                SELECT incoming.*, classification.CustomerClassificationID
                FROM #LegacyCustomerImport AS incoming
                INNER JOIN dbo.CustomerClassifications AS classification
                    ON classification.BusinessID = @business
                   AND classification.Code COLLATE DATABASE_DEFAULT = incoming.ClassificationCode COLLATE DATABASE_DEFAULT
            ) AS source
            ON target.BusinessID = @business
               AND target.LegacySource COLLATE DATABASE_DEFAULT = N'shop1'
               AND target.LegacyCustomerKey COLLATE DATABASE_DEFAULT = source.LegacyCustomerKey COLLATE DATABASE_DEFAULT
            WHEN MATCHED THEN UPDATE SET
                CustomerNumber = source.CustomerNumber,
                Name = source.Name,
                Phone = source.Phone,
                NormalizedPhone = source.NormalizedPhone,
                CustomerClassificationID = source.CustomerClassificationID,
                PriceTypeID = source.PriceTypeID,
                VariableValueDiscountPercent = source.Discount,
                IsActive = 1,
                Notes = source.Notes,
                NameSearchStrict = source.NameSearchStrict,
                NameSearchLoose = source.NameSearchLoose,
                UpdatedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (
                CustomerID, BusinessID, CustomerNumber, Name, Phone, NormalizedPhone,
                SecondaryPhone, SecondaryNormalizedPhone, CustomerClassificationID, PriceTypeID,
                VariableValueDiscountPercent, IsActive, Notes, LegacySource, LegacyCustomerKey,
                NameSearchStrict, NameSearchLoose, CreatedAt, UpdatedAt)
            VALUES (
                NEWID(), @business, source.CustomerNumber, source.Name, source.Phone, source.NormalizedPhone,
                NULL, NULL, source.CustomerClassificationID, source.PriceTypeID,
                source.Discount, 1, source.Notes, N'shop1', source.LegacyCustomerKey,
                source.NameSearchStrict, source.NameSearchLoose, SYSUTCDATETIME(), SYSUTCDATETIME())
            OUTPUT $action INTO @actions (ActionName);
            SELECT
                SUM(CASE WHEN ActionName = N'INSERT' THEN 1 ELSE 0 END),
                SUM(CASE WHEN ActionName = N'UPDATE' THEN 1 ELSE 0 END)
            FROM @actions;
            """, connection, transaction) { CommandTimeout = 180 })
        {
            merge.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = LegacyCustomerImportCommand.ApprovedBusinessId;
            await using var reader = await merge.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("Customer merge did not return counts.");
            }

            created = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            updated = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        }

        await transaction.CommitAsync(cancellationToken);
        await PrintVerificationAsync(connection, output, cancellationToken);
        return new ApplyCounts(created, updated, skipped, conflict, 0);
    }

    private static DataTable BuildTable(IReadOnlyList<LegacyCustomerDecision> eligible, Dictionary<int, string?> comments)
    {
        var table = new DataTable();
        table.Columns.Add("LegacyCustomerKey", typeof(string));
        table.Columns.Add("CustomerNumber", typeof(string));
        table.Columns.Add("Name", typeof(string));
        table.Columns.Add("Phone", typeof(string));
        table.Columns.Add("NormalizedPhone", typeof(string));
        table.Columns.Add("ClassificationCode", typeof(string));
        table.Columns.Add("PriceTypeID", typeof(Guid));
        table.Columns.Add("Discount", typeof(decimal));
        table.Columns.Add("Notes", typeof(string));
        table.Columns.Add("NameSearchStrict", typeof(string));
        table.Columns.Add("NameSearchLoose", typeof(string));
        foreach (var decision in eligible)
        {
            if (decision.ProposedPriceTypeId is null || decision.ProposedClassificationCode is null || decision.Name is null || decision.RawPhone is null || decision.ProposedVariableValueDiscountPercent is null)
            {
                throw new InvalidOperationException($"Eligible customer {decision.LegacyCustomerKey} is missing a required mapped value.");
            }

            comments.TryGetValue(decision.LegacyCustomerId, out var notes);
            table.Rows.Add(
                decision.LegacyCustomerKey,
                decision.RawPhone,
                decision.Name,
                decision.RawPhone,
                (object?)decision.NormalizedPhone ?? DBNull.Value,
                decision.ProposedClassificationCode,
                decision.ProposedPriceTypeId.Value,
                decision.ProposedVariableValueDiscountPercent.Value,
                (object?)notes ?? DBNull.Value,
                ArabicSearchNormalizer.NormalizeStrict(decision.Name),
                ArabicSearchNormalizer.NormalizeLoose(decision.Name));
        }

        return table;
    }

    private static async Task PrintVerificationAsync(SqlConnection connection, TextWriter output, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("""
            SELECT
                COUNT(*),
                COUNT(DISTINCT CustomerNumber),
                SUM(CASE WHEN CustomerNumber IS NULL OR LTRIM(RTRIM(CustomerNumber)) = N'' THEN 1 ELSE 0 END),
                SUM(CASE WHEN PriceTypeID IS NULL THEN 1 ELSE 0 END),
                SUM(CASE WHEN CustomerClassificationID IS NULL THEN 1 ELSE 0 END),
                SUM(CASE WHEN VariableValueDiscountPercent < 0 OR VariableValueDiscountPercent > 100 THEN 1 ELSE 0 END),
                SUM(CASE WHEN NameSearchStrict IS NULL OR NameSearchStrict = N'' THEN 1 ELSE 0 END),
                SUM(CASE WHEN NameSearchLoose IS NULL OR NameSearchLoose = N'' THEN 1 ELSE 0 END),
                SUM(CASE WHEN Phone IS NOT NULL AND LTRIM(RTRIM(Phone)) <> N'' AND NormalizedPhone IS NULL THEN 1 ELSE 0 END)
            FROM dbo.Customers
            WHERE BusinessID = @business;
            SELECT c.CustomerNumber, c.Name, class.Name, price.PriceCode, c.VariableValueDiscountPercent
            FROM dbo.Customers AS c
            LEFT JOIN dbo.CustomerClassifications AS class ON class.CustomerClassificationID = c.CustomerClassificationID
            LEFT JOIN dbo.PriceTypes AS price ON price.PriceTypeID = c.PriceTypeID
            WHERE c.BusinessID = @business
              AND c.CustomerNumber COLLATE DATABASE_DEFAULT IN (N'0502018080', N'0502122316', N'0502186512', N'0502023199');
            """, connection) { CommandTimeout = 180 };
        command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = LegacyCustomerImportCommand.ApprovedBusinessId;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
        {
            output.WriteLine($"CentralCustomers={reader.GetInt32(0)} DistinctNumbers={reader.GetInt32(1)} BlankNumbers={reader.GetInt32(2)} NullPrice={reader.GetInt32(3)} NullClassification={reader.GetInt32(4)} InvalidDiscount={reader.GetInt32(5)} MissingStrict={reader.GetInt32(6)} MissingLoose={reader.GetInt32(7)} MissingNormalizedPhone={reader.GetInt32(8)}");
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            output.WriteLine($"Sample {reader.GetString(0)} | {reader.GetString(1)} | {reader.GetString(2)} | {reader.GetString(3)} | {reader.GetDecimal(4)}");
        }
    }

    private static async Task EnsureClassificationsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        LegacyCustomerDryRunPlan plan,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        foreach (var proposal in plan.Classifications.Where(row => row.ProposedCode is not null && row.MappedPriceTypeId is not null && row.ProposedDefaultVariableValueDiscountPercent is not null))
        {
            await using var command = new SqlCommand("""
                DECLARE @id uniqueidentifier;
                SELECT @id = CustomerClassificationID
                FROM dbo.CustomerClassifications
                WHERE BusinessID = @business AND Code = @code;
                IF @id IS NULL
                BEGIN
                    SET @id = NEWID();
                    INSERT INTO dbo.CustomerClassifications (
                        CustomerClassificationID, BusinessID, Code, Name, DefaultPriceTypeID,
                        DefaultVariableValueDiscountPercent, IsDefault, IsActive, CreatedAt, UpdatedAt)
                    VALUES (
                        @id, @business, @code, @name, @price, @discount, @isDefault, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                END
                SELECT @id, @code, @name;
                """, connection, transaction);
            command.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = LegacyCustomerImportCommand.ApprovedBusinessId;
            command.Parameters.Add("@code", SqlDbType.NVarChar, 50).Value = proposal.ProposedCode;
            command.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = proposal.LegacyName;
            command.Parameters.Add("@price", SqlDbType.UniqueIdentifier).Value = proposal.MappedPriceTypeId!.Value;
            var discount = command.Parameters.Add("@discount", SqlDbType.Decimal);
            discount.Precision = 7;
            discount.Scale = 4;
            discount.Value = proposal.ProposedDefaultVariableValueDiscountPercent!.Value;
            command.Parameters.Add("@isDefault", SqlDbType.Bit).Value = proposal.ProposedIsDefault;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                output.WriteLine($"Classification {reader.GetString(1)} {reader.GetGuid(0)} {reader.GetString(2)}");
            }
        }
    }
}
