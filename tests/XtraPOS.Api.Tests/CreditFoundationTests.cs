using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Credit;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using XtraPOS.Api.Data.Migrations;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("CreditFoundation")]
public sealed class CreditFoundationTests
{
    private const string CentralName = "XtraPOS_Central_Test_CreditFoundation";
    private const string BranchName = "XtraPOS_Branch_Test_CreditFoundation";

    [Fact]
    public async Task Migrations_apply_twice_and_credit_rules_hold_under_concurrency()
    {
        CentralDatabase.Require(CentralName);
        var branch = TestDatabase.Require(BranchName);
        await DropAsync(CentralName);
        await DropAsync(branch);
        try
        {
            await CreateAsync(CentralName);
            await CreateAsync(branch);
            var centralCs = Connection(CentralName);
            var branchCs = Connection(branch);
            var central = new CentralMigrationRunner();
            await central.ApplyAsync(centralCs, CancellationToken.None);
            await central.ApplyAsync(centralCs, CancellationToken.None);
            var runner = new MigrationRunner(Factory(branch), NullLogger<MigrationRunner>.Instance);
            await runner.ApplyPendingAsync(CancellationToken.None);
            await runner.ApplyPendingAsync(CancellationToken.None);

            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'011_CustomerCredit';"));
            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM sys.indexes WHERE name = N'UQ_CustomerReceivables_SourceSale';"));
            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM sys.indexes WHERE name = N'UQ_CustomerCreditLedger_Idempotency';"));
            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM sys.triggers WHERE name = N'TR_CustomerCreditLedger_AppendOnly';"));
            Assert.Equal(7, await IntAsync(branchCs, "SELECT COUNT(*) FROM dbo.Permissions WHERE PermissionCode LIKE N'Credit.%';"));
            Assert.Equal(0, await IntAsync(branchCs, "SELECT COUNT(*) FROM sys.tables WHERE name IN (N'CustomerReceivables', N'CustomerCreditLedger');"));
            Assert.Equal(7, await IntAsync(branchCs, """
                SELECT COUNT(*)
                FROM dbo.RolePermissions AS grantRow
                INNER JOIN dbo.Roles AS role ON role.RoleID = grantRow.RoleID
                INNER JOIN dbo.Permissions AS permission ON permission.PermissionID = grantRow.PermissionID
                WHERE role.RoleCode = N'Admin' AND permission.PermissionCode LIKE N'Credit.%';
                """));

            var customerId = Guid.NewGuid();
            var branchId = Guid.NewGuid();
            var actorId = Guid.NewGuid();
            await ExecuteAsync(centralCs, """
                DECLARE @business uniqueidentifier = NEWID();
                INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt)
                VALUES (@business, N'Credit test', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt)
                VALUES (@customer, @business, N'Credit customer', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CustomerCreditPolicies (CustomerID, CreditEnabled, CreditLimit, PaymentTermDays, CreditStatus)
                VALUES (@customer, 1, 1000, 30, N'Active');
                INSERT INTO dbo.CustomerReceivables
                    (ReceivableID, CustomerID, SourceSaleID, SourceBranchID, OriginalAmount, OutstandingAmount, DueDate, CurrencyCode, Status)
                VALUES (NEWID(), @customer, NEWID(), @branch, 600, 600, DATEADD(day, 30, CAST(SYSUTCDATETIME() AS date)), N'ILS', N'Open');
                """, ("@customer", customerId), ("@branch", branchId));

            var service = new CreditReservationService();
            await using var first = new SqlConnection(centralCs);
            await using var second = new SqlConnection(centralCs);
            await first.OpenAsync();
            await second.OpenAsync();
            var expires = DateTime.UtcNow.AddMinutes(10);
            var races = await Task.WhenAll(
                service.ReserveAsync(first, customerId, branchId, 300m, "race-a", expires, CancellationToken.None),
                service.ReserveAsync(second, customerId, branchId, 300m, "race-b", expires, CancellationToken.None));
            Assert.Equal(1, races.Count(item => item.Decision == CreditDecision.Approved && !item.Replay));
            Assert.Equal(1, races.Count(item => item.Decision == CreditDecision.LimitExceeded));

            var approved = races.Single(item => item.Decision == CreditDecision.Approved);
            var approvedKey = races[0].AuthorizationId == approved.AuthorizationId ? "race-a" : "race-b";
            var replay = await service.ReserveAsync(first, customerId, branchId, 300m, approvedKey, expires, CancellationToken.None);
            Assert.True(replay.Replay);
            Assert.Equal(approved.AuthorizationId, replay.AuthorizationId);
            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM dbo.CreditAuthorizations WHERE IdempotencyKey = N'race-a';"));

            var saleId = Guid.NewGuid();
            await service.FinalizeAsync(first, approved.AuthorizationId, saleId, actorId, "ledger-1", DateTime.UtcNow.Date.AddDays(30), CancellationToken.None);
            await service.FinalizeAsync(first, approved.AuthorizationId, saleId, actorId, "ledger-1", DateTime.UtcNow.Date.AddDays(30), CancellationToken.None);
            Assert.Equal(2, await IntAsync(centralCs, "SELECT COUNT(*) FROM dbo.CustomerReceivables;"));
            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM dbo.CustomerReceivables WHERE SourceSaleID = @sale;".Replace("@sale", $"'{saleId}'")));
            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM dbo.CustomerCreditLedger;"));

            await service.ReleaseAsync(first, approved.AuthorizationId, CancellationToken.None);
            Assert.Equal("Completed", await TextAsync(centralCs, $"SELECT Status FROM dbo.CreditAuthorizations WHERE AuthorizationID = '{approved.AuthorizationId}';"));

            var rejected = races.Single(item => item.Decision == CreditDecision.LimitExceeded);
            await service.ReleaseAsync(first, rejected.AuthorizationId, CancellationToken.None);
            Assert.Equal(0m, await DecimalAsync(centralCs, """
                SELECT ISNULL(SUM(RequestedAmount), 0)
                FROM dbo.CreditAuthorizations
                WHERE Status IN (N'Pending', N'Authorized', N'Posting') AND ExpiresAt > SYSUTCDATETIME();
                """));

            await ExecuteAsync(centralCs, """
                INSERT INTO dbo.CreditAuditLog (AuditID, CustomerID, ActorUserID, Action, Result)
                VALUES (NEWID(), @customer, @customer, N'Test', N'Ok');
                """, ("@customer", customerId));
            await Assert.ThrowsAnyAsync<SqlException>(() => ExecuteAsync(centralCs, "UPDATE dbo.CustomerCreditLedger SET Amount = 1;"));
            await Assert.ThrowsAnyAsync<SqlException>(() => ExecuteAsync(centralCs, "DELETE FROM dbo.CreditAuditLog;"));
        }
        finally
        {
            await DropAsync(CentralName);
            await DropAsync(branch);
        }
    }

    private static SqlConnectionFactory Factory(string database)
    {
        return new SqlConnectionFactory(Options.Create(new SqlServerOptions
        {
            Server = "localhost",
            Database = database,
            ConnectTimeoutSeconds = 15,
        }));
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, Guid Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(parameter.Name, System.Data.SqlDbType.UniqueIdentifier).Value = parameter.Value;
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> IntAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<decimal> DecimalAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToDecimal(await command.ExecuteScalarAsync());
    }

    private static async Task<string> TextAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }

    private static string Connection(string name)
    {
        return new SqlConnectionStringBuilder
        {
            DataSource = "localhost",
            InitialCatalog = name,
            IntegratedSecurity = true,
            Encrypt = true,
            TrustServerCertificate = true,
            ApplicationName = "XtraPOS.Tests",
        }.ConnectionString;
    }

    private static async Task CreateAsync(string name)
    {
        await MasterAsync($"""
            IF DB_ID(N'{name}') IS NULL
                CREATE DATABASE [{name}] COLLATE Arabic_CI_AS;
            """);
    }

    private static async Task DropAsync(string name)
    {
        if (!name.Contains("_Test_CreditFoundation", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Refusing to drop a database outside the credit foundation test.");
        }

        SqlConnection.ClearAllPools();
        await MasterAsync($"""
            IF DB_ID(N'{name}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{name}];
            END
            """);
    }

    private static async Task MasterAsync(string sql)
    {
        await using var connection = new SqlConnection("Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;");
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }
}
