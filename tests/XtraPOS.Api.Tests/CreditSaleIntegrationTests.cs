using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Credit;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using XtraPOS.Api.Data.Migrations;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class CreditSaleIntegrationTests
{
    private const string CentralName = "XtraPOS_Central_Test_CreditSale";
    private const string BranchName = "XtraPOS_Branch_Test_CreditSale";

    [Fact]
    public async Task Credit_sale_outbox_recovers_without_duplicate_financial_rows()
    {
        CentralDatabase.Require(CentralName);
        var branchName = TestDatabase.Require(BranchName);
        await DropAsync(CentralName);
        await DropAsync(branchName);
        try
        {
            await CreateAsync(CentralName);
            await CreateAsync(branchName);
            var centralCs = Connection(CentralName);
            var branchCs = Connection(branchName);
            await new CentralMigrationRunner().ApplyAsync(centralCs, CancellationToken.None);
            var runner = new MigrationRunner(new SqlConnectionFactory(Options.Create(new SqlServerOptions { Server = "localhost", Database = branchName, ConnectTimeoutSeconds = 15 })), NullLogger<MigrationRunner>.Instance);
            await runner.ApplyPendingAsync(CancellationToken.None);

            var graph = await SeedAsync(centralCs, branchCs);
            await using var central = new SqlConnection(centralCs);
            await using var branch = new SqlConnection(branchCs);
            await central.OpenAsync();
            await branch.OpenAsync();
            var coordinator = Coordinator();

            var full = Request(graph, graph.CustomerId, 300m, 0m, 300m);
            var completed = await coordinator.StageAsync(central, branch, full, true, false, false, null, CancellationToken.None);
            Assert.Equal(CreditSaleOutcome.Completed, completed.Outcome);
            Assert.Equal(300m, await MoneyAsync(centralCs, "SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE SourceSaleID = '" + full.SaleId + "';"));
            Assert.Equal(await MoneyAsync(branchCs, "SELECT SaleNumber FROM dbo.Sales WHERE SaleID = '" + full.SaleId + "';"), await MoneyAsync(centralCs, "SELECT SourceSaleNumber FROM dbo.CustomerReceivables WHERE SourceSaleID = '" + full.SaleId + "';"));
            Assert.Equal(0, await IntAsync(branchCs, "SELECT COUNT(*) FROM dbo.SalePayments WHERE SaleID = '" + full.SaleId + "';"));
            Assert.Equal(1, await IntAsync(branchCs, "SELECT COUNT(*) FROM dbo.SaleItems WHERE SaleID = '" + full.SaleId + "';"));
            Assert.Equal(1, await IntAsync(branchCs, "SELECT COUNT(*) FROM dbo.StockMovements WHERE SourceDocumentID = '" + full.SaleId + "';"));

            var partial = Request(graph, graph.PartialCustomerId, 600m, 200m, 400m);
            var partialResult = await coordinator.StageAsync(central, branch, partial, true, false, false, null, CancellationToken.None);
            Assert.Equal(CreditSaleOutcome.Completed, partialResult.Outcome);
            Assert.Equal(600m, await MoneyAsync(branchCs, "SELECT Total FROM dbo.Sales WHERE SaleID = '" + partial.SaleId + "';"));
            Assert.Equal(200m, await MoneyAsync(branchCs, "SELECT Amount FROM dbo.SalePayments WHERE SaleID = '" + partial.SaleId + "';"));
            Assert.Equal(400m, await MoneyAsync(centralCs, "SELECT OriginalAmount FROM dbo.CustomerReceivables WHERE SourceSaleID = '" + partial.SaleId + "';"));
            Assert.Equal(1, await IntAsync(branchCs, "SELECT COUNT(*) FROM dbo.SalePayments WHERE SaleID = '" + partial.SaleId + "';"));

            var limited = Request(graph, graph.CustomerId, 300m, 0m, 300m) with { BranchId = Guid.NewGuid() };
            var refused = await coordinator.StageAsync(central, branch, limited, true, false, false, null, CancellationToken.None);
            Assert.Equal(CreditSaleOutcome.Refused, refused.Outcome);

            var blocked = Request(graph, graph.BlockedCustomerId, 50m, 0m, 50m);
            Assert.Equal(CreditSaleOutcome.Refused, (await coordinator.StageAsync(central, branch, blocked, true, false, false, null, CancellationToken.None)).Outcome);

            var denied = Request(graph, graph.PartialCustomerId, 10m, 0m, 10m) with { ActorUserId = graph.UnauthorizedUserId };
            Assert.Equal(CreditSaleOutcome.Unauthorized, (await coordinator.StageAsync(central, branch, denied, true, false, false, null, CancellationToken.None)).Outcome);

            var rolled = Request(graph, graph.PartialCustomerId, 20m, 0m, 20m);
            var rolledResult = await coordinator.StageAsync(central, branch, rolled, true, true, false, null, CancellationToken.None);
            Assert.Equal(CreditSaleOutcome.LocalRolledBack, rolledResult.Outcome);
            Assert.Equal(0, await IntAsync(branchCs, "SELECT COUNT(*) FROM dbo.Sales WHERE SaleID = '" + rolled.SaleId + "';"));
            Assert.Equal("Expired", await TextAsync(centralCs, "SELECT Status FROM dbo.CreditAuthorizations WHERE AuthorizationID = '" + rolledResult.AuthorizationId + "';"));

            var pending = Request(graph, graph.PartialCustomerId, 30m, 0m, 30m);
            var parked = await coordinator.StageAsync(central, branch, pending, true, false, true, null, CancellationToken.None);
            Assert.Equal(CreditSaleOutcome.PendingCentral, parked.Outcome);
            Assert.Equal("CreditPending", await TextAsync(branchCs, "SELECT Status FROM dbo.Sales WHERE SaleID = '" + pending.SaleId + "';"));
            var restarted = Coordinator();
            Assert.Equal(CreditSaleOutcome.Completed, (await restarted.DeliverAsync(central, branch, pending.OperationId, graph.ActorUserId, CancellationToken.None)).Outcome);
            Assert.Equal(CreditSaleOutcome.Completed, (await restarted.DeliverAsync(central, branch, pending.OperationId, graph.ActorUserId, CancellationToken.None)).Outcome);
            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM dbo.CustomerReceivables WHERE SourceSaleID = '" + pending.SaleId + "';"));
            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE SourceSaleID = '" + pending.SaleId + "';"));
            Assert.Equal(1, await IntAsync(branchCs, "SELECT COUNT(*) FROM dbo.Sales WHERE SaleID = '" + pending.SaleId + "';"));

            var offline = Request(graph, graph.PartialCustomerId, 15m, 0m, 15m);
            Assert.Equal(CreditSaleOutcome.CentralUnavailable, (await coordinator.StageAsync(central, branch, offline, false, false, false, null, CancellationToken.None)).Outcome);
            Assert.Equal(0, await IntAsync(branchCs, "SELECT COUNT(*) FROM dbo.Sales WHERE SaleID = '" + offline.SaleId + "';"));
            await ExecuteAsync(branchCs, "INSERT INTO dbo.Sales (SaleID, BusinessID, BranchID, POSStationID, DeviceID, CartID, CustomerID, CashierUserID, CashSessionID, SaleNumber, Status, Subtotal, DiscountTotal, Total) VALUES (@sale, @business, @branch, @station, @device, NEWID(), @customer, @actor, @session, NEXT VALUE FOR dbo.SaleNumberSequence, N'Completed', 15, 0, 15);",
                ("@sale", Guid.NewGuid()), ("@business", graph.BusinessId), ("@branch", graph.BranchId), ("@station", graph.StationId), ("@device", graph.DeviceId), ("@customer", graph.PartialCustomerId), ("@actor", graph.ActorUserId), ("@session", graph.CashSessionId));
            Assert.Equal(1, await IntAsync(branchCs, "SELECT COUNT(*) FROM dbo.Sales WHERE Status = N'Completed' AND Total = 15;"));

            var expired = Request(graph, graph.PartialCustomerId, 25m, 0m, 25m);
            var reconciled = await coordinator.StageAsync(central, branch, expired, true, false, false, async () =>
            {
                await ExecuteAsync(centralCs, "UPDATE dbo.CreditAuthorizations SET Status = N'Expired' WHERE IdempotencyKey = N'" + expired.OperationId.ToString("N") + "';");
            }, CancellationToken.None);
            Assert.Equal(CreditSaleOutcome.ReconciliationRequired, reconciled.Outcome);
            Assert.Equal(0, await IntAsync(centralCs, "SELECT COUNT(*) FROM dbo.CustomerReceivables WHERE SourceSaleID = '" + expired.SaleId + "';"));

            var queued = Request(graph, graph.PartialCustomerId, 40m, 0m, 40m);
            Assert.Equal(CreditSaleOutcome.PendingCentral, (await coordinator.StageAsync(central, branch, queued, true, false, true, null, CancellationToken.None)).Outcome);
            var processor = new CreditOutboxProcessor(coordinator);
            Assert.Equal(CreditSaleOutcome.Completed, await processor.ProcessOnceAsync(central, branch, "worker-a", CancellationToken.None));
            Assert.Null(await processor.ProcessOnceAsync(central, branch, "worker-b", CancellationToken.None));
            var visible = await processor.ReadAsync(branch, graph.BranchId, queued.OperationId, CancellationToken.None);
            Assert.True(visible!.CentrallyConfirmed);
            Assert.Null(await processor.ReadAsync(branch, Guid.NewGuid(), queued.OperationId, CancellationToken.None));
            Assert.Equal(CreditSaleOutcome.Completed, (await coordinator.StageAsync(central, branch, queued, true, false, false, null, CancellationToken.None)).Outcome);
            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM dbo.CustomerReceivables WHERE SourceSaleID = '" + queued.SaleId + "';"));

            var leased = Request(graph, graph.PartialCustomerId, 12m, 0m, 12m);
            Assert.Equal(CreditSaleOutcome.PendingCentral, (await coordinator.StageAsync(central, branch, leased, true, false, true, null, CancellationToken.None)).Outcome);
            await ExecuteAsync(branchCs, "UPDATE dbo.BranchFinancialOutbox SET LeaseUntil = DATEADD(minute, 5, SYSUTCDATETIME()), LeaseOwner = N'crashed' WHERE CreditOperationID = @operation;", ("@operation", leased.OperationId));
            Assert.Null(await processor.ProcessOnceAsync(central, branch, "worker-c", CancellationToken.None));
            await ExecuteAsync(branchCs, "UPDATE dbo.BranchFinancialOutbox SET LeaseUntil = DATEADD(minute, -1, SYSUTCDATETIME()) WHERE CreditOperationID = @operation;", ("@operation", leased.OperationId));
            Assert.Equal(CreditSaleOutcome.Completed, await processor.ProcessOnceAsync(central, branch, "worker-d", CancellationToken.None));
            var raced = Request(graph, graph.PartialCustomerId, 11m, 0m, 11m);
            Assert.Equal(CreditSaleOutcome.PendingCentral, (await coordinator.StageAsync(central, branch, raced, true, false, true, null, CancellationToken.None)).Outcome);
            await using var centralTwo = new SqlConnection(centralCs);
            await using var branchTwo = new SqlConnection(branchCs);
            await centralTwo.OpenAsync();
            await branchTwo.OpenAsync();
            var racedResults = await Task.WhenAll(
                processor.ProcessOnceAsync(central, branch, "worker-e", CancellationToken.None),
                processor.ProcessOnceAsync(centralTwo, branchTwo, "worker-f", CancellationToken.None));
            Assert.Equal(1, racedResults.Count(item => item == CreditSaleOutcome.Completed));
            Assert.Equal(1, await IntAsync(centralCs, "SELECT COUNT(*) FROM dbo.CustomerReceivables WHERE SourceSaleID = '" + raced.SaleId + "';"));
            Assert.Contains(await processor.ListUnresolvedAsync(branch, graph.BranchId, CancellationToken.None), item => item.ReconciliationRequired);

            var forged = Request(graph, graph.PartialCustomerId, 10m, 0m, 10m) with { InvoiceTotal = 1m, DeferredAmount = 1m };
            Assert.Equal(CreditSaleOutcome.Refused, (await coordinator.StageAsync(central, branch, forged, true, false, false, null, CancellationToken.None)).Outcome);
            Assert.Equal(0, await IntAsync(branchCs, "SELECT COUNT(*) FROM dbo.Sales WHERE SaleID = '" + forged.SaleId + "';"));
            var foreignSession = Request(graph, graph.PartialCustomerId, 10m, 0m, 10m) with { CashSessionId = Guid.NewGuid() };
            Assert.Equal(CreditSaleOutcome.Refused, (await coordinator.StageAsync(central, branch, foreignSession, true, false, false, null, CancellationToken.None)).Outcome);

            var clash = Request(graph, graph.PartialCustomerId, 13m, 0m, 13m);
            Assert.Equal(CreditSaleOutcome.PendingCentral, (await coordinator.StageAsync(central, branch, clash, true, false, true, null, CancellationToken.None)).Outcome);
            await ExecuteAsync(branchCs, "UPDATE dbo.BranchFinancialOutbox SET Payload = N'0' WHERE CreditOperationID = @operation;", ("@operation", clash.OperationId));
            Assert.Equal(CreditSaleOutcome.ReconciliationRequired, (await coordinator.DeliverAsync(central, branch, clash.OperationId, graph.ActorUserId, CancellationToken.None)).Outcome);
            var clashError = await TextAsync(branchCs, "SELECT LastError FROM dbo.BranchFinancialOutbox WHERE CreditOperationID = '" + clash.OperationId + "';");
            Assert.DoesNotContain("Exception", clashError, StringComparison.OrdinalIgnoreCase);
            var retryAt = DateTime.Parse(await TextAsync(branchCs, "SELECT CONVERT(nvarchar(30), NextRetryAt, 126) FROM dbo.BranchFinancialOutbox WHERE CreditOperationID = '" + clash.OperationId + "';"));
            Assert.True(retryAt > DateTime.UtcNow);

            var fenced = Request(graph, graph.PartialCustomerId, 14m, 0m, 14m);
            Assert.Equal(CreditSaleOutcome.PendingCentral, (await coordinator.StageAsync(central, branch, fenced, true, false, true, null, CancellationToken.None)).Outcome);
            await ExecuteAsync(branchCs, "UPDATE dbo.BranchFinancialOutbox SET LeaseOwner = N'owner', LeaseUntil = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE CreditOperationID = @operation;", ("@operation", fenced.OperationId));
            Assert.Equal(CreditSaleOutcome.PendingCentral, (await coordinator.DeliverAsync(central, branch, fenced.OperationId, graph.ActorUserId, CancellationToken.None, true, "stale")).Outcome);
            Assert.Equal("CreditPending", await TextAsync(branchCs, "SELECT Status FROM dbo.Sales WHERE SaleID = '" + fenced.SaleId + "';"));
            Assert.Equal(CreditSaleOutcome.Completed, (await coordinator.DeliverAsync(central, branch, fenced.OperationId, graph.ActorUserId, CancellationToken.None, true, "owner")).Outcome);

            var recover = Request(graph, graph.PartialCustomerId, 16m, 0m, 16m);
            Assert.Equal(CreditSaleOutcome.PendingCentral, (await coordinator.StageAsync(central, branch, recover, true, false, true, null, CancellationToken.None)).Outcome);
            await using (var endpoint = new SqlConnection(branchCs))
            {
                await endpoint.OpenAsync();
                await using var fix = new SqlCommand("""
                    INSERT INTO dbo.SyncEndpoints
                        (EndpointID, DatabaseRole, LocationKind, ServerName, DatabaseName, AuthenticationType, EncryptConnection, TrustServerCertificate, Status)
                    VALUES (NEWID(), N'Central', N'Local', N'localhost', @name, N'Integrated', 1, 1, N'Ready');
                    """, endpoint);
                fix.Parameters.AddWithValue("@name", CentralName);
                await fix.ExecuteNonQueryAsync();
            }

            await using var host = new CreditHostFactory(branchName);
            using var client = host.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/pos/credit-sales", new { })).StatusCode);
            var recovered = false;
            for (var attempt = 0; attempt < 20 && !recovered; attempt++)
            {
                await Task.Delay(500);
                recovered = await TextAsync(branchCs, "SELECT Status FROM dbo.Sales WHERE SaleID = '" + recover.SaleId + "';") == "Completed";
            }

            Assert.True(recovered);
            var ledger = await MoneyAsync(centralCs, "SELECT ISNULL(SUM(CASE WHEN TransactionType = N'CreditSale' THEN Amount ELSE 0 END), 0) FROM dbo.CustomerCreditLedger;");
            var open = await MoneyAsync(centralCs, "SELECT ISNULL(SUM(OutstandingAmount), 0) FROM dbo.CustomerReceivables WHERE Status = N'Open';");
            Assert.Equal(ledger, open - 600m);
        }
        finally
        {
            await DropAsync(CentralName);
            await DropAsync(branchName);
        }
    }

    private static CreditSaleRequest Request(Seed graph, Guid customerId, decimal total, decimal immediate, decimal deferred)
    {
        return new CreditSaleRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), graph.BusinessId, graph.BranchId, graph.StationId, graph.DeviceId, graph.CashSessionId, customerId, graph.ActorUserId, Guid.Empty, total, immediate, deferred, [new CreditSaleLine(graph.ItemId, total / 10m)]);
    }

    private static async Task<Seed> SeedAsync(string centralCs, string branchCs)
    {
        var business = Guid.NewGuid();
        var branch = Guid.NewGuid();
        var station = Guid.NewGuid();
        var device = Guid.NewGuid();
        var session = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var customer = Guid.NewGuid();
        var partial = Guid.NewGuid();
        var blocked = Guid.NewGuid();
        var item = Guid.NewGuid();
        await ExecuteAsync(centralCs, """
            INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Credit sale', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES
                (@customer, @business, N'Buyer', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (@partial, @business, N'Partial', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (@blocked, @business, N'Blocked', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.CustomerCreditPolicies (CustomerID, CreditEnabled, CreditLimit, PaymentTermDays, CreditStatus) VALUES
                (@customer, 1, 1000, 30, N'Active'),
                (@partial, 1, 1000, 30, N'Active'),
                (@blocked, 0, 0, 30, N'Inactive');
            INSERT INTO dbo.CustomerReceivables (ReceivableID, CustomerID, SourceSaleID, SourceBranchID, OriginalAmount, OutstandingAmount, DueDate, CurrencyCode, Status)
            VALUES (NEWID(), @customer, NEWID(), @branch, 600, 600, DATEADD(day, 30, CAST(SYSUTCDATETIME() AS date)), N'ILS', N'Open');
            """, ("@business", business), ("@customer", customer), ("@partial", partial), ("@blocked", blocked), ("@branch", branch));
        await ExecuteAsync(branchCs, """
            INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Credit sale', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt) VALUES (@branch, @business, N'01', N'Branch', N'Retail', 1, SYSUTCDATETIME());
            INSERT INTO dbo.POSStations (StationID, BranchID, StationNumber, StationName, IsActive, CreatedAt) VALUES (@station, @branch, 1, N'POS', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Devices (DeviceID, BranchID, StationID, DeviceName, DeviceType, IsActive, RegisteredAt, CreatedAt) VALUES (@device, @branch, @station, N'Till', N'POS', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive) VALUES
                (@actor, N'cashier', N'CASHIER', N'Cashier', N'x', 1),
                (@stranger, N'stranger', N'STRANGER', N'Stranger', N'x', 1);
            INSERT INTO dbo.UserRoles (UserID, RoleID)
            SELECT @actor, RoleID FROM dbo.Roles WHERE RoleCode = N'User';
            INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES
                (@customer, @business, N'Buyer', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (@partial, @business, N'Partial', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (@blocked, @business, N'Blocked', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.POSCashSessions (CashSessionID, BusinessID, BranchID, POSStationID, DeviceID, OpenedByUserID, OpenedAt, OpeningCashAmount, Status)
            VALUES (@session, @business, @branch, @station, @device, @actor, SYSUTCDATETIME(), 0, N'Open');
            INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName, IsActive, IsProhibited) VALUES (@item, N'CR-1', N'Credit item', 1, 0);
            INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive)
            SELECT NEWID(), @item, PriceTypeID, @branch, 10, 1 FROM dbo.PriceTypes WHERE PriceCode = N'Price0';
            INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (N'BusinessID', CONVERT(nvarchar(36), @business)), (N'BranchID', CONVERT(nvarchar(36), @branch));
            """, ("@business", business), ("@branch", branch), ("@station", station), ("@device", device), ("@actor", actor), ("@stranger", stranger), ("@customer", customer), ("@partial", partial), ("@blocked", blocked), ("@session", session), ("@item", item));
        return new Seed(business, branch, station, device, session, actor, stranger, customer, partial, blocked, item);
    }

    private static CreditSaleCoordinator Coordinator()
    {
        var connections = new SqlConnectionFactory(Options.Create(new SqlServerOptions { Server = "localhost", Database = BranchName, ConnectTimeoutSeconds = 15 }));
        var protection = new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider();
        var endpoints = new XtraPOS.Api.Sync.SyncEndpointStore(connections, protection, new XtraPOS.Api.Sync.CentralConnectionFileStore(protection, Path.GetTempPath()));
        var pricing = new XtraPOS.Api.Catalog.PricingResolver(connections, endpoints);
        var variable = new XtraPOS.Api.Catalog.VariableValuePricingResolver(connections, endpoints);
        var stock = new XtraPOS.Api.Catalog.StockPostingService(connections);
        var payments = new XtraPOS.Api.Sales.SalePaymentService(connections);
        var database = new BranchDbContext(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<BranchDbContext>().UseSqlServer(Connection(BranchName)).Options);
        var checkout = new XtraPOS.Api.Sales.CheckoutService(connections, pricing, variable, stock, payments, new XtraPOS.Api.Security.PermissionService(database));
        return new CreditSaleCoordinator(new CreditReservationService(), new CreditSaleDocumentWriter(checkout));
    }

    private sealed record Seed(Guid BusinessId, Guid BranchId, Guid StationId, Guid DeviceId, Guid CashSessionId, Guid ActorUserId, Guid UnauthorizedUserId, Guid CustomerId, Guid PartialCustomerId, Guid BlockedCustomerId, Guid ItemId);

    private sealed class CreditHostFactory(string database) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "XtraPOS.Api")));
            builder.UseSetting("SqlServer:Server", "localhost");
            builder.UseSetting("SqlServer:Database", database);
            builder.UseSetting("CreditSales:Enabled", "false");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SqlServer:Server"] = "localhost",
                    ["SqlServer:Database"] = database,
                    ["CreditSales:Enabled"] = "false",
                });
            });
        }
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

    private static async Task<decimal> MoneyAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return Convert.ToDecimal(await new SqlCommand(sql, connection).ExecuteScalarAsync());
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
        if (!name.Contains("_Test_CreditSale", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing to drop a database outside the credit sale test.");
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
