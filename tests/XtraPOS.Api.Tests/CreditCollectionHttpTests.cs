using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Credit;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using XtraPOS.Api.Security;
using Xunit;

namespace XtraPOS.Api.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CreditCollectionHttpCollection : ICollectionFixture<CreditCollectionHttpFixture>
{
    public const string Name = "CreditCollectionHttp";
}

public sealed class CreditCollectionHttpFixture : IAsyncLifetime
{
    public const string CentralName = "XtraPOS_Central_Test_CollectHttp";
    public const string OriginName = "XtraPOS_Branch_Test_CollectHttpA";
    public const string OtherName = "XtraPOS_Branch_Test_CollectHttpB";
    public Guid BusinessId { get; } = Guid.NewGuid();
    public Guid OriginBranchId { get; } = Guid.NewGuid();
    public Guid OtherBranchId { get; } = Guid.NewGuid();
    public Guid OriginDeviceId { get; } = Guid.NewGuid();
    public Guid OtherDeviceId { get; } = Guid.NewGuid();
    public Guid ClosedDeviceId { get; } = Guid.NewGuid();
    public Guid CollectorId { get; } = Guid.NewGuid();
    public Guid ViewerId { get; } = Guid.NewGuid();
    public Guid DeniedId { get; } = Guid.NewGuid();
    public Guid OriginSessionId { get; } = Guid.NewGuid();
    public Guid OtherSessionId { get; } = Guid.NewGuid();
    public Guid MainReceivableId { get; } = Guid.NewGuid();
    public Guid OlderReceivableId { get; } = Guid.NewGuid();
    public Guid NewerReceivableId { get; } = Guid.NewGuid();
    public Guid RaceReceivableId { get; } = Guid.NewGuid();
    public Guid CustomerId { get; } = Guid.NewGuid();
    public Guid StatusCustomerId { get; } = Guid.NewGuid();
    public Guid RecoveryCustomerId { get; } = Guid.NewGuid();
    public Guid StatusReceivableId { get; } = Guid.NewGuid();
    public Guid RecoveryReceivableId { get; } = Guid.NewGuid();
    public Guid FifoCustomerId { get; } = Guid.NewGuid();
    public Guid RaceCustomerId { get; } = Guid.NewGuid();
    public const string DeviceToken = "collect-token";
    public const string Password = "Collect1234";

    public async Task InitializeAsync()
    {
        CentralDatabase.Require(CentralName);
        TestDatabase.Require(OriginName);
        TestDatabase.Require(OtherName);
        await DropAsync(CentralName);
        await DropAsync(OriginName);
        await DropAsync(OtherName);
        await CreateAsync(CentralName);
        await CreateAsync(OriginName);
        await CreateAsync(OtherName);
        await new CentralMigrationRunner().ApplyAsync(Connection(CentralName), CancellationToken.None);
        await MigrateAsync(OriginName);
        await MigrateAsync(OtherName);
        var originStation = Guid.NewGuid();
        var otherStation = Guid.NewGuid();
        await SeedBranchAsync(OriginName, OriginBranchId, originStation, OriginDeviceId, ClosedDeviceId, OriginSessionId, "01");
        await SeedBranchAsync(OtherName, OtherBranchId, otherStation, OtherDeviceId, Guid.NewGuid(), OtherSessionId, "02");
        await ExecuteAsync(Connection(CentralName), """
            INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Collect HTTP', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES
                (@customer, @business, N'Origin', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (@fifo, @business, N'Fifo', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (@race, @business, N'Race', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (@statusCustomer, @business, N'Status', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (@recoveryCustomer, @business, N'Recovery', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.CustomerReceivables (ReceivableID, CustomerID, SourceSaleID, SourceBranchID, OriginalAmount, OutstandingAmount, DueDate, Status) VALUES
                (@main, @customer, NEWID(), @origin, 800, 800, '2026-02-01', N'Open'),
                (@older, @fifo, NEWID(), @origin, 200, 200, '2026-01-01', N'Open'),
                (@newer, @fifo, NEWID(), @origin, 350, 350, '2026-03-01', N'Open'),
                (@raceReceivable, @race, NEWID(), @origin, 500, 500, '2026-01-01', N'Open'),
                (@statusReceivable, @statusCustomer, NEWID(), @origin, 100, 100, '2026-01-01', N'Open'),
                (@recoveryReceivable, @recoveryCustomer, NEWID(), @origin, 100, 100, '2026-01-01', N'Open');
            """,
            ("@business", BusinessId), ("@customer", CustomerId), ("@fifo", FifoCustomerId), ("@race", RaceCustomerId),
            ("@origin", OriginBranchId), ("@main", MainReceivableId), ("@older", OlderReceivableId), ("@newer", NewerReceivableId), ("@raceReceivable", RaceReceivableId),
            ("@statusCustomer", StatusCustomerId), ("@recoveryCustomer", RecoveryCustomerId), ("@statusReceivable", StatusReceivableId), ("@recoveryReceivable", RecoveryReceivableId));
        await ExecuteAsync(Connection(OriginName), """
            INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES (@customer, @business, N'Origin', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.Sales (SaleID, BusinessID, BranchID, POSStationID, DeviceID, CartID, CustomerID, CashierUserID, CashSessionID, SaleNumber, Status, Subtotal, DiscountTotal, Total)
            VALUES (NEWID(), @business, @branch, @station, @device, NEWID(), @customer, @collector, @session, NEXT VALUE FOR dbo.SaleNumberSequence, N'Completed', 800, 0, 800);
            """,
            ("@customer", CustomerId), ("@business", BusinessId), ("@branch", OriginBranchId), ("@station", originStation), ("@device", OriginDeviceId), ("@collector", CollectorId), ("@session", OriginSessionId));
    }

    public Task DisposeAsync() => Task.WhenAll(DropAsync(CentralName), DropAsync(OriginName), DropAsync(OtherName));

    private async Task SeedBranchAsync(string database, Guid branchId, Guid stationId, Guid deviceId, Guid closedDeviceId, Guid sessionId, string code)
    {
        var collectorHash = new PasswordService().Hash(new AppUser { UserId = CollectorId, Username = "collector" }, Password);
        var viewerHash = new PasswordService().Hash(new AppUser { UserId = ViewerId, Username = "viewer" }, Password);
        var deniedHash = new PasswordService().Hash(new AppUser { UserId = DeniedId, Username = "denied" }, Password);
        await ExecuteAsync(Connection(database), """
            INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Collect HTTP', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt) VALUES (@branch, @business, N'00', N'Branch', N'Retail', 1, SYSUTCDATETIME());
            INSERT INTO dbo.POSStations (StationID, BranchID, StationNumber, StationName, IsActive, CreatedAt) VALUES (@station, @branch, 1, N'Till', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Devices (DeviceID, BranchID, StationID, DeviceName, DeviceType, IsActive, RegisteredAt, CreatedAt) VALUES
                (@device, @branch, @station, N'Till', N'POS', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                (@closedDevice, @branch, @station, N'Closed', N'POS', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive) VALUES
                (@collector, N'collector', N'COLLECTOR', N'Collector', N'pending', 1),
                (@viewer, N'viewer', N'VIEWER', N'Viewer', N'pending', 1),
                (@denied, N'denied', N'DENIED', N'Denied', N'pending', 1);
            INSERT INTO dbo.Roles (RoleCode, RoleName, IsSystemRole, IsActive, CreatedAt) VALUES (N'CreditViewOnly', N'View only', 0, 1, SYSUTCDATETIME());
            INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
            SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
            FROM dbo.Roles AS role CROSS JOIN dbo.Permissions AS permission
            WHERE role.RoleCode = N'CreditViewOnly' AND permission.PermissionCode = N'Credit.View';
            INSERT INTO dbo.UserBranchAccess (UserID, BranchID) VALUES (@collector, @branch), (@viewer, @branch), (@denied, @branch);
            INSERT INTO dbo.UserRoles (UserID, RoleID) SELECT @collector, RoleID FROM dbo.Roles WHERE RoleCode = N'User';
            INSERT INTO dbo.UserRoles (UserID, RoleID) SELECT @viewer, RoleID FROM dbo.Roles WHERE RoleCode = N'CreditViewOnly';
            INSERT INTO dbo.POSCashSessions (CashSessionID, BusinessID, BranchID, POSStationID, DeviceID, OpenedByUserID, OpenedAt, OpeningCashAmount, Status)
            VALUES (@session, @business, @branch, @station, @device, @collector, SYSUTCDATETIME(), 0, N'Open');
            INSERT INTO dbo.POSCashSessions (CashSessionID, BusinessID, BranchID, POSStationID, DeviceID, OpenedByUserID, OpenedAt, OpeningCashAmount, Status, ClosedByUserID, ClosedAt, ExpectedCashAmount, CountedCashAmount, CashDifference)
            VALUES (NEWID(), @business, @branch, @station, @closedDevice, @collector, SYSUTCDATETIME(), 0, N'Closed', @collector, SYSUTCDATETIME(), 0, 0, 0);
            INSERT INTO dbo.SyncEndpoints (EndpointID, DatabaseRole, LocationKind, ServerName, DatabaseName, AuthenticationType, EncryptConnection, TrustServerCertificate, Status)
            VALUES (NEWID(), N'Central', N'Local', N'localhost', N'pending', N'Integrated', 1, 1, N'Ready');
            """,
            ("@business", BusinessId), ("@branch", branchId), ("@station", stationId), ("@device", deviceId), ("@closedDevice", closedDeviceId),
            ("@collector", CollectorId), ("@viewer", ViewerId), ("@denied", DeniedId), ("@session", sessionId));
        await using var fix = new SqlConnection(Connection(database));
        await fix.OpenAsync();
        await using var command = new SqlCommand("""
            UPDATE dbo.Branches SET BranchCode = @code WHERE BranchID = @branch;
            UPDATE dbo.Users SET PasswordHash = @collectorHash WHERE UserID = @collector;
            UPDATE dbo.Users SET PasswordHash = @viewerHash WHERE UserID = @viewer;
            UPDATE dbo.Users SET PasswordHash = @deniedHash WHERE UserID = @denied;
            UPDATE dbo.Devices SET DeviceTokenHash = @token WHERE DeviceID = @device OR DeviceID = @closedDevice;
            UPDATE dbo.SyncEndpoints SET DatabaseName = @central WHERE DatabaseRole = N'Central';
            """, fix);
        command.Parameters.AddWithValue("@code", code);
        command.Parameters.AddWithValue("@branch", branchId);
        command.Parameters.AddWithValue("@collectorHash", collectorHash);
        command.Parameters.AddWithValue("@viewerHash", viewerHash);
        command.Parameters.AddWithValue("@deniedHash", deniedHash);
        command.Parameters.AddWithValue("@collector", CollectorId);
        command.Parameters.AddWithValue("@viewer", ViewerId);
        command.Parameters.AddWithValue("@denied", DeniedId);
        command.Parameters.Add("@token", System.Data.SqlDbType.VarBinary, 64).Value = DeviceTokenService.Hash(DeviceToken);
        command.Parameters.AddWithValue("@device", deviceId);
        command.Parameters.AddWithValue("@closedDevice", closedDeviceId);
        command.Parameters.AddWithValue("@central", CentralName);
        await command.ExecuteNonQueryAsync();
    }

    public static string Connection(string name) => new SqlConnectionStringBuilder { DataSource = "localhost", InitialCatalog = name, IntegratedSecurity = true, Encrypt = true, TrustServerCertificate = true, ApplicationName = "XtraPOS.Tests" }.ConnectionString;

    private static async Task MigrateAsync(string database)
    {
        var connections = new SqlConnectionFactory(Microsoft.Extensions.Options.Options.Create(new XtraPOS.Api.Configuration.SqlServerOptions { Server = "localhost", Database = database, ConnectTimeoutSeconds = 15 }));
        await new XtraPOS.Api.Data.Migrations.MigrationRunner(connections, Microsoft.Extensions.Logging.Abstractions.NullLogger<XtraPOS.Api.Data.Migrations.MigrationRunner>.Instance).ApplyPendingAsync(CancellationToken.None);
    }

    public static async Task ExecuteAsync(string connectionString, string sql, params (string Name, Guid Value)[] parameters)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.Add(parameter.Name, System.Data.SqlDbType.UniqueIdentifier).Value = parameter.Value;
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<decimal> MoneyAsync(string name, string sql)
    {
        await using var connection = new SqlConnection(Connection(name));
        await connection.OpenAsync();
        return Convert.ToDecimal(await new SqlCommand(sql, connection).ExecuteScalarAsync());
    }

    public static async Task<int> IntAsync(string name, string sql)
    {
        await using var connection = new SqlConnection(Connection(name));
        await connection.OpenAsync();
        return Convert.ToInt32(await new SqlCommand(sql, connection).ExecuteScalarAsync());
    }

    public static async Task<string> TextAsync(string name, string sql)
    {
        await using var connection = new SqlConnection(Connection(name));
        await connection.OpenAsync();
        return Convert.ToString(await new SqlCommand(sql, connection).ExecuteScalarAsync()) ?? "";
    }

    private static async Task CreateAsync(string name)
    {
        var master = new SqlConnectionStringBuilder { DataSource = "localhost", InitialCatalog = "master", IntegratedSecurity = true, Encrypt = true, TrustServerCertificate = true }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await new SqlCommand($"IF DB_ID(N'{name}') IS NULL CREATE DATABASE [{name}] COLLATE Arabic_CI_AS;", connection).ExecuteNonQueryAsync();
    }

    private static async Task DropAsync(string name)
    {
        if (!name.Contains("_Test_CollectHttp", StringComparison.Ordinal)) throw new InvalidOperationException(name);
        SqlConnection.ClearAllPools();
        var master = new SqlConnectionStringBuilder { DataSource = "localhost", InitialCatalog = "master", IntegratedSecurity = true, Encrypt = true, TrustServerCertificate = true }.ConnectionString;
        await using var connection = new SqlConnection(master);
        await connection.OpenAsync();
        await new SqlCommand($"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END", connection).ExecuteNonQueryAsync();
    }
}

[Collection(CreditCollectionHttpCollection.Name)]
public sealed class CreditCollectionHttpTests(CreditCollectionHttpFixture fixture) : IDisposable
{
    private readonly List<IDisposable> hosts = [];
    [Fact]
    public async Task Central_unavailable_rejects_a_new_collection()
    {
        await using var connection = new SqlConnection(CreditCollectionHttpFixture.Connection(CreditCollectionHttpFixture.OtherName));
        await connection.OpenAsync();
        await new SqlCommand("UPDATE dbo.SyncEndpoints SET DatabaseName = N'XtraPOS_Central_Test_Missing' WHERE DatabaseRole = N'Central';", connection).ExecuteNonQueryAsync();
        try
        {
            using var client = await LoginAsync(CreditCollectionHttpFixture.OtherName, true, "collector", fixture.OtherDeviceId);
            var response = await client.PostAsJsonAsync("/api/pos/credit-collections", Body(Guid.NewGuid(), fixture.CustomerId, 10m, null));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            await new SqlCommand("UPDATE dbo.SyncEndpoints SET DatabaseName = N'" + CreditCollectionHttpFixture.CentralName + "' WHERE DatabaseRole = N'Central';", connection).ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task Anonymous_collection_post_is_unauthorized()
    {
        await using var host = new CollectionHost(CreditCollectionHttpFixture.OtherName, true);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.CreateClient().PostAsJsonAsync("/api/pos/credit-collections", new { })).StatusCode);
    }

    [Fact]
    public async Task Disabled_flag_rejects_new_collection_and_cookie_is_strict()
    {
        using var client = await LoginAsync(CreditCollectionHttpFixture.OtherName, false, "collector", fixture.OtherDeviceId);
        var response = await client.PostAsJsonAsync("/api/pos/credit-collections", Body(Guid.NewGuid(), fixture.CustomerId, 10m, null));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("COLLECTION_DISABLED", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Permissions_device_session_and_origin_are_enforced()
    {
        using var viewer = await LoginAsync(CreditCollectionHttpFixture.OtherName, true, "viewer", fixture.OtherDeviceId);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/pos/credit-collections", Body(Guid.NewGuid(), fixture.CustomerId, 10m, null))).StatusCode);
        using var denied = await LoginAsync(CreditCollectionHttpFixture.OtherName, true, "denied", fixture.OtherDeviceId);
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.GetAsync($"/api/pos/credit-collections/customers/{fixture.CustomerId}/summary")).StatusCode);
        using var collector = await LoginAsync(CreditCollectionHttpFixture.OtherName, true, "collector", fixture.OtherDeviceId);
        Assert.Equal(HttpStatusCode.Forbidden, (await collector.PostAsJsonAsync("/api/pos/credit-collections", Body(Guid.NewGuid(), fixture.CustomerId, 10m, null, fixture.OriginSessionId))).StatusCode);
        using var closed = await LoginAsync(CreditCollectionHttpFixture.OriginName, true, "collector", fixture.ClosedDeviceId);
        Assert.Equal(HttpStatusCode.Forbidden, (await closed.PostAsJsonAsync("/api/pos/credit-collections", Body(Guid.NewGuid(), fixture.CustomerId, 10m, null))).StatusCode);
        using var foreign = await LoginAsync(CreditCollectionHttpFixture.OtherName, true, "collector", fixture.OtherDeviceId);
        foreign.DefaultRequestHeaders.Remove("X-Device-Token");
        foreign.DefaultRequestHeaders.Add("X-Device-Token", "wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await foreign.PostAsJsonAsync("/api/pos/credit-collections", Body(Guid.NewGuid(), fixture.CustomerId, 10m, null))).StatusCode);
        using var crossSite = await LoginAsync(CreditCollectionHttpFixture.OtherName, true, "collector", fixture.OtherDeviceId);
        var evil = new HttpRequestMessage(HttpMethod.Post, "/api/pos/credit-collections") { Content = JsonContent.Create(Body(Guid.NewGuid(), fixture.CustomerId, 10m, null)) };
        evil.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await crossSite.SendAsync(evil)).StatusCode);
    }

    [Fact]
    public async Task Cross_branch_http_collection_reconciles_and_is_idempotent()
    {
        using var other = await LoginAsync(CreditCollectionHttpFixture.OtherName, true, "collector", fixture.OtherDeviceId);
        var operationId = Guid.NewGuid();
        var body = Body(operationId, fixture.CustomerId, 300m, null, actorUserId: Guid.NewGuid(), claimedOutstanding: 1m);
        var first = await other.PostAsJsonAsync("/api/pos/credit-collections", body);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync("/api/pos/credit-collections", body)).StatusCode);
        var changed = Body(operationId, fixture.CustomerId, 301m, null);
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsJsonAsync("/api/pos/credit-collections", changed)).StatusCode);
        Assert.Equal(500m, await CreditCollectionHttpFixture.MoneyAsync(CreditCollectionHttpFixture.CentralName, "SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE ReceivableID = '" + fixture.MainReceivableId + "';"));
        Assert.Equal(fixture.OriginBranchId, await GuidAsync(CreditCollectionHttpFixture.CentralName, "SELECT SourceBranchID FROM dbo.CustomerReceivables WHERE ReceivableID = '" + fixture.MainReceivableId + "';"));
        Assert.Equal(1, await CreditCollectionHttpFixture.IntAsync(CreditCollectionHttpFixture.OtherName, "SELECT COUNT(*) FROM dbo.BranchDebtCollections WHERE CollectionOperationID = '" + operationId + "' AND CashierUserID = '" + fixture.CollectorId + "' AND DeviceID = '" + fixture.OtherDeviceId + "' AND CashSessionID = '" + fixture.OtherSessionId + "' AND BranchID = '" + fixture.OtherBranchId + "';"));
        Assert.Equal(0, await CreditCollectionHttpFixture.IntAsync(CreditCollectionHttpFixture.OtherName, "SELECT COUNT(*) FROM dbo.Sales;"));
        Assert.Equal(800m, await CreditCollectionHttpFixture.MoneyAsync(CreditCollectionHttpFixture.OriginName, "SELECT SUM(Total) FROM dbo.Sales;"));
        Assert.Equal(300m, await CreditCollectionHttpFixture.MoneyAsync(CreditCollectionHttpFixture.CentralName, "SELECT SUM(Amount) FROM dbo.CustomerCreditLedger WHERE IdempotencyKey = N'collection:" + operationId.ToString("N") + "';"));
        Assert.Equal(300m, await CreditCollectionHttpFixture.MoneyAsync(CreditCollectionHttpFixture.CentralName, "SELECT SUM(Amount) FROM dbo.CreditPaymentAllocations WHERE ReceivableID = '" + fixture.MainReceivableId + "';"));
        using var origin = await LoginAsync(CreditCollectionHttpFixture.OriginName, true, "collector", fixture.OriginDeviceId);
        var summary = await origin.GetFromJsonAsync<SummaryBody>($"/api/pos/credit-collections/customers/{fixture.CustomerId}/summary");
        Assert.Equal(500m, summary!.Outstanding);
        var overpay = await other.PostAsJsonAsync("/api/pos/credit-collections", Body(Guid.NewGuid(), fixture.CustomerId, 900m, null));
        Assert.Equal(HttpStatusCode.Forbidden, overpay.StatusCode);
        var drawer = await other.GetFromJsonAsync<DrawerBody>("/api/pos/cash-session/current");
        var drawerCollections = await CreditCollectionHttpFixture.MoneyAsync(CreditCollectionHttpFixture.OtherName, "SELECT COALESCE(SUM(Amount), 0) FROM dbo.BranchDebtCollections WHERE CashSessionID = '" + fixture.OtherSessionId + "' AND Status IN (N'Pending', N'Completed', N'ReconciliationRequired');");
        Assert.Equal(drawerCollections, drawer!.ExpectedCashPreview);
        Assert.True(drawerCollections >= 300m);
    }

    [Fact]
    public async Task Fifo_selected_invoice_and_concurrent_requests_use_server_balances()
    {
        using var origin = await LoginAsync(CreditCollectionHttpFixture.OriginName, true, "collector", fixture.OriginDeviceId);
        var quote = await origin.GetFromJsonAsync<QuoteBody>($"/api/pos/credit-collections/customers/{fixture.FifoCustomerId}/receivables");
        Assert.Equal(fixture.OlderReceivableId, quote!.Receivables[0].ReceivableId);
        var fifo = await origin.PostAsJsonAsync("/api/pos/credit-collections", Body(Guid.NewGuid(), fixture.FifoCustomerId, 300m, null));
        Assert.Equal(HttpStatusCode.OK, fifo.StatusCode);
        Assert.Equal(0m, await CreditCollectionHttpFixture.MoneyAsync(CreditCollectionHttpFixture.CentralName, "SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE ReceivableID = '" + fixture.OlderReceivableId + "';"));
        Assert.Equal(250m, await CreditCollectionHttpFixture.MoneyAsync(CreditCollectionHttpFixture.CentralName, "SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE ReceivableID = '" + fixture.NewerReceivableId + "';"));
        var selected = await origin.PostAsJsonAsync("/api/pos/credit-collections", Body(Guid.NewGuid(), fixture.FifoCustomerId, 50m, fixture.NewerReceivableId));
        Assert.Equal(HttpStatusCode.OK, selected.StatusCode);
        Assert.Equal(200m, await CreditCollectionHttpFixture.MoneyAsync(CreditCollectionHttpFixture.CentralName, "SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE ReceivableID = '" + fixture.NewerReceivableId + "';"));
        var stale = await origin.PostAsJsonAsync("/api/pos/credit-collections", Body(Guid.NewGuid(), fixture.FifoCustomerId, 250m, fixture.NewerReceivableId));
        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);

        using var other = await LoginAsync(CreditCollectionHttpFixture.OtherName, true, "collector", fixture.OtherDeviceId);
        var left = Body(Guid.NewGuid(), fixture.RaceCustomerId, 400m, null);
        var right = Body(Guid.NewGuid(), fixture.RaceCustomerId, 400m, null);
        var results = await Task.WhenAll(origin.PostAsJsonAsync("/api/pos/credit-collections", left), other.PostAsJsonAsync("/api/pos/credit-collections", right));
        Assert.Contains(results, result => result.StatusCode == HttpStatusCode.OK);
        Assert.Contains(results, result => result.StatusCode == HttpStatusCode.Forbidden);
        Assert.Equal(100m, await CreditCollectionHttpFixture.MoneyAsync(CreditCollectionHttpFixture.CentralName, "SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE ReceivableID = '" + fixture.RaceReceivableId + "';"));
    }

    [Fact]
    public async Task Other_branch_cannot_read_collection_status()
    {
        using var other = await LoginAsync(CreditCollectionHttpFixture.OtherName, true, "collector", fixture.OtherDeviceId);
        var operationId = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync("/api/pos/credit-collections", Body(operationId, fixture.StatusCustomerId, 20m, null))).StatusCode);
        using var origin = await LoginAsync(CreditCollectionHttpFixture.OriginName, true, "collector", fixture.OriginDeviceId);
        var hidden = await origin.GetAsync($"/api/pos/credit-collections/{operationId}");
        Assert.True(hidden.StatusCode == HttpStatusCode.NotFound, await hidden.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync($"/api/pos/credit-collections/{operationId}")).StatusCode);
    }

    [Fact]
    public async Task Disabled_host_still_recovers_pending_and_keeps_unresolved_cash()
    {
        var pendingId = Guid.NewGuid();
        var expiredId = Guid.NewGuid();
        await InsertCollectionAsync(pendingId, 15m, "Posting", "Pending", DateTime.UtcNow.AddMinutes(10));
        await InsertCollectionAsync(expiredId, 12m, "Authorized", "Pending", DateTime.UtcNow.AddMinutes(-5));
        await using var disabled = new CollectionHost(CreditCollectionHttpFixture.OtherName, false);
        using var scope = disabled.Services.CreateScope();
        var collections = scope.ServiceProvider.GetRequiredService<CreditCollectionService>();
        await using var central = new SqlConnection(CreditCollectionHttpFixture.Connection(CreditCollectionHttpFixture.CentralName));
        await using var branch = new SqlConnection(CreditCollectionHttpFixture.Connection(CreditCollectionHttpFixture.OtherName));
        await central.OpenAsync();
        await branch.OpenAsync();
        await new SqlCommand("UPDATE dbo.BranchCollectionOutbox SET NextRetryAt = SYSUTCDATETIME(), LeaseUntil = NULL, LeaseOwner = NULL WHERE CreditOperationID = '" + pendingId + "';", branch).ExecuteNonQueryAsync();
        Assert.Equal(CreditCollectionOutcome.Completed, await collections.RecoverOnceAsync(central, branch, "http-worker", CancellationToken.None));
        await new SqlCommand("UPDATE dbo.BranchCollectionOutbox SET NextRetryAt = SYSUTCDATETIME(), LeaseUntil = NULL, LeaseOwner = NULL WHERE CreditOperationID = '" + expiredId + "';", branch).ExecuteNonQueryAsync();
        var second = await collections.RecoverOnceAsync(central, branch, "http-worker-2", CancellationToken.None);
        Assert.Equal(CreditCollectionOutcome.ReconciliationRequired, second);
        Assert.Null(await collections.RecoverOnceAsync(central, branch, "http-worker-3", CancellationToken.None));
        Assert.Equal(1, await CreditCollectionHttpFixture.IntAsync(CreditCollectionHttpFixture.CentralName, "SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE IdempotencyKey = N'collection:" + pendingId.ToString("N") + "';"));
        Assert.Equal(0, await CreditCollectionHttpFixture.IntAsync(CreditCollectionHttpFixture.CentralName, "SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE IdempotencyKey = N'collection:" + expiredId.ToString("N") + "';"));
        Assert.Equal(1, await CreditCollectionHttpFixture.IntAsync(CreditCollectionHttpFixture.OtherName, "SELECT COUNT(*) FROM dbo.BranchDebtCollections WHERE CollectionOperationID = '" + expiredId + "' AND Status = N'ReconciliationRequired';"));
        await using var leased = new SqlCommand("UPDATE dbo.BranchCollectionOutbox SET Status = N'Pending', LeaseOwner = N'held', LeaseUntil = DATEADD(minute, 5, SYSUTCDATETIME()) WHERE CreditOperationID = @operation;", branch);
        leased.Parameters.AddWithValue("@operation", pendingId);
        await leased.ExecuteNonQueryAsync();
        Assert.Null(await collections.RecoverOnceAsync(central, branch, "late-worker", CancellationToken.None));
    }

    private async Task InsertCollectionAsync(Guid operationId, decimal amount, string intentStatus, string branchStatus, DateTime expiresAt)
    {
        await CreditCollectionHttpFixture.ExecuteAsync(CreditCollectionHttpFixture.Connection(CreditCollectionHttpFixture.CentralName), """
            INSERT INTO dbo.CreditCollectionIntents (CollectionOperationID, CustomerID, BranchID, Amount, PaymentMethodCode, Status, IdempotencyKey, ExpiresAt, CreatedBy)
            VALUES (@operation, @customer, @branch, 1, N'Cash', N'Authorized', @operation, DATEADD(minute, 10, SYSUTCDATETIME()), @actor);
            INSERT INTO dbo.CreditCollectionReservations (ReservationID, CollectionOperationID, ReceivableID, Amount)
            VALUES (NEWID(), @operation, @receivable, 1);
            """,
            ("@operation", operationId), ("@customer", fixture.RecoveryCustomerId), ("@branch", fixture.OtherBranchId), ("@actor", fixture.CollectorId), ("@receivable", fixture.RecoveryReceivableId));
        await using var central = new SqlConnection(CreditCollectionHttpFixture.Connection(CreditCollectionHttpFixture.CentralName));
        await central.OpenAsync();
        await using var amounts = new SqlCommand("UPDATE dbo.CreditCollectionIntents SET Amount = @amount, Status = @status, ExpiresAt = @expires WHERE CollectionOperationID = @operation; UPDATE dbo.CreditCollectionReservations SET Amount = @amount WHERE CollectionOperationID = @operation;", central);
        amounts.Parameters.AddWithValue("@amount", amount);
        amounts.Parameters.AddWithValue("@status", intentStatus);
        amounts.Parameters.AddWithValue("@expires", expiresAt);
        amounts.Parameters.AddWithValue("@operation", operationId);
        await amounts.ExecuteNonQueryAsync();
        await using var branch = new SqlConnection(CreditCollectionHttpFixture.Connection(CreditCollectionHttpFixture.OtherName));
        await branch.OpenAsync();
        await using var insert = new SqlCommand("""
            INSERT INTO dbo.BranchDebtCollections (CollectionOperationID, CustomerID, BranchID, CashSessionID, DeviceID, CashierUserID, Amount, PaymentMethodCode, Status)
            VALUES (@operation, @customer, @branch, @session, @device, @actor, @amount, N'Cash', @status);
            INSERT INTO dbo.BranchCollectionOutbox (OutboxEventID, CreditOperationID, BranchID, CustomerID, Payload, Status, NextRetryAt, LeaseUntil)
            VALUES (NEWID(), @operation, @branch, @customer, @payload, @status, DATEADD(minute, 10, SYSUTCDATETIME()), DATEADD(minute, 10, SYSUTCDATETIME()));
            """, branch);
        insert.Parameters.AddWithValue("@operation", operationId);
        insert.Parameters.AddWithValue("@customer", fixture.RecoveryCustomerId);
        insert.Parameters.AddWithValue("@branch", fixture.OtherBranchId);
        insert.Parameters.AddWithValue("@session", fixture.OtherSessionId);
        insert.Parameters.AddWithValue("@device", fixture.OtherDeviceId);
        insert.Parameters.AddWithValue("@actor", fixture.CollectorId);
        insert.Parameters.AddWithValue("@amount", amount);
        insert.Parameters.AddWithValue("@status", branchStatus);
        insert.Parameters.AddWithValue("@payload", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{amount}|{fixture.CustomerId:N}|{fixture.OtherBranchId:N}|{Guid.Empty:N}"));
        await insert.ExecuteNonQueryAsync();
    }

    private async Task<HttpClient> LoginAsync(string database, bool enabled, string username, Guid deviceId)
    {
        var host = new CollectionHost(database, enabled);
        hosts.Add(host);
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Device-Id", deviceId.ToString());
        client.DefaultRequestHeaders.Add("X-Device-Token", CreditCollectionHttpFixture.DeviceToken);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = CreditCollectionHttpFixture.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("samesite=strict", login.Headers.GetValues("Set-Cookie").First(), StringComparison.OrdinalIgnoreCase);
        return client;
    }

    public void Dispose()
    {
        foreach (var host in hosts) host.Dispose();
    }

    private static object Body(Guid operationId, Guid customerId, decimal amount, Guid? receivableId, Guid? cashSessionId = null, Guid? actorUserId = null, decimal? claimedOutstanding = null) =>
        new { operationId, customerId, amount, receivableId, cashSessionId, actorUserId, outstanding = claimedOutstanding, paymentMethod = "Cash" };

    private static async Task<Guid> GuidAsync(string name, string sql)
    {
        await using var connection = new SqlConnection(CreditCollectionHttpFixture.Connection(name));
        await connection.OpenAsync();
        return (Guid)(await new SqlCommand(sql, connection).ExecuteScalarAsync() ?? Guid.Empty);
    }

    private sealed record SummaryBody(decimal Outstanding);
    private sealed record QuoteBody(ReceivableBody[] Receivables);
    private sealed record ReceivableBody(Guid ReceivableId);
    private sealed record DrawerBody(decimal? ExpectedCashPreview);

    private sealed class CollectionHost(string database, bool enabled) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "XtraPOS.Api")));
            builder.UseSetting("SqlServer:Server", "localhost");
            builder.UseSetting("SqlServer:Database", database);
            builder.UseSetting("CreditCollections:Enabled", enabled ? "true" : "false");
            builder.UseSetting("CreditSales:Enabled", "false");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["SqlServer:Server"] = "localhost",
                ["SqlServer:Database"] = database,
                ["CreditCollections:Enabled"] = enabled ? "true" : "false",
                ["CreditSales:Enabled"] = "false",
            }));
        }

        public new HttpClient CreateClient() => CreateClient(new WebApplicationFactoryClientOptions());

        public new HttpClient CreateClient(WebApplicationFactoryClientOptions options)
        {
            var client = base.CreateClient(options);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            return client;
        }
    }
}
