using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public static class LinkedBranchSetupCommand
{
    public const string CommandName = "--setup-linked-main-branch";
    public const string DatabaseName = "XtraPOS_Branch_01";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(arg => string.Equals(arg, CommandName, StringComparison.Ordinal));

    public static async Task<int> ExecuteAsync(
        IReadOnlyList<string> args,
        IServiceProvider services,
        SqlEndpointSettings central,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var confirmed = DevelopmentDatabaseReset.ReadConfirmedDatabase(args);
        var connections = services.GetRequiredService<SqlConnectionFactory>();
        if (!string.Equals(confirmed, DatabaseName, StringComparison.Ordinal)
            || !BranchDatabase.IsOperationalName(confirmed)
            || !string.Equals(connections.ConfiguredDatabase, confirmed, StringComparison.Ordinal))
        {
            output.WriteLine("Refusing a branch setup that is not the configured database XtraPOS_Branch_01.");
            return 1;
        }

        if (!string.Equals(central.DatabaseName, CentralDatabase.Name, StringComparison.Ordinal))
        {
            output.WriteLine("Linked branch setup requires the saved Central database XtraPOS_Central.");
            return 1;
        }

        output.WriteLine("== OLD BRANCH BEFORE");
        await PrintOldBranchAsync(output, cancellationToken);
        output.WriteLine("== CENTRAL BEFORE");
        if (!await PrintCentralAsync(central, output, cancellationToken))
        {
            return 2;
        }

        await using var scope = services.CreateAsyncScope();
        var setup = scope.ServiceProvider.GetRequiredService<SetupService>();
        var failed = false;
        var alreadyConfigured = false;
        var topology = new TopologyPlan("CentralManagement", false, true, central, null);
        var input = new BranchSetup(
            "Smoker World",
            "SW",
            null,
            null,
            "فرع نابلس",
            "01",
            "Retail",
            1,
            "كاونتر 1",
            "صندوق 1",
            new AdminAccount("branch01", "إعداد الفرع", null, null, "unused-setup-secret"));
        await setup.RunAsync(input, (setupEvent, _) =>
        {
            if (string.Equals(setupEvent.Type, "failed", StringComparison.Ordinal))
            {
                failed = true;
                output.WriteLine("SETUP_FAILED " + setupEvent.Message);
            }
            else if (string.Equals(setupEvent.Type, "completed", StringComparison.Ordinal))
            {
                alreadyConfigured = setupEvent.AlreadyConfigured;
                output.WriteLine("alreadyConfigured=" + setupEvent.AlreadyConfigured);
                output.WriteLine("BranchName=" + setupEvent.Device?.BranchName);
                output.WriteLine("StationName=" + setupEvent.Device?.StationName);
                output.WriteLine("DeviceName=" + setupEvent.Device?.DeviceName);
            }
            else
            {
                output.WriteLine(setupEvent.Step + " " + setupEvent.State);
            }

            return Task.CompletedTask;
        }, cancellationToken, topology);

        if (failed)
        {
            return 1;
        }

        output.WriteLine("== BRANCH AFTER");
        await PrintBranchAsync(connections, output, cancellationToken);
        output.WriteLine("== CENTRAL AFTER");
        await PrintCentralAsync(central, output, cancellationToken);
        output.WriteLine("== OLD BRANCH AFTER");
        await PrintOldBranchAsync(output, cancellationToken);
        output.WriteLine(alreadyConfigured ? "SETUP_ALREADY_CONFIGURED" : "SETUP_COMPLETE");
        output.WriteLine("ItemBranchInventory policy: no rows are created. Branch item state stays uninitialized, with no quantity, cost, or sale dates.");
        return 0;
    }

    private static async Task PrintOldBranchAsync(TextWriter output, CancellationToken cancellationToken)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = "localhost",
            InitialCatalog = BranchDatabase.Name,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
        };
        await using var connection = new SqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await PrintAsync(connection, output, """
            SELECT MigrationID FROM dbo.DatabaseSchemaHistory ORDER BY AppliedAt, MigrationID;
            SELECT 'Businesses', COUNT(*) FROM dbo.Businesses
            UNION ALL SELECT 'Branches', COUNT(*) FROM dbo.Branches
            UNION ALL SELECT 'Users', COUNT(*) FROM dbo.Users
            UNION ALL SELECT 'ItemsTable', CASE WHEN OBJECT_ID(N'dbo.Items', N'U') IS NULL THEN 0 ELSE 1 END;
            """, cancellationToken);
    }

    private static async Task<bool> PrintCentralAsync(
        SqlEndpointSettings central,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(central.BuildConnectionString(CentralDatabase.Name));
        await connection.OpenAsync(cancellationToken);
        await using var role = new SqlCommand("SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';", connection);
        var value = await role.ExecuteScalarAsync(cancellationToken) as string;
        output.WriteLine("DatabaseRole=" + value);
        if (!string.Equals(value, DatabaseRole.Central, StringComparison.Ordinal))
        {
            output.WriteLine("CENTRAL_ROLE_MISMATCH");
            return false;
        }

        await PrintAsync(connection, output, """
            SELECT 'Businesses', COUNT(*) FROM dbo.Businesses
            UNION ALL SELECT 'Branches', COUNT(*) FROM dbo.Branches
            UNION ALL SELECT 'Items', COUNT(*) FROM dbo.Items
            UNION ALL SELECT 'ItemUnits', COUNT(*) FROM dbo.ItemUnits
            UNION ALL SELECT 'PriceTypes', COUNT(*) FROM dbo.PriceTypes
            UNION ALL SELECT 'ItemPrices', COUNT(*) FROM dbo.ItemPrices
            UNION ALL SELECT 'Users', COUNT(*) FROM dbo.Users;
            SELECT BranchID, BusinessID, BranchCode, BranchName, BranchType FROM dbo.Branches;
            """, cancellationToken);
        return true;
    }

    private static async Task PrintBranchAsync(
        SqlConnectionFactory connections,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await PrintAsync(connection, output, """
            SELECT MigrationID FROM dbo.DatabaseSchemaHistory ORDER BY AppliedAt, MigrationID;
            SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';
            SELECT 'Businesses', COUNT(*) FROM dbo.Businesses
            UNION ALL SELECT 'Branches', COUNT(*) FROM dbo.Branches
            UNION ALL SELECT 'Items', COUNT(*) FROM dbo.Items
            UNION ALL SELECT 'ItemUnits', COUNT(*) FROM dbo.ItemUnits
            UNION ALL SELECT 'PriceTypes', COUNT(*) FROM dbo.PriceTypes
            UNION ALL SELECT 'ItemPrices', COUNT(*) FROM dbo.ItemPrices
            UNION ALL SELECT 'BranchPriceOverrides', COUNT(*) FROM dbo.ItemPrices WHERE BranchID IS NOT NULL
            UNION ALL SELECT 'Users', COUNT(*) FROM dbo.Users
            UNION ALL SELECT 'Roles', COUNT(*) FROM dbo.Roles
            UNION ALL SELECT 'Permissions', COUNT(*) FROM dbo.Permissions
            UNION ALL SELECT 'UserRoles', COUNT(*) FROM dbo.UserRoles
            UNION ALL SELECT 'UserBranchAccess', COUNT(*) FROM dbo.UserBranchAccess
            UNION ALL SELECT 'POSStations', COUNT(*) FROM dbo.POSStations
            UNION ALL SELECT 'Devices', COUNT(*) FROM dbo.Devices
            UNION ALL SELECT 'ItemBranchInventory', COUNT(*) FROM dbo.ItemBranchInventory
            UNION ALL SELECT 'BranchStockBalances', COUNT(*) FROM dbo.BranchStockBalances
            UNION ALL SELECT 'StockMovements', COUNT(*) FROM dbo.StockMovements
            UNION ALL SELECT 'SalesTable', CASE WHEN OBJECT_ID(N'dbo.Sales', N'U') IS NULL THEN 0 ELSE 1 END
            UNION ALL SELECT 'NegativePrices', COUNT(*) FROM dbo.ItemPrices WHERE Price < 0
            UNION ALL SELECT 'TechnicalItem', COUNT(*) FROM dbo.Items WHERE ItemNumber = N'XTRAPOS_DELETE_TEST_20260917115506'
            UNION ALL SELECT 'Item50', COUNT(*) FROM dbo.Items WHERE ItemNumber = N'50'
            UNION ALL SELECT 'MustChangePassword', COUNT(*) FROM dbo.Users WHERE MustChangePassword = 1;
            SELECT b.BranchID, b.BusinessID, b.BranchCode, b.BranchName, b.BranchType, s.StationName, d.DeviceName
            FROM dbo.Branches AS b
            INNER JOIN dbo.POSStations AS s ON s.BranchID = b.BranchID
            INNER JOIN dbo.Devices AS d ON d.BranchID = b.BranchID;
            SELECT i.ItemNumber, i.ItemName, u.UnitsPerPackage, u.InternalUnitItemNumber
            FROM dbo.Items AS i
            LEFT JOIN dbo.ItemUnits AS u ON u.ItemID = i.ItemID
            WHERE i.ItemNumber IN (N'1087', N'1300', N'1350', N'1360');
            SELECT u.EmployeeNumber, u.IsActive, u.MustChangePassword, r.RoleCode
            FROM dbo.Users AS u
            INNER JOIN dbo.UserRoles AS ur ON ur.UserID = u.UserID
            INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID
            WHERE u.EmployeeNumber IN (N'905017083', N'402120067', N'421600008');
            SELECT DatabaseRole, ServerName, DatabaseName, Status FROM dbo.SyncEndpoints;
            SELECT StreamName, LastAppliedVersion FROM dbo.SyncCheckpoints;
            """, cancellationToken);
    }

    private static async Task PrintAsync(
        SqlConnection connection,
        TextWriter output,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        do
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var fields = new string[reader.FieldCount];
                for (var index = 0; index < reader.FieldCount; index++)
                {
                    fields[index] = reader.IsDBNull(index) ? "" : reader.GetValue(index).ToString() ?? "";
                }

                output.WriteLine(string.Join(" | ", fields));
            }
        }
        while (await reader.NextResultAsync(cancellationToken));
    }
}
