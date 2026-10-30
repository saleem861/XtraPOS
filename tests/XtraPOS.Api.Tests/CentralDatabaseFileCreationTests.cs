using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class CentralDatabaseFileCreationTests(BranchApiFixture fixture)
{
    private const string OwnerName = "XtraPOS_Central_Test_FileOwner";
    private const string CollisionName = "XtraPOS_Central_Test_Collision";
    private const string SecondName = "XtraPOS_Central_Test_FilesTwo";
    private const string TakenOwnerName = "XtraPOS_Central_Test_TakenOwn";
    private const string RetryName = "XtraPOS_Central_Test_Retry";
    private const string FailName = "XtraPOS_Central_Test_FileFail";
    private const string AbsentName = "XtraPOS_Central_Test_NoCreate";

    [Fact]
    public async Task OccupiedDefaultFiles_CreateAUniquelyNamedDatabaseAndLeaveTheOldFiles()
    {
        await DropAsync(OwnerName);
        await DropAsync(CollisionName);
        await DropAsync(SecondName);
        await DropAsync(TakenOwnerName);
        await DropAsync(RetryName);
        await DropAsync(FailName);
        await DropAsync(AbsentName);
        try
        {
            await using var master = Master();
            var directories = await CentralDatabaseFiles.ReadDirectoriesAsync(master, CancellationToken.None);
            var propertyData = await TextAsync("SELECT CONVERT(nvarchar(400), SERVERPROPERTY('InstanceDefaultDataPath'));");
            var propertyLog = await TextAsync("SELECT CONVERT(nvarchar(400), SERVERPROPERTY('InstanceDefaultLogPath'));");
            if (!string.IsNullOrWhiteSpace(propertyData))
            {
                Assert.Equal(propertyData.TrimEnd('\\', '/'), directories.DataDirectory.TrimEnd('\\', '/'));
            }

            if (!string.IsNullOrWhiteSpace(propertyLog))
            {
                Assert.Equal(propertyLog.TrimEnd('\\', '/'), directories.LogDirectory.TrimEnd('\\', '/'));
            }

            var occupiedData = CentralDatabaseFiles.Combine(directories.DataDirectory, CollisionName + ".mdf");
            var occupiedLog = CentralDatabaseFiles.Combine(directories.LogDirectory, CollisionName + "_log.ldf");
            await ExecuteMasterAsync(CentralDatabaseFiles.BuildCreateSql(OwnerName, new CentralDatabaseFilePlan(
                "FileOwnerData",
                occupiedData,
                "FileOwnerLog",
                occupiedLog)));
            var ownerBefore = await FilesAsync(OwnerName);

            var created = await RunAsync(NewRequest(CollisionName));
            Assert.Contains(created, item => item.Type == "completed");
            Assert.Equal(CollisionName, await TextAsync($"SELECT DB_NAME();", CollisionName));
            Assert.Equal("Arabic_CI_AS", await TextAsync("SELECT CONVERT(nvarchar(128), DATABASEPROPERTYEX(DB_NAME(), 'Collation'));", CollisionName));
            var collisionFiles = await FilesAsync(CollisionName);
            Assert.NotEqual(occupiedData, collisionFiles.DataFile);
            Assert.NotEqual(occupiedLog, collisionFiles.LogFile);
            Assert.StartsWith(directories.DataDirectory.TrimEnd('\\', '/'), collisionFiles.DataFile.TrimEnd(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains(CollisionName + "_", collisionFiles.DataFile, StringComparison.Ordinal);
            Assert.EndsWith(".mdf", collisionFiles.DataFile, StringComparison.OrdinalIgnoreCase);
            Assert.EndsWith("_log.ldf", collisionFiles.LogFile, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ownerBefore, await FilesAsync(OwnerName));

            var second = await RunAsync(NewRequest(SecondName));
            Assert.Contains(second, item => item.Type == "completed");
            var secondFiles = await FilesAsync(SecondName);
            Assert.NotEqual(collisionFiles.DataFile, secondFiles.DataFile);
            Assert.NotEqual(collisionFiles.LogFile, secondFiles.LogFile);

            var taken = CentralDatabaseFiles.CreatePlan(RetryName, directories, "aaa111bbb222");
            await ExecuteMasterAsync(CentralDatabaseFiles.BuildCreateSql(TakenOwnerName, taken));
            var retried = await CentralDatabaseFiles.CreateNewAsync(
                master,
                RetryName,
                ["aaa111bbb222", "ccc333ddd444"],
                CancellationToken.None);
            Assert.EndsWith("XtraPOS_Central_Test_Retry_ccc333ddd444.mdf", retried.DataFileName, StringComparison.Ordinal);
            Assert.Equal(taken.DataFileName, (await FilesAsync(TakenOwnerName)).DataFile);

            var absent = await RunAsync(new CentralSetupRequest
            {
                Intent = "new",
                LocationKind = "Local",
                DatabaseName = AbsentName,
                BusinessName = "",
            });
            Assert.Contains(absent, item => item.Type == "failed");
            Assert.False(await ExistsAsync(AbsentName));

            var executor = Executor();
            executor.ApplyOverride = (_, _) => throw new InvalidOperationException("migration failed");
            var failed = new List<CentralSetupEvent>();
            await executor.ExecuteAsync(NewRequest(FailName), (item, _) =>
            {
                failed.Add(item);
                return Task.CompletedTask;
            }, CancellationToken.None);
            Assert.Contains(failed, item => item.Type == "failed" && (item.Message ?? "").Contains("تمت إزالة القاعدة التي أُنشئت في هذه العملية.", StringComparison.Ordinal));
            Assert.False(await ExistsAsync(FailName));
            Assert.True(await ExistsAsync(OwnerName));
            Assert.Equal(ownerBefore, await FilesAsync(OwnerName));
            Assert.True(await ExistsAsync(CollisionName));
        }
        finally
        {
            await DropAsync(OwnerName);
            await DropAsync(CollisionName);
            await DropAsync(SecondName);
            await DropAsync(TakenOwnerName);
            await DropAsync(RetryName);
            await DropAsync(FailName);
            await DropAsync(AbsentName);
        }
    }

    private static CentralSetupRequest NewRequest(string name) => new()
    {
        Intent = "new",
        LocationKind = "Local",
        DatabaseName = name,
        BusinessName = "منشأة الملفات",
    };

    private async Task<List<CentralSetupEvent>> RunAsync(CentralSetupRequest request)
    {
        var events = new List<CentralSetupEvent>();
        await Executor().ExecuteAsync(request, (item, _) =>
        {
            events.Add(item);
            return Task.CompletedTask;
        }, CancellationToken.None);
        return events;
    }

    private CentralSetupExecutor Executor()
    {
        var scope = fixture.Factory.Services.CreateAsyncScope();
        return scope.ServiceProvider.GetRequiredService<CentralSetupExecutor>();
    }

    private static async Task<(string DataFile, string LogFile)> FilesAsync(string name)
    {
        await using var connection = Master();
        await using var command = new SqlCommand(
            """
            SELECT type_desc, physical_name
            FROM sys.master_files
            WHERE database_id = DB_ID(@name);
            """,
            connection);
        command.Parameters.AddWithValue("@name", CentralDatabase.Require(name));
        string? data = null;
        string? log = null;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.GetString(0) == "ROWS")
            {
                data = reader.GetString(1);
            }
            else if (reader.GetString(0) == "LOG")
            {
                log = reader.GetString(1);
            }
        }

        return (data ?? "", log ?? "");
    }

    private static async Task<string> TextAsync(string sql, string? database = null)
    {
        await using var connection = database is null ? Master() : new SqlConnection(Connection(database));
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = new SqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync() ?? "");
    }

    private static async Task<bool> ExistsAsync(string name)
    {
        await using var connection = Master();
        await using var command = new SqlCommand("SELECT DB_ID(@name);", connection);
        command.Parameters.AddWithValue("@name", CentralDatabase.Require(name));
        var value = await command.ExecuteScalarAsync();
        return value is not null and not DBNull;
    }

    private static async Task ExecuteMasterAsync(string sql)
    {
        await using var connection = Master();
        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = 60;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropAsync(string name)
    {
        var safe = CentralDatabase.Require(name);
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

    private static SqlConnection Master()
    {
        var connection = new SqlConnection("Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;");
        connection.Open();
        return connection;
    }

    private static string Connection(string name) =>
        $"Server=localhost;Database={CentralDatabase.Require(name)};Integrated Security=True;Encrypt=True;TrustServerCertificate=True;";
}
