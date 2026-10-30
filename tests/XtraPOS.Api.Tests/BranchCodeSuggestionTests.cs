using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class BranchCodeSequenceTests
{
    [Fact]
    public void EmptyBusiness_Suggests01()
    {
        Assert.Equal("01", BranchCodeSequence.Suggest([]));
        Assert.Equal("01", BranchCodeSequence.Suggest([null, " ", "المستودع"]));
    }

    [Fact]
    public void PaddedCodes_SuggestTheNextCode()
    {
        Assert.Equal("04", BranchCodeSequence.Suggest(["01", "02", "03"]));
    }

    [Fact]
    public void Gap_IsFilledInsteadOfCountingRows()
    {
        Assert.Equal("03", BranchCodeSequence.Suggest(["01", "02", "04"]));
        Assert.Equal("4", BranchCodeSequence.Suggest(["1", "2", "3"]));
    }

    [Fact]
    public void DuplicateManualCode_IsRejected_AndADifferentCode_IsAccepted()
    {
        var codes = new[] { "01", "02", "03" };
        Assert.True(BranchCodeSequence.IsDuplicate(codes, "02"));
        Assert.Equal(BranchCodeSequence.DuplicateMessage, "رمز الفرع مستخدم مسبقًا. اختر رمزًا آخر.");
        Assert.False(BranchCodeSequence.IsDuplicate(codes, "09"));
        Assert.False(BranchCodeSequence.IsDuplicate(codes, "04"));
        Assert.False(BranchCodeSequence.IsDuplicate(["02", "03"], "01"));
        Assert.True(BranchCodeSequence.IsDuplicate(["01", "01"], "01"));
    }
}

[Collection("Branch")]
public sealed class BranchCodeSuggestionApiTests(BranchApiFixture fixture)
{
    private const string CentralName = "XtraPOS_Central_Test_BranchCode";

    [Fact]
    public async Task Suggestion_ComesFromCentralForALinkedBranch_AndFromTheLocalBranchDatabaseForStandalone()
    {
        await DropCentralAsync();
        await DeleteMarkerBranchesAsync();
        try
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var databases = scope.ServiceProvider.GetRequiredService<CentralDatabaseService>();
            await databases.CreateIfMissingAsync(LocalCentral(), CancellationToken.None);
            await CentralExecuteAsync(
                """
                DECLARE @populated uniqueidentifier = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa1';
                DECLARE @empty uniqueidentifier = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaa2';
                INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt)
                VALUES
                    (@populated, N'منشأة الرموز', 1, SYSUTCDATETIME()),
                    (@empty, N'منشأة فارغة', 1, DATEADD(minute, 1, SYSUTCDATETIME()));
                INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
                VALUES
                    (NEWID(), @populated, N'01', N'فرع 01', N'Retail', 1, SYSUTCDATETIME()),
                    (NEWID(), @populated, N'02', N'فرع 02', N'Retail', 1, SYSUTCDATETIME()),
                    (NEWID(), @populated, N'03', N'فرع 03', N'Retail', 1, SYSUTCDATETIME());
                """);
            await BranchExecuteAsync(
                """
                DECLARE @business uniqueidentifier = (SELECT TOP (1) BusinessID FROM dbo.Businesses ORDER BY CreatedAt);
                INSERT INTO dbo.Branches (BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
                VALUES
                    (@business, N'01', N'رمز محلي 01', N'Retail', 1, SYSUTCDATETIME()),
                    (@business, N'02', N'رمز محلي 02', N'Retail', 1, SYSUTCDATETIME());
                """);

            Assert.Equal(1, await CentralIntAsync(
                "SELECT COUNT(*) FROM sys.indexes WHERE name = N'UX_Branches_BusinessID_BranchCode' AND object_id = OBJECT_ID(N'dbo.Branches');"));
            Assert.Equal(1, await BranchIntAsync(
                "SELECT COUNT(*) FROM sys.indexes WHERE name = N'UX_Branches_BusinessID_BranchCode' AND object_id = OBJECT_ID(N'dbo.Branches');"));
            await Assert.ThrowsAsync<SqlException>(() => CentralExecuteAsync(
                """
                INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
                SELECT NEWID(), BusinessID, N'01', N'مكرر', N'Retail', 1, SYSUTCDATETIME()
                FROM dbo.Businesses WHERE BusinessName = N'منشأة الرموز';
                """));

            using var client = fixture.CreateClient();
            var central = await client.PostAsJsonAsync("/api/setup/branch-code", CentralBody());
            var centralBody = await central.Content.ReadFromJsonAsync<BranchCodeResponse>();
            Assert.Equal(HttpStatusCode.OK, central.StatusCode);
            Assert.Equal("04", centralBody?.SuggestedCode);

            var empty = await client.PostAsJsonAsync("/api/setup/branch-code", CentralBody(businessName: "منشأة فارغة"));
            var emptyBody = await empty.Content.ReadFromJsonAsync<BranchCodeResponse>();
            Assert.Equal("01", emptyBody?.SuggestedCode);

            var duplicate = await client.PostAsJsonAsync("/api/setup/branch-code", CentralBody(branchCode: "02"));
            var duplicateText = await duplicate.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);
            Assert.Contains(BranchCodeSequence.DuplicateMessage, duplicateText, StringComparison.Ordinal);
            Assert.DoesNotContain("SqlSecret-NotForBrowser", duplicateText, StringComparison.Ordinal);

            var edited = await client.PostAsJsonAsync("/api/setup/branch-code", CentralBody(branchCode: "09"));
            var editedBody = await edited.Content.ReadFromJsonAsync<BranchCodeResponse>();
            Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
            Assert.True(editedBody?.Available);

            var local = await client.PostAsJsonAsync("/api/setup/branch-code", new
            {
                source = "Branch",
                intent = "existing",
                locationKind = "Local",
                server = "localhost",
                databaseName = TestDatabase.Name,
            });
            var localBody = await local.Content.ReadFromJsonAsync<BranchCodeResponse>();
            Assert.Equal(HttpStatusCode.OK, local.StatusCode);
            Assert.Equal("03", localBody?.SuggestedCode);
        }
        finally
        {
            await DeleteMarkerBranchesAsync();
            await DropCentralAsync();
        }
    }

    private static object CentralBody(string? businessName = "منشأة الرموز", string? branchCode = null) => new
    {
        source = "Central",
        intent = "existing",
        locationKind = "Local",
        server = "localhost",
        databaseName = CentralName,
        businessName,
        branchCode,
        password = "SqlSecret-NotForBrowser",
    };

    private static SqlEndpointSettings LocalCentral() =>
        new(
            DatabaseRole.Central,
            SqlEndpointSettings.Local,
            "localhost",
            null,
            null,
            CentralName,
            SqlEndpointSettings.Integrated,
            null,
            null,
            true,
            true);

    private static async Task DropCentralAsync()
    {
        var safe = CentralDatabase.Require(CentralName);
        SqlConnection.ClearAllPools();
        await using var connection = Master();
        await using var command = new SqlCommand(
            $"""
            IF DB_ID(N'{safe}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{safe}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{safe}];
            END
            """,
            connection);
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task CentralExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(LocalCentral().BuildConnectionString(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CentralIntAsync(string sql)
    {
        await using var connection = new SqlConnection(LocalCentral().BuildConnectionString(CentralName));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task BranchExecuteAsync(string sql)
    {
        await using var connection = OpenBranch();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> BranchIntAsync(string sql)
    {
        await using var connection = OpenBranch();
        await using var command = new SqlCommand(sql, connection);
        return (int)(await command.ExecuteScalarAsync() ?? 0);
    }

    private static async Task DeleteMarkerBranchesAsync()
    {
        await BranchExecuteAsync("DELETE FROM dbo.Branches WHERE BranchName IN (N'رمز محلي 01', N'رمز محلي 02');");
    }

    private static SqlConnection OpenBranch()
    {
        var connection = new SqlConnection(TestDatabase.ConnectionString());
        connection.Open();
        return connection;
    }

    private static SqlConnection Master()
    {
        var connection = new SqlConnection("Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;");
        connection.Open();
        return connection;
    }
}
