using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Setup;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class BranchAgentTests
{
    private const string DisposableName = "XtraPOS_Branch_97";

    [Fact]
    public void Pairing_is_single_use_and_only_local_instances_are_accepted()
    {
        var directory = Path.Combine(Path.GetTempPath(), "xtrapos-agent-" + Guid.NewGuid().ToString("N"));
        var registry = new BranchAgentRegistry(directory);
        var now = DateTimeOffset.UtcNow;
        var (code, _) = registry.IssuePairingCode(now);
        var (agentId, token) = registry.Pair(code, "Branch-PC", ["localhost", @"localhost\BARTENDER"], now);
        Assert.Throws<BranchAgentRejectedException>(() => registry.Pair(code, "Branch-PC", ["localhost"], now));
        Assert.Throws<BranchAgentRejectedException>(() => registry.Pair("missing", "Branch-PC", ["10.1.1.1"], now));
        Assert.False(BranchAgentRules.TryNormalizeLocalInstance("69.30.235.148,10173", out _));
        Assert.True(BranchAgentRules.TryNormalizeLocalInstance(@"localhost\BARTENDER", out var instance));
        Assert.Equal(@"localhost\BARTENDER", instance);

        var expired = new BranchAgentRegistry(directory);
        Assert.Throws<BranchAgentRejectedException>(() => expired.Pair(code, "Branch-PC", ["localhost"], now));
        var online = expired.List(now);
        Assert.Contains(online, computer => computer.Id == agentId && !computer.Online);

        registry.Heartbeat(token, ["localhost", @"localhost\BARTENDER"], now);
        var job = registry.Approve(agentId, DisposableName, @"localhost\BARTENDER", """{"sql":"DROP DATABASE Shop1","branchDatabaseName":"other"}""", now);
        Assert.Equal(BranchAgentRules.ProvisionOperation, job.Operation);
        Assert.Equal(DisposableName, job.DatabaseName);
        Assert.DoesNotContain("DROP", job.SetupJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Shop1", job.SetupJson, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<BranchAgentRejectedException>(() => registry.Claim("wrong-token", now));
        var claimed = registry.Claim(token, now);
        Assert.NotNull(claimed);
        Assert.Null(registry.Claim(token, now));
        registry.Report(token, claimed!.Id, claimed.Nonce, new BranchAgentProgress("completed", null, null, "done", true), now);
        Assert.Throws<BranchAgentRejectedException>(() =>
            registry.Report(token, claimed.Id, claimed.Nonce, new BranchAgentProgress("completed", null, null, "again", true), now));
        Assert.Throws<BranchAgentRejectedException>(() => registry.Approve(agentId, "XtraPOS_Branch_Test_Staging", "localhost", null, now));

        var unclaimed = registry.Approve(agentId, "XtraPOS_Branch_96", "localhost", null, now);
        Assert.False(registry.IsClaimed(unclaimed.Id));
        Assert.True(registry.CancelUnclaimed(unclaimed.Id, "timed out"));
        Assert.True(registry.IsTerminal(unclaimed.Id));
        var retried = registry.Approve(agentId, "XtraPOS_Branch_96", "localhost", null, now);
        Assert.NotEqual(unclaimed.Id, retried.Id);
    }

    [Fact]
    public async Task Disposable_branch_is_provisioned_locally_and_removed()
    {
        await using var master = new SqlConnection(
            "Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;Connect Timeout=15");
        await master.OpenAsync();
        var shop1Before = await ScalarAsync(master, "SELECT CASE WHEN DB_ID(N'shop1') IS NULL THEN -1 ELSE (SELECT COUNT(*) FROM shop1.sys.tables WHERE is_ms_shipped = 0) END;");
        if (await ScalarAsync(master, "SELECT DB_ID(@name);", DisposableName) is not null and not DBNull)
        {
            throw new InvalidOperationException("The disposable catalog already exists.");
        }

        var directory = Path.Combine(Path.GetTempPath(), "xtrapos-agent-" + Guid.NewGuid().ToString("N"));
        var registry = new BranchAgentRegistry(directory);
        var now = DateTimeOffset.UtcNow;
        var (code, _) = registry.IssuePairingCode(now);
        var (agentId, token) = registry.Pair(code, "Branch-PC", ["localhost"], now);
        var approved = registry.Approve(agentId, DisposableName, "localhost", null, now);
        var claimed = registry.Claim(token, now);
        Assert.NotNull(claimed);
        Assert.Equal(approved.Id, claimed!.Id);

        var store = new OperationalBranchConnectionStore(new EphemeralDataProtectionProvider(), directory);
        var runner = new MigrationRunner(
            new SqlConnectionFactory(Options.Create(new SqlServerOptions
            {
                Server = "unused-staging-host",
                Database = "XtraPOS_Branch_Test_Staging",
                User = "xtrapos_staging",
                Password = "not-used",
                ConnectTimeoutSeconds = 30,
            })),
            NullLogger<MigrationRunner>.Instance);
        var executor = new BranchAgentExecutor(new SelectedDatabaseProvisioner(runner), runner, store);
        try
        {
            await executor.ProvisionAsync(claimed, (progress, _) =>
            {
                registry.Report(token, claimed.Id, claimed.Nonce, progress, DateTimeOffset.UtcNow);
                return Task.CompletedTask;
            }, CancellationToken.None);

            Assert.True(registry.IsTerminal(claimed.Id));
            Assert.NotNull(await ScalarAsync(master, "SELECT DB_ID(@name);", DisposableName));
            await using var branch = new SqlConnection(BranchAgentExecutor.LocalSettings("localhost", DisposableName).BuildConnectionString(DisposableName));
            await branch.OpenAsync();
            await using var sales = new SqlCommand("SELECT COUNT(*) FROM dbo.Sales;", branch);
            Assert.Equal(0, (int)(await sales.ExecuteScalarAsync() ?? 0));
            await using var history = new SqlCommand("SELECT OBJECT_ID(N'dbo.DatabaseSchemaHistory', N'U');", branch);
            Assert.NotNull(await history.ExecuteScalarAsync());
            var saved = await store.ReadAsync(CancellationToken.None);
            Assert.NotNull(saved);
            Assert.Equal(DisposableName, saved!.DatabaseName);
            Assert.Equal("localhost", saved.Server);
            Assert.Equal("Integrated", saved.AuthenticationType);
            Assert.Equal(shop1Before, await ScalarAsync(master, "SELECT CASE WHEN DB_ID(N'shop1') IS NULL THEN -1 ELSE (SELECT COUNT(*) FROM shop1.sys.tables WHERE is_ms_shipped = 0) END;"));
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var drop = new SqlCommand(
                """
                IF DB_ID(@name) IS NOT NULL
                BEGIN
                    ALTER DATABASE [XtraPOS_Branch_97] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [XtraPOS_Branch_97];
                END
                """,
                master);
            drop.Parameters.AddWithValue("@name", DisposableName);
            drop.CommandTimeout = 60;
            await drop.ExecuteNonQueryAsync();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }

        Assert.True(await ScalarAsync(master, "SELECT DB_ID(@name);", DisposableName) is null or DBNull);
    }

    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql, string? name = null)
    {
        await using var command = new SqlCommand(sql, connection);
        if (name is not null)
        {
            command.Parameters.AddWithValue("@name", name);
        }

        return await command.ExecuteScalarAsync();
    }
}
