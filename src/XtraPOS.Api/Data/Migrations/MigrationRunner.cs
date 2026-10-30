using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;

namespace XtraPOS.Api.Data.Migrations;

public sealed class MigrationRunner(SqlConnectionFactory connections, ILogger<MigrationRunner> logger)
{
    public const string InitialMigrationId = "001_InitialSchema";
    public const string InitialMigrationName = "Initial branch schema";
    public const string UsersMigrationId = "002_UsersRolesPermissions";
    public const string UsersMigrationName = "Users roles and permissions";
    public const string RolesMigrationId = "003_NormalizeSystemRoles";
    public const string RolesMigrationName = "Normalize system roles";
    public const string ItemsMigrationId = "004_ItemsInventoryFoundation";
    public const string ItemsMigrationName = "Items inventory foundation";
    public const string CatalogMigrationId = "005_CatalogModelRefinement";
    public const string CatalogMigrationName = "Catalog model refinement";
    public const string OwnershipMigrationId = "006_InventoryOwnership";
    public const string OwnershipMigrationName = "Inventory ownership";
    public const string SkuQuantityMigrationId = "007_RemoveSkuOnHandQuantity";
    public const string SkuQuantityMigrationName = "Remove SKU on-hand quantity";
    public const string ItemsViewMigrationId = "008_ItemsViewPermission";
    public const string ItemsViewMigrationName = "Items view permission";
    public const string ImportHistoryMigrationId = "009_DataImportHistory";
    public const string ImportHistoryMigrationName = "Data import history";
    public const string SyncFoundationMigrationId = "010_SyncFoundation";
    public const string SyncFoundationMigrationName = "Sync foundation";
    public const string ItemEditPermissionMigrationId = "011_ItemEditPermissions";
    public const string ItemEditPermissionMigrationName = "Item edit permissions";
    public const string DailySalesRateMigrationId = "012_NullableDailySalesRate";
    public const string DailySalesRateMigrationName = "Nullable daily sales rate";
    public const string UserAdministrationMigrationId = "013_UserAdministration";
    public const string UserAdministrationMigrationName = "User administration";
    public const string StockMovementSourceMigrationId = "014_StockMovementSource";
    public const string StockMovementSourceMigrationName = "Stock movement source identity";
    public const string CustomerMasterMigrationId = "015_CustomerMaster";
    public const string CustomerMasterMigrationName = "Customer master";
    public const string SalesFoundationMigrationId = "016_SalesFoundation";
    public const string SalesFoundationMigrationName = "Sales foundation";
    public const string PosPaymentsMigrationId = "017_POSPaymentsAndCashSessions";
    public const string PosPaymentsMigrationName = "POS payments and cash sessions";
    public const string ManualPriceMigrationId = "018_ManualLinePriceOverride";
    public const string ManualPriceMigrationName = "Manual line price override";
    public const string ArabicSearchMigrationId = "019_ArabicSearchKeys";
    public const string ArabicSearchMigrationName = "Arabic search keys";
    public const string CustomerVerificationMigrationId = "020_CustomerVerificationAndSpecialPrices";
    public const string CustomerVerificationMigrationName = "Customer verification and special prices";
    public const string TwilioVerificationMigrationId = "021_TwilioPhoneVerification";
    public const string TwilioVerificationMigrationName = "Twilio phone verification";
    public const string RegistrationVerificationMigrationId = "022_RegistrationPhoneVerification";
    public const string RegistrationVerificationMigrationName = "Registration phone verification";
    public const string QuantitySpecialPriceMigrationId = "023_QuantitySpecialPrices";
    public const string QuantitySpecialPriceMigrationName = "Quantity special prices";
    public const string CreditPermissionMigrationId = "024_CreditPermissions";
    public const string CreditPermissionMigrationName = "Credit permissions";
    public const string CreditSaleOutboxMigrationId = "025_CreditSaleOutbox";
    public const string CreditSaleOutboxMigrationName = "Credit sale outbox";
    public const string CreditOutboxLeaseMigrationId = "026_CreditOutboxLease";
    public const string DebtCollectionMigrationId = "027_DebtCollections";
    public const string DebtCollectionMigrationName = "Debt collections";
    public const string CollectionReversalMigrationId = "028_CollectionReversals";
    public const string CollectionReversalMigrationName = "Collection reversals";
    public const string SupervisorCustomerManageMigrationId = "029_SupervisorCustomerManage";
    public const string SupervisorCustomerManageMigrationName = "Supervisor customer manage";
    public const string CustomerCreatePermissionMigrationId = "030_CustomerCreatePermission";
    public const string CustomerPasskeyMigrationId = "031_CustomerPasskeys";
    public const string CustomerPasskeyMigrationName = "Customer passkeys";
    public const string DeviceConfigurationMigrationId = "032_POSDeviceConfigurations";
    public const string DeviceConfigurationMigrationName = "POS device configurations";
    public const string CustomerCreatePermissionMigrationName = "Customer create permission";
    public const string CreditOutboxLeaseMigrationName = "Credit outbox lease";

    private static readonly string ApplicationVersion =
        typeof(MigrationRunner).Assembly.GetName().Version?.ToString(3) ?? "0.2.0";

    private readonly SemaphoreSlim _gate = new(1, 1);

    private static readonly IReadOnlyList<MigrationDefinition> Migrations =
    [
        new(
            InitialMigrationId,
            InitialMigrationName,
            "XtraPOS.Api.Data.Migrations.001_InitialSchema.sql"),
        new(
            UsersMigrationId,
            UsersMigrationName,
            "XtraPOS.Api.Data.Migrations.002_UsersRolesPermissions.sql"),
        new(
            RolesMigrationId,
            RolesMigrationName,
            "XtraPOS.Api.Data.Migrations.003_NormalizeSystemRoles.sql"),
        new(
            ItemsMigrationId,
            ItemsMigrationName,
            "XtraPOS.Api.Data.Migrations.004_ItemsInventoryFoundation.sql"),
        new(
            CatalogMigrationId,
            CatalogMigrationName,
            "XtraPOS.Api.Data.Migrations.005_CatalogModelRefinement.sql"),
        new(
            OwnershipMigrationId,
            OwnershipMigrationName,
            "XtraPOS.Api.Data.Migrations.006_InventoryOwnership.sql"),
        new(
            SkuQuantityMigrationId,
            SkuQuantityMigrationName,
            "XtraPOS.Api.Data.Migrations.007_RemoveSkuOnHandQuantity.sql"),
        new(
            ItemsViewMigrationId,
            ItemsViewMigrationName,
            "XtraPOS.Api.Data.Migrations.008_ItemsViewPermission.sql"),
        new(
            ImportHistoryMigrationId,
            ImportHistoryMigrationName,
            "XtraPOS.Api.Data.Migrations.009_DataImportHistory.sql"),
        new(
            SyncFoundationMigrationId,
            SyncFoundationMigrationName,
            "XtraPOS.Api.Data.Migrations.010_SyncFoundation.sql"),
        new(
            ItemEditPermissionMigrationId,
            ItemEditPermissionMigrationName,
            "XtraPOS.Api.Data.Migrations.011_ItemEditPermissions.sql"),
        new(
            DailySalesRateMigrationId,
            DailySalesRateMigrationName,
            "XtraPOS.Api.Data.Migrations.012_NullableDailySalesRate.sql"),
        new(
            UserAdministrationMigrationId,
            UserAdministrationMigrationName,
            "XtraPOS.Api.Data.Migrations.013_UserAdministration.sql"),
        new(
            StockMovementSourceMigrationId,
            StockMovementSourceMigrationName,
            "XtraPOS.Api.Data.Migrations.014_StockMovementSource.sql"),
        new(
            CustomerMasterMigrationId,
            CustomerMasterMigrationName,
            "XtraPOS.Api.Data.Migrations.015_CustomerMaster.sql"),
        new(
            SalesFoundationMigrationId,
            SalesFoundationMigrationName,
            "XtraPOS.Api.Data.Migrations.016_SalesFoundation.sql"),
        new(
            PosPaymentsMigrationId,
            PosPaymentsMigrationName,
            "XtraPOS.Api.Data.Migrations.017_POSPaymentsAndCashSessions.sql"),
        new(
            ManualPriceMigrationId,
            ManualPriceMigrationName,
            "XtraPOS.Api.Data.Migrations.018_ManualLinePriceOverride.sql"),
        new(
            ArabicSearchMigrationId,
            ArabicSearchMigrationName,
            "XtraPOS.Api.Data.Migrations.019_ArabicSearchKeys.sql"),
        new(
            CustomerVerificationMigrationId,
            CustomerVerificationMigrationName,
            "XtraPOS.Api.Data.Migrations.020_CustomerVerificationAndSpecialPrices.sql"),
        new(
            TwilioVerificationMigrationId,
            TwilioVerificationMigrationName,
            "XtraPOS.Api.Data.Migrations.021_TwilioPhoneVerification.sql"),
        new(
            RegistrationVerificationMigrationId,
            RegistrationVerificationMigrationName,
            "XtraPOS.Api.Data.Migrations.022_RegistrationPhoneVerification.sql"),
        new(
            QuantitySpecialPriceMigrationId,
            QuantitySpecialPriceMigrationName,
            "XtraPOS.Api.Data.Migrations.023_QuantitySpecialPrices.sql"),
        new(
            CreditPermissionMigrationId,
            CreditPermissionMigrationName,
            "XtraPOS.Api.Data.Migrations.024_CreditPermissions.sql"),
        new(
            CreditSaleOutboxMigrationId,
            CreditSaleOutboxMigrationName,
            "XtraPOS.Api.Data.Migrations.025_CreditSaleOutbox.sql"),
        new(
            CreditOutboxLeaseMigrationId,
            CreditOutboxLeaseMigrationName,
            "XtraPOS.Api.Data.Migrations.026_CreditOutboxLease.sql"),
        new(
            DebtCollectionMigrationId,
            DebtCollectionMigrationName,
            "XtraPOS.Api.Data.Migrations.027_DebtCollections.sql"),
        new(
            CollectionReversalMigrationId,
            CollectionReversalMigrationName,
            "XtraPOS.Api.Data.Migrations.028_CollectionReversals.sql"),
        new(
            SupervisorCustomerManageMigrationId,
            SupervisorCustomerManageMigrationName,
            "XtraPOS.Api.Data.Migrations.029_SupervisorCustomerManage.sql"),
        new(
            CustomerCreatePermissionMigrationId,
            CustomerCreatePermissionMigrationName,
            "XtraPOS.Api.Data.Migrations.030_CustomerCreatePermission.sql"),
        new(
            CustomerPasskeyMigrationId,
            CustomerPasskeyMigrationName,
            "XtraPOS.Api.Data.Migrations.031_CustomerPasskeys.sql"),
        new(
            DeviceConfigurationMigrationId,
            DeviceConfigurationMigrationName,
            "XtraPOS.Api.Data.Migrations.032_POSDeviceConfigurations.sql"),
    ];

    public async Task ApplyPendingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await ApplyPendingCoreAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<string>> ListPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var exists = new SqlCommand("SELECT OBJECT_ID(N'dbo.DatabaseSchemaHistory', N'U');", connection);
        var objectId = await exists.ExecuteScalarAsync(cancellationToken);
        var applied = new HashSet<string>(StringComparer.Ordinal);
        if (objectId is not null and not DBNull)
        {
            await using var command = new SqlCommand("SELECT MigrationID FROM dbo.DatabaseSchemaHistory;", connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                applied.Add(reader.GetString(0));
            }
        }

        return Migrations.Select(migration => migration.Id).Where(id => !applied.Contains(id)).ToArray();
    }

    public async Task<SchemaInspection> InspectAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);

        var collation = await ReadCollationAsync(connection, cancellationToken);
        var tables = await ReadTablesAsync(connection, cancellationToken);
        var migrations = await ReadMigrationIdsAsync(connection, cancellationToken);
        var conflictingCollations = await CountConflictingCollationsAsync(connection, cancellationToken);

        return new SchemaInspection(collation, tables, migrations, conflictingCollations);
    }

    public async Task ApplyPendingToAsync(string connectionString, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var applied = await ReadAppliedIdsAsync(connection, transaction, cancellationToken);
            foreach (var migration in Migrations)
            {
                if (applied.Contains(migration.Id))
                {
                    continue;
                }

                var sql = ReadEmbeddedSql(migration.ResourceName);
                await using (var command = new SqlCommand(sql, connection, transaction))
                {
                    command.CommandTimeout = 120;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }

                await using var insert = new SqlCommand(
                    """
                    INSERT INTO dbo.DatabaseSchemaHistory (MigrationID, MigrationName, ApplicationVersion, AppliedAt)
                    VALUES (@id, @name, @version, SYSUTCDATETIME());
                    """,
                    connection,
                    transaction);
                insert.Parameters.Add("@id", SqlDbType.NVarChar, 100).Value = migration.Id;
                insert.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = migration.Name;
                insert.Parameters.Add("@version", SqlDbType.NVarChar, 50).Value = ApplicationVersion;
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            await ArabicSearchKeyBackfill.ApplyAsync(connection, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyPendingCoreAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var applied = await ReadAppliedIdsAsync(connection, transaction, cancellationToken);
        foreach (var migration in Migrations)
        {
            if (applied.Contains(migration.Id))
            {
                logger.LogInformation("Migration {MigrationId} is already applied.", migration.Id);
                continue;
            }

            var sql = ReadEmbeddedSql(migration.ResourceName);
            await using (var command = new SqlCommand(sql, connection, transaction))
            {
                command.CommandTimeout = 120;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var insert = new SqlCommand(
                """
                INSERT INTO dbo.DatabaseSchemaHistory (MigrationID, MigrationName, ApplicationVersion, AppliedAt)
                VALUES (@id, @name, @version, SYSUTCDATETIME());
                """,
                connection,
                transaction);
            insert.Parameters.Add("@id", SqlDbType.NVarChar, 100).Value = migration.Id;
            insert.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = migration.Name;
            insert.Parameters.Add("@version", SqlDbType.NVarChar, 50).Value = ApplicationVersion;
            await insert.ExecuteNonQueryAsync(cancellationToken);
            logger.LogInformation("Applied migration {MigrationId}.", migration.Id);
        }

        await transaction.CommitAsync(cancellationToken);
        await ArabicSearchKeyBackfill.ApplyAsync(connection, cancellationToken);
    }

    private static async Task<HashSet<string>> ReadAppliedIdsAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var exists = new SqlCommand(
            "SELECT OBJECT_ID(N'dbo.DatabaseSchemaHistory', N'U');",
            connection,
            transaction);
        var objectId = await exists.ExecuteScalarAsync(cancellationToken);
        if (objectId is null or DBNull)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using var command = new SqlCommand(
            "SELECT MigrationID FROM dbo.DatabaseSchemaHistory WITH (UPDLOCK, HOLDLOCK);",
            connection,
            transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }

    private static async Task<string?> ReadCollationAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT DATABASEPROPERTYEX(DB_NAME(), 'Collation');", connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string collation ? collation : null;
    }

    private static async Task<HashSet<string>> ReadTablesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = new SqlCommand(
            "SELECT name FROM sys.tables WHERE schema_id = SCHEMA_ID(N'dbo');",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static async Task<HashSet<string>> ReadMigrationIdsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var migrations = new HashSet<string>(StringComparer.Ordinal);
        await using var exists = new SqlCommand(
            "SELECT OBJECT_ID(N'dbo.DatabaseSchemaHistory', N'U');",
            connection);
        var objectId = await exists.ExecuteScalarAsync(cancellationToken);
        if (objectId is null or DBNull)
        {
            return migrations;
        }

        await using var command = new SqlCommand(
            "SELECT MigrationID FROM dbo.DatabaseSchemaHistory;",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            migrations.Add(reader.GetString(0));
        }

        return migrations;
    }

    private static async Task<int> CountConflictingCollationsAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT COUNT(*)
            FROM sys.columns AS c
            INNER JOIN sys.tables AS t ON t.object_id = c.object_id
            WHERE t.schema_id = SCHEMA_ID(N'dbo')
              AND c.collation_name IS NOT NULL
              AND c.collation_name <> N'Arabic_CI_AS';
            """,
            connection);
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static string ReadEmbeddedSql(string resourceName)
    {
        var assembly = typeof(MigrationRunner).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing migration resource {resourceName}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed record MigrationDefinition(string Id, string Name, string ResourceName);

public sealed class SchemaInspection(
    string? Collation,
    IReadOnlySet<string> Tables,
    IReadOnlySet<string> AppliedMigrationIds,
    int ConflictingCollationCount)
{
    private static readonly string[] RequiredTables =
    [
        "Businesses",
        "Branches",
        "POSStations",
        "Devices",
        "DatabaseSchemaHistory",
        "SystemSettings",
        "Users",
        "Roles",
        "Permissions",
        "UserRoles",
        "RolePermissions",
        "UserBranchAccess",
        "UserLoginAudit",
        "Items",
        "ItemUnits",
        "PriceTypes",
        "ItemPrices",
        "ItemPriceHistory",
        "ItemBranchInventory",
        "BranchStockBalances",
        "StockMovementTypes",
        "StockMovements",
        "DataImportHistory",
        "SyncEndpoints",
        "SyncCheckpoints",
        "SyncInbox",
        "SyncOutbox",
        "CustomerClassifications",
        "Customers",
        "CustomerAdministrationAudit",
        "Sales",
        "SaleItems",
        "POSCashSessions",
        "SalePayments",
    ];

    public bool IsValid { get; } =
        string.Equals(Collation, BranchDatabase.Collation, StringComparison.Ordinal)
        && ConflictingCollationCount == 0
        && AppliedMigrationIds.Contains(MigrationRunner.InitialMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.UsersMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.RolesMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.ItemsMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.CatalogMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.OwnershipMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.SkuQuantityMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.ItemsViewMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.ImportHistoryMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.SyncFoundationMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.ItemEditPermissionMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.DailySalesRateMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.UserAdministrationMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.StockMovementSourceMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.CustomerMasterMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.SalesFoundationMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.PosPaymentsMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.ManualPriceMigrationId)
        && AppliedMigrationIds.Contains(MigrationRunner.ArabicSearchMigrationId)
        && RequiredTables.All(Tables.Contains);

    public string Diagnostic =>
        $"Collation={Collation ?? "(none)"}; ConflictingCollations={ConflictingCollationCount}; Migrations={string.Join(",", AppliedMigrationIds)}; Tables={string.Join(",", Tables)}";
}
