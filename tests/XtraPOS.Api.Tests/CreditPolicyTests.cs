using Microsoft.Data.SqlClient;
using XtraPOS.Api.Credit;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class CreditPolicyTests
{
    private const string CentralName = "XtraPOS_Central_Test_CreditPolicy";

    [Fact]
    public async Task Credit_policy_is_central_and_preserves_debt()
    {
        CentralDatabase.Require(CentralName);
        await DropAsync(CentralName);
        try
        {
            await CreateAsync(CentralName);
            var connectionString = Connection(CentralName);
            await new CentralMigrationRunner().ApplyAsync(connectionString, CancellationToken.None);
            var customer = Guid.NewGuid();
            var business = Guid.NewGuid();
            var actor = Guid.NewGuid();
            var branchOne = Guid.NewGuid();
            var branchTwo = Guid.NewGuid();
            await using var central = new SqlConnection(connectionString);
            await central.OpenAsync();
            await using (var seed = new SqlCommand("""
                INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Policy', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES (@customer, @business, N'Buyer', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CustomerReceivables (ReceivableID, CustomerID, SourceSaleID, SourceBranchID, OriginalAmount, OutstandingAmount, DueDate, CurrencyCode, Status)
                VALUES (NEWID(), @customer, NEWID(), @branch, 80, 80, DATEADD(day, 30, CAST(SYSUTCDATETIME() AS date)), N'ILS', N'Open');
                """, central))
            {
                seed.Parameters.AddWithValue("@business", business);
                seed.Parameters.AddWithValue("@customer", customer);
                seed.Parameters.AddWithValue("@branch", branchOne);
                await seed.ExecuteNonQueryAsync();
            }

            var service = new CreditPolicyService(null!);
            var initial = await service.ReadAsync(central, customer, CancellationToken.None);
            Assert.False(initial.Configured);
            Assert.False(initial.CreditEnabled);
            Assert.Equal(0m, initial.CreditLimit);
            Assert.Equal(30, initial.PaymentTermDays);
            Assert.Equal(CreditDecision.CreditDisabled, CreditAvailability.Decide(initial.CreditEnabled, initial.CreditStatus, initial.CreditLimit, 0m, 0m, 1m));

            var denied = CreditPolicyService.Denial(false, false, false, initial, new CreditPolicyWrite(true, 100m, 30, "Active", "", "raise"));
            Assert.NotNull(denied);

            var enabled = await service.SaveAsync(central, customer, actor, branchOne, new CreditPolicyWrite(true, 100m, 15, "Active", "", "open"), CancellationToken.None);
            Assert.True(enabled.CreditEnabled);
            Assert.Equal(100m, enabled.CreditLimit);
            Assert.Equal(20m, enabled.Available);
            Assert.Equal(customer, enabled.CustomerId);

            var seenFromOtherBranch = await service.ReadAsync(central, customer, CancellationToken.None);
            Assert.Equal(enabled.CreditLimit, seenFromOtherBranch.CreditLimit);
            Assert.Equal(branchOne, await ScalarGuidAsync(central, "SELECT BranchID FROM dbo.CreditAuditLog WHERE CustomerID = @id;", customer));

            await Assert.ThrowsAsync<CreditPolicyException>(() => service.SaveAsync(central, customer, actor, branchTwo, new CreditPolicyWrite(true, -1m, 15, "Active", enabled.RowVersion, "bad"), CancellationToken.None));
            var lowered = await service.SaveAsync(central, customer, actor, branchTwo, new CreditPolicyWrite(true, 10m, 15, "Active", enabled.RowVersion, "lower"), CancellationToken.None);
            Assert.Equal(80m, lowered.Outstanding);
            Assert.Equal(0m, lowered.Available);
            Assert.Equal(80m, await ScalarDecimalAsync(central, "SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE CustomerID = @id;", customer));
            Assert.Equal(CreditDecision.LimitExceeded, CreditAvailability.Decide(lowered.CreditEnabled, lowered.CreditStatus, lowered.CreditLimit, lowered.Outstanding, 0m, 1m));

            var suspended = await service.SaveAsync(central, customer, actor, branchTwo, new CreditPolicyWrite(true, 100m, 15, "Suspended", lowered.RowVersion, "stop"), CancellationToken.None);
            Assert.NotEqual(CreditDecision.Approved, CreditAvailability.Decide(suspended.CreditEnabled, suspended.CreditStatus, suspended.CreditLimit, suspended.Outstanding, 0m, 1m));
            Assert.Equal("Suspended", suspended.CreditStatus);
            Assert.Equal(1, await ScalarIntAsync(central, "SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = @id;", customer));

            var stale = enabled.RowVersion;
            await Assert.ThrowsAsync<CreditPolicyException>(() => service.SaveAsync(central, customer, actor, branchOne, new CreditPolicyWrite(true, 50m, 15, "Active", stale, "stale"), CancellationToken.None));
            Assert.Equal(0m, CreditAvailability.Available(0m, 80m, 0m));
            _ = branchTwo;
        }
        finally
        {
            await DropAsync(CentralName);
        }
    }

    private static async Task<Guid> ScalarGuidAsync(SqlConnection connection, string sql, Guid id)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        return (Guid)(await command.ExecuteScalarAsync() ?? Guid.Empty);
    }

    private static async Task<decimal> ScalarDecimalAsync(SqlConnection connection, string sql, Guid id)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToDecimal(await command.ExecuteScalarAsync());
    }

    private static async Task<int> ScalarIntAsync(SqlConnection connection, string sql, Guid id)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task CreateAsync(string name)
    {
        await using var connection = new SqlConnection(Master());
        await connection.OpenAsync();
        await using var command = new SqlCommand($"CREATE DATABASE [{name}];", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropAsync(string name)
    {
        await using var connection = new SqlConnection(Master());
        await connection.OpenAsync();
        await using var command = new SqlCommand($"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Master() => new SqlConnectionStringBuilder(Connection("master")) { InitialCatalog = "master" }.ConnectionString;

    private static string Connection(string name) => new SqlConnectionStringBuilder
    {
        DataSource = "localhost",
        InitialCatalog = name,
        IntegratedSecurity = true,
        Encrypt = true,
        TrustServerCertificate = true,
    }.ConnectionString;
}
