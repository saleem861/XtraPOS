namespace XtraPOS.Api.Setup;

public static class SchemaConvergence
{
    public const string ApprovedVersion = "branch-045+central-031";
    public const string UnifiedSchemaMigrationId = "029_UnifiedSchemaObjects";

    public const string CentralStructureCompatibleSql = """
        SELECT CASE
            WHEN OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
             AND COL_LENGTH(N'dbo.Customers', N'CustomerID') IS NULL THEN 0
            WHEN OBJECT_ID(N'dbo.DatabaseSchemaHistory', N'U') IS NULL THEN 0
            WHEN (
                SELECT COUNT(*)
                FROM dbo.DatabaseSchemaHistory
                WHERE MigrationID IN (N'001_CentralFoundation', N'002_ItemOwnership', N'003_BranchCodeUniqueness')
            ) <> 3 THEN 0
            WHEN EXISTS (
                SELECT 1
                FROM dbo.DatabaseSchemaHistory
                WHERE MigrationID = N'029_UnifiedSchemaObjects'
            ) THEN 1
            WHEN OBJECT_ID(N'dbo.ItemBranchInventory', N'U') IS NOT NULL THEN 0
            WHEN OBJECT_ID(N'dbo.BranchStockBalances', N'U') IS NOT NULL THEN 0
            WHEN OBJECT_ID(N'dbo.StockMovements', N'U') IS NOT NULL THEN 0
            WHEN OBJECT_ID(N'dbo.POSStations', N'U') IS NOT NULL THEN 0
            WHEN OBJECT_ID(N'dbo.Devices', N'U') IS NOT NULL THEN 0
            ELSE 1
        END;
        """;

    public static readonly string[] BlockedSharedTables =
    [
        "Businesses",
        "Branches",
        "Items",
        "ItemUnits",
        "PriceTypes",
        "ItemPrices",
        "Users",
        "Roles",
        "Permissions",
        "RolePermissions",
        "UserRoles",
        "UserBranchAccess",
        "Customers",
        "CustomerClassifications",
        "CustomerAdministrationAudit",
        "CustomerCreditPolicies",
        "CustomerReceivables",
        "CustomerCreditLedger",
        "CreditPaymentAllocations",
        "CustomerItemSpecialPrices",
        "CustomerPhoneVerificationChallenges",
        "UserAdministrationAudit",
        "DataImportHistory",
        "SystemSettings",
        "DatabaseSchemaHistory",
    ];

    public static readonly string[] BranchOnlyTables =
    [
        "POSStations",
        "Devices",
        "Sales",
        "SaleItems",
        "POSCashSessions",
        "SalePayments",
        "ItemPriceHistory",
        "ItemBranchInventory",
        "StockMovementTypes",
        "StockMovements",
        "BranchStockBalances",
        "SyncEndpoints",
        "SyncCheckpoints",
        "SyncInbox",
        "SyncOutbox",
        "Cashboxes",
    ];

    public static readonly string[] CentralOnlyTables =
    [
        "CentralPosRegistry",
        "BranchSqlEndpoints",
        "CreditAuthorizations",
        "CreditAuditLog",
        "CentralFinancialEvents",
        "CustomerMembershipIdentities",
        "SyncEntityOwnership",
        "SyncChangeLog",
    ];

    public static bool IsBlocked(string table) =>
        BlockedSharedTables.Contains(table, StringComparer.OrdinalIgnoreCase);
}
