using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using XtraPOS.Api.Security;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class CreditHttpAuthenticationTests
{
    private const string BranchName = "XtraPOS_Branch_Test_CreditHttp";
    private const string CentralName = "XtraPOS_Central_Test_CreditHttp";

    [Fact]
    public async Task Authenticated_credit_http_enforces_permission_price_and_scope()
    {
        TestDatabase.Require(BranchName);
        CentralDatabase.Require(CentralName);
        await DropAsync(BranchName);
        await DropAsync(CentralName);
        try
        {
            await CreateAsync(CentralName);
            await CreateAsync(BranchName);
            await new CentralMigrationRunner().ApplyAsync(Connection(CentralName), CancellationToken.None);
            var branchConnections = new SqlConnectionFactory(Microsoft.Extensions.Options.Options.Create(new XtraPOS.Api.Configuration.SqlServerOptions { Server = "localhost", Database = BranchName, ConnectTimeoutSeconds = 15 }));
            await new XtraPOS.Api.Data.Migrations.MigrationRunner(branchConnections, Microsoft.Extensions.Logging.Abstractions.NullLogger<XtraPOS.Api.Data.Migrations.MigrationRunner>.Instance).ApplyPendingAsync(CancellationToken.None);
            var deviceId = Guid.NewGuid();
            var deviceToken = "till-token";
            var adminId = Guid.NewGuid();
            var businessId = Guid.NewGuid();
            var branchId = Guid.NewGuid();
            var stationId = Guid.NewGuid();
            var branchCs = Connection(BranchName);
            var adminHash = new PasswordService().Hash(new AppUser { UserId = adminId, Username = "admin" }, "Admin1234");
            await ExecuteAsync(branchCs, """
                INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Credit HTTP', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt) VALUES (@branch, @business, N'01', N'Credit', N'Retail', 1, SYSUTCDATETIME());
                INSERT INTO dbo.POSStations (StationID, BranchID, StationNumber, StationName, IsActive, CreatedAt) VALUES (@station, @branch, 1, N'Till', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Devices (DeviceID, BranchID, StationID, DeviceName, DeviceType, IsActive, RegisteredAt, CreatedAt) VALUES (@device, @branch, @station, N'Till', N'POS', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive) VALUES (@admin, N'admin', N'ADMIN', N'Admin', N'pending', 1);
                INSERT INTO dbo.UserRoles (UserID, RoleID) SELECT @admin, RoleID FROM dbo.Roles WHERE RoleCode = N'Admin';
                INSERT INTO dbo.UserBranchAccess (UserID, BranchID) VALUES (@admin, @branch);
                """, ("@business", businessId), ("@branch", branchId), ("@station", stationId), ("@device", deviceId), ("@admin", adminId));
            await using (var identity = new SqlConnection(branchCs))
            {
                await identity.OpenAsync();
                await using var hash = new SqlCommand("UPDATE dbo.Users SET PasswordHash = @hash WHERE UserID = @user; UPDATE dbo.Devices SET DeviceTokenHash = @token WHERE DeviceID = @device;", identity);
                hash.Parameters.AddWithValue("@hash", adminHash);
                hash.Parameters.Add("@token", System.Data.SqlDbType.VarBinary, 64).Value = DeviceTokenService.Hash(deviceToken);
                hash.Parameters.AddWithValue("@user", adminId);
                hash.Parameters.AddWithValue("@device", deviceId);
                await hash.ExecuteNonQueryAsync();
            }

            var customerId = Guid.NewGuid();
            var limitedId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var sessionId = Guid.NewGuid();
            await ExecuteAsync(Connection(CentralName), """
                INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Credit HTTP', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES
                    (@customer, @business, N'Regular', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (@limited, @business, N'Limited', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CustomerCreditPolicies (CustomerID, CreditEnabled, CreditLimit, PaymentTermDays, CreditStatus) VALUES
                    (@customer, 1, 10000, 30, N'Active'),
                    (@limited, 1, 1, 30, N'Active');
                """, ("@business", businessId), ("@customer", customerId), ("@limited", limitedId));
            await ExecuteAsync(branchCs, """
                INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES
                    (@customer, @business, N'Regular', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                    (@limited, @business, N'Limited', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName, IsActive, IsProhibited) VALUES (@item, N'HTTP-1', N'Priced', 1, 0);
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive)
                SELECT NEWID(), @item, PriceTypeID, @branch, CASE PriceCode
                    WHEN N'Price0' THEN 5.50 WHEN N'Retail' THEN 5.50 WHEN N'Price3' THEN 4.84
                    WHEN N'Price2' THEN 4.51 WHEN N'Price1' THEN 4.29 WHEN N'Dealer' THEN 4.00
                    WHEN N'SpecialCustomer' THEN 3.50 WHEN N'MajorCustomer' THEN 3.00 ELSE 5.50 END, 1
                FROM dbo.PriceTypes;
                INSERT INTO dbo.POSCashSessions (CashSessionID, BusinessID, BranchID, POSStationID, DeviceID, OpenedByUserID, OpenedAt, OpeningCashAmount, Status)
                VALUES (@session, @business, @branch, @station, @device, @admin, SYSUTCDATETIME(), 0, N'Open');
                INSERT INTO dbo.SyncEndpoints (EndpointID, DatabaseRole, LocationKind, ServerName, DatabaseName, AuthenticationType, EncryptConnection, TrustServerCertificate, Status)
                VALUES (NEWID(), N'Central', N'Local', N'localhost', N'pending', N'Integrated', 1, 1, N'Ready');
                INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (N'BusinessID', CONVERT(nvarchar(36), @business)), (N'BranchID', CONVERT(nvarchar(36), @branch));
                """,
                ("@customer", customerId), ("@limited", limitedId), ("@business", businessId), ("@item", itemId),
                ("@branch", branchId), ("@station", stationId), ("@device", deviceId), ("@admin", adminId), ("@session", sessionId));
            await using (var fix = new SqlConnection(branchCs))
            {
                await fix.OpenAsync();
                await using var name = new SqlCommand("UPDATE dbo.SyncEndpoints SET DatabaseName = @name WHERE DatabaseRole = N'Central';", fix);
                name.Parameters.AddWithValue("@name", CentralName);
                await name.ExecuteNonQueryAsync();
            }

            await using var disabled = new CreditHttpFactory(BranchName, false);
            await using var enabled = new CreditHttpFactory(BranchName, true);
            using var anon = enabled.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/pos/credit-sales", new { })).StatusCode);

            using var deniedClient = disabled.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
            deniedClient.DefaultRequestHeaders.Add("X-Device-Id", deviceId.ToString());
            deniedClient.DefaultRequestHeaders.Add("X-Device-Token", deviceToken);
            var deniedLogin = await deniedClient.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "Admin1234" });
            Assert.Equal(HttpStatusCode.OK, deniedLogin.StatusCode);
            Assert.Contains("samesite=strict", deniedLogin.Headers.GetValues("Set-Cookie").First(), StringComparison.OrdinalIgnoreCase);
            var disabledPost = await deniedClient.PostAsJsonAsync("/api/pos/credit-sales", Body(Guid.NewGuid(), customerId, sessionId, itemId, 1m, 5.50m, 0m));
            Assert.Equal(HttpStatusCode.Forbidden, disabledPost.StatusCode);
            Assert.Contains("CREDIT_DISABLED", await disabledPost.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            using var client = enabled.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
            client.DefaultRequestHeaders.Add("X-Device-Id", deviceId.ToString());
            client.DefaultRequestHeaders.Add("X-Device-Token", deviceToken);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/auth/login", new { username = "admin", password = "Admin1234" })).StatusCode);

            client.DefaultRequestHeaders.Remove("Origin");
            var missingOrigin = await client.PostAsJsonAsync("/api/pos/credit-sales", Body(Guid.NewGuid(), customerId, sessionId, itemId, 1m, 5.50m, 0m));
            Assert.Equal(HttpStatusCode.Forbidden, missingOrigin.StatusCode);
            Assert.Contains("MISSING_ORIGIN", await missingOrigin.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://branch.example");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
            var sameSite = await client.PostAsJsonAsync("/api/pos/credit-sales", Body(Guid.NewGuid(), customerId, sessionId, itemId, 1m, 5.50m, 0m));
            Assert.Contains("UNTRUSTED_ORIGIN", await sameSite.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            client.DefaultRequestHeaders.Remove("Origin");
            client.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
            var crossSite = await client.PostAsJsonAsync("/api/pos/credit-sales", Body(Guid.NewGuid(), customerId, sessionId, itemId, 1m, 5.50m, 0m));
            Assert.Contains("CROSS_SITE_REJECTED", await crossSite.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            client.DefaultRequestHeaders.Remove("Sec-Fetch-Site");
            client.DefaultRequestHeaders.Remove("Origin");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "http://127.0.0.1:5176");
            var proxy = await client.PostAsJsonAsync("/api/pos/credit-sales", Body(Guid.NewGuid(), customerId, sessionId, itemId, 1m, 5.50m, 0m));
            Assert.True(proxy.StatusCode == HttpStatusCode.OK, await proxy.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Remove("Origin");
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", client.BaseAddress.GetLeftPart(UriPartial.Authority));

            var scaleId = Guid.NewGuid();
            await ExecuteAsync(branchCs, """
                INSERT INTO dbo.Items (ItemID, ItemNumber, ItemName, IsActive, IsProhibited) VALUES (@item, N'1300', N'Scale', 1, 0);
                INSERT INTO dbo.ItemPrices (ItemPriceID, ItemID, PriceTypeID, BranchID, Price, IsActive)
                SELECT NEWID(), @item, PriceTypeID, NULL, 10, 1 FROM dbo.PriceTypes WHERE PriceCode = N'Price0';
                """, ("@item", scaleId));
            var missingWeight = await client.PostAsJsonAsync("/api/pos/credit-sales", new { operationId = Guid.NewGuid(), customerId, cashSessionId = sessionId, invoiceTotal = 20m, immediatePayment = 0m, lines = new[] { new { itemId = scaleId, quantity = 1m, lineType = "VariableValueByWeight", enteredScaleValue = 20m } } });
            Assert.Equal(HttpStatusCode.Forbidden, missingWeight.StatusCode);
            var wrongPrice = await client.PostAsJsonAsync("/api/pos/credit-sales", WeightBody(Guid.NewGuid(), customerId, sessionId, 20m, 20m, 9m, 2m, 2m));
            Assert.Equal(HttpStatusCode.Forbidden, wrongPrice.StatusCode);
            var wrongStock = await client.PostAsJsonAsync("/api/pos/credit-sales", WeightBody(Guid.NewGuid(), customerId, sessionId, 20m, 20m, 10m, 2m, 9m));
            Assert.Equal(HttpStatusCode.Forbidden, wrongStock.StatusCode);
            var weighedId = Guid.NewGuid();
            var weighed = await client.PostAsJsonAsync("/api/pos/credit-sales", WeightBody(weighedId, customerId, sessionId, 20m, 20m, 10m, 2m, 2m));
            Assert.True(weighed.StatusCode == HttpStatusCode.OK, await weighed.Content.ReadAsStringAsync());
            Assert.Equal(20m, await MoneyAsync(Connection(CentralName), "SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE OriginalAmount = 20;"));
            Assert.Equal(1, await IntAsync(branchCs, "SELECT COUNT(*) FROM dbo.StockMovements WHERE SourceDocumentID = (SELECT SaleID FROM dbo.Sales WHERE Total = 20);"));
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/pos/credit-sales", WeightBody(weighedId, customerId, sessionId, 20m, 20m, 10m, 2m, 2m))).StatusCode);
            Assert.Equal(1, await IntAsync(branchCs, "SELECT COUNT(*) FROM dbo.StockMovements WHERE SourceDocumentID = (SELECT SaleID FROM dbo.Sales WHERE Total = 20);"));

            var operationId = Guid.NewGuid();
            var created = await client.PostAsJsonAsync("/api/pos/credit-sales", Body(operationId, customerId, sessionId, itemId, 2m, 11.00m, 1.00m, actorUserId: Guid.NewGuid(), branchId: Guid.NewGuid()));
            Assert.True(created.StatusCode == HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
            Assert.Equal(adminId, await GuidAsync(branchCs, "SELECT CashierUserID FROM dbo.Sales WHERE Total = 11;"));
            Assert.Equal(branchId, await GuidAsync(branchCs, "SELECT BranchID FROM dbo.Sales WHERE Total = 11;"));
            Assert.Equal(deviceId, await GuidAsync(branchCs, "SELECT DeviceID FROM dbo.Sales WHERE Total = 11;"));
            Assert.Equal(sessionId, await GuidAsync(branchCs, "SELECT CashSessionID FROM dbo.Sales WHERE Total = 11;"));
            Assert.Equal(1.00m, await MoneyAsync(branchCs, "SELECT Amount FROM dbo.SalePayments WHERE SaleID = (SELECT SaleID FROM dbo.Sales WHERE Total = 11);"));
            Assert.Equal(10.00m, await MoneyAsync(Connection(CentralName), "SELECT OriginalAmount FROM dbo.CustomerReceivables WHERE OriginalAmount = 10;"));
            Assert.Equal(1, await IntAsync(branchCs, "SELECT COUNT(*) FROM dbo.StockMovements WHERE SourceDocumentID = (SELECT SaleID FROM dbo.Sales WHERE Total = 11);"));
            var replay = await client.PostAsJsonAsync("/api/pos/credit-sales", Body(operationId, customerId, sessionId, itemId, 2m, 11.00m, 1.00m));
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(1, await IntAsync(Connection(CentralName), "SELECT COUNT(*) FROM dbo.CustomerReceivables WHERE OriginalAmount = 10;"));
            var clash = await client.PostAsJsonAsync("/api/pos/credit-sales", Body(operationId, customerId, sessionId, itemId, 2m, 99m, 0m));
            Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);

            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/pos/credit-sales", Body(Guid.NewGuid(), customerId, Guid.NewGuid(), itemId, 1m, 5.50m, 0m))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/pos/credit-sales", Body(Guid.NewGuid(), customerId, sessionId, itemId, 1m, 1m, 0m))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/pos/credit-sales", Body(Guid.NewGuid(), limitedId, sessionId, itemId, 1m, 5.50m, 0m))).StatusCode);

            client.DefaultRequestHeaders.Remove("X-Device-Id");
            client.DefaultRequestHeaders.Add("X-Device-Id", Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/pos/credit-sales", Body(Guid.NewGuid(), customerId, sessionId, itemId, 1m, 5.50m, 0m))).StatusCode);
            client.DefaultRequestHeaders.Remove("X-Device-Id");
            client.DefaultRequestHeaders.Add("X-Device-Id", deviceId.ToString());

            var noCredit = await InsertUserAsync(enabled, "nocredit", "NoCredit123", businessId, branchId, null);
            using var noCreditClient = enabled.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
            noCreditClient.DefaultRequestHeaders.Add("X-Device-Id", deviceId.ToString());
            noCreditClient.DefaultRequestHeaders.Add("X-Device-Token", deviceToken);
            Assert.Equal(HttpStatusCode.OK, (await noCreditClient.PostAsJsonAsync("/api/auth/login", new { username = "nocredit", password = "NoCredit123" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await noCreditClient.PostAsJsonAsync("/api/pos/credit-sales", Body(Guid.NewGuid(), customerId, sessionId, itemId, 1m, 5.50m, 0m))).StatusCode);
            _ = noCredit;

            await AssertPriceAsync(client, branchCs, businessId, branchId, customerId, sessionId, itemId, null, 1m, 5.50m);
            await AssertPriceAsync(client, branchCs, businessId, branchId, customerId, sessionId, itemId, "Price3", 1m, 4.84m);
            await AssertPriceAsync(client, branchCs, businessId, branchId, customerId, sessionId, itemId, "Price2", 1m, 4.51m);
            await AssertPriceAsync(client, branchCs, businessId, branchId, customerId, sessionId, itemId, "Price1", 1m, 4.29m);
            await AssertPriceAsync(client, branchCs, businessId, branchId, customerId, sessionId, itemId, "Dealer", 1m, 4.00m);
            await AssertPriceAsync(client, branchCs, businessId, branchId, customerId, sessionId, itemId, "SpecialCustomer", 1m, 3.50m);
            await AssertPriceAsync(client, branchCs, businessId, branchId, customerId, sessionId, itemId, "MajorCustomer", 1m, 3.00m);
            await AssertPriceAsync(client, branchCs, businessId, branchId, customerId, sessionId, itemId, "Retail", 1m, 5.50m);

            var specialCustomer = Guid.NewGuid();
            await ExecuteAsync(Connection(CentralName), """
                INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES (@customer, @business, N'Special', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CustomerCreditPolicies (CustomerID, CreditEnabled, CreditLimit, PaymentTermDays, CreditStatus) VALUES (@customer, 1, 10000, 30, N'Active');
                """, ("@customer", specialCustomer), ("@business", businessId));
            await ExecuteAsync(branchCs, """
                INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES (@customer, @business, N'Special', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CustomerItemSpecialPrices (CustomerItemSpecialPriceID, BusinessID, CustomerID, ItemID, SpecialPrice, MinimumQuantity, BundlePrice, IsActive)
                VALUES (NEWID(), @business, @customer, @item, 4.50, 10, 45, 1);
                """, ("@customer", specialCustomer), ("@business", businessId), ("@item", itemId));
            await AssertPriceAsync(client, branchCs, businessId, branchId, specialCustomer, sessionId, itemId, null, 9m, 49.50m);
            await AssertPriceAsync(client, branchCs, businessId, branchId, specialCustomer, sessionId, itemId, null, 10m, 45.00m);
            await AssertPriceAsync(client, branchCs, businessId, branchId, specialCustomer, sessionId, itemId, null, 15m, 67.50m);

            var manual = await client.PostAsJsonAsync("/api/pos/credit-sales", new
            {
                operationId = Guid.NewGuid(),
                customerId,
                cashSessionId = sessionId,
                invoiceTotal = 3.00m,
                immediatePayment = 0m,
                lines = new[] { new { itemId, quantity = 1m, priceOverrideMode = "UnitPrice", manualUnitPrice = 3.00m } },
            });
            Assert.Equal(HttpStatusCode.OK, manual.StatusCode);
            Assert.Equal(3.00m, await MoneyAsync(branchCs, "SELECT TOP (1) UnitPrice FROM dbo.SaleItems WHERE PriceOverrideMode = N'UnitPrice' ORDER BY CreatedAt DESC;"));

            var closedSession = Guid.NewGuid();
            await ExecuteAsync(branchCs, """
                INSERT INTO dbo.POSCashSessions (CashSessionID, BusinessID, BranchID, POSStationID, DeviceID, OpenedByUserID, OpenedAt, ClosedByUserID, ClosedAt, OpeningCashAmount, ExpectedCashAmount, CountedCashAmount, CashDifference, Status)
                VALUES (@session, @business, @branch, @station, @device, @admin, DATEADD(hour, -2, SYSUTCDATETIME()), @admin, DATEADD(hour, -1, SYSUTCDATETIME()), 0, 0, 0, 0, N'Closed');
                """, ("@session", closedSession), ("@business", businessId), ("@branch", branchId), ("@station", stationId), ("@device", deviceId), ("@admin", adminId));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/pos/credit-sales", Body(Guid.NewGuid(), customerId, closedSession, itemId, 1m, 5.50m, 0m))).StatusCode);

            var foreignBranch = Guid.NewGuid();
            var foreignStation = Guid.NewGuid();
            var foreignDevice = Guid.NewGuid();
            var foreignSession = Guid.NewGuid();
            var foreignSale = Guid.NewGuid();
            var foreignOperation = Guid.NewGuid();
            await ExecuteAsync(branchCs, """
                INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
                VALUES (@branch, @business, N'99', N'Other', N'Retail', 1, SYSUTCDATETIME());
                INSERT INTO dbo.POSStations (StationID, BranchID, StationNumber, StationName, IsActive, CreatedAt) VALUES (@station, @branch, 1, N'Other', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Devices (DeviceID, BranchID, StationID, DeviceName, DeviceType, IsActive, RegisteredAt, CreatedAt) VALUES (@device, @branch, @station, N'Other', N'POS', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.POSCashSessions (CashSessionID, BusinessID, BranchID, POSStationID, DeviceID, OpenedByUserID, OpenedAt, OpeningCashAmount, Status)
                VALUES (@session, @business, @branch, @station, @device, @admin, SYSUTCDATETIME(), 0, N'Open');
                INSERT INTO dbo.Sales (SaleID, BusinessID, BranchID, POSStationID, DeviceID, CartID, CustomerID, CashierUserID, CashSessionID, SaleNumber, Status, Subtotal, DiscountTotal, Total)
                VALUES (@sale, @business, @branch, @station, @device, NEWID(), @customer, @admin, @session, NEXT VALUE FOR dbo.SaleNumberSequence, N'CreditPending', 1, 0, 1);
                INSERT INTO dbo.BranchFinancialOutbox (OutboxEventID, CreditOperationID, SourceSaleID, BranchID, CustomerID, AuthorizationID, EventType, Payload, Status)
                VALUES (NEWID(), @operation, @sale, @branch, @customer, NEWID(), N'FinalizeCredit', N'1', N'Pending');
                """, ("@branch", foreignBranch), ("@business", businessId), ("@station", foreignStation), ("@device", foreignDevice), ("@session", foreignSession), ("@sale", foreignSale), ("@customer", customerId), ("@admin", adminId), ("@operation", foreignOperation));
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/pos/credit-sales/" + foreignOperation)).StatusCode);
            var list = await client.GetStringAsync("/api/pos/credit-sales");
            Assert.DoesNotContain(foreignOperation.ToString(), list, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DropAsync(BranchName);
            await DropAsync(CentralName);
        }
    }

    private static async Task AssertPriceAsync(HttpClient client, string branchCs, Guid businessId, Guid branchId, Guid templateCustomerId, Guid sessionId, Guid itemId, string? priceCode, decimal quantity, decimal total)
    {
        var customerId = templateCustomerId;
        if (priceCode is not null)
        {
            customerId = Guid.NewGuid();
            await ExecuteAsync(Connection(CentralName), """
                INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt)
                VALUES (@customer, @business, N'Priced', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CustomerCreditPolicies (CustomerID, CreditEnabled, CreditLimit, PaymentTermDays, CreditStatus) VALUES (@customer, 1, 10000, 30, N'Active');
                """, ("@customer", customerId), ("@business", businessId));
            await using var branch = new SqlConnection(branchCs);
            await branch.OpenAsync();
            await using var command = new SqlCommand("""
                INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, PriceTypeID, CreatedAt, UpdatedAt)
                SELECT @customer, @business, @code, 1, PriceTypeID, SYSUTCDATETIME(), SYSUTCDATETIME()
                FROM dbo.PriceTypes WHERE PriceCode = @code;
                """, branch);
            command.Parameters.AddWithValue("@customer", customerId);
            command.Parameters.AddWithValue("@business", businessId);
            command.Parameters.AddWithValue("@code", priceCode);
            await command.ExecuteNonQueryAsync();
        }

        var response = await client.PostAsJsonAsync("/api/pos/credit-sales", Body(Guid.NewGuid(), customerId, sessionId, itemId, quantity, total, 0m));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        Assert.Equal(total, await MoneyAsync(branchCs, "SELECT TOP (1) Total FROM dbo.Sales WHERE CustomerID = '" + customerId + "' ORDER BY SaleNumber DESC;"));
        Assert.Equal(decimal.Round(total / quantity, 4, MidpointRounding.AwayFromZero), await MoneyAsync(branchCs, "SELECT TOP (1) UnitPrice FROM dbo.SaleItems WHERE SaleID = (SELECT TOP (1) SaleID FROM dbo.Sales WHERE CustomerID = '" + customerId + "' ORDER BY SaleNumber DESC);"));
    }

    private static object WeightBody(Guid operationId, Guid customerId, Guid sessionId, decimal total, decimal entered, decimal referencePrice, decimal weight, decimal stock) => new
    {
        operationId,
        customerId,
        cashSessionId = sessionId,
        invoiceTotal = total,
        immediatePayment = 0m,
        lines = new[]
        {
            new
            {
                quantity = 1m,
                lineType = "VariableValueByWeight",
                enteredScaleValue = entered,
                expectedFinalSellingPrice = total,
                expectedReferenceBasePrice = referencePrice,
                expectedWeightKg = weight,
                expectedStockOwnerQuantity = stock,
            },
        },
    };

    private static object Body(Guid operationId, Guid customerId, Guid sessionId, Guid itemId, decimal quantity, decimal total, decimal immediate, Guid? actorUserId = null, Guid? branchId = null)
    {
        return new
        {
            operationId,
            customerId,
            cashSessionId = sessionId,
            invoiceTotal = total,
            immediatePayment = immediate,
            actorUserId,
            branchId,
            lines = new[] { new { itemId, quantity } },
        };
    }

    private static async Task<Guid> InsertUserAsync(WebApplicationFactory<Program> host, string username, string password, Guid businessId, Guid branchId, string? roleCode)
    {
        var userId = Guid.NewGuid();
        string hash;
        using (var scope = host.Services.CreateScope())
        {
            hash = scope.ServiceProvider.GetRequiredService<PasswordService>().Hash(new AppUser { UserId = userId, Username = username }, password);
        }

        await using var connection = new SqlConnection(Connection(BranchName));
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive, MustChangePassword)
            VALUES (@user, @username, @normalized, @username, @hash, 1, 0);
            INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive) VALUES (@user, @branch, 1);
            """, connection);
        command.Parameters.AddWithValue("@user", userId);
        command.Parameters.AddWithValue("@username", username);
        command.Parameters.AddWithValue("@normalized", username.ToUpperInvariant());
        command.Parameters.AddWithValue("@hash", hash);
        command.Parameters.AddWithValue("@branch", branchId);
        await command.ExecuteNonQueryAsync();
        _ = businessId;
        _ = roleCode;
        return userId;
    }

    private sealed class CreditHttpFactory(string database, bool creditEnabled) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "XtraPOS.Api")));
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["SqlServer:Server"] = "localhost",
                    ["SqlServer:Database"] = database,
                    ["CreditSales:Enabled"] = creditEnabled ? "true" : "false",
                    ["Financial:TrustedOrigins:0"] = "http://127.0.0.1:5176",
                });
            });
        }

        public new HttpClient CreateClient() => CreateClient(new WebApplicationFactoryClientOptions());

        public new HttpClient CreateClient(WebApplicationFactoryClientOptions options)
        {
            var client = base.CreateClient(options);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            return client;
        }
    }

    private static async Task CreateAsync(string name)
    {
        var master = new SqlConnectionStringBuilder(Connection("master")) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"CREATE DATABASE [{name}];", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropAsync(string name)
    {
        var master = new SqlConnectionStringBuilder(Connection("master")) { InitialCatalog = "master" }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static string Connection(string name) => new SqlConnectionStringBuilder
    {
        DataSource = "localhost",
        InitialCatalog = name,
        IntegratedSecurity = true,
        Encrypt = true,
        TrustServerCertificate = true,
    }.ConnectionString;

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

    private static async Task<Guid> GuidAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        return (Guid)(await new SqlCommand(sql, connection).ExecuteScalarAsync() ?? Guid.Empty);
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
}
