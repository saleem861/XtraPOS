using System.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Customers;

public sealed class CustomerMasterService(
    SqlConnectionFactory connections,
    SyncEndpointStore endpoints,
    ILogger<CustomerMasterService> logger)
{
    public const string CentralAuthority = "Central";
    public const string BranchAuthority = "Branch";

    public const string ReplicaAuthority = "BranchReplica";
    public const string SyncCurrent = "Current";
    public const string SyncOffline = "Offline";
    public const string SyncPreparing = "Preparing";
    public const string SyncFailed = "Failed";

    public async Task<CustomerPage> ListAsync(CustomerListFilter filter, CancellationToken cancellationToken)
    {
        var (connectionString, authority, syncState) = await CustomerReadTargetAsync(cancellationToken);
        try
        {
            var page = await ReadPageAsync(connectionString, filter, cancellationToken);
            return page with { Authority = authority, SyncState = syncState };
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            throw ReplicaUnavailable(syncState, exception);
        }
    }

    public async Task<CustomerRecord?> GetAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var (connectionString, _, syncState) = await CustomerReadTargetAsync(cancellationToken);
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return await ReadCustomerAsync(connection, null, customerId, cancellationToken);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            throw ReplicaUnavailable(syncState, exception);
        }
    }

    public async Task<CustomerClassificationPage> ListClassificationsAsync(CancellationToken cancellationToken)
    {
        var central = await LinkedCentralAsync(cancellationToken);
        var connectionString = central?.BuildConnectionString(central.DatabaseName) ?? connections.CreateBranchConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var businessId = await BusinessIdAsync(connection, null, cancellationToken);
        var rows = new List<CustomerClassificationRecord>();
        await using var command = new SqlCommand(
            """
            SELECT CustomerClassificationID, BusinessID, Code, Name, DefaultPriceTypeID,
                   DefaultVariableValueDiscountPercent, IsDefault, IsActive
            FROM dbo.CustomerClassifications
            WHERE BusinessID = @business
            ORDER BY Name, CustomerClassificationID;
            """,
            connection);
        command.Parameters.AddWithValue("@business", businessId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadClassification(reader));
        }

        return new CustomerClassificationPage(central is null ? BranchAuthority : CentralAuthority, rows);
    }

    public async Task<IReadOnlyList<CustomerPriceTypeRecord>> ListPriceTypesAsync(CancellationToken cancellationToken)
    {
        var central = await LinkedCentralAsync(cancellationToken);
        var connectionString = central?.BuildConnectionString(central.DatabaseName) ?? connections.CreateBranchConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var rows = new List<CustomerPriceTypeRecord>();
        await using var command = new SqlCommand(
            "SELECT PriceTypeID, PriceCode, PriceName FROM dbo.PriceTypes WHERE IsActive = 1 ORDER BY SortOrder, PriceCode;",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new CustomerPriceTypeRecord(reader.GetGuid(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    public async Task<CustomerMutationResult> CreateAsync(Guid actorUserId, CustomerWriteRequest? request, CancellationToken cancellationToken)
    {
        var draft = Validate(request, requireVersion: false);
        var customerId = Guid.NewGuid();
        await WriteAsync(
            actorUserId,
            customerId,
            async (connection, transaction) =>
            {
                await EnsureActorAsync(connection, transaction, actorUserId, cancellationToken);
                var businessId = await BusinessIdAsync(connection, transaction, cancellationToken);
                await EnsurePriceTypeAsync(connection, transaction, draft.PriceTypeId, cancellationToken);
                await EnsureClassificationAsync(connection, transaction, businessId, draft.ClassificationId, cancellationToken);
                await EnsureCustomerNumberAvailableAsync(connection, transaction, businessId, draft.CustomerNumber, cancellationToken);
                var verifiedPhone = await LockRegistrationAsync(connection, transaction, request, businessId, draft.CustomerNumber, draft.WhatsAppPhoneE164, cancellationToken);
                await using var command = Command(
                    connection,
                    transaction,
                    """
                    INSERT INTO dbo.Customers (
                        CustomerID, BusinessID, CustomerNumber, Name, NameSearchStrict, NameSearchLoose, Phone, NormalizedPhone,
                        SecondaryPhone, SecondaryNormalizedPhone, CustomerClassificationID, PriceTypeID,
                        VariableValueDiscountPercent, IsActive, Notes, WhatsAppPhoneE164, PhoneVerificationStatus, PhoneVerifiedAt, PhoneVerifiedE164, WhatsAppOptInAt,
                        CreatedAt, UpdatedAt)
                    VALUES (
                        @id, @business, @number, @name, @nameStrict, @nameLoose, @phone, @normalized,
                        @secondaryPhone, @secondaryNormalized, @classification, @priceType,
                        @discount, @active, @notes, @whatsApp, @phoneStatus, CASE WHEN @phoneStatus = N'Verified' THEN SYSUTCDATETIME() ELSE NULL END, @verifiedPhone, CASE WHEN @consent = 1 THEN SYSUTCDATETIME() ELSE NULL END,
                        SYSUTCDATETIME(), SYSUTCDATETIME());
                    """);
                AddCustomer(command, customerId, businessId, draft);
                command.Parameters.AddWithValue("@phoneStatus", verifiedPhone is null ? "Unverified" : "Verified");
                command.Parameters.AddWithValue("@verifiedPhone", (object?)verifiedPhone ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(cancellationToken);
                if (verifiedPhone is not null)
                {
                    await LinkRegistrationAsync(connection, transaction, request!.VerificationId!.Value, customerId, cancellationToken);
                }

                await CustomerItemSpecialPriceService.InsertDraftsAsync(connection, transaction, actorUserId, businessId, customerId, request?.SpecialPrices, cancellationToken);
                await AuditAsync(connection, transaction, actorUserId, customerId, CustomerActions.Created, cancellationToken);
                await LogChangeAsync(connection, transaction, customerId, actorUserId, cancellationToken);
            },
            cancellationToken);
        return await MutationAsync(customerId, cancellationToken);
    }

    public async Task<CustomerMutationResult> UpdateAsync(Guid actorUserId, Guid customerId, CustomerWriteRequest? request, CancellationToken cancellationToken)
    {
        var draft = Validate(request, requireVersion: true);
        await WriteAsync(
            actorUserId,
            customerId,
            async (connection, transaction) =>
            {
                await EnsureActorAsync(connection, transaction, actorUserId, cancellationToken);
                var existing = await ReadCustomerAsync(connection, transaction, customerId, cancellationToken)
                    ?? throw new CustomerMasterException("الزبون غير موجود.", StatusCodes.Status404NotFound);
                var businessId = existing.BusinessId;
                await EnsurePriceTypeAsync(connection, transaction, draft.PriceTypeId, cancellationToken);
                await EnsureClassificationAsync(connection, transaction, businessId, draft.ClassificationId, cancellationToken);
                await using var command = Command(
                    connection,
                    transaction,
                    """
                    UPDATE dbo.Customers
                    SET CustomerNumber = @number,
                        Name = @name,
                        NameSearchStrict = @nameStrict,
                        NameSearchLoose = @nameLoose,
                        Phone = @phone,
                        NormalizedPhone = @normalized,
                        SecondaryPhone = @secondaryPhone,
                        SecondaryNormalizedPhone = @secondaryNormalized,
                        CustomerClassificationID = @classification,
                        PriceTypeID = @priceType,
                        VariableValueDiscountPercent = @discount,
                        IsActive = @active,
                        Notes = @notes,
                        WhatsAppPhoneE164 = @whatsApp,
                        PhoneVerificationStatus = CASE WHEN ISNULL(WhatsAppPhoneE164, N'') <> ISNULL(@whatsApp, N'') THEN N'Unverified' ELSE PhoneVerificationStatus END,
                        PhoneVerifiedAt = CASE WHEN ISNULL(WhatsAppPhoneE164, N'') <> ISNULL(@whatsApp, N'') THEN NULL ELSE PhoneVerifiedAt END,
                        PhoneVerifiedE164 = CASE WHEN ISNULL(WhatsAppPhoneE164, N'') <> ISNULL(@whatsApp, N'') THEN NULL ELSE PhoneVerifiedE164 END,
                        WhatsAppOptInAt = CASE WHEN WhatsAppOptInAt IS NULL AND @consent = 1 THEN SYSUTCDATETIME() ELSE WhatsAppOptInAt END,
                        UpdatedAt = SYSUTCDATETIME()
                    WHERE CustomerID = @id AND RowVersion = @version;
                    """);
                AddCustomer(command, customerId, businessId, draft);
                command.Parameters.Add("@version", SqlDbType.Timestamp).Value = draft.RowVersion;
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw await MissingOrConflictAsync(connection, transaction, customerId, cancellationToken);
                }

                await AuditAsync(connection, transaction, actorUserId, customerId, CustomerActions.Updated, cancellationToken);
                if (existing.IsActive != draft.IsActive)
                {
                    await AuditAsync(
                        connection,
                        transaction,
                        actorUserId,
                        customerId,
                        draft.IsActive ? CustomerActions.Activated : CustomerActions.Deactivated,
                        cancellationToken);
                }

                await LogChangeAsync(connection, transaction, customerId, actorUserId, cancellationToken);
            },
            cancellationToken);
        return await MutationAsync(customerId, cancellationToken);
    }

    public async Task<CustomerMutationResult> SetActiveAsync(
        Guid actorUserId,
        Guid customerId,
        bool isActive,
        string? rowVersion,
        CancellationToken cancellationToken)
    {
        var version = ParseVersion(rowVersion);
        await WriteAsync(
            actorUserId,
            customerId,
            async (connection, transaction) =>
            {
                await EnsureActorAsync(connection, transaction, actorUserId, cancellationToken);
                await using var command = Command(
                    connection,
                    transaction,
                    """
                    UPDATE dbo.Customers
                    SET IsActive = @active, UpdatedAt = SYSUTCDATETIME()
                    WHERE CustomerID = @id AND RowVersion = @version;
                    """);
                command.Parameters.AddWithValue("@active", isActive);
                command.Parameters.AddWithValue("@id", customerId);
                command.Parameters.Add("@version", SqlDbType.Timestamp).Value = version;
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw await MissingOrConflictAsync(connection, transaction, customerId, cancellationToken);
                }

                await AuditAsync(
                    connection,
                    transaction,
                    actorUserId,
                    customerId,
                    isActive ? CustomerActions.Activated : CustomerActions.Deactivated,
                    cancellationToken);
                await LogChangeAsync(connection, transaction, customerId, actorUserId, cancellationToken);
            },
            cancellationToken);
        return await MutationAsync(customerId, cancellationToken);
    }

    private async Task WriteAsync(
        Guid actorUserId,
        Guid customerId,
        Func<SqlConnection, SqlTransaction, Task> change,
        CancellationToken cancellationToken)
    {
        var central = await LinkedCentralAsync(cancellationToken);
        try
        {
            if (central is null)
            {
                await using var branch = new SqlConnection(connections.CreateBranchConnectionString());
                await branch.OpenAsync(cancellationToken);
                await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
                await change(branch, transaction);
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await using var remote = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
                await remote.OpenAsync(cancellationToken);
                await using var transaction = (SqlTransaction)await remote.BeginTransactionAsync(cancellationToken);
                await change(remote, transaction);
                await transaction.CommitAsync(cancellationToken);
                await CopyToBranchAsync(central, customerId, cancellationToken);
            }
        }
        catch (CustomerMasterException)
        {
            throw;
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            throw UniqueMessage(exception);
        }
        catch (SqlException exception) when (exception.Number == 547)
        {
            logger.LogWarning(exception, "Customer save was rejected by SQL {SqlNumber}.", exception.Number);
            throw new CustomerMasterException(ConstraintMessage(exception));
        }
        catch (SqlException exception)
        {
            logger.LogWarning("Customer save failed with SQL {SqlNumber} for {CustomerId}.", exception.Number, customerId);
            throw new CustomerMasterException("تعذر حفظ بيانات الزبون.", StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation("Customer master change completed for {CustomerId} by {ActorUserId}.", customerId, actorUserId);
    }

    public async Task ReapplyBranchCopyAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var central = await LinkedCentralAsync(cancellationToken)
            ?? throw new CustomerMasterException("لا توجد إدارة مركزية مرتبطة.", StatusCodes.Status503ServiceUnavailable);
        await CopyToBranchAsync(central, customerId, cancellationToken);
    }

    private async Task CopyToBranchAsync(SqlEndpointSettings central, Guid customerId, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await CopyOnceAsync(central, customerId, cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is not CustomerMasterException)
            {
                last = exception;
            }
        }

        logger.LogWarning(last, "Branch customer copy failed for {CustomerId}.", customerId);
        throw new CustomerMasterException("تم الحفظ في الإدارة المركزية، وتعذر تحديث نسخة الفرع.", StatusCodes.Status503ServiceUnavailable);
    }

    private async Task CopyOnceAsync(SqlEndpointSettings central, Guid customerId, CancellationToken cancellationToken)
    {
        await using var remote = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await remote.OpenAsync(cancellationToken);
        var payload = await ReadPayloadAsync(remote, null, customerId, cancellationToken)
            ?? throw new CustomerMasterException("الزبون غير موجود في الإدارة المركزية.", StatusCodes.Status503ServiceUnavailable);
        var audits = new List<(Guid AuditId, Guid ActorUserId, string Action, DateTime OccurredAt)>();
        await using (var audit = new SqlCommand(
            "SELECT AuditID, ActorUserID, Action, OccurredAt FROM dbo.CustomerAdministrationAudit WHERE CustomerID = @id;",
            remote))
        {
            audit.Parameters.AddWithValue("@id", customerId);
            await using var reader = await audit.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                audits.Add((reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetDateTime(3)));
            }
        }

        await using var branch = new SqlConnection(connections.CreateBranchConnectionString());
        await branch.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
        await CentralChangeApply.ApplyAsync(branch, transaction, "Customers", customerId, CentralChangeApply.Serialize(payload), cancellationToken);
        foreach (var audit in audits)
        {
            await using var insert = Command(
                branch,
                transaction,
                """
                IF NOT EXISTS (SELECT 1 FROM dbo.CustomerAdministrationAudit WHERE AuditID = @id)
                    INSERT INTO dbo.CustomerAdministrationAudit (AuditID, ActorUserID, CustomerID, Action, OccurredAt)
                    VALUES (@id, @actor, @customer, @action, @occurred);
                """);
            insert.Parameters.AddWithValue("@id", audit.AuditId);
            insert.Parameters.AddWithValue("@actor", audit.ActorUserId);
            insert.Parameters.AddWithValue("@customer", customerId);
            insert.Parameters.AddWithValue("@action", audit.Action);
            insert.Parameters.AddWithValue("@occurred", audit.OccurredAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<CustomerMutationResult> MutationAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await GetAsync(customerId, cancellationToken)
            ?? throw new CustomerMasterException("الزبون غير موجود.", StatusCodes.Status404NotFound);
        var central = await LinkedCentralAsync(cancellationToken);
        var connectionString = central?.BuildConnectionString(central.DatabaseName) ?? connections.CreateBranchConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var warning = await DuplicateWarningAsync(connection, null, customer, cancellationToken);
        return new CustomerMutationResult(customer, warning);
    }

    private static async Task<CustomerPage> ReadPageAsync(string connectionString, CustomerListFilter filter, CancellationToken cancellationToken)
    {
        var page = filter.Page < 1 ? 1 : filter.Page;
        var pageSize = filter.PageSize is < 1 or > 100 ? 50 : filter.PageSize;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var businessId = await BusinessIdAsync(connection, null, cancellationToken);
        var normalized = PhoneNormalizer.Normalize(filter.Search);
        await using var count = new SqlCommand(FilterSql("SELECT COUNT(*)") + ";", connection);
        AddFilter(count, businessId, filter, normalized);
        var total = (int)(await count.ExecuteScalarAsync(cancellationToken) ?? 0);
        await using var command = new SqlCommand(
            FilterSql(
                """
                SELECT customer.CustomerID, customer.BusinessID, customer.CustomerNumber, customer.Name, customer.Phone, customer.NormalizedPhone,
                       customer.SecondaryPhone, customer.SecondaryNormalizedPhone, customer.CustomerClassificationID, customer.PriceTypeID,
                       customer.VariableValueDiscountPercent, customer.IsActive, customer.Notes, customer.RowVersion,
                       classification.Name, priceType.PriceCode, customer.WhatsAppPhoneE164, customer.PhoneVerificationStatus
                """) + """
                ORDER BY CASE
                    WHEN @exact IS NOT NULL AND customer.CustomerNumber = @exact THEN 0
                    WHEN @normalized IS NOT NULL AND (customer.NormalizedPhone = @normalized OR customer.SecondaryNormalizedPhone = @normalized) THEN 1
                    WHEN @exact IS NOT NULL AND customer.Name = @exact THEN 2
                    WHEN @strict <> N'' AND customer.NameSearchStrict = @strict THEN 3
                    WHEN @strictPrefix IS NOT NULL AND customer.NameSearchStrict LIKE @strictPrefix THEN 4
                    WHEN @strictLike IS NOT NULL AND customer.NameSearchStrict LIKE @strictLike THEN 5
                    ELSE 6
                END, customer.Name, customer.CustomerID
                OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY;
                """,
            connection);
        AddFilter(command, businessId, filter, normalized);
        command.Parameters.AddWithValue("@offset", (page - 1) * pageSize);
        command.Parameters.AddWithValue("@pageSize", pageSize);
        var rows = new List<CustomerRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(ReadCustomer(reader));
        }

        return new CustomerPage("", total, page, pageSize, rows);
    }

    private static string FilterSql(string select)
    {
        return $"""
            {select}
            FROM dbo.Customers AS customer
            LEFT JOIN dbo.CustomerClassifications AS classification
              ON classification.CustomerClassificationID = customer.CustomerClassificationID
            LEFT JOIN dbo.PriceTypes AS priceType
              ON priceType.PriceTypeID = customer.PriceTypeID
            WHERE customer.BusinessID = @business
              AND (@active IS NULL OR customer.IsActive = @active)
              AND (@classification IS NULL OR customer.CustomerClassificationID = @classification)
              AND (@priceType IS NULL OR customer.PriceTypeID = @priceType)
              AND (@search IS NULL
                    OR customer.Name LIKE @search
                    OR customer.NameSearchStrict = @strict
                    OR customer.NameSearchStrict LIKE @strictPrefix
                    OR customer.NameSearchStrict LIKE @strictLike
                    OR customer.NameSearchLoose LIKE @looseLike
                    OR customer.CustomerNumber LIKE @search
                    OR customer.Phone LIKE @search
                    OR customer.SecondaryPhone LIKE @search
                    OR (@normalized IS NOT NULL AND (customer.NormalizedPhone = @normalized OR customer.SecondaryNormalizedPhone = @normalized)))
            """;
    }

    private static void AddFilter(SqlCommand command, Guid businessId, CustomerListFilter filter, string? normalized)
    {
        command.Parameters.AddWithValue("@business", businessId);
        command.Parameters.AddWithValue("@active", (object?)filter.IsActive ?? DBNull.Value);
        command.Parameters.AddWithValue("@classification", (object?)filter.ClassificationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@priceType", (object?)filter.PriceTypeId ?? DBNull.Value);
        var exact = string.IsNullOrWhiteSpace(filter.Search) ? null : filter.Search.Trim();
        var strict = ArabicSearchNormalizer.NormalizeStrict(exact);
        var loose = ArabicSearchNormalizer.NormalizeLoose(exact);
        command.Parameters.AddWithValue("@search", (object?)(exact is null ? null : Like(exact)) ?? DBNull.Value);
        command.Parameters.AddWithValue("@exact", (object?)exact ?? DBNull.Value);
        command.Parameters.AddWithValue("@strict", strict);
        command.Parameters.AddWithValue("@strictPrefix", strict.Length == 0 ? DBNull.Value : strict + "%");
        command.Parameters.AddWithValue("@strictLike", strict.Length == 0 ? DBNull.Value : "%" + strict + "%");
        command.Parameters.AddWithValue("@looseLike", loose.Length == 0 ? DBNull.Value : "%" + loose + "%");
        command.Parameters.AddWithValue("@normalized", (object?)normalized ?? DBNull.Value);
    }

    private static async Task<CustomerDuplicateWarning?> DuplicateWarningAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        CustomerRecord customer,
        CancellationToken cancellationToken)
    {
        var phones = new List<string>();
        if (!string.IsNullOrWhiteSpace(customer.NormalizedPhone))
        {
            phones.Add(customer.NormalizedPhone);
        }

        if (!string.IsNullOrWhiteSpace(customer.SecondaryNormalizedPhone)
            && !phones.Contains(customer.SecondaryNormalizedPhone, StringComparer.Ordinal))
        {
            phones.Add(customer.SecondaryNormalizedPhone);
        }

        if (phones.Count == 0)
        {
            return null;
        }

        await using var command = Command(
            connection,
            transaction,
            """
            SELECT CustomerID, Name, CustomerNumber, NormalizedPhone, SecondaryNormalizedPhone
            FROM dbo.Customers
            WHERE BusinessID = @business
              AND CustomerID <> @id
              AND IsActive = 1
              AND (
                    (@primary IS NOT NULL AND (NormalizedPhone = @primary OR SecondaryNormalizedPhone = @primary))
                    OR (@secondary IS NOT NULL AND (NormalizedPhone = @secondary OR SecondaryNormalizedPhone = @secondary)));
            """);
        command.Parameters.AddWithValue("@business", customer.BusinessId);
        command.Parameters.AddWithValue("@id", customer.CustomerId);
        command.Parameters.AddWithValue("@primary", (object?)customer.NormalizedPhone ?? DBNull.Value);
        command.Parameters.AddWithValue("@secondary", (object?)customer.SecondaryNormalizedPhone ?? DBNull.Value);
        var matches = new List<CustomerDuplicateMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            matches.Add(new CustomerDuplicateMatch(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return matches.Count == 0 ? null : new CustomerDuplicateWarning(phones, matches);
    }

    private static async Task LogChangeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid customerId,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        await using var exists = Command(connection, transaction, "SELECT OBJECT_ID(N'dbo.SyncChangeLog', N'U');");
        if (await exists.ExecuteScalarAsync(cancellationToken) is null or DBNull)
        {
            return;
        }

        var payload = await ReadPayloadAsync(connection, transaction, customerId, cancellationToken);
        if (payload is null)
        {
            return;
        }

        await using var command = Command(
            connection,
            transaction,
            """
            INSERT INTO dbo.SyncChangeLog (EntityType, EntityId, Operation, Payload, ChangedByUserID)
            VALUES (N'Customers', @id, N'Upsert', @payload, @actor);
            """);
        command.Parameters.AddWithValue("@id", customerId);
        command.Parameters.AddWithValue("@payload", CentralChangeApply.Serialize(payload));
        command.Parameters.AddWithValue("@actor", actorUserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static async Task<CustomerSyncPayload?> ReadPayloadAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            connection,
            transaction,
            """
            SELECT c.CustomerID, c.BusinessID, c.CustomerNumber, c.Name, c.Phone, c.NormalizedPhone,
                   c.SecondaryPhone, c.SecondaryNormalizedPhone, c.CustomerClassificationID, c.PriceTypeID,
                   price.PriceCode, c.VariableValueDiscountPercent, c.IsActive, c.Notes, c.LegacySource, c.LegacyCustomerKey,
                   c.CreatedAt, c.UpdatedAt,
                   class.CustomerClassificationID, class.BusinessID, class.Code, class.Name, class.DefaultPriceTypeID,
                   classPrice.PriceCode,                    class.DefaultVariableValueDiscountPercent, class.IsDefault, class.IsActive,
                   class.CreatedAt, class.UpdatedAt,
                   c.WhatsAppPhoneE164, c.PhoneVerificationStatus, c.PhoneVerifiedAt, c.PhoneVerifiedE164
            FROM dbo.Customers AS c
            LEFT JOIN dbo.PriceTypes AS price ON price.PriceTypeID = c.PriceTypeID
            LEFT JOIN dbo.CustomerClassifications AS class ON class.CustomerClassificationID = c.CustomerClassificationID
            LEFT JOIN dbo.PriceTypes AS classPrice ON classPrice.PriceTypeID = class.DefaultPriceTypeID
            WHERE c.CustomerID = @id;
            """);
        command.Parameters.AddWithValue("@id", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        CustomerSyncClassification? classification = reader.IsDBNull(18)
            ? null
            : new CustomerSyncClassification(
                reader.GetGuid(18),
                reader.GetGuid(19),
                reader.IsDBNull(20) ? null : reader.GetString(20),
                reader.GetString(21),
                reader.IsDBNull(22) ? null : reader.GetGuid(22),
                reader.IsDBNull(23) ? null : reader.GetString(23),
                reader.GetDecimal(24),
                reader.GetBoolean(25),
                reader.GetBoolean(26),
                reader.GetDateTime(27),
                reader.GetDateTime(28));
        return new CustomerSyncPayload(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetGuid(8),
            reader.IsDBNull(9) ? null : reader.GetGuid(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetDecimal(11),
            reader.GetBoolean(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.GetDateTime(16),
            reader.GetDateTime(17),
            classification,
            reader.FieldCount > 29 && !reader.IsDBNull(29) ? reader.GetString(29) : null,
            reader.FieldCount > 30 && !reader.IsDBNull(30) ? reader.GetString(30) : null,
            reader.FieldCount > 31 && !reader.IsDBNull(31) ? reader.GetDateTime(31) : null,
            reader.FieldCount > 32 && !reader.IsDBNull(32) ? reader.GetString(32) : null);
    }

    private static CustomerDraft Validate(CustomerWriteRequest? request, bool requireVersion)
    {
        var name = Required(request?.Name, "يرجى إدخال اسم الزبون.", 300);
        var number = Optional(request?.CustomerNumber, 50);
        var phone = Optional(request?.Phone, 50);
        var secondary = Optional(request?.SecondaryPhone, 50);
        var notes = Optional(request?.Notes, 1000);
        string? whatsApp = null;
        var consent = request?.WhatsAppConsent == true;
        var normalized = PhoneNormalizer.Normalize(phone);
        if (!string.IsNullOrWhiteSpace(phone))
        {
            var formatted = WhatsAppPhoneFormatter.Format(phone, request?.WhatsAppCountryCode);
            phone = formatted.LocalNumber;
            normalized = PhoneNormalizer.Normalize(formatted.LocalNumber);
            whatsApp = formatted.E164;
            number ??= formatted.LocalNumber;
        }

        var secondaryNormalized = PhoneNormalizer.Normalize(secondary);
        if (PhoneNormalizer.IsWalkInPlaceholder(normalized) || PhoneNormalizer.IsWalkInPlaceholder(secondaryNormalized))
        {
            throw new CustomerMasterException("لا يُنشأ زبون برقم الهاتف 1. البيع المجهول يبقى بدون زبون.");
        }

        if (normalized?.Length > 50 || secondaryNormalized?.Length > 50)
        {
            throw new CustomerMasterException("رقم الهاتف أطول من الحد المسموح.");
        }

        var discount = decimal.Round(request?.VariableValueDiscountPercent ?? 0m, 4, MidpointRounding.AwayFromZero);
        if (discount < 0m || discount > 100m)
        {
            throw new CustomerMasterException("نسبة خصم القيمة المتغيرة يجب أن تكون بين 0 و 100.");
        }

        var version = requireVersion ? ParseVersion(request?.RowVersion) : [];
        return new CustomerDraft(
            number,
            name,
            phone,
            normalized,
            secondary,
            secondaryNormalized,
            request?.CustomerClassificationId is Guid classification && classification != Guid.Empty ? classification : null,
            request?.PriceTypeId is Guid priceType && priceType != Guid.Empty ? priceType : null,
            discount,
            request?.IsActive ?? true,
            notes,
            version,
            whatsApp,
            consent);
    }

    private static byte[] ParseVersion(string? rowVersion)
    {
        if (string.IsNullOrWhiteSpace(rowVersion))
        {
            throw new CustomerMasterException("إصدار السجل مطلوب لحفظ التعديل.");
        }

        try
        {
            var bytes = Convert.FromBase64String(rowVersion);
            if (bytes.Length != 8)
            {
                throw new CustomerMasterException("إصدار السجل غير صالح.");
            }

            return bytes;
        }
        catch (FormatException)
        {
            throw new CustomerMasterException("إصدار السجل غير صالح.");
        }
    }

    private static async Task EnsureActorAsync(SqlConnection connection, SqlTransaction transaction, Guid actorUserId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT COUNT(*) FROM dbo.Users WHERE UserID = @id AND IsActive = 1;");
        command.Parameters.AddWithValue("@id", actorUserId);
        if ((int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) != 1)
        {
            throw new CustomerMasterException("حسابك غير موجود في جهة حفظ الزبائن.", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<CustomerMasterException> MissingOrConflictAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = @id;");
        command.Parameters.AddWithValue("@id", customerId);
        var exists = (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) == 1;
        return exists
            ? new CustomerMasterException("تم تعديل الزبون من جهة أخرى. أعد التحميل ثم حاول مرة أخرى.", StatusCodes.Status409Conflict)
            : new CustomerMasterException("الزبون غير موجود.", StatusCodes.Status404NotFound);
    }

    private static async Task EnsurePriceTypeAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid? priceTypeId,
        CancellationToken cancellationToken)
    {
        if (priceTypeId is null)
        {
            return;
        }

        await using var command = Command(
            connection,
            transaction,
            "SELECT PriceCode FROM dbo.PriceTypes WHERE PriceTypeID = @id AND IsActive = 1;");
        command.Parameters.AddWithValue("@id", priceTypeId.Value);
        var code = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new CustomerMasterException("التسعيرة المحددة غير صالحة.");
        }

        if (string.Equals(code, "Cost", StringComparison.OrdinalIgnoreCase))
        {
            throw new CustomerMasterException("لا يُستخدم نوع السعر Cost للزبائن.");
        }
    }

    private static async Task EnsureClassificationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid businessId,
        Guid? classificationId,
        CancellationToken cancellationToken)
    {
        if (classificationId is null)
        {
            return;
        }

        await using var command = Command(
            connection,
            transaction,
            """
            SELECT COUNT(*)
            FROM dbo.CustomerClassifications
            WHERE CustomerClassificationID = @id AND BusinessID = @business;
            """);
        command.Parameters.AddWithValue("@id", classificationId.Value);
        command.Parameters.AddWithValue("@business", businessId);
        if ((int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) != 1)
        {
            throw new CustomerMasterException("نوع الزبون غير صالح.");
        }
    }

    private static async Task<Guid> BusinessIdAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using (var setting = Command(
            connection,
            transaction,
            "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'BusinessID';"))
        {
            var value = await setting.ExecuteScalarAsync(cancellationToken) as string;
            if (Guid.TryParse(value, out var configured))
            {
                return configured;
            }
        }

        await using var command = Command(
            connection,
            transaction,
            "SELECT TOP (1) BusinessID FROM dbo.Businesses WHERE IsActive = 1 ORDER BY CreatedAt, BusinessID;");
        var business = await command.ExecuteScalarAsync(cancellationToken);
        if (business is not Guid businessId)
        {
            throw new CustomerMasterException("هوية المنشأة غير متاحة.", StatusCodes.Status503ServiceUnavailable);
        }

        return businessId;
    }

    private static async Task AuditAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid actorUserId,
        Guid customerId,
        string action,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            connection,
            transaction,
            """
            INSERT INTO dbo.CustomerAdministrationAudit (AuditID, ActorUserID, CustomerID, Action, OccurredAt)
            VALUES (@id, @actor, @customer, @action, SYSUTCDATETIME());
            """);
        command.Parameters.AddWithValue("@id", Guid.NewGuid());
        command.Parameters.AddWithValue("@actor", actorUserId);
        command.Parameters.AddWithValue("@customer", customerId);
        command.Parameters.AddWithValue("@action", action);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<CustomerRecord?> ReadCustomerAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            connection,
            transaction,
            """
            SELECT customer.CustomerID, customer.BusinessID, customer.CustomerNumber, customer.Name, customer.Phone, customer.NormalizedPhone,
                   customer.SecondaryPhone, customer.SecondaryNormalizedPhone, customer.CustomerClassificationID, customer.PriceTypeID,
                   customer.VariableValueDiscountPercent, customer.IsActive, customer.Notes, customer.RowVersion,
                   classification.Name, priceType.PriceCode, customer.WhatsAppPhoneE164, customer.PhoneVerificationStatus
            FROM dbo.Customers AS customer
            LEFT JOIN dbo.CustomerClassifications AS classification
              ON classification.CustomerClassificationID = customer.CustomerClassificationID
            LEFT JOIN dbo.PriceTypes AS priceType
              ON priceType.PriceTypeID = customer.PriceTypeID
            WHERE customer.CustomerID = @id;
            """);
        command.Parameters.AddWithValue("@id", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadCustomer(reader) : null;
    }

    private static CustomerRecord ReadCustomer(SqlDataReader reader)
    {
        return new CustomerRecord(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetGuid(8),
            reader.IsDBNull(9) ? null : reader.GetGuid(9),
            reader.GetDecimal(10),
            reader.GetBoolean(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            Convert.ToBase64String((byte[])reader.GetValue(13)),
            reader.FieldCount > 14 && !reader.IsDBNull(14) ? reader.GetString(14) : null,
            reader.FieldCount > 15 && !reader.IsDBNull(15) ? reader.GetString(15) : null,
            reader.FieldCount > 16 && !reader.IsDBNull(16) ? reader.GetString(16) : null,
            reader.FieldCount > 17 && !reader.IsDBNull(17) ? reader.GetString(17) : null);
    }

    private static CustomerClassificationRecord ReadClassification(SqlDataReader reader)
    {
        return new CustomerClassificationRecord(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.GetDecimal(5),
            reader.GetBoolean(6),
            reader.GetBoolean(7));
    }

    private static async Task EnsureCustomerNumberAvailableAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid businessId,
        string? customerNumber,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customerNumber))
        {
            return;
        }

        await using var command = Command(
            connection,
            transaction,
            "SELECT COUNT(*) FROM dbo.Customers WITH (UPDLOCK, HOLDLOCK) WHERE BusinessID = @business AND CustomerNumber = @number;");
        command.Parameters.AddWithValue("@business", businessId);
        command.Parameters.AddWithValue("@number", customerNumber);
        if ((int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) > 0)
        {
            throw new CustomerMasterException("رقم الزبون مستخدم بالفعل.");
        }
    }

    private static async Task<string?> LockRegistrationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CustomerWriteRequest? request,
        Guid businessId,
        string? customerNumber,
        string? submittedPhone,
        CancellationToken cancellationToken)
    {
        if (!RegistrationVerification.SaveRequiresChallenge(request?.WhatsAppCountryCode))
        {
            return null;
        }

        if (request?.VerificationId is not Guid verificationId || verificationId == Guid.Empty)
        {
            throw new CustomerMasterException("يجب التحقق من رقم الهاتف قبل حفظ الزبون.");
        }

        await using var read = Command(
            connection,
            transaction,
            """
            SELECT DestinationE164, Status, VerifiedAt, CustomerID, BusinessID, CustomerNumber, Provider
            FROM dbo.CustomerPhoneVerificationChallenges WITH (UPDLOCK, ROWLOCK)
            WHERE VerificationID = @id;
            """);
        read.Parameters.AddWithValue("@id", verificationId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CustomerMasterException("طلب التحقق غير موجود.", StatusCodes.Status404NotFound);
        }

        var destination = reader.GetString(0);
        var status = reader.GetString(1);
        DateTime? verifiedAt = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
        Guid? linked = reader.IsDBNull(3) ? null : reader.GetGuid(3);
        var challengeBusiness = reader.GetGuid(4);
        var challengeNumber = reader.IsDBNull(5) ? null : reader.GetString(5);
        var provider = reader.IsDBNull(6) ? null : reader.GetString(6);
        await reader.CloseAsync();
        if (challengeBusiness != businessId
            || (!string.IsNullOrWhiteSpace(challengeNumber) && !string.Equals(challengeNumber, customerNumber, StringComparison.Ordinal))
            || (provider is not null && !provider.Equals("TwilioVerify", StringComparison.OrdinalIgnoreCase) && !provider.Equals("MetaWhatsApp", StringComparison.OrdinalIgnoreCase)))
        {
            throw new CustomerMasterException("جلسة التحقق غير صالحة أو انتهت.");
        }

        var rejection = RegistrationVerification.Rejection(status, linked, destination, submittedPhone, verifiedAt, DateTime.UtcNow);
        if (rejection is not null)
        {
            throw new CustomerMasterException(rejection);
        }

        return destination;
    }

    private static async Task LinkRegistrationAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid verificationId,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var consume = Command(
            connection,
            transaction,
            """
            UPDATE dbo.CustomerPhoneVerificationChallenges
            SET CustomerID = @customer, ConsumedAt = SYSUTCDATETIME()
            WHERE VerificationID = @id AND CustomerID IS NULL AND Status = N'Verified';
            """);
        consume.Parameters.AddWithValue("@customer", customerId);
        consume.Parameters.AddWithValue("@id", verificationId);
        if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new CustomerMasterException("طلب التحقق مستخدم بالفعل.");
        }
    }

    private static string ConstraintMessage(SqlException exception)
    {
        var text = exception.Message;
        if (text.Contains("FK_Customers_CustomerClassifications", StringComparison.OrdinalIgnoreCase))
        {
            return "نوع الزبون غير صالح.";
        }

        if (text.Contains("FK_Customers_PriceTypes", StringComparison.OrdinalIgnoreCase))
        {
            return "التسعيرة المحددة غير صالحة.";
        }

        if (text.Contains("FK_CustomerPhoneVerificationChallenges_Customer", StringComparison.OrdinalIgnoreCase))
        {
            return "جلسة التحقق غير صالحة أو انتهت.";
        }

        return "بيانات الزبون غير مقبولة.";
    }

    private static void AddCustomer(SqlCommand command, Guid customerId, Guid businessId, CustomerDraft draft)
    {
        command.Parameters.AddWithValue("@id", customerId);
        command.Parameters.AddWithValue("@business", businessId);
        command.Parameters.AddWithValue("@number", (object?)draft.CustomerNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("@name", draft.Name);
        command.Parameters.AddWithValue("@nameStrict", ArabicSearchNormalizer.NormalizeStrict(draft.Name));
        command.Parameters.AddWithValue("@nameLoose", ArabicSearchNormalizer.NormalizeLoose(draft.Name));
        command.Parameters.AddWithValue("@phone", (object?)draft.Phone ?? DBNull.Value);
        command.Parameters.AddWithValue("@normalized", (object?)draft.NormalizedPhone ?? DBNull.Value);
        command.Parameters.AddWithValue("@secondaryPhone", (object?)draft.SecondaryPhone ?? DBNull.Value);
        command.Parameters.AddWithValue("@secondaryNormalized", (object?)draft.SecondaryNormalizedPhone ?? DBNull.Value);
        command.Parameters.AddWithValue("@classification", (object?)draft.ClassificationId ?? DBNull.Value);
        command.Parameters.AddWithValue("@priceType", (object?)draft.PriceTypeId ?? DBNull.Value);
        var discount = command.Parameters.Add("@discount", SqlDbType.Decimal);
        discount.Precision = 7;
        discount.Scale = 4;
        discount.Value = draft.Discount;
        command.Parameters.AddWithValue("@active", draft.IsActive);
        command.Parameters.AddWithValue("@notes", (object?)draft.Notes ?? DBNull.Value);
        command.Parameters.AddWithValue("@whatsApp", (object?)draft.WhatsAppPhoneE164 ?? DBNull.Value);
        command.Parameters.AddWithValue("@consent", draft.WhatsAppConsent);
    }

    private async Task<(string ConnectionString, string Authority, string? SyncState)> CustomerReadTargetAsync(CancellationToken cancellationToken)
    {
        var state = await ReplicaStateAsync(cancellationToken);
        if (state is SharedDataSync.ReplicaReady or SharedDataSync.ReplicaStale)
        {
            var failed = await CentralLastErrorAsync(cancellationToken);
            var syncState = state == SharedDataSync.ReplicaStale || failed is not null
                ? (IsConnectivity(failed) ? SyncOffline : SyncFailed)
                : SyncCurrent;
            if (state == SharedDataSync.ReplicaStale && failed is null)
            {
                syncState = SyncOffline;
            }

            return (connections.CreateBranchConnectionString(), ReplicaAuthority, syncState);
        }

        SqlEndpointSettings? central;
        try
        {
            central = await LinkedCentralAsync(cancellationToken);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            throw new CustomerMasterException("تعذر قراءة اتصال القاعدة المركزية.", StatusCodes.Status503ServiceUnavailable);
        }

        if (central is null)
        {
            if (state == SharedDataSync.ReplicaBootstrapping)
            {
                throw new CustomerMasterException("جارٍ التحضير. نسخة الزبائن المحلية غير مكتملة.", StatusCodes.Status503ServiceUnavailable);
            }

            return (connections.CreateBranchConnectionString(), BranchAuthority, null);
        }

        return (central.BuildConnectionString(central.DatabaseName), CentralAuthority, state == SharedDataSync.ReplicaBootstrapping ? SyncPreparing : null);
    }

    private async Task<string> ReplicaStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = @key;",
            connection);
        command.Parameters.AddWithValue("@key", SharedDataSync.ReplicaStateKey);
        return await command.ExecuteScalarAsync(cancellationToken) as string ?? SharedDataSync.ReplicaNotInitialized;
    }

    private async Task<string?> CentralLastErrorAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT LastError FROM dbo.SyncEndpoints WHERE DatabaseRole = N'Central';",
            connection);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private static CustomerMasterException ReplicaUnavailable(string? syncState, Exception _)
    {
        var message = syncState == SyncPreparing
            ? "جارٍ التحضير. نسخة الزبائن المحلية غير مكتملة."
            : "تعذر الوصول إلى الزبائن. النسخة المحلية غير مهيأة.";
        return new CustomerMasterException(message, StatusCodes.Status503ServiceUnavailable);
    }

    private static bool IsConnectivity(string? error)
    {
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("network", StringComparison.OrdinalIgnoreCase)
            || error.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || error.Contains("unreachable", StringComparison.OrdinalIgnoreCase)
            || error.Contains("تعذر الوصول", StringComparison.Ordinal)
            || error.Contains("غير متاحة", StringComparison.Ordinal);
    }

    private async Task<SqlEndpointSettings?> LinkedCentralAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var syncTable = new SqlCommand("SELECT OBJECT_ID(N'dbo.SyncEndpoints', N'U');", connection);
        if (await syncTable.ExecuteScalarAsync(cancellationToken) is null or DBNull)
        {
            return null;
        }

        await using var roleCommand = new SqlCommand(
            "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';",
            connection);
        var role = await roleCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (string.Equals(role, DatabaseRole.Central, StringComparison.Ordinal))
        {
            return null;
        }

        var endpoint = await endpoints.GetAsync(DatabaseRole.Central, cancellationToken);
        if (endpoint is null || !string.Equals(endpoint.Status, SqlEndpointSettings.Linked, StringComparison.Ordinal))
        {
            return null;
        }

        var settings = await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (settings is null || !string.Equals(settings.DatabaseRole, DatabaseRole.Central, StringComparison.Ordinal))
        {
            return null;
        }

        return settings;
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql)
    {
        return new SqlCommand(sql, connection, transaction);
    }

    private static string Required(string? value, string message, int maxLength)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw new CustomerMasterException(message);
        }

        if (trimmed.Length > maxLength)
        {
            throw new CustomerMasterException(message);
        }

        return trimmed;
    }

    private static string? Optional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new CustomerMasterException("أحد الحقول أطول من الحد المسموح.");
        }

        return trimmed;
    }

    private static string Like(string value)
    {
        return "%" + value.Replace("[", "[[]", StringComparison.Ordinal).Replace("%", "[%]", StringComparison.Ordinal).Replace("_", "[_]", StringComparison.Ordinal) + "%";
    }

    private static CustomerMasterException UniqueMessage(SqlException exception)
    {
        if (exception.Message.Contains("CustomerNumber", StringComparison.OrdinalIgnoreCase))
        {
            return new CustomerMasterException("رقم الزبون مستخدم بالفعل.");
        }

        return new CustomerMasterException("تعذر حفظ الزبون لأن بياناته تتعارض مع سجل موجود.");
    }

    private sealed record CustomerDraft(
        string? CustomerNumber,
        string Name,
        string? Phone,
        string? NormalizedPhone,
        string? SecondaryPhone,
        string? SecondaryNormalizedPhone,
        Guid? ClassificationId,
        Guid? PriceTypeId,
        decimal Discount,
        bool IsActive,
        string? Notes,
        byte[] RowVersion,
        string? WhatsAppPhoneE164,
        bool WhatsAppConsent);
}
