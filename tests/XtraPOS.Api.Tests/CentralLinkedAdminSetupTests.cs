using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class CentralLinkedAdminSetupTests(BranchApiFixture fixture)
{
    private const string CentralName = "XtraPOS_Central_Test_AdminReuse";
    private const string BranchName = "XtraPOS_Branch_Test_AdminReuse";
    private const string StandaloneName = "XtraPOS_Branch_Test_StandaloneAdmin";
    private const string UserId = "11111111-1111-1111-1111-111111111111";
    private const string RoleId = "22222222-2222-2222-2222-222222222222";
    private const string PasswordHash = "HASH-905017083-KEEP";
    private const string StandalonePassword = "Admin1234";

    [Fact]
    public async Task LinkedNewBranch_ReusesTheCentralAdminWithoutADuplicateOrNewPassword()
    {
        await DropCentralAsync(CentralName);
        await DropBranchAsync(BranchName);
        Assert.NotNull(fixture);
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
                        LocationKind = "Local",
                        Server = "localhost",
                        DatabaseName = CentralName,
                        AuthenticationType = "Integrated",
                        Encrypt = true,
                        TrustServerCertificate = true,
                        BusinessName = "Admin Reuse Business",
                        BusinessDisplayName = "Reuse",
                        Description = "Existing central admin",
                    },
                    (_, _) => Task.CompletedTask,
                    CancellationToken.None);
            }

            await SeedCentralAdminAsync();
            await SeedCentralPriceCollisionAsync();
            await ExecuteAsync(CentralName, "IF COL_LENGTH(N'dbo.Businesses', N'Description') IS NOT NULL ALTER TABLE dbo.Businesses DROP COLUMN Description;");
            await ExecuteAsync(CentralName, """
                INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
                SELECT '55555555-5555-5555-5555-555555555555', BusinessID, N'99', N'فرع آخر', N'Retail', 1, SYSUTCDATETIME()
                FROM dbo.Businesses;
                """);
            using var client = factory.CreateClient();
            var created = await client.PostAsJsonAsync("/api/setup/create-branch", new
            {
                businessName = "Admin Reuse Business",
                branchName = "فرع إعادة الاستخدام",
                branchCode = "08",
                branchType = "Retail",
                stationNumber = 1,
                stationName = "كاونتر 1",
                deviceName = "QA-ADMIN-REUSE",
                username = "905017083",
                displayName = "سليم جيوسي",
                fullName = "سليم جيوسي",
                employeeNumber = "905017083",
                password = "",
                confirmPassword = "",
                centralAdminUserId = UserId,
                operatingMode = "CentralManagement",
                centralAction = "existing",
                centralLocation = "Local",
                centralServer = "localhost",
                centralDatabaseName = CentralName,
                linkToCentral = "yes",
                branchRemoteChoice = "no",
            });
            var body = await created.Content.ReadAsStringAsync();
            Assert.True(created.IsSuccessStatusCode, body);
            Assert.Contains("\"type\":\"completed\"", body);
            Assert.DoesNotContain("\"type\":\"failed\"", body);

            Assert.Equal(1, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.Users;"));
            Assert.Equal(1, await IntAsync(CentralName, "SELECT COUNT(*) FROM dbo.Users;"));
            Assert.Equal(UserId, (await TextAsync(BranchName, "SELECT CONVERT(nvarchar(36), UserID) FROM dbo.Users;")).ToLowerInvariant());
            Assert.Equal(UserId, (await TextAsync(CentralName, "SELECT CONVERT(nvarchar(36), UserID) FROM dbo.Users;")).ToLowerInvariant());
            Assert.Equal("905017083", await TextAsync(BranchName, "SELECT Username FROM dbo.Users;"));
            Assert.Equal("سليم جيوسي", await TextAsync(BranchName, "SELECT DisplayName FROM dbo.Users;"));
            Assert.Equal(PasswordHash, await TextAsync(BranchName, "SELECT PasswordHash FROM dbo.Users;"));
            Assert.Equal(PasswordHash, await TextAsync(CentralName, "SELECT PasswordHash FROM dbo.Users;"));
            Assert.Equal(1, await IntAsync(BranchName, "SELECT CAST(MustChangePassword AS int) FROM dbo.Users;"));
            Assert.Equal(1, await IntAsync(CentralName, "SELECT CAST(MustChangePassword AS int) FROM dbo.Users;"));
            Assert.Equal(1, await IntAsync(CentralName, "SELECT CAST(a.IsActive AS int) FROM dbo.UserBranchAccess a INNER JOIN dbo.Branches b ON b.BranchID = a.BranchID WHERE a.UserID = '" + UserId + "' AND b.BranchCode = N'08';"));
            Assert.Equal(1, await IntAsync(BranchName, "SELECT CAST(a.IsActive AS int) FROM dbo.UserBranchAccess a INNER JOIN dbo.Branches b ON b.BranchID = a.BranchID WHERE a.UserID = '" + UserId + "' AND b.BranchCode = N'08';"));
            Assert.Equal(1, await IntAsync(CentralName, "SELECT COUNT(*) FROM dbo.UserBranchAccess WHERE UserID = '" + UserId + "';"));
            Assert.Equal(1, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.PriceTypes WHERE PriceCode = N'Price3';"));
            Assert.Equal(0, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.PriceTypes WHERE PriceTypeID = '33333333-3333-3333-3333-333333333333';"));
            Assert.Equal(1, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.Items WHERE ItemNumber = N'PRICE-COLLISION';"));
            Assert.Equal(12.5m, await DecimalAsync(BranchName, """
                SELECT p.Price
                FROM dbo.ItemPrices AS p
                INNER JOIN dbo.Items AS i ON i.ItemID = p.ItemID
                INNER JOIN dbo.PriceTypes AS t ON t.PriceTypeID = p.PriceTypeID
                WHERE i.ItemNumber = N'PRICE-COLLISION' AND t.PriceCode = N'Price3';
                """));
            var status = await client.GetFromJsonAsync<SetupStatusResponse>("/api/setup/status");
            Assert.Equal(SetupStatuses.Configured, status?.Status);
            Assert.Equal("08", status?.BranchCode);
            Assert.Equal("فرع إعادة الاستخدام", status?.BranchName);
            Assert.NotNull(status?.Stations);
            Assert.Contains(status!.Stations!, station => station.StationName == "كاونتر 1");
            Assert.DoesNotContain("تعذر تحديد الفرع الحالي", status.Message ?? "");
            var recovery = await client.GetAsync("/api/setup/local-device-recovery");
            var recoveryBody = await recovery.Content.ReadAsStringAsync();
            Assert.True(recovery.IsSuccessStatusCode, recoveryBody);
            Assert.DoesNotContain("تعذر تحديد الفرع الحالي", recoveryBody);
            Assert.Equal(2, await IntAsync(BranchName, "SELECT COUNT(*) FROM dbo.Branches WHERE IsActive = 1;"));
            Assert.Equal(status.BranchId!.Value.ToString(), await TextAsync(BranchName, "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'BranchID';"));
            Assert.Equal(status.BusinessId!.Value.ToString(), await TextAsync(BranchName, "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'BusinessID';"));
        }
        finally
        {
            await factory.DisposeAsync();
            SqlConnection.ClearAllPools();
            await DropCentralAsync(CentralName);
            await DropBranchAsync(BranchName);
        }
    }

    [Fact]
    public async Task StandaloneNewBranch_CreatesTheFirstAdminWithTheEnteredNames()
    {
        await DropBranchAsync(StandaloneName);
        var factory = new XtraPosWebFactory(StandaloneName);
        try
        {
            using var client = factory.CreateClient();
            var created = await client.PostAsJsonAsync("/api/setup/create-branch", new
            {
                businessName = "منشأة مستقلة",
                branchName = "فرع مستقل",
                branchCode = "03",
                branchType = "Retail",
                stationNumber = 1,
                stationName = "كاونتر 1",
                deviceName = "QA-STANDALONE-ADMIN",
                username = "905017083",
                displayName = "سليم جيوسي",
                password = StandalonePassword,
                confirmPassword = StandalonePassword,
                operatingMode = "Standalone",
                linkToCentral = "no",
                branchRemoteChoice = "no",
            });
            var standaloneBody = await created.Content.ReadAsStringAsync();
            Assert.True(created.IsSuccessStatusCode, standaloneBody);
            Assert.Contains("\"type\":\"completed\"", standaloneBody);
            Assert.DoesNotContain("\"type\":\"failed\"", standaloneBody);
            Assert.Equal(1, await IntAsync(StandaloneName, "SELECT COUNT(*) FROM dbo.Users;"));
            Assert.Equal("905017083", await TextAsync(StandaloneName, "SELECT Username FROM dbo.Users;"));
            Assert.Equal("سليم جيوسي", await TextAsync(StandaloneName, "SELECT DisplayName FROM dbo.Users;"));
            Assert.Equal("Admin", await TextAsync(StandaloneName, "SELECT r.RoleCode FROM dbo.UserRoles ur INNER JOIN dbo.Roles r ON r.RoleID = ur.RoleID INNER JOIN dbo.Users u ON u.UserID = ur.UserID WHERE u.Username = N'905017083';"));
        }
        finally
        {
            await factory.DisposeAsync();
            SqlConnection.ClearAllPools();
            await DropBranchAsync(StandaloneName);
        }
    }

    [Fact]
    public void Validator_KeepsStandaloneNamesAndSkipsPasswordForACentralAdmin()
    {
        var standalone = Request("905017083", "سليم جيوسي", StandalonePassword, StandalonePassword, "no", null);
        Assert.True(BranchSetupValidator.TryValidate(standalone, out var created, out var message), message);
        Assert.Equal("905017083", created.Admin.Username);
        Assert.Equal("سليم جيوسي", created.Admin.DisplayName);

        var missingPassword = Request("905017083", "سليم جيوسي", "", "", "no", null);
        Assert.False(BranchSetupValidator.TryValidate(missingPassword, out _, out _));

        var reused = Request("", "", "", "", "yes", UserId);
        Assert.True(BranchSetupValidator.TryValidate(reused, out _, out var reusedMessage), reusedMessage);
    }

    private static CreateBranchRequest Request(
        string username,
        string displayName,
        string password,
        string confirmPassword,
        string linkToCentral,
        string? centralAdminUserId) => new()
    {
        BusinessName = "منشأة",
        BranchName = "فرع",
        BranchType = "Retail",
        StationNumber = 1,
        StationName = "كاونتر 1",
        DeviceName = "صندوق 1",
        Username = username,
        DisplayName = displayName,
        Password = password,
        ConfirmPassword = confirmPassword,
        LinkToCentral = linkToCentral,
        CentralAdminUserId = centralAdminUserId,
    };

    private static async Task SeedCentralAdminAsync()
    {
        await using var connection = new SqlConnection(Connection(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.Roles (RoleID, RoleCode, RoleName, IsSystemRole, IsActive, CreatedAt)
            VALUES (@roleId, N'Admin', N'مدير النظام', 1, 1, SYSUTCDATETIME());
            INSERT INTO dbo.Users
                (UserID, Username, NormalizedUsername, DisplayName, FullName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, CreatedAt)
            VALUES
                (@userId, N'905017083', N'905017083', N'سليم جيوسي', N'سليم جيوسي', N'905017083', @hash, 1, 1, SYSUTCDATETIME());
            INSERT INTO dbo.UserRoles (UserID, RoleID, AssignedAt)
            VALUES (@userId, @roleId, SYSUTCDATETIME());
            """,
            connection);
        command.Parameters.AddWithValue("@roleId", Guid.Parse(RoleId));
        command.Parameters.AddWithValue("@userId", Guid.Parse(UserId));
        command.Parameters.AddWithValue("@hash", PasswordHash);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SeedCentralPriceCollisionAsync()
    {
        await using var connection = new SqlConnection(Connection(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.PriceTypes (PriceTypeID, PriceCode, PriceName, SortOrder, IsActive, CreatedAt)
            VALUES ('33333333-3333-3333-3333-333333333333', N'Price3', N'Price3', 80, 1, SYSUTCDATETIME());
            INSERT INTO dbo.Items
                (ItemID, ItemNumber, ItemName, IsProhibited, IsPublishedOnline, IsActive, CreatedAt, UpdatedAt)
            VALUES
                ('44444444-4444-4444-4444-444444444444', N'PRICE-COLLISION', N'صنف السعر', 0, 0, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, Price, IsActive, CreatedAt, UpdatedAt)
            VALUES
                (NEWID(), '44444444-4444-4444-4444-444444444444', '33333333-3333-3333-3333-333333333333', 12.5, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            """,
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(string databaseName, string sql)
    {
        await using var connection = new SqlConnection(Connection(databaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Connection(string databaseName)
    {
        return new SqlConnectionStringBuilder
        {
            DataSource = "localhost",
            InitialCatalog = databaseName,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ConnectTimeout = 15,
        }.ConnectionString;
    }

    private static async Task<int> IntAsync(string databaseName, string sql)
    {
        await using var connection = new SqlConnection(Connection(databaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task<string> TextAsync(string databaseName, string sql)
    {
        await using var connection = new SqlConnection(Connection(databaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }

    private static async Task<decimal> DecimalAsync(string databaseName, string sql)
    {
        await using var connection = new SqlConnection(Connection(databaseName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToDecimal(await command.ExecuteScalarAsync());
    }

    private static async Task DropCentralAsync(string name)
    {
        var databaseName = CentralDatabase.Require(name);
        if (!CentralDatabase.IsTestName(databaseName))
        {
            return;
        }

        await DropAsync(databaseName);
    }

    private static async Task DropBranchAsync(string name)
    {
        await DropAsync(TestDatabase.Require(name));
    }

    private static async Task DropAsync(string databaseName)
    {
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(Connection("master"));
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
