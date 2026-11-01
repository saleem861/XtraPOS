using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;

namespace XtraPOS.Api.Sync;

public sealed record SyncRunResult(int Applied, string? Error, bool CentralReachable);

public static class BranchRemoteSync
{
    public const bool IsImplemented = false;
    public const string NormalDirection = "BranchLocalToBranchRemote";
    public const string ReverseDirection = "RestoreOnly";
    public const bool ReverseIsNormalSync = false;

    public static void Replicate() =>
        throw new InvalidOperationException("Branch remote synchronization is not implemented. Normal direction is Branch Local to Branch Remote Copy.");

    public static void Restore() =>
        throw new InvalidOperationException("استعادة الفرع من النسخة البعيدة is not implemented. Reverse direction is disaster recovery only.");
}

public sealed class SharedDataSync(
    SqlConnectionFactory branchConnections,
    CentralDatabaseService centralDatabases)
{
    public const string StreamName = "CentralMaster";
    public const string ReplicaStateKey = "CustomerReplicaState";
    public const string ReplicaNotInitialized = "NotInitialized";
    public const string ReplicaBootstrapping = "Bootstrapping";
    public const string ReplicaReady = "Ready";
    public const string ReplicaStale = "Stale";

    public async Task<int> PromoteAsync(SqlEndpointSettings central, CancellationToken cancellationToken)
    {
        await centralDatabases.VerifyExistingAsync(central, cancellationToken);
        var centralName = CentralDatabase.Require(central.DatabaseName);
        await using var connection = new SqlConnection(branchConnections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand(PromotionSql(centralName), connection, transaction);
        command.CommandTimeout = 300;
        var items = (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        await transaction.CommitAsync(cancellationToken);
        return items;
    }

    public async Task<int> BootstrapAsync(SqlEndpointSettings central, CancellationToken cancellationToken)
    {
        await centralDatabases.VerifyExistingAsync(central, cancellationToken);
        var centralName = CentralDatabase.Require(central.DatabaseName);
        await using var connection = new SqlConnection(branchConnections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        var beforeInventory = await CountAsync(connection, "SELECT COUNT(*) FROM dbo.ItemBranchInventory;", cancellationToken);
        var beforeBalances = await CountAsync(connection, "SELECT COUNT(*) FROM dbo.BranchStockBalances;", cancellationToken);
        var beforeMovements = await CountAsync(connection, "SELECT COUNT(*) FROM dbo.StockMovements;", cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var source = SharesInstance(central) ? $"[{centralName}].dbo." : "#";
        var hasBusinessDescription = await CentralColumnExistsAsync(central, "Businesses", "Description", cancellationToken);
        var businessDescription = hasBusinessDescription || source == "#"
            ? "source.Description"
            : "CAST(NULL AS nvarchar(500))";
        var hasItemSearch = await CentralColumnExistsAsync(central, "Items", "ItemNameSearchStrict", cancellationToken);
        var itemSearchStrict = hasItemSearch ? "source.ItemNameSearchStrict" : "CAST(NULL AS nvarchar(500))";
        var itemSearchLoose = hasItemSearch ? "source.ItemNameSearchLoose" : "CAST(NULL AS nvarchar(500))";
        if (source == "#")
        {
            await StageRemoteCatalogAsync(central, connection, transaction, hasBusinessDescription, hasItemSearch, cancellationToken);
        }

        await using var command = new SqlCommand(BootstrapSql(source, businessDescription, itemSearchStrict, itemSearchLoose), connection, transaction) { CommandTimeout = 600 };
        var items = (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
        await transaction.CommitAsync(cancellationToken);
        if (!hasItemSearch)
        {
            await ArabicSearchKeyBackfill.ApplyAsync(connection, cancellationToken);
        }
        var afterInventory = await CountAsync(connection, "SELECT COUNT(*) FROM dbo.ItemBranchInventory;", cancellationToken);
        var afterBalances = await CountAsync(connection, "SELECT COUNT(*) FROM dbo.BranchStockBalances;", cancellationToken);
        var afterMovements = await CountAsync(connection, "SELECT COUNT(*) FROM dbo.StockMovements;", cancellationToken);
        if (beforeInventory != afterInventory || beforeBalances != afterBalances || beforeMovements != afterMovements)
        {
            throw new InvalidOperationException("Central bootstrap changed branch operational stock.");
        }

        return items;
    }

    public async Task<string> EnsureCustomerReplicaAsync(Guid endpointId, SqlEndpointSettings central, CancellationToken cancellationToken)
    {
        await SetReplicaStateAsync(ReplicaBootstrapping, cancellationToken);
        await CopyCustomerReplicaAsync(central, cancellationToken);
        var pull = await PullAsync(endpointId, central, cancellationToken);
        if (!pull.CentralReachable)
        {
            return ReplicaBootstrapping;
        }

        var centralMax = await MaxCentralChangeSequenceAsync(central, cancellationToken);
        var checkpoint = await BranchCheckpointAsync(endpointId, cancellationToken);
        if (checkpoint < centralMax)
        {
            pull = await PullAsync(endpointId, central, cancellationToken);
            checkpoint = await BranchCheckpointAsync(endpointId, cancellationToken);
            if (!pull.CentralReachable || checkpoint < centralMax)
            {
                return ReplicaBootstrapping;
            }
        }

        await SetReplicaStateAsync(ReplicaReady, cancellationToken);
        return ReplicaReady;
    }

    public async Task<string> ReadReplicaStateAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(branchConnections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = @key;",
            connection);
        command.Parameters.AddWithValue("@key", ReplicaStateKey);
        var value = await command.ExecuteScalarAsync(cancellationToken) as string;
        return value is SharedDataSync.ReplicaReady or SharedDataSync.ReplicaStale or SharedDataSync.ReplicaBootstrapping
            ? value
            : ReplicaNotInitialized;
    }

    public async Task SetReplicaStateAsync(string state, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(branchConnections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            IF EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE SettingKey = @key)
                UPDATE dbo.SystemSettings SET SettingValue = @state WHERE SettingKey = @key;
            ELSE
                INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (@key, @state);
            """,
            connection);
        command.Parameters.AddWithValue("@key", ReplicaStateKey);
        command.Parameters.AddWithValue("@state", state);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task CopyCustomerReplicaAsync(SqlEndpointSettings central, CancellationToken cancellationToken)
    {
        await using var remote = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await using var branch = new SqlConnection(branchConnections.CreateBranchConnectionString());
        await remote.OpenAsync(cancellationToken);
        await branch.OpenAsync(cancellationToken);
        var priceTypes = await ReadPriceTypesAsync(remote, branch, cancellationToken);
        await CopyClassificationsAsync(remote, branch, priceTypes, cancellationToken);
        await CopyCustomersAsync(remote, branch, priceTypes, cancellationToken);
        await CopySpecialPricesAsync(remote, branch, cancellationToken);
    }

    private static async Task<Dictionary<Guid, Guid>> ReadPriceTypesAsync(SqlConnection remote, SqlConnection branch, CancellationToken cancellationToken)
    {
        var localByCode = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        await using (var command = new SqlCommand("SELECT PriceTypeID, PriceCode FROM dbo.PriceTypes;", branch))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                localByCode[reader.GetString(1)] = reader.GetGuid(0);
            }
        }

        var map = new Dictionary<Guid, Guid>();
        await using var remoteCommand = new SqlCommand("SELECT PriceTypeID, PriceCode FROM dbo.PriceTypes;", remote);
        await using var remoteReader = await remoteCommand.ExecuteReaderAsync(cancellationToken);
        while (await remoteReader.ReadAsync(cancellationToken))
        {
            var remoteId = remoteReader.GetGuid(0);
            var code = remoteReader.GetString(1);
            if (localByCode.TryGetValue(code, out var localId))
            {
                map[remoteId] = localId;
            }
        }

        return map;
    }

    private static async Task CopyClassificationsAsync(
        SqlConnection remote,
        SqlConnection branch,
        Dictionary<Guid, Guid> priceTypes,
        CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT CustomerClassificationID, BusinessID, Code, Name, DefaultPriceTypeID,
                   DefaultVariableValueDiscountPercent, IsDefault, IsActive, CreatedAt, UpdatedAt
            FROM dbo.CustomerClassifications;
            """,
            remote);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await using var upsert = new SqlCommand(
                """
                IF EXISTS (SELECT 1 FROM dbo.CustomerClassifications WHERE CustomerClassificationID = @id)
                    UPDATE dbo.CustomerClassifications
                    SET Code = @code, Name = @name, DefaultPriceTypeID = @priceType,
                        DefaultVariableValueDiscountPercent = @discount, IsDefault = @isDefault, IsActive = @active, UpdatedAt = @updated
                    WHERE CustomerClassificationID = @id AND (UpdatedAt IS NULL OR @updated >= UpdatedAt);
                ELSE
                    INSERT INTO dbo.CustomerClassifications
                        (CustomerClassificationID, BusinessID, Code, Name, DefaultPriceTypeID, DefaultVariableValueDiscountPercent, IsDefault, IsActive, CreatedAt, UpdatedAt)
                    VALUES (@id, @business, @code, @name, @priceType, @discount, @isDefault, @active, @created, @updated);
                """,
                branch);
            upsert.Parameters.AddWithValue("@id", reader.GetGuid(0));
            upsert.Parameters.AddWithValue("@business", reader.GetGuid(1));
            upsert.Parameters.AddWithValue("@code", reader.IsDBNull(2) ? DBNull.Value : reader.GetString(2));
            upsert.Parameters.AddWithValue("@name", reader.GetString(3));
            var priceType = reader.IsDBNull(4) ? (Guid?)null : reader.GetGuid(4);
            upsert.Parameters.AddWithValue("@priceType", priceType is Guid id && priceTypes.TryGetValue(id, out var localPriceType) ? localPriceType : DBNull.Value);
            upsert.Parameters.AddWithValue("@discount", reader.GetDecimal(5));
            upsert.Parameters.AddWithValue("@isDefault", reader.GetBoolean(6));
            upsert.Parameters.AddWithValue("@active", reader.GetBoolean(7));
            upsert.Parameters.AddWithValue("@created", reader.GetDateTime(8));
            upsert.Parameters.AddWithValue("@updated", reader.IsDBNull(9) ? reader.GetDateTime(8) : reader.GetDateTime(9));
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task CopyCustomersAsync(
        SqlConnection remote,
        SqlConnection branch,
        Dictionary<Guid, Guid> priceTypes,
        CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT CustomerID, BusinessID, CustomerNumber, Name, Phone, NormalizedPhone,
                   SecondaryPhone, SecondaryNormalizedPhone, CustomerClassificationID, PriceTypeID,
                   VariableValueDiscountPercent, IsActive, Notes, LegacySource, LegacyCustomerKey, CreatedAt, UpdatedAt
            FROM dbo.Customers;
            """,
            remote);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await using var upsert = new SqlCommand(
                """
                IF EXISTS (SELECT 1 FROM dbo.Customers WHERE CustomerID = @id)
                    UPDATE dbo.Customers
                    SET CustomerNumber = @number, Name = @name, Phone = @phone, NormalizedPhone = @normalized,
                        SecondaryPhone = @secondary, SecondaryNormalizedPhone = @secondaryNormalized,
                        CustomerClassificationID = @classification, PriceTypeID = @priceType,
                        VariableValueDiscountPercent = @discount, IsActive = @active, Notes = @notes,
                        LegacySource = @legacySource, LegacyCustomerKey = @legacyKey, UpdatedAt = @updated
                    WHERE CustomerID = @id AND (UpdatedAt IS NULL OR @updated >= UpdatedAt);
                ELSE
                    INSERT INTO dbo.Customers (
                        CustomerID, BusinessID, CustomerNumber, Name, Phone, NormalizedPhone,
                        SecondaryPhone, SecondaryNormalizedPhone, CustomerClassificationID, PriceTypeID,
                        VariableValueDiscountPercent, IsActive, Notes, LegacySource, LegacyCustomerKey, CreatedAt, UpdatedAt)
                    VALUES (
                        @id, @business, @number, @name, @phone, @normalized,
                        @secondary, @secondaryNormalized, @classification, @priceType,
                        @discount, @active, @notes, @legacySource, @legacyKey, @created, @updated);
                """,
                branch);
            upsert.Parameters.AddWithValue("@id", reader.GetGuid(0));
            upsert.Parameters.AddWithValue("@business", reader.GetGuid(1));
            upsert.Parameters.AddWithValue("@number", reader.IsDBNull(2) ? DBNull.Value : reader.GetString(2));
            upsert.Parameters.AddWithValue("@name", reader.GetString(3));
            upsert.Parameters.AddWithValue("@phone", reader.IsDBNull(4) ? DBNull.Value : reader.GetString(4));
            upsert.Parameters.AddWithValue("@normalized", reader.IsDBNull(5) ? DBNull.Value : reader.GetString(5));
            upsert.Parameters.AddWithValue("@secondary", reader.IsDBNull(6) ? DBNull.Value : reader.GetString(6));
            upsert.Parameters.AddWithValue("@secondaryNormalized", reader.IsDBNull(7) ? DBNull.Value : reader.GetString(7));
            upsert.Parameters.AddWithValue("@classification", reader.IsDBNull(8) ? DBNull.Value : reader.GetGuid(8));
            var priceType = reader.IsDBNull(9) ? (Guid?)null : reader.GetGuid(9);
            upsert.Parameters.AddWithValue("@priceType", priceType is Guid id && priceTypes.TryGetValue(id, out var localPriceType) ? localPriceType : DBNull.Value);
            upsert.Parameters.AddWithValue("@discount", reader.GetDecimal(10));
            upsert.Parameters.AddWithValue("@active", reader.GetBoolean(11));
            upsert.Parameters.AddWithValue("@notes", reader.IsDBNull(12) ? DBNull.Value : reader.GetString(12));
            upsert.Parameters.AddWithValue("@legacySource", reader.IsDBNull(13) ? DBNull.Value : reader.GetString(13));
            upsert.Parameters.AddWithValue("@legacyKey", reader.IsDBNull(14) ? DBNull.Value : reader.GetString(14));
            upsert.Parameters.AddWithValue("@created", reader.GetDateTime(15));
            upsert.Parameters.AddWithValue("@updated", reader.IsDBNull(16) ? reader.GetDateTime(15) : reader.GetDateTime(16));
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task CopySpecialPricesAsync(SqlConnection remote, SqlConnection branch, CancellationToken cancellationToken)
    {
        await using var exists = new SqlCommand("SELECT OBJECT_ID(N'dbo.CustomerItemSpecialPrices', N'U');", remote);
        if (await exists.ExecuteScalarAsync(cancellationToken) is null or DBNull)
        {
            return;
        }

        await using var read = new SqlCommand(
            """
            SELECT CustomerItemSpecialPriceID, BusinessID, CustomerID, ItemID, SpecialPrice, MinimumQuantity, BundlePrice,
                   IsActive, ValidFrom, ValidTo, Notes, CreatedAt, UpdatedAt
            FROM dbo.CustomerItemSpecialPrices;
            """,
            remote);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            await using var item = new SqlCommand("SELECT COUNT(*) FROM dbo.Items WHERE ItemID = @id;", branch);
            item.Parameters.AddWithValue("@id", reader.GetGuid(3));
            if ((int)(await item.ExecuteScalarAsync(cancellationToken) ?? 0) == 0)
            {
                continue;
            }

            await using var upsert = new SqlCommand(
                """
                IF EXISTS (SELECT 1 FROM dbo.CustomerItemSpecialPrices WHERE CustomerItemSpecialPriceID = @id)
                    UPDATE dbo.CustomerItemSpecialPrices
                    SET SpecialPrice = @price, MinimumQuantity = @minimum, BundlePrice = @bundle, IsActive = @active,
                        ValidFrom = @from, ValidTo = @to, Notes = @notes, UpdatedAt = @updated
                    WHERE CustomerItemSpecialPriceID = @id AND (UpdatedAt IS NULL OR @updated >= UpdatedAt);
                ELSE
                    INSERT INTO dbo.CustomerItemSpecialPrices (
                        CustomerItemSpecialPriceID, BusinessID, CustomerID, ItemID, SpecialPrice, MinimumQuantity, BundlePrice, IsActive,
                        ValidFrom, ValidTo, Notes, CreatedAt, UpdatedAt)
                    VALUES (@id, @business, @customer, @item, @price, @minimum, @bundle, @active, @from, @to, @notes, @created, @updated);
                """,
                branch);
            upsert.Parameters.AddWithValue("@id", reader.GetGuid(0));
            upsert.Parameters.AddWithValue("@business", reader.GetGuid(1));
            upsert.Parameters.AddWithValue("@customer", reader.GetGuid(2));
            upsert.Parameters.AddWithValue("@item", reader.GetGuid(3));
            upsert.Parameters.AddWithValue("@price", reader.GetDecimal(4));
            upsert.Parameters.AddWithValue("@minimum", reader.GetDecimal(5));
            upsert.Parameters.AddWithValue("@bundle", reader.GetDecimal(6));
            upsert.Parameters.AddWithValue("@active", reader.GetBoolean(7));
            upsert.Parameters.AddWithValue("@from", reader.IsDBNull(8) ? DBNull.Value : reader.GetDateTime(8));
            upsert.Parameters.AddWithValue("@to", reader.IsDBNull(9) ? DBNull.Value : reader.GetDateTime(9));
            upsert.Parameters.AddWithValue("@notes", reader.IsDBNull(10) ? DBNull.Value : reader.GetString(10));
            upsert.Parameters.AddWithValue("@created", reader.GetDateTime(11));
            upsert.Parameters.AddWithValue("@updated", reader.GetDateTime(12));
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<long> BranchCheckpointAsync(Guid endpointId, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(branchConnections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        return await CheckpointAsync(connection, endpointId, cancellationToken);
    }

    private async Task<long> MaxCentralChangeSequenceAsync(SqlEndpointSettings central, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT CASE WHEN OBJECT_ID(N'dbo.SyncChangeLog', N'U') IS NULL THEN 0
                        ELSE ISNULL(MAX(ChangeSequence), 0) END
            FROM dbo.SyncChangeLog;
            """,
            connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long sequence ? sequence : Convert.ToInt64(value ?? 0);
    }

    private bool SharesInstance(SqlEndpointSettings central)
    {
        return CentralRoute.SameInstance(branchConnections.ConfiguredServer, central);
    }

    public async Task<SyncRunResult> PullAsync(Guid endpointId, SqlEndpointSettings central, CancellationToken cancellationToken)
    {
        try
        {
            await centralDatabases.VerifyExistingAsync(central, cancellationToken);
            var centralName = CentralDatabase.Require(central.DatabaseName);
            await using var connection = new SqlConnection(branchConnections.CreateBranchConnectionString());
            await connection.OpenAsync(cancellationToken);
            await using var centralConnection = SharesInstance(central)
                ? null
                : new SqlConnection(central.BuildConnectionString(central.DatabaseName));
            if (centralConnection is not null)
            {
                await centralConnection.OpenAsync(cancellationToken);
            }

            var source = centralConnection ?? connection;
            var checkpoint = await CheckpointAsync(connection, endpointId, cancellationToken);
            await using var read = new SqlCommand(
                SharesInstance(central)
                    ? $"""
                      SELECT ChangeSequence, EntityType, EntityId, Payload
                      FROM [{centralName}].dbo.SyncChangeLog
                      WHERE ChangeSequence > @checkpoint
                      ORDER BY ChangeSequence;
                      """
                    : """
                      SELECT ChangeSequence, EntityType, EntityId, Payload
                      FROM dbo.SyncChangeLog
                      WHERE ChangeSequence > @checkpoint
                      ORDER BY ChangeSequence;
                      """,
                source);
            read.Parameters.AddWithValue("@checkpoint", checkpoint);
            var changes = new List<(long Sequence, string Entity, Guid Id, string? Payload)>();
            await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    changes.Add((
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetGuid(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
            }

            var applied = 0;
            foreach (var change in changes)
            {
                if (await InboxExistsAsync(connection, endpointId, change.Sequence, cancellationToken))
                {
                    checkpoint = change.Sequence;
                    continue;
                }

                if (CentralChangeApply.CanApply(change.Entity))
                {
                    await CentralChangeApply.ApplyAsync(connection, change.Entity, change.Id, change.Payload, cancellationToken);
                }

                await MarkInboxAsync(connection, endpointId, change, cancellationToken);
                checkpoint = change.Sequence;
                applied++;
            }

            if (centralConnection is not null)
            {
                await RepairNullCustomerPriceTypesAsync(centralConnection, connection, cancellationToken);
            }

            await SaveCheckpointAsync(connection, endpointId, checkpoint, cancellationToken);
            await SetEndpointStateAsync(connection, endpointId, null, cancellationToken);
            return new SyncRunResult(applied, null, true);
        }
        catch (Exception exception)
        {
            var message = SqlEndpointSettings.DescribeConnectionFailure(exception);
            await TryRecordErrorAsync(endpointId, message, cancellationToken);
            return new SyncRunResult(0, message, false);
        }
    }

    private static async Task RepairNullCustomerPriceTypesAsync(SqlConnection remote, SqlConnection branch, CancellationToken cancellationToken)
    {
        var priceTypes = await ReadPriceTypesAsync(remote, branch, cancellationToken);
        if (priceTypes.Count == 0)
        {
            return;
        }

        var missing = new List<Guid>();
        await using (var local = new SqlCommand("SELECT CustomerID FROM dbo.Customers WHERE PriceTypeID IS NULL;", branch))
        await using (var reader = await local.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                missing.Add(reader.GetGuid(0));
            }
        }

        if (missing.Count == 0)
        {
            return;
        }

        var centralPrices = new Dictionary<Guid, Guid>();
        await using (var remoteCommand = new SqlCommand("SELECT CustomerID, PriceTypeID FROM dbo.Customers WHERE PriceTypeID IS NOT NULL;", remote))
        await using (var reader = await remoteCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                centralPrices[reader.GetGuid(0)] = reader.GetGuid(1);
            }
        }

        foreach (var customerId in missing)
        {
            if (!centralPrices.TryGetValue(customerId, out var remotePriceType) || !priceTypes.TryGetValue(remotePriceType, out var localPriceType))
            {
                continue;
            }

            await using var update = new SqlCommand(
                """
                UPDATE dbo.Customers
                SET PriceTypeID = @priceType
                WHERE CustomerID = @id AND PriceTypeID IS NULL;
                """,
                branch);
            update.Parameters.AddWithValue("@priceType", localPriceType);
            update.Parameters.AddWithValue("@id", customerId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task TryRecordErrorAsync(Guid endpointId, string message, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqlConnection(branchConnections.CreateBranchConnectionString());
            await connection.OpenAsync(cancellationToken);
            await SetEndpointStateAsync(connection, endpointId, message, cancellationToken);
        }
        catch (Exception)
        {
            // The branch database itself is unavailable; local callers still surface the sync error.
        }
    }

    private static async Task<bool> InboxExistsAsync(SqlConnection connection, Guid endpointId, long sequence, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.SyncInbox WHERE EndpointID = @endpoint AND ChangeSequence = @sequence;",
            connection);
        command.Parameters.AddWithValue("@endpoint", endpointId);
        command.Parameters.AddWithValue("@sequence", sequence);
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) > 0;
    }

    private static async Task MarkInboxAsync(
        SqlConnection connection,
        Guid endpointId,
        (long Sequence, string Entity, Guid Id, string? Payload) change,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.SyncInbox WHERE EndpointID = @endpoint AND ChangeSequence = @sequence)
                INSERT INTO dbo.SyncInbox (EndpointID, ChangeSequence, EntityType, EntityId)
                VALUES (@endpoint, @sequence, @entity, @id);
            """,
            connection);
        command.Parameters.AddWithValue("@endpoint", endpointId);
        command.Parameters.AddWithValue("@sequence", change.Sequence);
        command.Parameters.AddWithValue("@entity", change.Entity);
        command.Parameters.AddWithValue("@id", change.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> CheckpointAsync(SqlConnection connection, Guid endpointId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT LastAppliedVersion FROM dbo.SyncCheckpoints WHERE EndpointID = @endpoint AND StreamName = @stream;",
            connection);
        command.Parameters.AddWithValue("@endpoint", endpointId);
        command.Parameters.AddWithValue("@stream", StreamName);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is long version ? version : 0;
    }

    private static async Task SaveCheckpointAsync(SqlConnection connection, Guid endpointId, long version, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            IF EXISTS (SELECT 1 FROM dbo.SyncCheckpoints WHERE EndpointID = @endpoint AND StreamName = @stream)
                UPDATE dbo.SyncCheckpoints SET LastAppliedVersion = @version WHERE EndpointID = @endpoint AND StreamName = @stream;
            ELSE
                INSERT INTO dbo.SyncCheckpoints (EndpointID, StreamName, LastAppliedVersion) VALUES (@endpoint, @stream, @version);
            """,
            connection);
        command.Parameters.AddWithValue("@endpoint", endpointId);
        command.Parameters.AddWithValue("@stream", StreamName);
        command.Parameters.AddWithValue("@version", version);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SetEndpointStateAsync(SqlConnection connection, Guid endpointId, string? error, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            UPDATE dbo.SyncEndpoints
            SET LastError = @error,
                LastSuccessfulSyncAt = CASE WHEN @error IS NULL THEN SYSUTCDATETIME() ELSE LastSuccessfulSyncAt END
            WHERE EndpointID = @endpoint;
            """,
            connection);
        command.Parameters.AddWithValue("@endpoint", endpointId);
        command.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> CountAsync(SqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static string PromotionSql(string central) =>
        $"""
        INSERT INTO [{central}].dbo.Businesses (BusinessID, BusinessName, BusinessDisplayName, Phone, Description, Address, IsActive, CreatedAt, UpdatedAt)
        SELECT BusinessID, BusinessName, BusinessDisplayName, Phone, Description, Address, IsActive, CreatedAt, UpdatedAt
        FROM dbo.Businesses AS source
        WHERE NOT EXISTS (SELECT 1 FROM [{central}].dbo.Businesses AS target WHERE target.BusinessID = source.BusinessID);

        INSERT INTO [{central}].dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt, UpdatedAt)
        SELECT BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt, UpdatedAt
        FROM dbo.Branches AS source
        WHERE NOT EXISTS (SELECT 1 FROM [{central}].dbo.Branches AS target WHERE target.BranchID = source.BranchID);

        INSERT INTO [{central}].dbo.Roles (RoleID, RoleCode, RoleName, Description, IsSystemRole, IsActive, CreatedAt, UpdatedAt)
        SELECT RoleID, RoleCode, RoleName, Description, IsSystemRole, IsActive, CreatedAt, UpdatedAt
        FROM dbo.Roles AS source
        WHERE NOT EXISTS (SELECT 1 FROM [{central}].dbo.Roles AS target WHERE target.RoleID = source.RoleID);

        INSERT INTO [{central}].dbo.Permissions (PermissionID, PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
        SELECT PermissionID, PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt
        FROM dbo.Permissions AS source
        WHERE NOT EXISTS (SELECT 1 FROM [{central}].dbo.Permissions AS target WHERE target.PermissionID = source.PermissionID)
          AND NOT EXISTS (SELECT 1 FROM [{central}].dbo.Permissions AS target WHERE target.PermissionCode = source.PermissionCode);

        INSERT INTO [{central}].dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
        SELECT source.RoleID, permission.PermissionID, source.GrantedAt
        FROM dbo.RolePermissions AS source
        INNER JOIN dbo.Permissions AS sourcePermission ON sourcePermission.PermissionID = source.PermissionID
        INNER JOIN [{central}].dbo.Permissions AS permission ON permission.PermissionCode = sourcePermission.PermissionCode
        WHERE NOT EXISTS (
            SELECT 1 FROM [{central}].dbo.RolePermissions AS target
            WHERE target.RoleID = source.RoleID AND target.PermissionID = permission.PermissionID);

        INSERT INTO [{central}].dbo.Users (UserID, Username, NormalizedUsername, DisplayName, FullName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, CreatedAt, UpdatedAt)
        SELECT UserID, Username, NormalizedUsername, DisplayName, FullName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, CreatedAt, UpdatedAt
        FROM dbo.Users AS source
        WHERE NOT EXISTS (SELECT 1 FROM [{central}].dbo.Users AS target WHERE target.UserID = source.UserID);

        INSERT INTO [{central}].dbo.UserRoles (UserID, RoleID, AssignedAt)
        SELECT UserID, RoleID, AssignedAt FROM dbo.UserRoles AS source
        WHERE NOT EXISTS (
            SELECT 1 FROM [{central}].dbo.UserRoles AS target
            WHERE target.UserID = source.UserID AND target.RoleID = source.RoleID);

        INSERT INTO [{central}].dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
        SELECT UserID, BranchID, IsActive, GrantedAt FROM dbo.UserBranchAccess AS source
        WHERE NOT EXISTS (
            SELECT 1 FROM [{central}].dbo.UserBranchAccess AS target
            WHERE target.UserID = source.UserID AND target.BranchID = source.BranchID);

        INSERT INTO [{central}].dbo.PriceTypes (PriceTypeID, PriceCode, PriceName, SortOrder, IsActive, CreatedAt)
        SELECT PriceTypeID, PriceCode, PriceName, SortOrder, IsActive, CreatedAt
        FROM dbo.PriceTypes AS source
        WHERE NOT EXISTS (SELECT 1 FROM [{central}].dbo.PriceTypes AS target WHERE target.PriceTypeID = source.PriceTypeID);

        INSERT INTO [{central}].dbo.Items (
            ItemID, ItemNumber, ItemName, ItemType, Manufacturer, Color, Taste, Size, Packing, Classification,
            CountryOfOrigin, ProductType, ParentItemID, IsProhibited, IsPublishedOnline, IsActive, CreatedAt, UpdatedAt)
        SELECT
            ItemID, ItemNumber, ItemName, ItemType, Manufacturer, Color, Taste, Size, Packing, Classification,
            CountryOfOrigin, ProductType, NULL, IsProhibited, IsPublishedOnline, IsActive, CreatedAt, UpdatedAt
        FROM dbo.Items AS source
        WHERE NOT EXISTS (SELECT 1 FROM [{central}].dbo.Items AS target WHERE target.ItemID = source.ItemID);

        UPDATE target
        SET ParentItemID = source.ParentItemID,
            ItemName = source.ItemName,
            IsActive = source.IsActive,
            IsProhibited = source.IsProhibited,
            UpdatedAt = source.UpdatedAt
        FROM [{central}].dbo.Items AS target
        INNER JOIN dbo.Items AS source ON source.ItemID = target.ItemID;

        INSERT INTO [{central}].dbo.ItemUnits (
            ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, InternalUnitItemNumber, InternalUnitItemName, IsBaseUnit, IsActive, CreatedAt, UpdatedAt)
        SELECT ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, InternalUnitItemNumber, InternalUnitItemName, IsBaseUnit, IsActive, CreatedAt, UpdatedAt
        FROM dbo.ItemUnits AS source
        WHERE NOT EXISTS (SELECT 1 FROM [{central}].dbo.ItemUnits AS target WHERE target.ItemUnitID = source.ItemUnitID);

        INSERT INTO [{central}].dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, Price, IsActive, CreatedAt, UpdatedAt)
        SELECT ItemPriceID, ItemID, PriceTypeID, Price, IsActive, CreatedAt, UpdatedAt
        FROM dbo.ItemPrices AS source
        WHERE source.BranchID IS NULL
          AND NOT EXISTS (SELECT 1 FROM [{central}].dbo.ItemPrices AS target WHERE target.ItemPriceID = source.ItemPriceID);

        {CustomerMasterCopySql("dbo", $"[{central}].dbo")}

        INSERT INTO [{central}].dbo.DataImportHistory (ImportID, ImportType, Source, StartedAt, CompletedAt, Status, RowsProcessed, Notes)
        VALUES (NEWID(), N'PromoteSharedDataToCentral', DB_NAME(), SYSUTCDATETIME(), SYSUTCDATETIME(), N'Completed', (SELECT COUNT(*) FROM dbo.Items), N'Explicit promotion of shared master data. Operational stock was not copied.');

        INSERT INTO dbo.DataImportHistory (ImportID, ImportType, Source, StartedAt, CompletedAt, Status, RowsProcessed, Notes)
        VALUES (NEWID(), N'PromoteSharedDataToCentral', N'{central}', SYSUTCDATETIME(), SYSUTCDATETIME(), N'Completed', (SELECT COUNT(*) FROM dbo.Items), N'Explicit promotion of shared master data. Operational stock was not copied.');

        SELECT COUNT(*) FROM [{central}].dbo.Items;
        """;

    private static string BootstrapSql(string source, string businessDescription, string itemSearchStrict, string itemSearchLoose) =>
        $"""
        INSERT INTO dbo.Businesses (BusinessID, BusinessName, BusinessDisplayName, Phone, Description, Address, IsActive, CreatedAt, UpdatedAt)
        SELECT BusinessID, BusinessName, BusinessDisplayName, Phone, {businessDescription}, Address, IsActive, CreatedAt, UpdatedAt
        FROM {source}Businesses AS source
        WHERE NOT EXISTS (SELECT 1 FROM dbo.Businesses AS target WHERE target.BusinessID = source.BusinessID);

        INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt, UpdatedAt)
        SELECT BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt, UpdatedAt
        FROM {source}Branches AS source
        WHERE NOT EXISTS (SELECT 1 FROM dbo.Branches AS target WHERE target.BranchID = source.BranchID);

        UPDATE target
        SET BusinessName = source.BusinessName,
            BusinessDisplayName = source.BusinessDisplayName,
            Phone = source.Phone,
            Description = {businessDescription},
            Address = source.Address,
            IsActive = source.IsActive
        FROM dbo.Businesses AS target
        INNER JOIN {source}Businesses AS source ON source.BusinessID = target.BusinessID;

        UPDATE target
        SET BranchName = source.BranchName, BranchCode = source.BranchCode, BranchType = source.BranchType, IsActive = source.IsActive
        FROM dbo.Branches AS target
        INNER JOIN {source}Branches AS source ON source.BranchID = target.BranchID;

        INSERT INTO dbo.Roles (RoleID, RoleCode, RoleName, Description, IsSystemRole, IsActive, CreatedAt, UpdatedAt)
        SELECT RoleID, RoleCode, RoleName, Description, IsSystemRole, IsActive, CreatedAt, UpdatedAt
        FROM {source}Roles AS source
        WHERE NOT EXISTS (SELECT 1 FROM dbo.Roles AS target WHERE target.RoleID = source.RoleID)
          AND NOT EXISTS (SELECT 1 FROM dbo.Roles AS target WHERE target.RoleCode = source.RoleCode);

        INSERT INTO dbo.Permissions (PermissionID, PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
        SELECT PermissionID, PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt
        FROM {source}Permissions AS source
        WHERE NOT EXISTS (SELECT 1 FROM dbo.Permissions AS target WHERE target.PermissionID = source.PermissionID)
          AND NOT EXISTS (SELECT 1 FROM dbo.Permissions AS target WHERE target.PermissionCode = source.PermissionCode);

        INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
        SELECT localRole.RoleID, permission.PermissionID, source.GrantedAt
        FROM {source}RolePermissions AS source
        INNER JOIN {source}Roles AS sourceRole ON sourceRole.RoleID = source.RoleID
        INNER JOIN dbo.Roles AS localRole ON localRole.RoleCode = sourceRole.RoleCode
        INNER JOIN {source}Permissions AS sourcePermission ON sourcePermission.PermissionID = source.PermissionID
        INNER JOIN dbo.Permissions AS permission ON permission.PermissionCode = sourcePermission.PermissionCode
        WHERE NOT EXISTS (
            SELECT 1 FROM dbo.RolePermissions AS target
            WHERE target.RoleID = localRole.RoleID AND target.PermissionID = permission.PermissionID);

        INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, FullName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, FailedLoginCount, CreatedAt, UpdatedAt)
        SELECT UserID, Username, NormalizedUsername, DisplayName, FullName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, 0, CreatedAt, UpdatedAt
        FROM {source}Users AS source
        WHERE NOT EXISTS (SELECT 1 FROM dbo.Users AS target WHERE target.UserID = source.UserID);

        UPDATE target
        SET DisplayName = source.DisplayName,
            FullName = source.FullName,
            EmployeeNumber = source.EmployeeNumber,
            PasswordHash = source.PasswordHash,
            IsActive = source.IsActive,
            MustChangePassword = source.MustChangePassword
        FROM dbo.Users AS target
        INNER JOIN {source}Users AS source ON source.UserID = target.UserID;

        INSERT INTO dbo.UserRoles (UserID, RoleID, AssignedAt)
        SELECT source.UserID, localRole.RoleID, source.AssignedAt
        FROM {source}UserRoles AS source
        INNER JOIN {source}Roles AS sourceRole ON sourceRole.RoleID = source.RoleID
        INNER JOIN dbo.Roles AS localRole ON localRole.RoleCode = sourceRole.RoleCode
        WHERE NOT EXISTS (
            SELECT 1 FROM dbo.UserRoles AS target
            WHERE target.UserID = source.UserID AND target.RoleID = localRole.RoleID);

        INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
        SELECT UserID, BranchID, IsActive, GrantedAt FROM {source}UserBranchAccess AS source
        WHERE NOT EXISTS (
            SELECT 1 FROM dbo.UserBranchAccess AS target
            WHERE target.UserID = source.UserID AND target.BranchID = source.BranchID);

        UPDATE target
        SET IsActive = source.IsActive
        FROM dbo.UserBranchAccess AS target
        INNER JOIN {source}UserBranchAccess AS source
            ON source.UserID = target.UserID AND source.BranchID = target.BranchID;

        INSERT INTO dbo.PriceTypes (PriceTypeID, PriceCode, PriceName, SortOrder, IsActive, CreatedAt)
        SELECT PriceTypeID, PriceCode, PriceName, SortOrder, IsActive, CreatedAt
        FROM {source}PriceTypes AS source
        WHERE NOT EXISTS (SELECT 1 FROM dbo.PriceTypes AS target WHERE target.PriceTypeID = source.PriceTypeID)
          AND NOT EXISTS (SELECT 1 FROM dbo.PriceTypes AS target WHERE target.PriceCode = source.PriceCode);

        INSERT INTO dbo.Items (
            ItemID, ItemNumber, ItemName, ItemNameSearchStrict, ItemNameSearchLoose, ItemType, Manufacturer, Color, Taste, Size, Packing, Classification,
            CountryOfOrigin, ProductType, ParentItemID, IsProhibited, IsPublishedOnline, IsActive, CreatedAt, UpdatedAt)
        SELECT
            ItemID, ItemNumber, ItemName, {itemSearchStrict}, {itemSearchLoose}, ItemType, Manufacturer, Color, Taste, Size, Packing, Classification,
            CountryOfOrigin, ProductType, NULL, IsProhibited, IsPublishedOnline, IsActive, CreatedAt, UpdatedAt
        FROM {source}Items AS source
        WHERE NOT EXISTS (SELECT 1 FROM dbo.Items AS target WHERE target.ItemID = source.ItemID);

        UPDATE target
        SET ItemName = source.ItemName,
            ItemNameSearchStrict = {itemSearchStrict},
            ItemNameSearchLoose = {itemSearchLoose},
            ItemType = source.ItemType,
            Manufacturer = source.Manufacturer,
            Color = source.Color,
            Taste = source.Taste,
            Size = source.Size,
            Packing = source.Packing,
            Classification = source.Classification,
            CountryOfOrigin = source.CountryOfOrigin,
            ProductType = source.ProductType,
            ParentItemID = source.ParentItemID,
            IsProhibited = source.IsProhibited,
            IsPublishedOnline = source.IsPublishedOnline,
            IsActive = source.IsActive,
            UpdatedAt = source.UpdatedAt
        FROM dbo.Items AS target
        INNER JOIN {source}Items AS source ON source.ItemID = target.ItemID;

        INSERT INTO dbo.ItemUnits (
            ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, InternalUnitItemNumber, InternalUnitItemName, IsBaseUnit, IsActive, CreatedAt, UpdatedAt)
        SELECT ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, InternalUnitItemNumber, InternalUnitItemName, IsBaseUnit, IsActive, CreatedAt, UpdatedAt
        FROM {source}ItemUnits AS source
        WHERE NOT EXISTS (SELECT 1 FROM dbo.ItemUnits AS target WHERE target.ItemUnitID = source.ItemUnitID);

        INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive, CreatedAt, UpdatedAt)
        SELECT NEWID(), source.ItemID, localPrice.PriceTypeID, NULL, source.Price, source.IsActive, source.CreatedAt, source.UpdatedAt
        FROM {source}ItemPrices AS source
        INNER JOIN {source}PriceTypes AS sourcePrice ON sourcePrice.PriceTypeID = source.PriceTypeID
        INNER JOIN dbo.PriceTypes AS localPrice ON localPrice.PriceCode = sourcePrice.PriceCode
        WHERE NOT EXISTS (
            SELECT 1 FROM dbo.ItemPrices AS target
            WHERE target.ItemID = source.ItemID AND target.PriceTypeID = localPrice.PriceTypeID AND target.BranchID IS NULL);

        UPDATE target
        SET Price = source.Price, IsActive = source.IsActive, UpdatedAt = source.UpdatedAt
        FROM dbo.ItemPrices AS target
        INNER JOIN dbo.PriceTypes AS localPrice ON localPrice.PriceTypeID = target.PriceTypeID
        INNER JOIN {source}PriceTypes AS sourcePrice ON sourcePrice.PriceCode = localPrice.PriceCode
        INNER JOIN {source}ItemPrices AS source
            ON source.ItemID = target.ItemID AND source.PriceTypeID = sourcePrice.PriceTypeID
        WHERE target.BranchID IS NULL;

        {CustomerMasterCopySql(source, "dbo")}

        SELECT COUNT(*) FROM dbo.Items;
        """;

    private static string CustomerMasterCopySql(string source, string target)
    {
        var from = Qualify(source);
        var into = Qualify(target);
        var specials = source == "#"
            ? ""
            : $"""

            INSERT INTO {into}CustomerItemSpecialPrices (
                CustomerItemSpecialPriceID, BusinessID, CustomerID, ItemID, SpecialPrice, IsActive,
                ValidFrom, ValidTo, Notes, CreatedAt, UpdatedAt, CreatedByUserID, UpdatedByUserID,
                MinimumQuantity, BundlePrice)
            SELECT
                source.CustomerItemSpecialPriceID, source.BusinessID, source.CustomerID, source.ItemID, source.SpecialPrice, source.IsActive,
                source.ValidFrom, source.ValidTo, source.Notes, source.CreatedAt, source.UpdatedAt, source.CreatedByUserID, source.UpdatedByUserID,
                source.MinimumQuantity, source.BundlePrice
            FROM {from}CustomerItemSpecialPrices AS source
            WHERE EXISTS (SELECT 1 FROM {into}Customers AS customer WHERE customer.CustomerID = source.CustomerID)
              AND EXISTS (SELECT 1 FROM {into}Items AS item WHERE item.ItemID = source.ItemID)
              AND NOT EXISTS (
                SELECT 1 FROM {into}CustomerItemSpecialPrices AS target
                WHERE target.CustomerItemSpecialPriceID = source.CustomerItemSpecialPriceID);

            UPDATE target
            SET SpecialPrice = source.SpecialPrice,
                IsActive = source.IsActive,
                ValidFrom = source.ValidFrom,
                ValidTo = source.ValidTo,
                Notes = source.Notes,
                MinimumQuantity = source.MinimumQuantity,
                BundlePrice = source.BundlePrice,
                UpdatedAt = source.UpdatedAt
            FROM {into}CustomerItemSpecialPrices AS target
            INNER JOIN {from}CustomerItemSpecialPrices AS source
                ON source.CustomerItemSpecialPriceID = target.CustomerItemSpecialPriceID
            WHERE target.UpdatedAt IS NULL OR source.UpdatedAt IS NULL OR source.UpdatedAt >= target.UpdatedAt;
            """;
        return $"""
            INSERT INTO {into}CustomerClassifications (
                CustomerClassificationID, BusinessID, Code, Name, DefaultPriceTypeID,
                DefaultVariableValueDiscountPercent, IsDefault, IsActive, CreatedAt, UpdatedAt)
            SELECT
                source.CustomerClassificationID, source.BusinessID, source.Code, source.Name,
                COALESCE(samePrice.PriceTypeID, mappedPrice.PriceTypeID),
                source.DefaultVariableValueDiscountPercent, source.IsDefault, source.IsActive,
                source.CreatedAt, source.UpdatedAt
            FROM {from}CustomerClassifications AS source
            LEFT JOIN {into}PriceTypes AS samePrice ON samePrice.PriceTypeID = source.DefaultPriceTypeID
            LEFT JOIN {from}PriceTypes AS sourcePrice ON sourcePrice.PriceTypeID = source.DefaultPriceTypeID
            LEFT JOIN {into}PriceTypes AS mappedPrice ON mappedPrice.PriceCode = sourcePrice.PriceCode
            WHERE NOT EXISTS (
                SELECT 1 FROM {into}CustomerClassifications AS target
                WHERE target.CustomerClassificationID = source.CustomerClassificationID);

            UPDATE target
            SET Code = source.Code,
                Name = source.Name,
                DefaultPriceTypeID = COALESCE(samePrice.PriceTypeID, mappedPrice.PriceTypeID),
                DefaultVariableValueDiscountPercent = source.DefaultVariableValueDiscountPercent,
                IsDefault = source.IsDefault,
                IsActive = source.IsActive,
                UpdatedAt = source.UpdatedAt
            FROM {into}CustomerClassifications AS target
            INNER JOIN {from}CustomerClassifications AS source
                ON source.CustomerClassificationID = target.CustomerClassificationID
            LEFT JOIN {into}PriceTypes AS samePrice ON samePrice.PriceTypeID = source.DefaultPriceTypeID
            LEFT JOIN {from}PriceTypes AS sourcePrice ON sourcePrice.PriceTypeID = source.DefaultPriceTypeID
            LEFT JOIN {into}PriceTypes AS mappedPrice ON mappedPrice.PriceCode = sourcePrice.PriceCode;

            INSERT INTO {into}Customers (
                CustomerID, BusinessID, CustomerNumber, Name, Phone, NormalizedPhone,
                SecondaryPhone, SecondaryNormalizedPhone, CustomerClassificationID, PriceTypeID,
                VariableValueDiscountPercent, IsActive, Notes, LegacySource, LegacyCustomerKey,
                CreatedAt, UpdatedAt)
            SELECT
                source.CustomerID, source.BusinessID, source.CustomerNumber, source.Name, source.Phone, source.NormalizedPhone,
                source.SecondaryPhone, source.SecondaryNormalizedPhone, source.CustomerClassificationID,
                COALESCE(samePrice.PriceTypeID, mappedPrice.PriceTypeID),
                source.VariableValueDiscountPercent, source.IsActive, source.Notes, source.LegacySource, source.LegacyCustomerKey,
                source.CreatedAt, source.UpdatedAt
            FROM {from}Customers AS source
            LEFT JOIN {into}PriceTypes AS samePrice ON samePrice.PriceTypeID = source.PriceTypeID
            LEFT JOIN {from}PriceTypes AS sourcePrice ON sourcePrice.PriceTypeID = source.PriceTypeID
            LEFT JOIN {into}PriceTypes AS mappedPrice ON mappedPrice.PriceCode = sourcePrice.PriceCode
            WHERE NOT EXISTS (
                SELECT 1 FROM {into}Customers AS target WHERE target.CustomerID = source.CustomerID);

            UPDATE target
            SET CustomerNumber = source.CustomerNumber,
                Name = source.Name,
                Phone = source.Phone,
                NormalizedPhone = source.NormalizedPhone,
                SecondaryPhone = source.SecondaryPhone,
                SecondaryNormalizedPhone = source.SecondaryNormalizedPhone,
                CustomerClassificationID = source.CustomerClassificationID,
                PriceTypeID = COALESCE(samePrice.PriceTypeID, mappedPrice.PriceTypeID),
                VariableValueDiscountPercent = source.VariableValueDiscountPercent,
                IsActive = source.IsActive,
                Notes = source.Notes,
                LegacySource = source.LegacySource,
                LegacyCustomerKey = source.LegacyCustomerKey,
                UpdatedAt = source.UpdatedAt
            FROM {into}Customers AS target
            INNER JOIN {from}Customers AS source ON source.CustomerID = target.CustomerID
            LEFT JOIN {into}PriceTypes AS samePrice ON samePrice.PriceTypeID = source.PriceTypeID
            LEFT JOIN {from}PriceTypes AS sourcePrice ON sourcePrice.PriceTypeID = source.PriceTypeID
            LEFT JOIN {into}PriceTypes AS mappedPrice ON mappedPrice.PriceCode = sourcePrice.PriceCode
            WHERE target.UpdatedAt IS NULL OR source.UpdatedAt IS NULL OR source.UpdatedAt >= target.UpdatedAt;
            {specials}
            """;
    }

    private static string Qualify(string name)
    {
        if (name is "#")
        {
            return "#";
        }

        return name.EndsWith('.') ? name : name + ".";
    }

    private static async Task<bool> CentralColumnExistsAsync(
        SqlEndpointSettings central,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT COL_LENGTH(@table, @column);",
            connection);
        command.Parameters.AddWithValue("@table", "dbo." + tableName);
        command.Parameters.AddWithValue("@column", columnName);
        var length = await command.ExecuteScalarAsync(cancellationToken);
        return length is not null and not DBNull;
    }

    private static async Task StageRemoteCatalogAsync(
        SqlEndpointSettings central,
        SqlConnection branch,
        SqlTransaction transaction,
        bool hasBusinessDescription,
        bool hasItemSearch,
        CancellationToken cancellationToken)
    {
        await using var source = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await source.OpenAsync(cancellationToken);
        await ExecuteAsync(branch, transaction, """
            CREATE TABLE #Businesses (
                BusinessID uniqueidentifier NOT NULL,
                BusinessName nvarchar(200) COLLATE DATABASE_DEFAULT NOT NULL,
                BusinessDisplayName nvarchar(200) COLLATE DATABASE_DEFAULT NULL,
                Phone nvarchar(50) COLLATE DATABASE_DEFAULT NULL,
                Description nvarchar(500) COLLATE DATABASE_DEFAULT NULL,
                Address nvarchar(500) COLLATE DATABASE_DEFAULT NULL,
                IsActive bit NOT NULL,
                CreatedAt datetime2(0) NOT NULL,
                UpdatedAt datetime2(0) NULL);
            CREATE TABLE #Branches (
                BranchID uniqueidentifier NOT NULL,
                BusinessID uniqueidentifier NOT NULL,
                BranchCode nvarchar(50) COLLATE DATABASE_DEFAULT NULL,
                BranchName nvarchar(200) COLLATE DATABASE_DEFAULT NOT NULL,
                BranchType nvarchar(30) COLLATE DATABASE_DEFAULT NOT NULL,
                IsActive bit NOT NULL,
                CreatedAt datetime2(0) NOT NULL,
                UpdatedAt datetime2(0) NULL);
            CREATE TABLE #Roles (
                RoleID uniqueidentifier NOT NULL,
                RoleCode nvarchar(50) COLLATE DATABASE_DEFAULT NOT NULL,
                RoleName nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                Description nvarchar(500) COLLATE DATABASE_DEFAULT NULL,
                IsSystemRole bit NOT NULL,
                IsActive bit NOT NULL,
                CreatedAt datetime2(0) NOT NULL,
                UpdatedAt datetime2(0) NULL);
            CREATE TABLE #Permissions (
                PermissionID uniqueidentifier NOT NULL,
                PermissionCode nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                PermissionName nvarchar(150) COLLATE DATABASE_DEFAULT NOT NULL,
                Module nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                Description nvarchar(500) COLLATE DATABASE_DEFAULT NULL,
                IsActive bit NOT NULL,
                CreatedAt datetime2(0) NOT NULL);
            CREATE TABLE #RolePermissions (
                RoleID uniqueidentifier NOT NULL,
                PermissionID uniqueidentifier NOT NULL,
                GrantedAt datetime2(0) NOT NULL);
            CREATE TABLE #Users (
                UserID uniqueidentifier NOT NULL,
                Username nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                NormalizedUsername nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                DisplayName nvarchar(150) COLLATE DATABASE_DEFAULT NOT NULL,
                FullName nvarchar(300) COLLATE DATABASE_DEFAULT NULL,
                EmployeeNumber nvarchar(50) COLLATE DATABASE_DEFAULT NULL,
                PasswordHash nvarchar(1000) COLLATE DATABASE_DEFAULT NOT NULL,
                IsActive bit NOT NULL,
                MustChangePassword bit NOT NULL,
                CreatedAt datetime2(0) NOT NULL,
                UpdatedAt datetime2(0) NULL);
            CREATE TABLE #UserRoles (
                UserID uniqueidentifier NOT NULL,
                RoleID uniqueidentifier NOT NULL,
                AssignedAt datetime2(0) NOT NULL);
            CREATE TABLE #UserBranchAccess (
                UserID uniqueidentifier NOT NULL,
                BranchID uniqueidentifier NOT NULL,
                IsActive bit NOT NULL,
                GrantedAt datetime2(0) NOT NULL);
            CREATE TABLE #PriceTypes (
                PriceTypeID uniqueidentifier NOT NULL,
                PriceCode nvarchar(50) COLLATE DATABASE_DEFAULT NOT NULL,
                PriceName nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                SortOrder int NOT NULL,
                IsActive bit NOT NULL,
                CreatedAt datetime2(0) NOT NULL);
            CREATE TABLE #Items (
                ItemID uniqueidentifier NOT NULL,
                ItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NOT NULL,
                ItemName nvarchar(500) COLLATE DATABASE_DEFAULT NOT NULL,
                ItemNameSearchStrict nvarchar(500) COLLATE DATABASE_DEFAULT NULL,
                ItemNameSearchLoose nvarchar(500) COLLATE DATABASE_DEFAULT NULL,
                ItemType nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                Manufacturer nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                Color nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                Taste nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                Size nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                Packing nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                Classification nvarchar(200) COLLATE DATABASE_DEFAULT NULL,
                CountryOfOrigin nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                ProductType nvarchar(50) COLLATE DATABASE_DEFAULT NULL,
                ParentItemID uniqueidentifier NULL,
                IsProhibited bit NOT NULL,
                IsPublishedOnline bit NOT NULL,
                IsActive bit NOT NULL,
                CreatedAt datetime2(0) NOT NULL,
                UpdatedAt datetime2(0) NOT NULL);
            CREATE TABLE #ItemUnits (
                ItemUnitID uniqueidentifier NOT NULL,
                ItemID uniqueidentifier NOT NULL,
                UnitsPerPackage decimal(18, 4) NOT NULL,
                InternalUnitItemID uniqueidentifier NULL,
                InternalUnitItemNumber nvarchar(100) COLLATE DATABASE_DEFAULT NULL,
                InternalUnitItemName nvarchar(500) COLLATE DATABASE_DEFAULT NULL,
                IsBaseUnit bit NOT NULL,
                IsActive bit NOT NULL,
                CreatedAt datetime2(0) NOT NULL,
                UpdatedAt datetime2(0) NOT NULL);
            CREATE TABLE #ItemPrices (
                ItemID uniqueidentifier NOT NULL,
                PriceTypeID uniqueidentifier NOT NULL,
                Price decimal(19, 4) NOT NULL,
                IsActive bit NOT NULL,
                CreatedAt datetime2(0) NOT NULL,
                UpdatedAt datetime2(0) NOT NULL);
            CREATE TABLE #CustomerClassifications (
                CustomerClassificationID uniqueidentifier NOT NULL,
                BusinessID uniqueidentifier NOT NULL,
                Code nvarchar(50) COLLATE DATABASE_DEFAULT NULL,
                Name nvarchar(200) COLLATE DATABASE_DEFAULT NOT NULL,
                DefaultPriceTypeID uniqueidentifier NULL,
                DefaultVariableValueDiscountPercent decimal(7, 4) NOT NULL,
                IsDefault bit NOT NULL,
                IsActive bit NOT NULL,
                CreatedAt datetime2(0) NOT NULL,
                UpdatedAt datetime2(0) NOT NULL);
            CREATE TABLE #Customers (
                CustomerID uniqueidentifier NOT NULL,
                BusinessID uniqueidentifier NOT NULL,
                CustomerNumber nvarchar(50) COLLATE DATABASE_DEFAULT NULL,
                Name nvarchar(300) COLLATE DATABASE_DEFAULT NOT NULL,
                Phone nvarchar(50) COLLATE DATABASE_DEFAULT NULL,
                NormalizedPhone nvarchar(50) COLLATE DATABASE_DEFAULT NULL,
                SecondaryPhone nvarchar(50) COLLATE DATABASE_DEFAULT NULL,
                SecondaryNormalizedPhone nvarchar(50) COLLATE DATABASE_DEFAULT NULL,
                CustomerClassificationID uniqueidentifier NULL,
                PriceTypeID uniqueidentifier NULL,
                VariableValueDiscountPercent decimal(7, 4) NOT NULL,
                IsActive bit NOT NULL,
                Notes nvarchar(1000) COLLATE DATABASE_DEFAULT NULL,
                LegacySource nvarchar(64) COLLATE DATABASE_DEFAULT NULL,
                LegacyCustomerKey nvarchar(64) COLLATE DATABASE_DEFAULT NULL,
                CreatedAt datetime2(0) NOT NULL,
                UpdatedAt datetime2(0) NOT NULL);
            """, cancellationToken);

        await CopyAsync(source, branch, transaction, "SELECT BusinessID, BusinessName, BusinessDisplayName, Phone, " + (hasBusinessDescription ? "Description" : "CAST(NULL AS nvarchar(500))") + ", Address, IsActive, CreatedAt, UpdatedAt FROM dbo.Businesses;", "#Businesses", cancellationToken);
        await CopyAsync(source, branch, transaction, "SELECT BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt, UpdatedAt FROM dbo.Branches;", "#Branches", cancellationToken);
        await CopyAsync(source, branch, transaction, "SELECT RoleID, RoleCode, RoleName, Description, IsSystemRole, IsActive, CreatedAt, UpdatedAt FROM dbo.Roles;", "#Roles", cancellationToken);
        await CopyAsync(source, branch, transaction, "SELECT PermissionID, PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt FROM dbo.Permissions;", "#Permissions", cancellationToken);
        await CopyAsync(source, branch, transaction, "SELECT RoleID, PermissionID, GrantedAt FROM dbo.RolePermissions;", "#RolePermissions", cancellationToken);
        await CopyAsync(source, branch, transaction, "SELECT UserID, Username, NormalizedUsername, DisplayName, FullName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, CreatedAt, UpdatedAt FROM dbo.Users;", "#Users", cancellationToken);
        await CopyAsync(source, branch, transaction, "SELECT UserID, RoleID, AssignedAt FROM dbo.UserRoles;", "#UserRoles", cancellationToken);
        await CopyAsync(source, branch, transaction, "SELECT UserID, BranchID, IsActive, GrantedAt FROM dbo.UserBranchAccess;", "#UserBranchAccess", cancellationToken);
        await CopyAsync(source, branch, transaction, "SELECT PriceTypeID, PriceCode, PriceName, SortOrder, IsActive, CreatedAt FROM dbo.PriceTypes;", "#PriceTypes", cancellationToken);
        var itemSearchColumns = hasItemSearch ? "ItemNameSearchStrict, ItemNameSearchLoose, " : "";
        await CopyAsync(source, branch, transaction, "SELECT ItemID, ItemNumber, ItemName, " + itemSearchColumns + "ItemType, Manufacturer, Color, Taste, Size, Packing, Classification, CountryOfOrigin, ProductType, ParentItemID, IsProhibited, IsPublishedOnline, IsActive, CreatedAt, UpdatedAt FROM dbo.Items;", "#Items", cancellationToken);
        await CopyAsync(source, branch, transaction, "SELECT ItemUnitID, ItemID, UnitsPerPackage, InternalUnitItemID, InternalUnitItemNumber, InternalUnitItemName, IsBaseUnit, IsActive, CreatedAt, UpdatedAt FROM dbo.ItemUnits;", "#ItemUnits", cancellationToken);
        await CopyAsync(source, branch, transaction, "SELECT ItemID, PriceTypeID, Price, IsActive, CreatedAt, UpdatedAt FROM dbo.ItemPrices;", "#ItemPrices", cancellationToken);
        await CopyAsync(source, branch, transaction, "SELECT CustomerClassificationID, BusinessID, Code, Name, DefaultPriceTypeID, DefaultVariableValueDiscountPercent, IsDefault, IsActive, CreatedAt, UpdatedAt FROM dbo.CustomerClassifications;", "#CustomerClassifications", cancellationToken);
        await CopyAsync(source, branch, transaction, "SELECT CustomerID, BusinessID, CustomerNumber, Name, Phone, NormalizedPhone, SecondaryPhone, SecondaryNormalizedPhone, CustomerClassificationID, PriceTypeID, VariableValueDiscountPercent, IsActive, Notes, LegacySource, LegacyCustomerKey, CreatedAt, UpdatedAt FROM dbo.Customers;", "#Customers", cancellationToken);
    }

    private static async Task CopyAsync(
        SqlConnection source,
        SqlConnection destination,
        SqlTransaction transaction,
        string sql,
        string tableName,
        CancellationToken cancellationToken)
    {
        var table = new DataTable();
        await using (var command = new SqlCommand(sql, source) { CommandTimeout = 180 })
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            table.Load(reader);
        }

        using var bulk = new SqlBulkCopy(destination, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = tableName, BulkCopyTimeout = 180 };
        foreach (DataColumn column in table.Columns)
        {
            bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
        }

        await bulk.WriteToServerAsync(table, cancellationToken);
    }

    private static async Task ExecuteAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection, transaction) { CommandTimeout = 60 };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
