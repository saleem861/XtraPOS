using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Security;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class CreditBrowserEnvironment
{
    public const string CentralName = "XtraPOS_Central_Test_BrowserE2E";
    public const string OriginName = "XtraPOS_Branch_Test_BrowserA";
    public const string OtherName = "XtraPOS_Branch_Test_BrowserB";

    [Fact]
    public async Task Prepare_isolated_browser_databases()
    {
        CentralDatabase.Require(CentralName);
        TestDatabase.Require(OriginName);
        TestDatabase.Require(OtherName);
        await DropAsync(CentralName);
        await DropAsync(OriginName);
        await DropAsync(OtherName);
        await CreateAsync(CentralName);
        await CreateAsync(OriginName);
        await CreateAsync(OtherName);
        await new CentralMigrationRunner().ApplyAsync(Connection(CentralName), CancellationToken.None);
        await MigrateAsync(OriginName);
        await MigrateAsync(OtherName);

        var business = Guid.NewGuid();
        var originBranch = Guid.NewGuid();
        var otherBranch = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var item = Guid.NewGuid();
        var origin = await SeedBranchAsync(OriginName, business, originBranch, "01", "فرع 01", item, customer);
        var other = await SeedBranchAsync(OtherName, business, otherBranch, "02", "فرع 02", item, customer);
        await ExecuteAsync(Connection(CentralName), """
            INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Browser E2E', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt) VALUES
                (@origin, @business, N'01', N'فرع 01', N'Retail', 1, SYSUTCDATETIME()),
                (@other, @business, N'02', N'فرع 02', N'Retail', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES (@customer, @business, N'زبون الدين', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.CustomerCreditPolicies (CustomerID, CreditEnabled, CreditLimit, PaymentTermDays, CreditStatus) VALUES (@customer, 1, 10000, 30, N'Active');
            """,
            ("@business", business), ("@origin", originBranch), ("@other", otherBranch), ("@customer", customer));
        var payload = new
        {
            central = CentralName,
            originDatabase = OriginName,
            otherDatabase = OtherName,
            businessId = business,
            customerId = customer,
            customerNumber = "800100",
            itemNumber = "E2E800",
            password = "Collect1234",
            username = "cashier",
            deviceToken = "browser-token",
            origin,
            other,
        };
        await File.WriteAllTextAsync(@"C:\XtraPOS-phase52-snapshot\e2e-ids.json", JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<BranchSeed> SeedBranchAsync(string database, Guid businessId, Guid branchId, string code, string name, Guid itemId, Guid customerId)
    {
        var station = Guid.NewGuid();
        var device = Guid.NewGuid();
        var user = Guid.NewGuid();
        var session = Guid.NewGuid();
        var hash = new PasswordService().Hash(new AppUser { UserId = user, Username = "cashier" }, "Collect1234");
        await ExecuteAsync(Connection(database), """
            INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Browser E2E', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt) VALUES (@branch, @business, N'00', N'Branch', N'Retail', 1, SYSUTCDATETIME());
            INSERT INTO dbo.POSStations (StationID, BranchID, StationNumber, StationName, IsActive, CreatedAt) VALUES (@station, @branch, 1, N'Till', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Devices (DeviceID, BranchID, StationID, DeviceName, DeviceType, IsActive, RegisteredAt, CreatedAt) VALUES (@device, @branch, @station, N'Till', N'POS', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive) VALUES (@user, N'cashier', N'CASHIER', N'Cashier', N'pending', 1);
            INSERT INTO dbo.UserRoles (UserID, RoleID) SELECT @user, RoleID FROM dbo.Roles WHERE RoleCode = N'Admin';
            INSERT INTO dbo.UserBranchAccess (UserID, BranchID) VALUES (@user, @branch);
            INSERT INTO dbo.Customers (CustomerID, BusinessID, CustomerNumber, Name, IsActive, CreatedAt, UpdatedAt) VALUES (@customer, @business, N'800100', N'زبون الدين', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName, IsActive, IsProhibited) VALUES (@item, N'E2E800', N'صنف الاختبار', 1, 0);
            INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive)
            SELECT NEWID(), @item, PriceTypeID, @branch, 800, 1 FROM dbo.PriceTypes;
            INSERT INTO dbo.POSCashSessions (CashSessionID, BusinessID, BranchID, POSStationID, DeviceID, OpenedByUserID, OpenedAt, OpeningCashAmount, Status)
            VALUES (@session, @business, @branch, @station, @device, @user, SYSUTCDATETIME(), 0, N'Open');
            INSERT INTO dbo.SyncEndpoints (EndpointID, DatabaseRole, LocationKind, ServerName, DatabaseName, AuthenticationType, EncryptConnection, TrustServerCertificate, Status)
            VALUES (NEWID(), N'Central', N'Local', N'localhost', N'pending', N'Integrated', 1, 1, N'Ready');
            INSERT INTO dbo.SystemSettings (SettingKey, SettingValue)
            SELECT keyName, keyValue FROM (VALUES
                (N'BusinessID', CONVERT(nvarchar(36), @business)),
                (N'BranchID', CONVERT(nvarchar(36), @branch)),
                (N'SetupCompletedAt', CONVERT(nvarchar(30), SYSUTCDATETIME(), 126))
            ) AS settings(keyName, keyValue)
            WHERE NOT EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE SettingKey = settings.keyName);
            """,
            ("@business", businessId), ("@branch", branchId), ("@station", station), ("@device", device), ("@user", user), ("@customer", customerId), ("@item", itemId), ("@session", session));
        await using var fix = new SqlConnection(Connection(database));
        await fix.OpenAsync();
        await using var command = new SqlCommand("""
            UPDATE dbo.Branches SET BranchCode = @code, BranchName = @name WHERE BranchID = @branch;
            UPDATE dbo.Users SET PasswordHash = @hash WHERE UserID = @user;
            UPDATE dbo.Devices SET DeviceTokenHash = @token WHERE DeviceID = @device;
            UPDATE dbo.SyncEndpoints SET DatabaseName = @central WHERE DatabaseRole = N'Central';
            """, fix);
        command.Parameters.AddWithValue("@code", code);
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@branch", branchId);
        command.Parameters.AddWithValue("@hash", hash);
        command.Parameters.AddWithValue("@user", user);
        command.Parameters.Add("@token", System.Data.SqlDbType.VarBinary, 64).Value = DeviceTokenService.Hash("browser-token");
        command.Parameters.AddWithValue("@device", device);
        command.Parameters.AddWithValue("@central", CentralName);
        await command.ExecuteNonQueryAsync();
        return new BranchSeed(branchId, station, device, user, session);
    }

    private sealed record BranchSeed(Guid BranchId, Guid StationId, Guid DeviceId, Guid UserId, Guid SessionId);

    private static async Task MigrateAsync(string database)
    {
        var connections = new SqlConnectionFactory(Options.Create(new SqlServerOptions { Server = "localhost", Database = database, ConnectTimeoutSeconds = 30 }));
        await new MigrationRunner(connections, NullLogger<MigrationRunner>.Instance).ApplyPendingAsync(CancellationToken.None);
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, Guid Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.Add(parameter.Name, System.Data.SqlDbType.UniqueIdentifier).Value = parameter.Value;
        await command.ExecuteNonQueryAsync();
    }

    private static string Connection(string name) => new SqlConnectionStringBuilder { DataSource = "localhost", InitialCatalog = name, IntegratedSecurity = true, Encrypt = true, TrustServerCertificate = true, ApplicationName = "XtraPOS.Tests" }.ConnectionString;

    private static async Task CreateAsync(string name)
    {
        await MasterAsync($"IF DB_ID(N'{name}') IS NULL CREATE DATABASE [{name}] COLLATE Arabic_CI_AS;");
    }

    private static async Task DropAsync(string name)
    {
        if (!name.Contains("_Test_Browser", StringComparison.Ordinal)) throw new InvalidOperationException(name);
        SqlConnection.ClearAllPools();
        await MasterAsync($"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END");
    }

    private static async Task MasterAsync(string sql)
    {
        var master = new SqlConnectionStringBuilder { DataSource = "localhost", InitialCatalog = "master", IntegratedSecurity = true, Encrypt = true, TrustServerCertificate = true }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await new SqlCommand(sql, connection) { CommandTimeout = 60 }.ExecuteNonQueryAsync();
    }
}
