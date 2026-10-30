using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class CentralRouteTests
{
    [Fact]
    public void RemoteWithoutAServer_IsRejected()
    {
        var error = Assert.Throws<SetupRejectedException>(() => CentralRoute.Bind(
            "Remote", null, null, null, "XtraPOS_Central", "Integrated", null, null, true, true));
        Assert.Contains("المحلية", error.Message);
    }

    [Fact]
    public void RemoteLocalhostWithoutInstanceOrPort_IsRejected()
    {
        Assert.Throws<SetupRejectedException>(() => CentralRoute.Bind(
            "Remote", "localhost", null, null, "XtraPOS_Central", "Integrated", null, null, true, true));
    }

    [Fact]
    public void RemoteServerAndPort_StayOnThatEndpoint()
    {
        var settings = CentralRoute.Bind(
            "Remote", "69.30.235.148,10173", null, null, "XtraPOS_Central", "Sql", "user", "secret", true, true);
        Assert.Equal("69.30.235.148", settings.Server);
        Assert.Equal(10173, settings.Port);
        Assert.Equal("XtraPOS_Central", settings.DatabaseName);
        Assert.Equal(SqlEndpointSettings.Remote, settings.LocationKind);
        Assert.Equal("69.30.235.148,10173/XtraPOS_Central", CentralRoute.Describe(settings));
        Assert.False(CentralRoute.SameInstance("localhost", settings));
    }

    [Fact]
    public void SameDatabaseNameOnAnotherInstance_IsNotTheLocalCentral()
    {
        var settings = CentralRoute.Bind(
            "Remote", "localhost", "BARTENDER", null, "XtraPOS_Central", "Integrated", null, null, true, true);
        Assert.Equal("BARTENDER", settings.InstanceName);
        Assert.Equal("localhost\\BARTENDER/XtraPOS_Central", CentralRoute.Describe(settings));
        Assert.False(CentralRoute.SameInstance("localhost", settings));
    }
}

[Collection("Branch")]
public sealed class CentralSourceBindingTests
{
    private const string BranchName = "XtraPOS_Branch_Test_CentralRoute";
    private const string CatalogName = "XtraPOS_Central_Test_RouteBinding";
    private const string LocalBusinessId = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA";
    private const string RemoteBusiness = "Business B Route";
    private const string RemoteBranchId = "C0C0C0C0-C0C0-C0C0-C0C0-C0C0C0C0C0C0";
    private const string RemoteUserId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public async Task SelectedRemoteCentral_IsNotResolvedByDatabaseName()
    {
        CentralDatabase.Require(CatalogName);
        await DropLocalDecoyAsync();
        await CreateLocalDecoyAsync();
        var localItems = await ScalarAsync(Local(CatalogName), "SELECT COUNT(*) FROM dbo.Items;");
        var localBusiness = await ScalarAsync(Local(CatalogName), "SELECT CONVERT(nvarchar(36), BusinessID) FROM dbo.Businesses;");
        await DropRemoteCentralAsync();
        await DropBranchAsync();
        var factory = new XtraPosWebFactory(BranchName);
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var executor = scope.ServiceProvider.GetRequiredService<CentralSetupExecutor>();
                await executor.ExecuteAsync(
                    new CentralSetupRequest
                    {
                        Intent = "new",
                        LocationKind = "Remote",
                        Server = "localhost",
                        InstanceName = "BARTENDER",
                        DatabaseName = CatalogName,
                        AuthenticationType = "Integrated",
                        Encrypt = true,
                        TrustServerCertificate = true,
                        BusinessName = RemoteBusiness,
                        BusinessDisplayName = "B",
                    },
                    (_, _) => Task.CompletedTask,
                    CancellationToken.None);
            }

            await SeedRemoteCatalogAsync();
            using var client = factory.CreateClient();
            var created = await client.PostAsJsonAsync("/api/setup/create-branch", new
            {
                businessName = RemoteBusiness,
                branchName = "فرع الاختبار",
                branchCode = "02",
                branchType = "Retail",
                stationNumber = 1,
                stationName = "كاونتر 1",
                deviceName = "صندوق 1",
                username = "905017083",
                displayName = "سليم",
                password = "",
                confirmPassword = "",
                centralAdminUserId = RemoteUserId,
                operatingMode = "CentralManagement",
                centralAction = "existing",
                centralLocation = "Remote",
                centralServer = "localhost",
                centralInstance = "BARTENDER",
                centralDatabaseName = CatalogName,
                centralAuthentication = "Integrated",
                linkToCentral = "yes",
                branchRemoteChoice = "no",
            });
            var body = await created.Content.ReadAsStringAsync();
            Assert.True(created.IsSuccessStatusCode, body);
            Assert.Contains("\"type\":\"completed\"", body);
            Assert.DoesNotContain("\"type\":\"failed\"", body);

            Assert.Equal("1", await ScalarAsync(Branch(), "SELECT COUNT(*) FROM dbo.Items;"));
            Assert.Equal("ROUTE-B-ONLY", await ScalarAsync(Branch(), "SELECT ItemNumber FROM dbo.Items;"));
            Assert.Equal("1", await ScalarAsync(Branch(), "SELECT COUNT(*) FROM dbo.ItemPrices;"));
            Assert.Equal(RemoteUserId, (await ScalarAsync(Branch(), "SELECT CONVERT(nvarchar(36), UserID) FROM dbo.Users;")).ToLowerInvariant());
            Assert.Equal(RemoteBranchId, await ScalarAsync(Branch(), "SELECT CONVERT(nvarchar(36), SettingValue) FROM dbo.SystemSettings WHERE SettingKey = N'BranchID';"), StringComparer.OrdinalIgnoreCase);
            var businessId = (await ScalarAsync(Branch(), "SELECT CONVERT(nvarchar(36), SettingValue) FROM dbo.SystemSettings WHERE SettingKey = N'BusinessID';")).ToLowerInvariant();
            Assert.NotEqual(localBusiness.ToLowerInvariant(), businessId);
            Assert.Equal(businessId, (await ScalarAsync(Remote(), "SELECT CONVERT(nvarchar(36), BusinessID) FROM dbo.Businesses;")).ToLowerInvariant());
            Assert.Equal($"localhost\\BARTENDER/{CatalogName}", await ScalarAsync(Branch(), "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'CentralSource';"));
            Assert.Equal("BARTENDER", await ScalarAsync(Branch(), "SELECT InstanceName FROM dbo.SyncEndpoints WHERE DatabaseRole = N'Central';"));
            Assert.Equal("localhost", await ScalarAsync(Branch(), "SELECT ServerName FROM dbo.SyncEndpoints WHERE DatabaseRole = N'Central';"));
            Assert.Equal(CatalogName, await ScalarAsync(Branch(), "SELECT DatabaseName FROM dbo.SyncEndpoints WHERE DatabaseRole = N'Central';"));
            Assert.Equal(localItems, await ScalarAsync(Local(CatalogName), "SELECT COUNT(*) FROM dbo.Items;"));
            Assert.Equal(localBusiness, await ScalarAsync(Local(CatalogName), "SELECT CONVERT(nvarchar(36), BusinessID) FROM dbo.Businesses;"));
            Assert.Equal("LOCAL-DECOY-ITEM", await ScalarAsync(Local(CatalogName), "SELECT ItemNumber FROM dbo.Items;"));
        }
        finally
        {
            await factory.DisposeAsync();
            SqlConnection.ClearAllPools();
            await DropBranchAsync();
            await DropRemoteCentralAsync();
            await DropLocalDecoyAsync();
        }
    }

    private static async Task SeedRemoteCatalogAsync()
    {
        await using var connection = new SqlConnection(Remote());
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            IF NOT EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleCode = N'Admin')
                INSERT INTO dbo.Roles (RoleID, RoleCode, RoleName, IsSystemRole, IsActive, CreatedAt)
                VALUES ('22222222-2222-2222-2222-222222222222', N'Admin', N'مدير النظام', 1, 1, SYSUTCDATETIME());

            IF NOT EXISTS (SELECT 1 FROM dbo.PriceTypes WHERE PriceCode = N'Price1')
                INSERT INTO dbo.PriceTypes (PriceTypeID, PriceCode, PriceName, SortOrder, IsActive, CreatedAt)
                VALUES ('33333333-3333-3333-3333-333333333333', N'Price1', N'Price1', 60, 1, SYSUTCDATETIME());

            INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
            SELECT @branchId, BusinessID, N'02', N'فرع الاختبار', N'Retail', 1, SYSUTCDATETIME()
            FROM dbo.Businesses;

            INSERT INTO dbo.Users
                (UserID, Username, NormalizedUsername, DisplayName, FullName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, CreatedAt)
            VALUES
                (@userId, N'905017083', N'905017083', N'سليم', N'سليم', N'905017083', N'HASH-ROUTE-B', 1, 0, SYSUTCDATETIME());

            INSERT INTO dbo.UserRoles (UserID, RoleID, AssignedAt)
            SELECT @userId, RoleID, SYSUTCDATETIME() FROM dbo.Roles WHERE RoleCode = N'Admin';

            INSERT INTO dbo.Items
                (ItemID, ItemNumber, ItemName, IsProhibited, IsPublishedOnline, IsActive, CreatedAt, UpdatedAt)
            VALUES
                ('D0D0D0D0-D0D0-D0D0-D0D0-D0D0D0D0D0D0', N'ROUTE-B-ONLY', N'صنف بعيد', 0, 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());

            INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, Price, IsActive, CreatedAt, UpdatedAt)
            SELECT NEWID(), 'D0D0D0D0-D0D0-D0D0-D0D0-D0D0D0D0D0D0', PriceTypeID, 9.5, 1, SYSUTCDATETIME(), SYSUTCDATETIME()
            FROM dbo.PriceTypes
            WHERE PriceCode = N'Price1';
            """,
            connection);
        command.Parameters.AddWithValue("@branchId", Guid.Parse(RemoteBranchId));
        command.Parameters.AddWithValue("@userId", Guid.Parse(RemoteUserId));
        await command.ExecuteNonQueryAsync();
    }

    private static string Local(string database) =>
        new SqlConnectionStringBuilder
        {
            DataSource = "localhost",
            InitialCatalog = database,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
        }.ConnectionString;

    private static string Remote() =>
        new SqlConnectionStringBuilder
        {
            DataSource = @"localhost\BARTENDER",
            InitialCatalog = CatalogName,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
        }.ConnectionString;

    private static string Branch() => Local(BranchName);

    private static async Task<string> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }

    private static async Task CreateLocalDecoyAsync()
    {
        CentralDatabase.Require(CatalogName);
        await using var master = new SqlConnection(Local("master"));
        await master.OpenAsync();
        await using var create = new SqlCommand(
            $"""
            CREATE DATABASE [{CatalogName}] COLLATE Arabic_CI_AS;
            """,
            master);
        await create.ExecuteNonQueryAsync();
        await using var connection = new SqlConnection(Local(CatalogName));
        await connection.OpenAsync();
        await using var seed = new SqlCommand(
            """
            CREATE TABLE dbo.Businesses (
                BusinessID uniqueidentifier NOT NULL PRIMARY KEY,
                BusinessName nvarchar(200) NOT NULL);
            CREATE TABLE dbo.Items (
                ItemID uniqueidentifier NOT NULL PRIMARY KEY,
                ItemNumber nvarchar(50) NOT NULL);
            INSERT INTO dbo.Businesses (BusinessID, BusinessName)
            VALUES (@businessId, N'LOCAL-DECOY');
            INSERT INTO dbo.Items (ItemID, ItemNumber)
            VALUES ('BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB', N'LOCAL-DECOY-ITEM');
            """,
            connection);
        seed.Parameters.AddWithValue("@businessId", Guid.Parse(LocalBusinessId));
        await seed.ExecuteNonQueryAsync();
    }

    private static async Task DropLocalDecoyAsync()
    {
        CentralDatabase.Require(CatalogName);
        await using var master = new SqlConnection(Local("master"));
        await master.OpenAsync();
        await using var command = new SqlCommand(
            $"""
            IF DB_ID(N'{CatalogName}') IS NOT NULL
            BEGIN
                DECLARE @safe bit = 0;
                IF NOT EXISTS (SELECT 1 FROM [{CatalogName}].sys.tables WHERE name = N'Businesses')
                    SET @safe = 1;
                ELSE IF NOT EXISTS (
                    SELECT 1 FROM [{CatalogName}].dbo.Businesses
                    WHERE BusinessName <> N'LOCAL-DECOY')
                    SET @safe = 1;
                IF @safe = 1
                BEGIN
                    ALTER DATABASE [{CatalogName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{CatalogName}];
                END
            END
            """,
            master);
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropBranchAsync()
    {
        await DropAsync(Local("master"), TestDatabase.Require(BranchName));
    }

    private static async Task DropRemoteCentralAsync()
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder
        {
            DataSource = @"localhost\BARTENDER",
            InitialCatalog = "master",
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
        }.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            IF DB_ID(N'XtraPOS_Central_Test_RouteBinding') IS NOT NULL
            BEGIN
                DECLARE @safe bit = 0;
                IF NOT EXISTS (SELECT 1 FROM [XtraPOS_Central_Test_RouteBinding].sys.tables WHERE name = N'Businesses')
                    SET @safe = 1;
                ELSE IF NOT EXISTS (SELECT 1 FROM [XtraPOS_Central_Test_RouteBinding].dbo.Businesses)
                    SET @safe = 1;
                ELSE IF NOT EXISTS (
                    SELECT 1 FROM [XtraPOS_Central_Test_RouteBinding].dbo.Businesses
                    WHERE BusinessName <> N'Business B Route')
                    SET @safe = 1;

                IF @safe = 1
                BEGIN
                    ALTER DATABASE [XtraPOS_Central_Test_RouteBinding] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [XtraPOS_Central_Test_RouteBinding];
                END
            END
            """,
            connection);
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropAsync(string masterConnection, string databaseName)
    {
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(masterConnection);
        await master.OpenAsync();
        await using var drop = new SqlCommand(
            $"""
            IF DB_ID(N'{databaseName}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{databaseName}];
            END
            """,
            master);
        drop.CommandTimeout = 60;
        await drop.ExecuteNonQueryAsync();
    }
}
