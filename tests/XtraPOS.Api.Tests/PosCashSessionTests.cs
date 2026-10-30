using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Sales;
using XtraPOS.Api.Security;
using Xunit;

namespace XtraPOS.Api.Tests;

[Collection("Branch")]
public sealed class PosCashSessionTests(BranchApiFixture fixture)
{
    private const string Password = "Admin1234";

    [Fact]
    public async Task Migration017_StoresMoneyAsDecimalAndGrantsCashierPermissions()
    {
        Assert.Equal(1, await QueryIntAsync("SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'017_POSPaymentsAndCashSessions';"));
        Assert.Equal(13, await QueryIntAsync(
            """
            SELECT COUNT(*)
            FROM sys.columns AS c
            INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(N'dbo.SaleItems')
              AND c.name IN (
                N'WeightKg', N'WeightGrams', N'StockOwnerConversionFactor', N'StockOwnerQuantity',
                N'EnteredScaleValue', N'ReferenceBasePrice', N'VariableValueDiscountPercent',
                N'RawDiscountedValue', N'FinalSellingPrice', N'UnitPrice', N'LineTotal',
                N'UnitCostSnapshot', N'LineCostSnapshot')
              AND t.name = N'decimal';
            """));
        Assert.Equal(0, await QueryIntAsync(
            """
            SELECT COUNT(*)
            FROM sys.columns AS c
            INNER JOIN sys.types AS t ON t.user_type_id = c.user_type_id
            WHERE c.object_id IN (OBJECT_ID(N'dbo.SaleItems'), OBJECT_ID(N'dbo.POSCashSessions'), OBJECT_ID(N'dbo.SalePayments'))
              AND c.name IN (
                N'WeightKg', N'WeightGrams', N'StockOwnerConversionFactor', N'StockOwnerQuantity',
                N'EnteredScaleValue', N'ReferenceBasePrice', N'VariableValueDiscountPercent',
                N'RawDiscountedValue', N'FinalSellingPrice', N'UnitPrice', N'LineTotal',
                N'UnitCostSnapshot', N'LineCostSnapshot', N'OpeningCashAmount', N'ExpectedCashAmount',
                N'CountedCashAmount', N'CashDifference', N'Amount', N'TenderedAmount', N'ChangeAmount')
              AND t.name IN (N'nvarchar', N'varchar', N'text', N'ntext', N'float', N'real');
            """));
        Assert.Equal(1, await QueryIntAsync("SELECT is_nullable FROM sys.columns WHERE object_id = OBJECT_ID(N'dbo.Sales') AND name = N'CashSessionID';"));
        Assert.Equal(1, await QueryIntAsync("SELECT has_filter FROM sys.indexes WHERE name = N'UX_POSCashSessions_BranchDevice_Open';"));
        Assert.Equal(6, await QueryIntAsync(
            """
            SELECT COUNT(*)
            FROM dbo.RolePermissions AS granted
            INNER JOIN dbo.Roles AS role ON role.RoleID = granted.RoleID
            INNER JOIN dbo.Permissions AS permission ON permission.PermissionID = granted.PermissionID
            WHERE permission.PermissionCode IN (N'POS.Sell', N'POS.CashSession')
              AND role.RoleCode IN (N'Admin', N'Supervisor', N'User');
            """));
        Assert.Equal(0, await QueryIntAsync(
            """
            SELECT COUNT(*)
            FROM dbo.RolePermissions AS granted
            INNER JOIN dbo.Roles AS role ON role.RoleID = granted.RoleID
            INNER JOIN dbo.Permissions AS permission ON permission.PermissionID = granted.PermissionID
            WHERE role.RoleCode = N'User'
              AND permission.PermissionCode IN (N'Users.Manage', N'System.Administer', N'Customers.Manage');
            """));
        var inputNames = typeof(CheckoutPaymentInput).GetProperties().Select(property => property.Name).ToHashSet();
        Assert.DoesNotContain("BranchId", inputNames);
        Assert.DoesNotContain("SaleId", inputNames);
        Assert.DoesNotContain("CashSessionId", inputNames);
        Assert.DoesNotContain("CreatedByUserId", inputNames);
    }

    [Fact]
    public async Task Open_IsDeviceScopedAndRejectsASecondOpen()
    {
        var actor = await ActorAsync(fixture.DeviceId);
        var otherDevice = Guid.NewGuid();
        var otherBranch = Guid.NewGuid();
        var otherStation = Guid.NewGuid();
        var otherBranchDevice = Guid.NewGuid();
        try
        {
            await Assert.ThrowsAsync<CashSessionException>(() => Sessions().OpenAsync(actor, -1m, CancellationToken.None));
            Assert.Equal(0, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.POSCashSessions WHERE DeviceID = '{actor.DeviceId}';"));

            var opened = Assert.IsType<CashSessionOpened>(await Sessions().OpenAsync(actor, 0m, CancellationToken.None));
            Assert.Equal(0m, opened.Session.OpeningCashAmount);
            Assert.Equal(actor.DisplayName, opened.Session.OpenedByDisplayName);
            var again = Assert.IsType<CashSessionAlreadyOpen>(await Sessions().OpenAsync(actor, 12m, CancellationToken.None));
            Assert.Equal(opened.Session.CashSessionId, again.Session.CashSessionId);
            Assert.Equal(0m, await QueryDecimalAsync($"SELECT OpeningCashAmount FROM dbo.POSCashSessions WHERE CashSessionID = '{opened.Session.CashSessionId}';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.POSCashSessions WHERE DeviceID = '{actor.DeviceId}' AND Status = N'Open';"));

            await ExecuteAsync($"""
                INSERT INTO dbo.Devices (DeviceID, BranchID, StationID, DeviceName, DeviceType, IsActive, RegisteredAt)
                VALUES ('{otherDevice}', '{actor.BranchId}', '{actor.StationId}', N'صندوق آخر', N'POS', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Branches (BranchID, BusinessID, BranchName, BranchType)
                VALUES ('{otherBranch}', '{actor.BusinessId}', N'فرع آخر', N'Retail');
                INSERT INTO dbo.POSStations (StationID, BranchID, StationNumber, StationName)
                VALUES ('{otherStation}', '{otherBranch}', 1, N'كاونتر آخر');
                INSERT INTO dbo.Devices (DeviceID, BranchID, StationID, DeviceName, DeviceType, IsActive, RegisteredAt)
                VALUES ('{otherBranchDevice}', '{otherBranch}', '{otherStation}', N'صندوق فرع آخر', N'POS', 1, SYSUTCDATETIME());
                """);
            var other = await ActorAsync(otherDevice);
            var foreign = await ActorAsync(otherBranchDevice);
            var otherOpen = Assert.IsType<CashSessionOpened>(await Sessions().OpenAsync(other, 4m, CancellationToken.None));
            var foreignOpen = Assert.IsType<CashSessionOpened>(await Sessions().OpenAsync(foreign, 8m, CancellationToken.None));
            var current = await Sessions().GetCurrentAsync(actor, CancellationToken.None);
            Assert.Equal(opened.Session.CashSessionId, current!.CashSessionId);
            Assert.NotEqual(otherOpen.Session.CashSessionId, current.CashSessionId);
            Assert.NotEqual(foreignOpen.Session.CashSessionId, current.CashSessionId);
            Assert.Equal(otherOpen.Session.CashSessionId, (await Sessions().GetCurrentAsync(other, CancellationToken.None))!.CashSessionId);
            Assert.Equal(2, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.POSCashSessions WHERE BranchID = '{actor.BranchId}' AND Status = N'Open';"));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.POSCashSessions WHERE BranchID = '{otherBranch}' AND Status = N'Open';"));
        }
        finally
        {
            await CleanupAsync([actor.DeviceId, otherDevice, otherBranchDevice], [], [otherDevice, otherBranchDevice], otherStation, otherBranch);
        }
    }

    [Fact]
    public async Task ConcurrentOpens_CreateOneSession()
    {
        var actor = await ActorAsync(fixture.DeviceId);
        try
        {
            var results = await Task.WhenAll(
                OpenInNewScopeAsync(actor, 10m),
                OpenInNewScopeAsync(actor, 25m));
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.POSCashSessions WHERE DeviceID = '{actor.DeviceId}' AND Status = N'Open';"));
            Assert.Single(results.Select(result => result.Session.CashSessionId).Distinct());
            var amount = await QueryDecimalAsync($"SELECT OpeningCashAmount FROM dbo.POSCashSessions WHERE DeviceID = '{actor.DeviceId}' AND Status = N'Open';");
            Assert.Contains(amount, new[] { 10m, 25m });
        }
        finally
        {
            await CleanupAsync([actor.DeviceId], [], [], null, null);
        }
    }

    [Fact]
    public async Task Close_UsesOpeningCashUntilCompletedCashPaymentsExist()
    {
        var actor = await ActorAsync(fixture.DeviceId);
        var outbox = await QueryIntAsync("SELECT COUNT(*) FROM dbo.SyncOutbox;");
        var imports = await QueryIntAsync("SELECT COUNT(*) FROM dbo.DataImportHistory;");
        var stock = await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovements;") + await QueryIntAsync("SELECT COUNT(*) FROM dbo.BranchStockBalances;");
        Guid? emptySession = null;
        try
        {
            var opened = Assert.IsType<CashSessionOpened>(await Sessions().OpenAsync(actor, 40m, CancellationToken.None));
            emptySession = opened.Session.CashSessionId;
            var closed = await Sessions().CloseAsync(actor, 40m, CancellationToken.None);
            Assert.Equal(40m, closed.ExpectedCashAmount);
            Assert.Equal(40m, closed.CountedCashAmount);
            Assert.Equal(0m, closed.CashDifference);
            Assert.Null(await Sessions().GetCurrentAsync(actor, CancellationToken.None));
            var second = await Assert.ThrowsAsync<CashSessionException>(() => Sessions().CloseAsync(actor, 1m, CancellationToken.None));
            Assert.Equal(CashSessionCodes.AlreadyClosed, second.Code);
            Assert.Equal(40m, await QueryDecimalAsync($"SELECT CountedCashAmount FROM dbo.POSCashSessions WHERE CashSessionID = '{emptySession}';"));

            var next = Assert.IsType<CashSessionOpened>(await Sessions().OpenAsync(actor, 100m, CancellationToken.None));
            var completed = await InsertSaleAsync(actor, "Completed", 100m);
            var draft = await InsertSaleAsync(actor, "Draft", 7m);
            var payments = Payments();
            await payments.RecordAsync(completed, next.Session.CashSessionId, actor.UserId, Cash(20m, 50m), CancellationToken.None);
            await payments.RecordAsync(completed, next.Session.CashSessionId, actor.UserId, Card(5m), CancellationToken.None);
            await payments.RecordAsync(draft, next.Session.CashSessionId, actor.UserId, Cash(7m, 7m), CancellationToken.None);
            Assert.Equal("100.0000|0.0000|100.0000|Completed", await SaleFingerprintAsync(completed));
            Assert.Null(await QueryNullableGuidAsync($"SELECT CashSessionID FROM dbo.Sales WHERE SaleID = '{completed}';"));

            var counted = await Sessions().CloseAsync(actor, 90m, CancellationToken.None);
            Assert.Equal(120m, counted.ExpectedCashAmount);
            Assert.Equal(90m, counted.CountedCashAmount);
            Assert.Equal(-30m, counted.CashDifference);
            Assert.Null(await Sessions().GetCurrentAsync(actor, CancellationToken.None));
            var closedPayment = await Assert.ThrowsAsync<SalePaymentException>(() =>
                payments.RecordAsync(completed, next.Session.CashSessionId, actor.UserId, Cash(1m, 1m), CancellationToken.None));
            Assert.Equal(SalePaymentCodes.CashSessionInvalid, closedPayment.Code);
            Assert.Equal(stock, await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovements;") + await QueryIntAsync("SELECT COUNT(*) FROM dbo.BranchStockBalances;"));
            Assert.Equal(outbox, await QueryIntAsync("SELECT COUNT(*) FROM dbo.SyncOutbox;"));
            Assert.Equal(imports, await QueryIntAsync("SELECT COUNT(*) FROM dbo.DataImportHistory;"));
        }
        finally
        {
            await CleanupAsync([actor.DeviceId], [], [], null, null);
        }
    }

    [Fact]
    public async Task Payments_KeepCashCardSemanticsAndRejectCrossBranchSessions()
    {
        var actor = await ActorAsync(fixture.DeviceId);
        var otherBranch = Guid.NewGuid();
        var otherStation = Guid.NewGuid();
        var otherDevice = Guid.NewGuid();
        var stock = await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovements;") + await QueryIntAsync("SELECT COUNT(*) FROM dbo.BranchStockBalances;");
        try
        {
            await ExecuteAsync($"""
                INSERT INTO dbo.Branches (BranchID, BusinessID, BranchName, BranchType)
                VALUES ('{otherBranch}', '{actor.BusinessId}', N'فرع الدفع', N'Retail');
                INSERT INTO dbo.POSStations (StationID, BranchID, StationNumber, StationName)
                VALUES ('{otherStation}', '{otherBranch}', 1, N'كاونتر الدفع');
                INSERT INTO dbo.Devices (DeviceID, BranchID, StationID, DeviceName, DeviceType, IsActive, RegisteredAt)
                VALUES ('{otherDevice}', '{otherBranch}', '{otherStation}', N'صندوق الدفع', N'POS', 1, SYSUTCDATETIME());
                """);
            var opened = Assert.IsType<CashSessionOpened>(await Sessions().OpenAsync(actor, 0m, CancellationToken.None));
            var foreign = Assert.IsType<CashSessionOpened>(await Sessions().OpenAsync(await ActorAsync(otherDevice), 0m, CancellationToken.None));
            var sale = await InsertSaleAsync(actor, "Completed", 100m);
            var fingerprint = await SaleFingerprintAsync(sale);
            var payments = Payments();
            var cashKey = Guid.NewGuid();
            var cardKey = Guid.NewGuid();
            var cash = await payments.RecordAsync(sale, opened.Session.CashSessionId, actor.UserId, Cash(40m, 40m, cashKey), CancellationToken.None);
            var card = await payments.RecordAsync(sale, opened.Session.CashSessionId, actor.UserId, Card(60m, cardKey, "AUTH-1"), CancellationToken.None);
            Assert.Equal(30m, (await payments.RecordAsync(sale, opened.Session.CashSessionId, actor.UserId, Cash(20m, 50m), CancellationToken.None)).ChangeAmount);
            Assert.Equal(0m, card.ChangeAmount);
            Assert.Null(card.TenderedAmount);
            Assert.Equal(0, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.SalePayments WHERE SalePaymentID = '{card.SalePaymentId}' AND (TenderedAmount IS NOT NULL OR ChangeAmount <> 0);"));
            Assert.Equal(2, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.SalePayments WHERE ClientPaymentKey IN ('{cashKey}', '{cardKey}');"));
            var replay = await payments.RecordAsync(sale, opened.Session.CashSessionId, actor.UserId, Cash(40m, 40m, cashKey), CancellationToken.None);
            Assert.Equal(cash.SalePaymentId, replay.SalePaymentId);
            Assert.True(replay.IdempotentReplay);
            Assert.Equal(1, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.SalePayments WHERE ClientPaymentKey = '{cashKey}';"));
            var conflict = await Assert.ThrowsAsync<SalePaymentException>(() =>
                payments.RecordAsync(sale, opened.Session.CashSessionId, actor.UserId, Cash(41m, 41m, cashKey), CancellationToken.None));
            Assert.Equal(SalePaymentCodes.KeyConflict, conflict.Code);

            await Assert.ThrowsAsync<SalePaymentException>(() => payments.RecordAsync(sale, opened.Session.CashSessionId, actor.UserId, Cash(0m, 0m), CancellationToken.None));
            await Assert.ThrowsAsync<SalePaymentException>(() => payments.RecordAsync(sale, opened.Session.CashSessionId, actor.UserId, Cash(-5m, 0m), CancellationToken.None));
            await Assert.ThrowsAsync<SalePaymentException>(() => payments.RecordAsync(sale, opened.Session.CashSessionId, actor.UserId, Cash(5m, 4m), CancellationToken.None));
            var debt = new CheckoutPaymentInput { ClientPaymentKey = Guid.NewGuid(), Method = "Debt", Amount = 5m };
            var rejectedDebt = await Assert.ThrowsAsync<SalePaymentException>(() => payments.RecordAsync(sale, opened.Session.CashSessionId, actor.UserId, debt, CancellationToken.None));
            Assert.Equal(SalePaymentCodes.InvalidMethod, rejectedDebt.Code);
            var cross = await Assert.ThrowsAsync<SalePaymentException>(() => payments.RecordAsync(sale, foreign.Session.CashSessionId, actor.UserId, Cash(3m, 3m), CancellationToken.None));
            Assert.Equal(SalePaymentCodes.CashSessionInvalid, cross.Code);
            var missing = await Assert.ThrowsAsync<SalePaymentException>(() => payments.RecordAsync(sale, Guid.NewGuid(), actor.UserId, Cash(3m, 3m), CancellationToken.None));
            Assert.Equal(SalePaymentCodes.CashSessionInvalid, missing.Code);
            await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync($"""
                INSERT INTO dbo.SalePayments
                (
                    SalePaymentID, SaleID, CashSessionID, BranchID, ClientPaymentKey,
                    PaymentMethodCode, Amount, TenderedAmount, ChangeAmount, CreatedByUserID
                )
                VALUES
                (
                    NEWID(), '{sale}', '{foreign.Session.CashSessionId}', '{actor.BranchId}', NEWID(),
                    N'Cash', 1, 1, 0, '{actor.UserId}'
                );
                """));
            Assert.Equal(fingerprint, await SaleFingerprintAsync(sale));
            Assert.Null(await QueryNullableGuidAsync($"SELECT CashSessionID FROM dbo.Sales WHERE SaleID = '{sale}';"));
            Assert.Equal(stock, await QueryIntAsync("SELECT COUNT(*) FROM dbo.StockMovements;") + await QueryIntAsync("SELECT COUNT(*) FROM dbo.BranchStockBalances;"));
            Assert.Equal(3, await QueryIntAsync($"SELECT COUNT(*) FROM dbo.SalePayments WHERE SaleID = '{sale}';"));
        }
        finally
        {
            await CleanupAsync([actor.DeviceId, otherDevice], [], [otherDevice], otherStation, otherBranch);
        }
    }

    [Fact]
    public async Task SessionApi_DerivesContextAndHasNoPaymentPostRoute()
    {
        using var client = DeviceClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        try
        {
            var missing = await client.PostAsJsonAsync($"/api/sales/{Guid.NewGuid()}/payments", new { amount = 5, method = "Cash" });
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/pos/payments", new { amount = 5 })).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/sales/{Guid.NewGuid()}/payments/{Guid.NewGuid()}", new { amount = 5 })).StatusCode);
            using var anonymous = fixture.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/pos/cash-session/current")).StatusCode);

            var negative = await client.PostAsJsonAsync("/api/pos/cash-session/open", new { openingCashAmount = -1 });
            Assert.Equal(HttpStatusCode.BadRequest, negative.StatusCode);
            var opened = await client.PostAsJsonAsync("/api/pos/cash-session/open", new { openingCashAmount = 0, branchId = Guid.NewGuid(), deviceId = Guid.NewGuid() });
            Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
            using var document = JsonDocument.Parse(await opened.Content.ReadAsStringAsync());
            var sessionId = document.RootElement.GetProperty("session").GetProperty("cashSessionId").GetGuid();
            Assert.Equal(fixture.DeviceId, await QueryGuidAsync($"SELECT DeviceID FROM dbo.POSCashSessions WHERE CashSessionID = '{sessionId}';"));
            var current = await client.GetAsync("/api/pos/cash-session/current");
            Assert.Equal(HttpStatusCode.OK, current.StatusCode);
            using var currentDocument = JsonDocument.Parse(await current.Content.ReadAsStringAsync());
            Assert.Equal(sessionId, currentDocument.RootElement.GetProperty("session").GetProperty("cashSessionId").GetGuid());
            var closed = await client.PostAsJsonAsync("/api/pos/cash-session/close", new { countedCashAmount = 0 });
            Assert.Equal(HttpStatusCode.OK, closed.StatusCode);
            var twice = await client.PostAsJsonAsync("/api/pos/cash-session/close", new { countedCashAmount = 0 });
            Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
            Assert.Contains(CashSessionCodes.AlreadyClosed, await twice.Content.ReadAsStringAsync());
        }
        finally
        {
            await CleanupAsync([fixture.DeviceId], [], [], null, null);
            await ExecuteAsync("UPDATE dbo.Users SET FailedLoginCount = 0, LockoutEndAt = NULL WHERE NormalizedUsername = N'ADMIN';");
        }
    }

    private async Task<CashSessionOpenResult> OpenInNewScopeAsync(PosDeviceContext actor, decimal amount)
    {
        using var scope = fixture.Factory.Services.CreateScope();
        var sessions = scope.ServiceProvider.GetRequiredService<CashSessionService>();
        return await sessions.OpenAsync(actor, amount, CancellationToken.None);
    }

    private CashSessionService Sessions()
    {
        var scope = fixture.Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<CashSessionService>();
    }

    private SalePaymentService Payments()
    {
        var scope = fixture.Factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<SalePaymentService>();
    }

    private static CheckoutPaymentInput Cash(decimal amount, decimal tendered, Guid? key = null) => new()
    {
        ClientPaymentKey = key ?? Guid.NewGuid(),
        Method = PaymentMethodCodes.Cash,
        Amount = amount,
        TenderedAmount = tendered,
    };

    private static CheckoutPaymentInput Card(decimal amount, Guid? key = null, string? reference = null) => new()
    {
        ClientPaymentKey = key ?? Guid.NewGuid(),
        Method = PaymentMethodCodes.Card,
        Amount = amount,
        ExternalReference = reference,
    };

    private async Task<PosDeviceContext> ActorAsync(Guid deviceId)
    {
        var branchId = await QueryGuidAsync($"SELECT BranchID FROM dbo.Devices WHERE DeviceID = '{deviceId}';");
        var businessId = await QueryGuidAsync($"SELECT BusinessID FROM dbo.Branches WHERE BranchID = '{branchId}';");
        var stationId = await QueryGuidAsync($"SELECT StationID FROM dbo.Devices WHERE DeviceID = '{deviceId}';");
        var userId = await QueryGuidAsync("SELECT UserID FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';");
        var name = await QueryTextAsync("SELECT DisplayName FROM dbo.Users WHERE NormalizedUsername = N'ADMIN';");
        return new PosDeviceContext(userId, name, businessId, branchId, stationId, deviceId);
    }

    private static async Task<Guid> InsertSaleAsync(PosDeviceContext actor, string status, decimal total)
    {
        var saleId = Guid.NewGuid();
        var completed = status == "Completed" ? "SYSUTCDATETIME()" : "NULL";
        await ExecuteAsync($"""
            INSERT INTO dbo.Sales
            (
                SaleID, BusinessID, BranchID, POSStationID, DeviceID, CartID, CashierUserID,
                SaleNumber, Status, Subtotal, DiscountTotal, Total, CompletedAt
            )
            VALUES
            (
                '{saleId}', '{actor.BusinessId}', '{actor.BranchId}', '{actor.StationId}', '{actor.DeviceId}', NEWID(), '{actor.UserId}',
                NEXT VALUE FOR dbo.SaleNumberSequence, N'{status}', {total.ToString(System.Globalization.CultureInfo.InvariantCulture)}, 0, {total.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {completed}
            );
            """);
        return saleId;
    }

    private static async Task<string> SaleFingerprintAsync(Guid saleId) =>
        await QueryTextAsync($"SELECT CONCAT(Subtotal, N'|', DiscountTotal, N'|', Total, N'|', Status) FROM dbo.Sales WHERE SaleID = '{saleId}';");

    private HttpClient DeviceClient()
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Id", fixture.DeviceId.ToString());
        client.DefaultRequestHeaders.Add("X-Device-Token", fixture.DeviceToken);
        return client;
    }

    private static async Task CleanupAsync(Guid[] devices, Guid[] sales, Guid[] extraDevices, Guid? stationId, Guid? branchId)
    {
        if (devices.Length == 0)
        {
            return;
        }

        var deviceList = string.Join(", ", devices.Select(id => $"'{id}'"));
        var saleFilter = sales.Length == 0 ? "" : $" OR SaleID IN ({string.Join(", ", sales.Select(id => $"'{id}'"))})";
        var saleDelete = sales.Length == 0 ? "" : $"DELETE FROM dbo.Sales WHERE SaleID IN ({string.Join(", ", sales.Select(id => $"'{id}'"))});";
        var extraDelete = extraDevices.Length == 0 ? "" : $"DELETE FROM dbo.Devices WHERE DeviceID IN ({string.Join(", ", extraDevices.Select(id => $"'{id}'"))});";
        var stationDelete = stationId is null ? "" : $"DELETE FROM dbo.POSStations WHERE StationID = '{stationId}';";
        var branchDelete = branchId is null ? "" : $"DELETE FROM dbo.Branches WHERE BranchID = '{branchId}';";
        await ExecuteAsync($"""
            DELETE FROM dbo.SalePayments
            WHERE CashSessionID IN (SELECT CashSessionID FROM dbo.POSCashSessions WHERE DeviceID IN ({deviceList})){saleFilter};
            UPDATE dbo.Sales SET CashSessionID = NULL
            WHERE CashSessionID IN (SELECT CashSessionID FROM dbo.POSCashSessions WHERE DeviceID IN ({deviceList}));
            {saleDelete}
            DELETE FROM dbo.Sales
            WHERE DeviceID IN ({deviceList})
              AND NOT EXISTS (SELECT 1 FROM dbo.SaleItems WHERE SaleItems.SaleID = dbo.Sales.SaleID);
            DELETE FROM dbo.POSCashSessions WHERE DeviceID IN ({deviceList});
            {extraDelete}
            {stationDelete}
            {branchDelete}
            """);
    }

    private static async Task ExecuteAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> QueryIntAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<decimal> QueryDecimalAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToDecimal(await command.ExecuteScalarAsync());
    }

    private static async Task<Guid> QueryGuidAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return (Guid)(await command.ExecuteScalarAsync() ?? throw new InvalidOperationException("Missing guid."));
    }

    private static async Task<Guid?> QueryNullableGuidAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : (Guid)value;
    }

    private static async Task<string> QueryTextAsync(string sql)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString());
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }
}
