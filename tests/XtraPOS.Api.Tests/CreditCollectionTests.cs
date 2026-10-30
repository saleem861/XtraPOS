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

public sealed class CreditCollectionTests
{
    private const string CentralName = "XtraPOS_Central_Test_CreditCollect";
    private const string OriginName = "XtraPOS_Branch_Test_CreditCollectA";
    private const string OtherName = "XtraPOS_Branch_Test_CreditCollectB";

    [Fact]
    public async Task Cross_branch_collection_allocates_once_and_recovers()
    {
        CentralDatabase.Require(CentralName);
        var originName = TestDatabase.Require(OriginName);
        var otherName = TestDatabase.Require(OtherName);
        await DropAsync(CentralName);
        await DropAsync(originName);
        await DropAsync(otherName);
        try
        {
            await CreateAsync(CentralName);
            await CreateAsync(originName);
            await CreateAsync(otherName);
            await new CentralMigrationRunner().ApplyAsync(Connection(CentralName), CancellationToken.None);
            await MigrateAsync(originName);
            await MigrateAsync(otherName);

            var business = Guid.NewGuid();
            var originBranch = Guid.NewGuid();
            var otherBranch = Guid.NewGuid();
            var customer = Guid.NewGuid();
            var fifo = Guid.NewGuid();
            var race = Guid.NewGuid();
            var sale = Guid.NewGuid();
            var invoiceA = Guid.NewGuid();
            var invoiceB = Guid.NewGuid();
            var raceReceivable = Guid.NewGuid();
            var fullReceivable = Guid.NewGuid();
            var origin = await SeedBranchAsync(originName, business, originBranch, "01");
            var other = await SeedBranchAsync(otherName, business, otherBranch, "02");
            await ExecuteAsync(Connection(CentralName), """
                INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Collect', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES
                    (@customer, @business, N'Origin', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (@fifo, @business, N'Fifo', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (@race, @business, N'Race', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CustomerReceivables (ReceivableID, CustomerID, SourceSaleID, SourceBranchID, OriginalAmount, OutstandingAmount, DueDate, Status) VALUES
                    (@full, @customer, @sale, @origin, 100, 100, '2026-01-01', N'Open'),
                    (@main, @customer, NEWID(), @origin, 800, 800, '2026-02-01', N'Open'),
                    (@a, @fifo, NEWID(), @origin, 200, 200, '2026-01-01', N'Open'),
                    (@b, @fifo, NEWID(), @origin, 350, 350, '2026-03-01', N'Open'),
                    (@raceReceivable, @race, NEWID(), @origin, 500, 500, '2026-01-01', N'Open');
                """,
                ("@business", business), ("@customer", customer), ("@fifo", fifo), ("@race", race), ("@full", fullReceivable), ("@sale", sale), ("@origin", originBranch), ("@main", Guid.NewGuid()), ("@a", invoiceA), ("@b", invoiceB), ("@raceReceivable", raceReceivable));
            await ExecuteAsync(Connection(originName), """
                INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES (@customer, @business, N'Origin', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.Sales (SaleID, BusinessID, BranchID, POSStationID, DeviceID, CartID, CustomerID, CashierUserID, CashSessionID, SaleNumber, Status, Subtotal, DiscountTotal, Total)
                VALUES (@sale, @business, @branch, @station, @device, NEWID(), @customer, @actor, @session, NEXT VALUE FOR dbo.SaleNumberSequence, N'Completed', 800, 0, 800);
                """,
                ("@sale", sale), ("@business", business), ("@branch", originBranch), ("@station", origin.StationId), ("@device", origin.DeviceId), ("@customer", customer), ("@actor", origin.UserId), ("@session", origin.SessionId));

            var service = new CreditCollectionService();
            await using var central = new SqlConnection(Connection(CentralName));
            await using var originDb = new SqlConnection(Connection(originName));
            await using var otherDb = new SqlConnection(Connection(otherName));
            await central.OpenAsync();
            await originDb.OpenAsync();
            await otherDb.OpenAsync();

            Assert.Equal(CreditCollectionOutcome.Completed, await service.CollectAsync(central, originDb, Request(origin, customer, 100m, null), true, false, false, CancellationToken.None));
            Assert.Equal(0m, await MoneyAsync(CentralName, "SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE ReceivableID = '" + fullReceivable + "';"));
            Assert.Equal(800m, await MoneyAsync(originName, "SELECT Total FROM dbo.Sales WHERE SaleID = '" + sale + "';"));

            var partial = Request(other, customer, 300m, null);
            Assert.Equal(CreditCollectionOutcome.Completed, await service.CollectAsync(central, otherDb, partial, true, false, false, CancellationToken.None));
            Assert.Equal(500m, await MoneyAsync(CentralName, "SELECT SUM(OutstandingAmount) FROM dbo.CustomerReceivables WHERE CustomerID = '" + customer + "';"));
            Assert.Equal(1, await IntAsync(otherName, "SELECT COUNT(*) FROM dbo.BranchDebtCollections;"));
            Assert.Equal(0, await IntAsync(otherName, "SELECT COUNT(*) FROM dbo.Sales;"));
            Assert.Equal(CreditCollectionOutcome.Completed, await service.CollectAsync(central, otherDb, partial, true, false, false, CancellationToken.None));
            Assert.Equal(1, await IntAsync(CentralName, "SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE IdempotencyKey = N'collection:" + partial.OperationId.ToString("N") + "';"));

            var quote = await service.QuoteAsync(central, fifo, 300m, null, CancellationToken.None);
            Assert.Equal(200m, quote.Allocations[0].Amount);
            Assert.Equal(100m, quote.Allocations[1].Amount);
            Assert.Equal(CreditCollectionOutcome.Completed, await service.CollectAsync(central, otherDb, Request(other, fifo, 300m, null), true, false, false, CancellationToken.None));
            Assert.Equal(0m, await MoneyAsync(CentralName, "SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE ReceivableID = '" + invoiceA + "';"));
            Assert.Equal(250m, await MoneyAsync(CentralName, "SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE ReceivableID = '" + invoiceB + "';"));

            var selected = Request(origin, fifo, 50m, invoiceB);
            Assert.Equal(CreditCollectionOutcome.Completed, await service.CollectAsync(central, originDb, selected, true, false, false, CancellationToken.None));
            Assert.Equal(200m, await MoneyAsync(CentralName, "SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE ReceivableID = '" + invoiceB + "';"));

            var first = Request(origin, race, 400m, null);
            var second = Request(other, race, 400m, null);
            var raceResults = await Task.WhenAll(RunAsync(originName, first), RunAsync(otherName, second));
            Assert.Contains(CreditCollectionOutcome.Completed, raceResults);
            Assert.Contains(CreditCollectionOutcome.Refused, raceResults);
            Assert.Equal(100m, await MoneyAsync(CentralName, "SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE ReceivableID = '" + raceReceivable + "';"));

            Assert.Equal(CreditCollectionOutcome.Refused, await service.CollectAsync(central, otherDb, Request(other, customer, 900m, null), true, false, false, CancellationToken.None));
            Assert.Equal(CreditCollectionOutcome.Refused, await service.CollectAsync(central, otherDb, Request(other, customer, 10m, null) with { PaymentMethod = "Card" }, true, false, false, CancellationToken.None));
            Assert.Equal(CreditCollectionOutcome.Unauthorized, await service.CollectAsync(central, otherDb, Request(other, customer, 10m, null) with { ActorUserId = other.DeniedUserId }, true, false, false, CancellationToken.None));
            Assert.Equal(CreditCollectionOutcome.Refused, await service.CollectAsync(central, otherDb, Request(other, customer, 10m, null) with { CashSessionId = Guid.NewGuid() }, true, false, false, CancellationToken.None));
            Assert.Equal(CreditCollectionOutcome.Refused, await service.CollectAsync(central, otherDb, Request(other, customer, 10m, null) with { DeviceId = other.ClosedDeviceId }, true, false, false, CancellationToken.None));
            Assert.Equal(CreditCollectionOutcome.CentralUnavailable, await service.CollectAsync(central, otherDb, Request(other, customer, 10m, null), false, false, false, CancellationToken.None));

            var rolled = Request(other, customer, 10m, null);
            Assert.Equal(CreditCollectionOutcome.LocalRolledBack, await service.CollectAsync(central, otherDb, rolled, true, true, false, CancellationToken.None));
            Assert.Equal("Released", await TextAsync(CentralName, "SELECT Status FROM dbo.CreditCollectionIntents WHERE CollectionOperationID = '" + rolled.OperationId + "';"));
            Assert.Equal(0, await IntAsync(otherName, "SELECT COUNT(*) FROM dbo.BranchDebtCollections WHERE CollectionOperationID = '" + rolled.OperationId + "';"));

            var pending = Request(other, customer, 20m, null);
            Assert.Equal(CreditCollectionOutcome.PendingCentral, await service.CollectAsync(central, otherDb, pending, true, false, true, CancellationToken.None));
            Assert.Equal(CreditCollectionOutcome.ReconciliationRequired, await service.CollectAsync(central, otherDb, pending with { Amount = 21m }, true, false, false, CancellationToken.None));
            Assert.Equal(CreditCollectionOutcome.Completed, await service.RecoverOnceAsync(central, otherDb, "worker-a", CancellationToken.None));
            Assert.Null(await service.RecoverOnceAsync(central, otherDb, "worker-b", CancellationToken.None));
            Assert.Equal(CreditCollectionOutcome.Completed, await service.CollectAsync(central, otherDb, pending, true, false, false, CancellationToken.None));
            Assert.Equal(1, await IntAsync(CentralName, "SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE IdempotencyKey = N'collection:" + pending.OperationId.ToString("N") + "';"));
            Assert.Equal(1, await IntAsync(otherName, "SELECT COUNT(*) FROM dbo.BranchDebtCollections WHERE CollectionOperationID = '" + pending.OperationId + "';"));

            var expired = Request(origin, customer, 15m, null);
            Assert.Equal(CreditCollectionOutcome.PendingCentral, await service.CollectAsync(central, originDb, expired, true, false, true, CancellationToken.None));
            await ExecuteAsync(Connection(CentralName), "UPDATE dbo.CreditCollectionIntents SET Status = N'Authorized', ExpiresAt = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE CollectionOperationID = @operation;", ("@operation", expired.OperationId));
            Assert.Equal(CreditCollectionOutcome.ReconciliationRequired, await service.RecoverOnceAsync(central, originDb, "worker-c", CancellationToken.None));
            Assert.Equal(1, await IntAsync(originName, "SELECT COUNT(*) FROM dbo.BranchDebtCollections WHERE CollectionOperationID = '" + expired.OperationId + "' AND Status = N'ReconciliationRequired';"));

            var ledger = await MoneyAsync(CentralName, "SELECT COALESCE(SUM(Amount), 0) FROM dbo.CustomerCreditLedger WHERE TransactionType = N'Repayment';");
            var allocations = await MoneyAsync(CentralName, "SELECT COALESCE(SUM(Amount), 0) FROM dbo.CreditPaymentAllocations;");
            var collected = await MoneyAsync(originName, "SELECT COALESCE(SUM(Amount), 0) FROM dbo.BranchDebtCollections WHERE Status = N'Completed';")
                + await MoneyAsync(otherName, "SELECT COALESCE(SUM(Amount), 0) FROM dbo.BranchDebtCollections WHERE Status = N'Completed';");
            Assert.Equal(ledger, allocations);
            Assert.Equal(ledger, collected);
            Assert.Equal(800m, await MoneyAsync(originName, "SELECT Total FROM dbo.Sales WHERE SaleID = '" + sale + "';"));
        }
        finally
        {
            await DropAsync(CentralName);
            await DropAsync(originName);
            await DropAsync(otherName);
        }
    }

    private static async Task<CreditCollectionOutcome> RunAsync(string branchDatabase, CreditCollectionRequest request)
    {
        var service = new CreditCollectionService();
        await using var central = new SqlConnection(Connection(CentralName));
        await using var branch = new SqlConnection(Connection(branchDatabase));
        await central.OpenAsync();
        await branch.OpenAsync();
        return await service.CollectAsync(central, branch, request, true, false, false, CancellationToken.None);
    }

    private static CreditCollectionRequest Request(BranchSeed seed, Guid customerId, decimal amount, Guid? receivableId) =>
        new(Guid.NewGuid(), customerId, seed.BranchId, seed.DeviceId, seed.UserId, seed.SessionId, amount, "Cash", receivableId);

    private static async Task<BranchSeed> SeedBranchAsync(string database, Guid businessId, Guid branchId, string code)
    {
        var station = Guid.NewGuid();
        var device = Guid.NewGuid();
        var closedDevice = Guid.NewGuid();
        var user = Guid.NewGuid();
        var denied = Guid.NewGuid();
        var session = Guid.NewGuid();
        var closed = Guid.NewGuid();
        await ExecuteAsync(Connection(database), """
            INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Collect', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt) VALUES (@branch, @business, N'00', N'Branch', N'Retail', 1, SYSUTCDATETIME());
            INSERT INTO dbo.POSStations (StationID, BranchID, StationNumber, StationName, IsActive, CreatedAt) VALUES (@station, @branch, 1, N'Till', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Devices (DeviceID, BranchID, StationID, DeviceName, DeviceType, IsActive, RegisteredAt, CreatedAt) VALUES
                (@device, @branch, @station, N'Till', N'POS', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (@closedDevice, @branch, @station, N'Closed', N'POS', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive) VALUES
                (@user, N'cashier', N'CASHIER', N'Cashier', N'pending', 1),
                (@denied, N'denied', N'DENIED', N'Denied', N'pending', 1);
            INSERT INTO dbo.UserRoles (UserID, RoleID) SELECT @user, RoleID FROM dbo.Roles WHERE RoleCode = N'User';
            INSERT INTO dbo.POSCashSessions (CashSessionID, BusinessID, BranchID, POSStationID, DeviceID, OpenedByUserID, OpenedAt, OpeningCashAmount, Status)
            VALUES (@session, @business, @branch, @station, @device, @user, SYSUTCDATETIME(), 0, N'Open');
            INSERT INTO dbo.POSCashSessions (CashSessionID, BusinessID, BranchID, POSStationID, DeviceID, OpenedByUserID, OpenedAt, OpeningCashAmount, Status, ClosedByUserID, ClosedAt, ExpectedCashAmount, CountedCashAmount, CashDifference)
            VALUES (@closed, @business, @branch, @station, @closedDevice, @user, SYSUTCDATETIME(), 0, N'Closed', @user, SYSUTCDATETIME(), 0, 0, 0);
            """,
            ("@business", businessId), ("@branch", branchId), ("@station", station), ("@device", device), ("@closedDevice", closedDevice), ("@user", user), ("@denied", denied), ("@session", session), ("@closed", closed));
        await using var codeCommand = new SqlConnection(Connection(database));
        await codeCommand.OpenAsync();
        await using var update = new SqlCommand("UPDATE dbo.Branches SET BranchCode = @code WHERE BranchID = @branch;", codeCommand);
        update.Parameters.AddWithValue("@code", code);
        update.Parameters.AddWithValue("@branch", branchId);
        await update.ExecuteNonQueryAsync();
        return new BranchSeed(branchId, station, device, closedDevice, user, denied, session);
    }

    private static async Task MigrateAsync(string database)
    {
        var runner = new MigrationRunner(new SqlConnectionFactory(Options.Create(new SqlServerOptions { Server = "localhost", Database = database, ConnectTimeoutSeconds = 15 })), NullLogger<MigrationRunner>.Instance);
        await runner.ApplyPendingAsync(CancellationToken.None);
    }

    private sealed record BranchSeed(Guid BranchId, Guid StationId, Guid DeviceId, Guid ClosedDeviceId, Guid UserId, Guid DeniedUserId, Guid SessionId);

    private static async Task ExecuteAsync(string connectionString, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> IntAsync(string name, string sql)
    {
        await using var connection = new SqlConnection(Connection(name));
        await connection.OpenAsync();
        return Convert.ToInt32(await new SqlCommand(sql, connection).ExecuteScalarAsync());
    }

    private static async Task<decimal> MoneyAsync(string name, string sql)
    {
        await using var connection = new SqlConnection(Connection(name));
        await connection.OpenAsync();
        return Convert.ToDecimal(await new SqlCommand(sql, connection).ExecuteScalarAsync());
    }

    private static async Task<string> TextAsync(string name, string sql)
    {
        await using var connection = new SqlConnection(Connection(name));
        await connection.OpenAsync();
        return Convert.ToString(await new SqlCommand(sql, connection).ExecuteScalarAsync()) ?? "";
    }

    private static string Connection(string name) => new SqlConnectionStringBuilder { DataSource = "localhost", InitialCatalog = name, IntegratedSecurity = true, Encrypt = true, TrustServerCertificate = true, ApplicationName = "XtraPOS.Tests" }.ConnectionString;

    private static async Task CreateAsync(string name) => await MasterAsync($"IF DB_ID(N'{name}') IS NULL CREATE DATABASE [{name}] COLLATE Arabic_CI_AS;");

    private static async Task DropAsync(string name)
    {
        if (!name.Contains("_Test_CreditCollect", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing to drop a database outside the collection test.");
        SqlConnection.ClearAllPools();
        await MasterAsync($"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END");
    }

    private static async Task MasterAsync(string sql)
    {
        var master = new SqlConnectionStringBuilder { DataSource = "localhost", InitialCatalog = "master", IntegratedSecurity = true, Encrypt = true, TrustServerCertificate = true }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
