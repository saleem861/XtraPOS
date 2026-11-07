using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Credit;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Sales;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class FinancialDistributionTests
{
    private const string CentralName = "XtraPOS_Central_Test_CreditSync";
    private const string BranchName = "XtraPOS_Branch_Test_CreditSync";
    private const string OtherBranchName = "XtraPOS_Branch_Test_CreditSyncB";

    [Fact]
    public async Task Confirmed_debt_is_distributed_once_and_reversal_stays_append_only()
    {
        CentralDatabase.Require(CentralName);
        var branchName = TestDatabase.Require(BranchName);
        var otherName = TestDatabase.Require(OtherBranchName);
        await DropAsync(CentralName);
        await DropAsync(branchName);
        await DropAsync(otherName);
        try
        {
            await CreateAsync(CentralName);
            await CreateAsync(branchName);
            await CreateAsync(otherName);
            var centralCs = Connection(CentralName);
            var branchCs = Connection(branchName);
            var otherCs = Connection(otherName);
            await new CentralMigrationRunner().ApplyAsync(centralCs, CancellationToken.None);
            await new MigrationRunner(Factory(branchName), NullLogger<MigrationRunner>.Instance).ApplyPendingAsync(CancellationToken.None);
            await new MigrationRunner(Factory(otherName), NullLogger<MigrationRunner>.Instance).ApplyPendingAsync(CancellationToken.None);
            var graph = await SeedAsync(centralCs, branchCs, otherCs);
            await using var central = new SqlConnection(centralCs);
            await using var branch = new SqlConnection(branchCs);
            await using var other = new SqlConnection(otherCs);
            await central.OpenAsync();
            await branch.OpenAsync();
            await other.OpenAsync();

            Assert.False(await CreditOperationalDatabase.HasSalesAsync(central, CancellationToken.None));
            Assert.Null(await CreditOperationalDatabase.OpenRoutedBranchAsync(Factory(branchName), central, graph.BranchId, CancellationToken.None));
            await ExecuteAsync(centralCs, """
                INSERT INTO dbo.BranchSqlEndpoints
                    (BranchID, Host, Port, DatabaseName, AuthenticationMode, PrincipalName, TlsMode, Status)
                VALUES (@branch, N'localhost', 1433, N'XtraPOS_Branch_Test_CreditSync', N'Integrated', N'test', N'Required', N'Active');
                """, ("@branch", graph.BranchId));
            await using var routed = await CreditOperationalDatabase.OpenRoutedBranchAsync(Factory(branchName), central, graph.BranchId, CancellationToken.None);
            Assert.NotNull(routed);
            Assert.True(await CreditOperationalDatabase.HasSalesAsync(routed!, CancellationToken.None));
            Assert.Equal(0, await IntAsync(centralCs, "SELECT COUNT(*) FROM sys.tables WHERE name = N'Sales';"));

            await using var raceCentral = new SqlConnection(centralCs);
            await raceCentral.OpenAsync();
            var credit = new CreditReservationService();
            var races = await Task.WhenAll(
                credit.ReserveAsync(central, graph.RaceCustomerId, graph.BranchId, 80m, "race-a", DateTime.UtcNow.AddMinutes(10), CancellationToken.None),
                credit.ReserveAsync(raceCentral, graph.RaceCustomerId, Guid.NewGuid(), 80m, "race-b", DateTime.UtcNow.AddMinutes(10), CancellationToken.None));
            Assert.Equal(1, races.Count(item => item.Decision == CreditDecision.Approved));
            var refused = Assert.Single(races, item => item.Decision == CreditDecision.LimitExceeded);
            Assert.Equal(20m, refused.Available);

            var coordinator = Coordinator(branchName);
            var sale = Request(graph, 50m);
            var completed = await coordinator.StageAsync(central, branch, sale, true, false, false, null, CancellationToken.None);
            Assert.Equal(CreditSaleOutcome.Completed, completed.Outcome);
            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM dbo.CentralFinancialEvents WHERE OperationID = N'" + sale.OperationId.ToString("N") + "';"));
            Assert.Equal(CreditSaleOutcome.Completed, (await coordinator.DeliverAsync(central, branch, sale.OperationId, graph.ActorUserId, CancellationToken.None)).Outcome);
            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM dbo.CustomerReceivables WHERE SourceSaleID = '" + sale.SaleId + "';"));
            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM dbo.CentralFinancialEvents WHERE SourceSaleID = '" + sale.SaleId + "';"));

            var over = Request(graph, 80m);
            var rejected = await coordinator.StageAsync(central, branch, over, true, false, false, null, CancellationToken.None);
            Assert.Equal(CreditSaleOutcome.Refused, rejected.Outcome);
            Assert.Equal(50m, rejected.MaxDeferred);
            Assert.Equal(0, await IntAsync(branchCs, "SELECT COUNT(*) FROM dbo.Sales WHERE SaleID = '" + over.SaleId + "';"));
            var corrected = Request(graph, 20m) with { OperationId = over.OperationId, SaleId = over.SaleId };
            Assert.Equal(CreditSaleOutcome.Completed, (await coordinator.StageAsync(central, branch, corrected, true, false, false, null, CancellationToken.None)).Outcome);

            var pull = await FinancialInboxSync.PullAsync(central, other, CancellationToken.None);
            Assert.True(pull.Applied >= 1);
            Assert.Equal(0, await IntAsync(otherCs, "SELECT COUNT(*) FROM dbo.Sales;"));
            var again = await FinancialInboxSync.PullAsync(central, other, CancellationToken.None);
            Assert.Equal(0, again.Applied);
            Assert.Equal(pull.InboxCount, again.InboxCount);
            Assert.Equal(again.CentralCount, again.InboxCount);

            var foreign = Guid.NewGuid();
            await ExecuteAsync(centralCs, """
                INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Other', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES (@customer, @business, N'Other', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CentralFinancialEvents
                    (OperationID, EventType, BusinessID, CustomerID, OriginBranchID, Amount, SignedEffect, Payload)
                VALUES (N'foreign-event', N'CreditSale', @business, @customer, @branch, 5, 5, N'{}');
                """, ("@business", foreign), ("@customer", Guid.NewGuid()), ("@branch", graph.BranchId));
            await FinancialInboxSync.PullAsync(central, other, CancellationToken.None);
            Assert.Equal(0, await IntAsync(otherCs, "SELECT COUNT(*) FROM dbo.BranchFinancialInbox WHERE OperationID = N'foreign-event';"));

            var reversal = new CreditDebtReversalService();
            var reversed = await reversal.ReverseAsync(central, branch, Guid.NewGuid(), sale.SaleId, graph.ActorUserId, graph.BusinessId, "تصحيح", true, CancellationToken.None);
            Assert.Equal(CreditDebtReversalOutcome.Completed, reversed.Outcome);
            await FinancialInboxSync.PullAsync(central, other, CancellationToken.None);
            Assert.Equal(1, await IntAsync(otherCs, "SELECT COUNT(*) FROM dbo.BranchFinancialInbox WHERE EventType = N'DebtReversal';"));
            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE SourceSaleID = '" + sale.SaleId + "' AND TransactionType = N'CreditSale';"));
            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE SourceSaleID = '" + sale.SaleId + "' AND TransactionType = N'Reversal';"));
            Assert.Equal("Reversed", await TextAsync(centralCs, "SELECT Status FROM dbo.CustomerReceivables WHERE SourceSaleID = '" + sale.SaleId + "';"));
            var duplicate = await reversal.ReverseAsync(central, branch, Guid.NewGuid(), sale.SaleId, graph.ActorUserId, graph.BusinessId, "مرة ثانية", true, CancellationToken.None);
            Assert.Equal(CreditDebtReversalOutcome.AlreadyReversed, duplicate.Outcome);
            await ExecuteAsync(centralCs, "UPDATE dbo.CustomerReceivables SET OutstandingAmount = 10, Status = N'Partial' WHERE SourceSaleID = '" + corrected.SaleId + "';");
            var paid = await reversal.ReverseAsync(central, branch, Guid.NewGuid(), corrected.SaleId, graph.ActorUserId, graph.BusinessId, "بعد تسديد", true, CancellationToken.None);
            Assert.Equal(CreditDebtReversalOutcome.HasPayments, paid.Outcome);
            var offline = await reversal.ReverseAsync(central, branch, Guid.NewGuid(), corrected.SaleId, graph.ActorUserId, graph.BusinessId, "انقطاع", false, CancellationToken.None);
            Assert.Equal(CreditDebtReversalOutcome.CentralUnavailable, offline.Outcome);
            await Assert.ThrowsAnyAsync<SqlException>(() => ExecuteAsync(centralCs, "UPDATE dbo.CustomerCreditLedger SET Reason = N'changed' WHERE SourceSaleID = '" + sale.SaleId + "';"));
        }
        finally
        {
            await DropAsync(CentralName);
            await DropAsync(branchName);
            await DropAsync(otherName);
        }
    }

    [Fact]
    public async Task Three_branches_share_one_debt_and_one_repayment()
    {
        CentralDatabase.Require(CentralName);
        var branchA = TestDatabase.Require(BranchName);
        var branchB = TestDatabase.Require(OtherBranchName);
        var branchC = TestDatabase.Require("XtraPOS_Branch_Test_CreditSyncC");
        var branchD = TestDatabase.Require("XtraPOS_Branch_Test_CreditSyncD");
        await DropAsync(CentralName);
        await DropAsync(branchA);
        await DropAsync(branchB);
        await DropAsync(branchC);
        await DropAsync(branchD);
        try
        {
            await CreateAsync(CentralName);
            await CreateAsync(branchA);
            await CreateAsync(branchB);
            await CreateAsync(branchC);
            await CreateAsync(branchD);
            foreach (var name in new[] { branchA, branchB, branchC, branchD })
            {
                await new MigrationRunner(Factory(name), NullLogger<MigrationRunner>.Instance).ApplyPendingAsync(CancellationToken.None);
            }

            await new CentralMigrationRunner().ApplyAsync(Connection(CentralName), CancellationToken.None);
            var graph = await SeedAsync(Connection(CentralName), Connection(branchA), Connection(branchB));
            await ExecuteAsync(Connection(branchC), "INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Sync', 1, SYSUTCDATETIME()); INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (N'BusinessID', CONVERT(nvarchar(36), @business));", ("@business", graph.BusinessId));
            var otherBranch = Guid.Parse(await TextAsync(Connection(branchB), "SELECT CONVERT(nvarchar(36), BranchID) FROM dbo.Branches;"));
            var station = Guid.NewGuid();
            var device = Guid.NewGuid();
            var actor = Guid.NewGuid();
            var session = Guid.NewGuid();
            await ExecuteAsync(Connection(branchB), """
                INSERT INTO dbo.POSStations (StationID, BranchID, StationNumber, StationName, IsActive, CreatedAt) VALUES (@station, @branch, 2, N'POS B', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Devices (DeviceID, BranchID, StationID, DeviceName, DeviceType, IsActive, RegisteredAt, CreatedAt) VALUES (@device, @branch, @station, N'Till B', N'POS', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive) VALUES (@actor, N'cashierb', N'CASHIERB', N'Cashier B', N'x', 1);
                INSERT INTO dbo.UserRoles (UserID, RoleID) SELECT @actor, RoleID FROM dbo.Roles WHERE RoleCode = N'User';
                INSERT INTO dbo.POSCashSessions (CashSessionID, BusinessID, BranchID, POSStationID, DeviceID, OpenedByUserID, OpenedAt, OpeningCashAmount, Status)
                VALUES (@session, @business, @branch, @station, @device, @actor, SYSUTCDATETIME(), 0, N'Open');
                """, ("@station", station), ("@device", device), ("@actor", actor), ("@session", session), ("@business", graph.BusinessId), ("@branch", otherBranch));
            await using var central = new SqlConnection(Connection(CentralName));
            await using var a = new SqlConnection(Connection(branchA));
            await using var b = new SqlConnection(Connection(branchB));
            await using var c = new SqlConnection(Connection(branchC));
            await central.OpenAsync();
            await a.OpenAsync();
            await b.OpenAsync();
            await c.OpenAsync();
            var sale = Request(graph, 50m);
            Assert.Equal(CreditSaleOutcome.Completed, (await Coordinator(branchA).StageAsync(central, a, sale, true, false, false, null, CancellationToken.None)).Outcome);
            Assert.Equal(1, await IntAsync(Connection(CentralName), "SELECT COUNT(*) FROM dbo.CentralFinancialEvents WHERE SourceSaleID = '" + sale.SaleId + "';"));
            await FinancialInboxSync.PullAsync(central, b, CancellationToken.None);
            await FinancialInboxSync.PullAsync(central, c, CancellationToken.None);
            var history = new CreditHistoryService();
            var remoteDocument = await history.DocumentAsync(null, c, graph.BusinessId, sale.SaleId, null, CancellationToken.None);
            Assert.Contains("Sync item", remoteDocument);
            var collect = new CreditCollectionService();
            var operation = Guid.NewGuid();
            var request = new CreditCollectionRequest(operation, graph.CustomerId, otherBranch, device, actor, session, 20m, "Cash", null);
            Assert.Equal(CreditCollectionOutcome.Completed, await collect.CollectAsync(central, b, request, true, false, false, CancellationToken.None));
            Assert.Equal(CreditCollectionOutcome.Completed, await collect.CollectAsync(central, b, request, true, false, false, CancellationToken.None));
            Assert.Equal(1, await IntAsync(Connection(CentralName), "SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE TransactionType = N'Repayment' AND CustomerID = '" + graph.CustomerId + "';"));
            Assert.Equal(30m, await DecimalAsync(Connection(CentralName), "SELECT SUM(OutstandingAmount) FROM dbo.CustomerReceivables WHERE CustomerID = '" + graph.CustomerId + "' AND Status IN (N'Open', N'Partial');"));
            await a.CloseAsync();
            await b.CloseAsync();
            await c.CloseAsync();
            await using var a2 = new SqlConnection(Connection(branchA));
            await using var b2 = new SqlConnection(Connection(branchB));
            await using var c2 = new SqlConnection(Connection(branchC));
            await a2.OpenAsync();
            await b2.OpenAsync();
            await c2.OpenAsync();
            await FinancialInboxSync.PullAsync(central, a2, CancellationToken.None);
            await FinancialInboxSync.PullAsync(central, b2, CancellationToken.None);
            Assert.True((await FinancialInboxSync.PullAsync(central, c2, CancellationToken.None)).Applied >= 1);
            var again = await FinancialInboxSync.PullAsync(central, c2, CancellationToken.None);
            Assert.Equal(0, again.Applied);
            foreach (var cs in new[] { Connection(branchA), Connection(branchB), Connection(branchC) })
            {
                Assert.Equal(1, await IntAsync(cs, "SELECT COUNT(*) FROM dbo.BranchFinancialInbox WHERE EventType = N'Repayment';"));
            }

            await ExecuteAsync(Connection(branchD), "INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Sync', 1, SYSUTCDATETIME()); INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (N'BusinessID', CONVERT(nvarchar(36), @business));", ("@business", graph.BusinessId));
            await using var d = new SqlConnection(Connection(branchD));
            await d.OpenAsync();
            var backfill = await FinancialInboxSync.PullAsync(central, d, CancellationToken.None);
            Assert.Equal(backfill.CentralCount, backfill.InboxCount);
            Assert.Equal(backfill.InboxCount, await IntAsync(Connection(branchD), "SELECT COUNT(*) FROM dbo.BranchFinancialInbox;"));
            await ExecuteAsync(Connection(branchC), "DELETE TOP (1) FROM dbo.BranchFinancialInbox;");
            var centralCount = await IntAsync(Connection(CentralName), "SELECT COUNT(*) FROM dbo.CentralFinancialEvents WHERE BusinessID = '" + graph.BusinessId + "';");
            var inboxCount = await IntAsync(Connection(branchC), "SELECT COUNT(*) FROM dbo.BranchFinancialInbox WHERE BusinessID = '" + graph.BusinessId + "';");
            Assert.NotEqual(centralCount, inboxCount);
        }
        finally
        {
            await DropAsync(CentralName);
            await DropAsync(branchA);
            await DropAsync(branchB);
            await DropAsync(branchC);
            await DropAsync(branchD);
        }
    }

    private static async Task<decimal> DecimalAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return Convert.ToDecimal(await new SqlCommand(sql, connection).ExecuteScalarAsync());
    }

    private static SqlConnectionFactory Factory(string database) =>
        new(Options.Create(new SqlServerOptions { Server = "localhost", Database = database, ConnectTimeoutSeconds = 15 }));

    private static CreditSaleCoordinator Coordinator(string database)
    {
        var connections = Factory(database);
        var protection = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        var endpoints = new SyncEndpointStore(connections, protection, new CentralConnectionFileStore(protection, Path.GetTempPath()), new TestHost());
        var pricing = new PricingResolver(connections, endpoints);
        var variable = new VariableValuePricingResolver(connections, endpoints);
        var stock = new StockPostingService(connections);
        var payments = new SalePaymentService(connections);
        var databaseContext = new BranchDbContext(new DbContextOptionsBuilder<BranchDbContext>().UseSqlServer(Connection(database)).Options);
        var checkout = new CheckoutService(connections, pricing, variable, stock, payments, new PermissionService(databaseContext));
        return new CreditSaleCoordinator(new CreditReservationService(), new CreditSaleDocumentWriter(checkout));
    }

    private static CreditSaleRequest Request(Seed graph, decimal total) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), graph.BusinessId, graph.BranchId, graph.StationId, graph.DeviceId, graph.CashSessionId, graph.CustomerId, graph.ActorUserId, Guid.Empty, total, 0m, total, [new CreditSaleLine(graph.ItemId, total / 10m)]);

    private static async Task<Seed> SeedAsync(string centralCs, string branchCs, string otherCs)
    {
        var business = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var station = Guid.NewGuid();
        var device = Guid.NewGuid();
        var session = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var race = Guid.NewGuid();
        var item = Guid.NewGuid();
        await ExecuteAsync(centralCs, """
            INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Sync', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES
                (@customer, @business, N'Buyer', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (@race, @business, N'Race', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.CustomerCreditPolicies (CustomerID, CreditEnabled, CreditLimit, PaymentTermDays, CreditStatus) VALUES
                (@customer, 1, 100, 30, N'Active'),
                (@race, 1, 100, 30, N'Active');
            """, ("@business", business), ("@customer", customer), ("@race", race));
        var branchSql = """
            INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Sync', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt) VALUES (@branch, @business, N'01', N'Branch', N'Retail', 1, SYSUTCDATETIME());
            INSERT INTO dbo.POSStations (StationID, BranchID, StationNumber, StationName, IsActive, CreatedAt) VALUES (@station, @branch, 1, N'POS', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Devices (DeviceID, BranchID, StationID, DeviceName, DeviceType, IsActive, RegisteredAt, CreatedAt) VALUES (@device, @branch, @station, N'Till', N'POS', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive) VALUES (@actor, N'cashier', N'CASHIER', N'Cashier', N'x', 1);
            INSERT INTO dbo.UserRoles (UserID, RoleID) SELECT @actor, RoleID FROM dbo.Roles WHERE RoleCode = N'User';
            INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
            SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
            FROM dbo.Roles AS role
            INNER JOIN dbo.Permissions AS permission ON permission.PermissionCode = N'Credit.Reverse'
            WHERE role.RoleCode = N'User'
              AND NOT EXISTS (SELECT 1 FROM dbo.RolePermissions AS existing WHERE existing.RoleID = role.RoleID AND existing.PermissionID = permission.PermissionID);
            INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES (@customer, @business, N'Buyer', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.POSCashSessions (CashSessionID, BusinessID, BranchID, POSStationID, DeviceID, OpenedByUserID, OpenedAt, OpeningCashAmount, Status)
            VALUES (@session, @business, @branch, @station, @device, @actor, SYSUTCDATETIME(), 0, N'Open');
            INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName, IsActive, IsProhibited) VALUES (@item, N'SY-1', N'Sync item', 1, 0);
            INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive)
            SELECT NEWID(), @item, PriceTypeID, @branch, 10, 1 FROM dbo.PriceTypes WHERE PriceCode = N'Price0';
            INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (N'BusinessID', CONVERT(nvarchar(36), @business)), (N'BranchID', CONVERT(nvarchar(36), @branch));
            """;
        await ExecuteAsync(branchCs, branchSql, ("@business", business), ("@branch", branch), ("@station", station), ("@device", device), ("@actor", actor), ("@customer", customer), ("@session", session), ("@item", item));
        await ExecuteAsync(otherCs, """
            INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Sync', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt) VALUES (@branch, @business, N'02', N'Other', N'Retail', 1, SYSUTCDATETIME());
            INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (N'BusinessID', CONVERT(nvarchar(36), @business));
            """, ("@business", business), ("@branch", Guid.NewGuid()));
        return new Seed(business, branch, station, device, session, actor, customer, race, item);
    }

    private sealed record Seed(Guid BusinessId, Guid BranchId, Guid StationId, Guid DeviceId, Guid CashSessionId, Guid ActorUserId, Guid CustomerId, Guid RaceCustomerId, Guid ItemId);

    private sealed class TestHost : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "XtraPOS.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
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
        return Convert.ToInt32(await new SqlCommand(sql, connection).ExecuteScalarAsync());
    }

    private static async Task<string> TextAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return Convert.ToString(await new SqlCommand(sql, connection).ExecuteScalarAsync()) ?? "";
    }

    private static string Connection(string name) => new SqlConnectionStringBuilder { DataSource = "localhost", InitialCatalog = name, IntegratedSecurity = true, Encrypt = true, TrustServerCertificate = true, ApplicationName = "XtraPOS.Tests" }.ConnectionString;

    private static async Task CreateAsync(string name) => await MasterAsync($"IF DB_ID(N'{name}') IS NULL CREATE DATABASE [{name}] COLLATE Arabic_CI_AS;");

    private static async Task DropAsync(string name)
    {
        if (!name.Contains("_Test_CreditSync", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing to drop a database outside the credit sync test.");
        SqlConnection.ClearAllPools();
        await MasterAsync($"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END");
    }

    private static async Task MasterAsync(string sql)
    {
        await using var connection = new SqlConnection("Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;");
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }
}
