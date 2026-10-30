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

[CollectionDefinition("CreditReversal", DisableParallelization = true)]
public sealed class CreditReversalCollection : ICollectionFixture<CreditReversalFixture>;

[Collection("CreditReversal")]
public sealed class CreditReversalTests(CreditReversalFixture fixture) : IDisposable
{
    private readonly List<IDisposable> hosts = [];

    [Fact]
    public async Task Full_reversal_restores_debt_and_preserves_original_records()
    {
        var seeded = await fixture.SeedCompletedAsync(300m);
        var outcome = await fixture.ReverseAsync(seeded, "RefundToCustomer");
        Assert.Equal(CreditReversalOutcome.Completed, outcome);
        Assert.Equal(800m, await fixture.MoneyAsync($"SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE ReceivableID = '{seeded.ReceivableId}';"));
        Assert.Equal(800m, await fixture.MoneyAsync($"SELECT Total FROM dbo.Sales WHERE SaleID = '{seeded.SaleId}';", origin: true));
        Assert.Equal(1, await fixture.IntAsync($"SELECT COUNT(*) FROM dbo.BranchDebtCollections WHERE CollectionOperationID = '{seeded.CollectionId}' AND Amount = 300 AND Status = N'Completed';", origin: false));
        Assert.Equal(1, await fixture.IntAsync($"SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE IdempotencyKey = N'collection:{seeded.CollectionId:N}' AND TransactionType = N'Repayment';"));
        Assert.Equal(1, await fixture.IntAsync($"SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE IdempotencyKey = N'reversal:{seeded.ReversalId:N}' AND TransactionType = N'Reversal' AND ReversalReference IS NOT NULL;"));
        Assert.Equal(1, await fixture.IntAsync($"SELECT COUNT(*) FROM dbo.CreditPaymentAllocations WHERE AllocationID = '{seeded.AllocationId}';"));
        Assert.Equal(1, await fixture.IntAsync($"SELECT COUNT(*) FROM dbo.CreditAllocationReversals WHERE OriginalAllocationID = '{seeded.AllocationId}' AND Amount = 300;"));
        Assert.Equal(0, await fixture.IntAsync("SELECT COUNT(*) FROM dbo.Sales;", origin: false));
        Assert.Equal(0m, await fixture.MoneyAsync($"SELECT SUM(CASE WHEN TransactionType = N'Repayment' THEN Amount ELSE -Amount END) FROM dbo.CustomerCreditLedger WHERE TransactionID IN ('{seeded.CollectionId}', '{seeded.ReversalId}');"));
        Assert.Equal("RefundOutflow", await fixture.TextAsync($"SELECT CashEffect FROM dbo.BranchCollectionReversals WHERE ReversalOperationID = '{seeded.ReversalId}';", origin: false));
    }

    [Fact]
    public async Task Incorrect_recording_uses_a_distinct_cash_correction()
    {
        var seeded = await fixture.SeedCompletedAsync(300m);
        Assert.Equal(CreditReversalOutcome.Completed, await fixture.ReverseAsync(seeded, "IncorrectRecording"));
        Assert.Equal("RecordingCorrection", await fixture.TextAsync($"SELECT CashEffect FROM dbo.BranchCollectionReversals WHERE ReversalOperationID = '{seeded.ReversalId}';", origin: false));
        Assert.Equal(300m, await fixture.MoneyAsync($"SELECT Amount FROM dbo.BranchDebtCollections WHERE CollectionOperationID = '{seeded.CollectionId}';", branch: true));
        Assert.Equal(0m, await fixture.NetCashAsync(seeded));
    }

    [Fact]
    public async Task Duplicate_reversal_is_idempotent_and_a_second_operation_is_rejected()
    {
        var seeded = await fixture.SeedCompletedAsync(300m);
        Assert.Equal(CreditReversalOutcome.Completed, await fixture.ReverseAsync(seeded, "RefundToCustomer"));
        Assert.Equal(CreditReversalOutcome.Completed, await fixture.ReverseAsync(seeded, "RefundToCustomer"));
        Assert.Equal(1, await fixture.IntAsync($"SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE IdempotencyKey = N'reversal:{seeded.ReversalId:N}';"));
        var other = seeded with { ReversalId = Guid.NewGuid() };
        Assert.Equal(CreditReversalOutcome.Rejected, await fixture.ReverseAsync(other, "RefundToCustomer"));
        Assert.Equal(800m, await fixture.MoneyAsync($"SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE ReceivableID = '{seeded.ReceivableId}';"));
    }

    [Fact]
    public async Task Conflicting_replay_requires_reconciliation()
    {
        var seeded = await fixture.SeedCompletedAsync(300m);
        Assert.Equal(CreditReversalOutcome.Completed, await fixture.ReverseAsync(seeded, "RefundToCustomer"));
        Assert.Equal(CreditReversalOutcome.ReconciliationRequired, await fixture.ReverseAsync(seeded, "IncorrectRecording"));
        Assert.Equal(1, await fixture.IntAsync($"SELECT COUNT(*) FROM dbo.CreditAllocationReversals WHERE ReversalOperationID = '{seeded.ReversalId}';"));
    }

    [Fact]
    public async Task Concurrent_reversals_create_one_movement()
    {
        var seeded = await fixture.SeedCompletedAsync(300m);
        var left = seeded with { ReversalId = Guid.NewGuid() };
        var right = seeded with { ReversalId = Guid.NewGuid() };
        var results = await Task.WhenAll(fixture.ReverseAsync(left, "RefundToCustomer"), fixture.ReverseAsync(right, "RefundToCustomer"));
        Assert.Contains(CreditReversalOutcome.Completed, results);
        Assert.Contains(CreditReversalOutcome.Rejected, results);
        Assert.Equal(1, await fixture.IntAsync($"SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE TransactionType = N'Reversal' AND PaymentID = '{seeded.CollectionId}';"));
    }

    [Fact]
    public async Task Collector_and_supervisor_cannot_reverse()
    {
        var seeded = await fixture.SeedCompletedAsync(300m);
        Assert.Equal(CreditReversalOutcome.Unauthorized, await fixture.ReverseAsync(seeded, "RefundToCustomer", fixture.CollectorId));
        Assert.Equal(CreditReversalOutcome.Unauthorized, await fixture.ReverseAsync(seeded, "RefundToCustomer", fixture.SupervisorId));
        Assert.Equal(500m, await fixture.MoneyAsync($"SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE ReceivableID = '{seeded.ReceivableId}';"));
    }

    [Fact]
    public async Task Foreign_branch_is_rejected()
    {
        var seeded = await fixture.SeedCompletedAsync(300m);
        Assert.Equal(CreditReversalOutcome.Rejected, await fixture.ReverseAsync(seeded with { BranchId = fixture.OtherBranchId }, "RefundToCustomer"));
    }

    [Fact]
    public async Task Reversal_after_session_closure_uses_the_current_open_session()
    {
        var seeded = await fixture.SeedCompletedAsync(300m);
        var current = Guid.NewGuid();
        await fixture.ExecuteAsync(CreditReversalFixture.Collecting, """
            UPDATE dbo.POSCashSessions SET Status = N'Closed', ClosedByUserID = @actor, ClosedAt = SYSUTCDATETIME(), ExpectedCashAmount = 300, CountedCashAmount = 300, CashDifference = 0 WHERE CashSessionID = @session;
            INSERT INTO dbo.POSCashSessions (CashSessionID, BusinessID, BranchID, POSStationID, DeviceID, OpenedByUserID, OpenedAt, OpeningCashAmount, Status)
            VALUES (@current, @business, @branch, @station, @device, @actor, SYSUTCDATETIME(), 0, N'Open');
            """, ("@actor", fixture.AdminId), ("@session", seeded.SessionId), ("@current", current), ("@business", fixture.BusinessId), ("@branch", fixture.CollectingBranchId), ("@station", fixture.StationId), ("@device", fixture.DeviceId));
        Assert.Equal(CreditReversalOutcome.Completed, await fixture.ReverseAsync(seeded, "RefundToCustomer"));
        Assert.Equal(current, await fixture.GuidAsync($"SELECT CashSessionID FROM dbo.BranchCollectionReversals WHERE ReversalOperationID = '{seeded.ReversalId}';", origin: false));
        Assert.Equal(seeded.SessionId, await fixture.GuidAsync($"SELECT CashSessionID FROM dbo.BranchDebtCollections WHERE CollectionOperationID = '{seeded.CollectionId}';", origin: false));
    }

    [Fact]
    public async Task Skipped_finalization_is_recovered_without_a_duplicate_ledger()
    {
        var seeded = await fixture.SeedCompletedAsync(300m);
        Assert.Equal(CreditReversalOutcome.PendingCentral, await fixture.ReverseAsync(seeded, "RefundToCustomer", skipFinalization: true));
        Assert.Equal(CreditReversalOutcome.Completed, await fixture.RecoverAsync());
        Assert.Equal(CreditReversalOutcome.Completed, await fixture.ReverseAsync(seeded, "RefundToCustomer"));
        Assert.Equal(1, await fixture.IntAsync($"SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE IdempotencyKey = N'reversal:{seeded.ReversalId:N}';"));
        Assert.Null(await fixture.RecoverAsync());
    }

    [Fact]
    public async Task Authorized_reversal_behind_cash_requires_reconciliation()
    {
        var seeded = await fixture.SeedCompletedAsync(300m);
        Assert.Equal(CreditReversalOutcome.PendingCentral, await fixture.ReverseAsync(seeded, "RefundToCustomer", skipFinalization: true));
        await fixture.ExecuteAsync(CreditReversalFixture.Central, "UPDATE dbo.CreditRepaymentReversals SET Status = N'Authorized' WHERE ReversalOperationID = @operation;", ("@operation", seeded.ReversalId));
        Assert.Equal(CreditReversalOutcome.ReconciliationRequired, await fixture.RecoverAsync());
        Assert.Equal(0, await fixture.IntAsync($"SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE IdempotencyKey = N'reversal:{seeded.ReversalId:N}';"));
    }

    [Fact]
    public async Task Restored_debt_is_visible_and_blocks_new_borrowing_above_the_limit()
    {
        var seeded = await fixture.SeedCompletedAsync(300m);
        await fixture.ExecuteAsync(CreditReversalFixture.Central, """
            INSERT INTO dbo.CustomerCreditPolicies (CustomerID, CreditEnabled, CreditLimit, PaymentTermDays, CreditStatus)
            VALUES (@customer, 1, 100, 30, N'Active');
            """, ("@customer", seeded.CustomerId));
        Assert.Equal(CreditReversalOutcome.Completed, await fixture.ReverseAsync(seeded, "IncorrectRecording"));
        Assert.Equal(800m, await fixture.MoneyAsync($"SELECT OutstandingAmount FROM dbo.CustomerReceivables WHERE CustomerID = '{seeded.CustomerId}';"));
        Assert.Equal(0m, await fixture.MoneyAsync($"SELECT CASE WHEN CreditLimit - (SELECT SUM(OutstandingAmount) FROM dbo.CustomerReceivables WHERE CustomerID = '{seeded.CustomerId}') > 0 THEN CreditLimit - (SELECT SUM(OutstandingAmount) FROM dbo.CustomerReceivables WHERE CustomerID = '{seeded.CustomerId}') ELSE 0 END FROM dbo.CustomerCreditPolicies WHERE CustomerID = '{seeded.CustomerId}';"));
    }

    [Fact]
    public async Task Http_requires_reverse_permission_and_trusted_origin()
    {
        var seeded = await fixture.SeedCompletedAsync(300m);
        using var collector = await LoginAsync("collector", fixture.DeviceId, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await collector.PostAsJsonAsync("/api/pos/credit-reversals", Body(seeded, "RefundToCustomer"))).StatusCode);
        using var supervisor = await LoginAsync("supervisor", fixture.DeviceId, null);
        Assert.Equal(HttpStatusCode.Forbidden, (await supervisor.PostAsJsonAsync("/api/pos/credit-reversals", Body(seeded, "RefundToCustomer"))).StatusCode);
        using var admin = await LoginAsync("admin", fixture.DeviceId, ["https://trusted.example"]);
        admin.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://evil.example");
        admin.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsJsonAsync("/api/pos/credit-reversals", Body(seeded, "RefundToCustomer"))).StatusCode);
        using var missing = await LoginAsync("admin", fixture.DeviceId, ["https://trusted.example"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await missing.PostAsJsonAsync("/api/pos/credit-reversals", Body(seeded, "RefundToCustomer"))).StatusCode);
        using var trusted = await LoginAsync("admin", fixture.DeviceId, ["https://trusted.example"]);
        trusted.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "https://trusted.example");
        trusted.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Site", "same-site");
        Assert.Equal(HttpStatusCode.OK, (await trusted.PostAsJsonAsync("/api/pos/credit-reversals", Body(seeded, "RefundToCustomer"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await trusted.PostAsJsonAsync("/api/pos/credit-reversals", Body(seeded, "RefundToCustomer"))).StatusCode);
        Assert.Equal(1, await fixture.IntAsync($"SELECT COUNT(*) FROM dbo.CustomerCreditLedger WHERE IdempotencyKey = N'reversal:{seeded.ReversalId:N}';"));
    }

    [Fact]
    public async Task Empty_reason_is_rejected()
    {
        var seeded = await fixture.SeedCompletedAsync(300m);
        Assert.Equal(CreditReversalOutcome.Rejected, await fixture.ReverseAsync(seeded, ""));
    }

    private async Task<HttpClient> LoginAsync(string username, Guid deviceId, string[]? trusted)
    {
        var host = new ReversalHost(CreditReversalFixture.Collecting, trusted);
        hosts.Add(host);
        var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        client.DefaultRequestHeaders.Add("X-Device-Id", deviceId.ToString());
        client.DefaultRequestHeaders.Add("X-Device-Token", CreditReversalFixture.DeviceToken);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username, password = CreditReversalFixture.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private static object Body(ReversalSeed seeded, string reason) => new { operationId = seeded.ReversalId, collectionOperationId = seeded.CollectionId, customerId = seeded.CustomerId, reasonCode = reason, notes = "سبب الاختبار" };

    public void Dispose()
    {
        foreach (var host in hosts) host.Dispose();
    }

    private sealed class ReversalHost(string database, string[]? trusted) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "XtraPOS.Api")));
            builder.UseSetting("SqlServer:Server", "localhost");
            builder.UseSetting("SqlServer:Database", database);
            builder.UseSetting("CreditReversals:Enabled", "true");
            builder.UseSetting("CreditCollections:Enabled", "false");
            builder.UseSetting("CreditSales:Enabled", "false");
            var values = new Dictionary<string, string?>
            {
                ["SqlServer:Server"] = "localhost",
                ["SqlServer:Database"] = database,
                ["CreditReversals:Enabled"] = "true",
                ["CreditCollections:Enabled"] = "false",
                ["CreditSales:Enabled"] = "false",
            };
            if (trusted is not null)
            {
                for (var index = 0; index < trusted.Length; index++) values[$"CreditReversals:TrustedOrigins:{index}"] = trusted[index];
            }
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(values));
        }
    }
}

public sealed class CreditReversalFixture : IAsyncLifetime
{
    public const string Central = "XtraPOS_Central_Test_Reversal";
    public const string Collecting = "XtraPOS_Branch_Test_ReversalB";
    public const string Origin = "XtraPOS_Branch_Test_ReversalA";
    public const string Password = "Reverse1234";
    public const string DeviceToken = "reverse-token";
    public Guid BusinessId { get; } = Guid.NewGuid();
    public Guid CollectingBranchId { get; } = Guid.NewGuid();
    public Guid OtherBranchId { get; } = Guid.NewGuid();
    public Guid OriginBranchId { get; } = Guid.NewGuid();
    public Guid DeviceId { get; } = Guid.NewGuid();
    public Guid StationId { get; } = Guid.NewGuid();
    public Guid AdminId { get; } = Guid.NewGuid();
    public Guid SupervisorId { get; } = Guid.NewGuid();
    public Guid CollectorId { get; } = Guid.NewGuid();
    public Guid SessionId { get; } = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        CentralDatabase.Require(Central);
        TestDatabase.Require(Collecting);
        TestDatabase.Require(Origin);
        foreach (var name in new[] { Central, Collecting, Origin })
        {
            await DropAsync(name);
            await CreateAsync(name);
        }
        await new CentralMigrationRunner().ApplyAsync(Connection(Central), CancellationToken.None);
        await MigrateAsync(Collecting);
        await MigrateAsync(Origin);
        await SeedBranchAsync();
        await ExecuteAsync(Central, """
            INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Reversal', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt) VALUES
                (@origin, @business, N'01', N'فرع 01', N'Retail', 1, SYSUTCDATETIME()),
                (@collecting, @business, N'02', N'فرع 02', N'Retail', 1, SYSUTCDATETIME());
            """, ("@business", BusinessId), ("@origin", OriginBranchId), ("@collecting", CollectingBranchId));
    }

    public async Task<ReversalSeed> SeedCompletedAsync(decimal amount)
    {
        var seed = new ReversalSeed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), CollectingBranchId, SessionId);
        await ExecuteAsync(Central, """
            INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES (@customer, @business, N'زبون الدين', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.CustomerReceivables (ReceivableID, CustomerID, SourceSaleID, SourceBranchID, SourceSaleNumber, OriginalAmount, OutstandingAmount, DueDate, Status)
            VALUES (@receivable, @customer, @sale, @origin, 1, 800, 500, '2026-11-26', N'Partial');
            INSERT INTO dbo.CreditCollectionIntents (CollectionOperationID, CustomerID, BranchID, Amount, PaymentMethodCode, Status, IdempotencyKey, ExpiresAt, CreatedBy)
            VALUES (@collection, @customer, @branch, @amount, N'Cash', N'Completed', @collection, DATEADD(minute, 15, SYSUTCDATETIME()), @actor);
            INSERT INTO dbo.CustomerCreditLedger (LedgerEntryID, CustomerID, TransactionID, TransactionType, Amount, CurrencyCode, SourceBranchID, PaymentID, CreatedBy, IdempotencyKey)
            VALUES (@ledger, @customer, @collection, N'Repayment', @amount, N'ILS', @branch, @collection, @actor, N'collection:' + REPLACE(CONVERT(nvarchar(36), @collection), N'-', N''));
            INSERT INTO dbo.CreditPaymentAllocations (AllocationID, LedgerEntryID, ReceivableID, Amount) VALUES (@allocation, @ledger, @receivable, @amount);
            """,
            ("@customer", seed.CustomerId), ("@business", BusinessId), ("@receivable", seed.ReceivableId), ("@sale", seed.SaleId), ("@origin", OriginBranchId),
            ("@collection", seed.CollectionId), ("@branch", CollectingBranchId), ("@actor", AdminId), ("@ledger", Guid.NewGuid()), ("@allocation", seed.AllocationId), ("@amount", amount));
        await ExecuteAsync(Origin, """
            INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES (@customer, @business, N'زبون الدين', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.Sales (SaleID, BusinessID, BranchID, POSStationID, DeviceID, CartID, CustomerID, CashierUserID, CashSessionID, SaleNumber, Status, Subtotal, DiscountTotal, Total)
            VALUES (@sale, @business, @branch, @station, @device, NEWID(), @customer, @actor, @session, NEXT VALUE FOR dbo.SaleNumberSequence, N'Completed', 800, 0, 800);
            """, ("@customer", seed.CustomerId), ("@business", BusinessId), ("@sale", seed.SaleId), ("@branch", OriginBranchId), ("@station", StationId), ("@device", DeviceId), ("@actor", AdminId), ("@session", SessionId));
        await ExecuteAsync(Collecting, """
            INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES (@customer, @business, N'زبون الدين', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.BranchDebtCollections (CollectionOperationID, CustomerID, BranchID, CashSessionID, DeviceID, CashierUserID, Amount, PaymentMethodCode, Status)
            VALUES (@collection, @customer, @branch, @session, @device, @actor, @amount, N'Cash', N'Completed');
            """, ("@customer", seed.CustomerId), ("@business", BusinessId), ("@collection", seed.CollectionId), ("@branch", CollectingBranchId), ("@session", SessionId), ("@device", DeviceId), ("@actor", AdminId), ("@amount", amount));
        return seed with { ReversalId = Guid.NewGuid() };
    }

    public async Task<CreditReversalOutcome> ReverseAsync(ReversalSeed seed, string reason, Guid? actor = null, bool skipFinalization = false)
    {
        await using var central = new SqlConnection(Connection(Central));
        await using var branch = new SqlConnection(Connection(Collecting));
        await central.OpenAsync();
        await branch.OpenAsync();
        return await new CreditReversalService().ReverseAsync(central, branch, new CreditReversalRequest(seed.ReversalId, seed.CollectionId, seed.CustomerId, seed.BranchId, DeviceId, actor ?? AdminId, Guid.Empty, reason, "سبب الاختبار"), true, skipFinalization, CancellationToken.None);
    }

    public async Task<CreditReversalOutcome?> RecoverAsync()
    {
        await using var central = new SqlConnection(Connection(Central));
        await using var branch = new SqlConnection(Connection(Collecting));
        await central.OpenAsync();
        await branch.OpenAsync();
        return await new CreditReversalService().RecoverOnceAsync(central, branch, "reversal-worker", CancellationToken.None);
    }

    public async Task DisposeAsync()
    {
        await DropAsync(Central);
        await DropAsync(Collecting);
        await DropAsync(Origin);
    }

    public Task<decimal> MoneyAsync(string sql, bool origin = false, bool branch = false) => ScalarAsync<decimal>(branch ? Collecting : origin ? Origin : Central, sql);
    public Task<int> IntAsync(string sql, bool origin = false) => ScalarAsync<int>(origin ? Central : Collecting, sql);
    public async Task<int> IntAsync(string sql) => await ScalarAsync<int>(Central, sql);
    public Task<string> TextAsync(string sql, bool origin) => ScalarAsync<string>(origin ? Origin : Collecting, sql);
    public Task<Guid> GuidAsync(string sql, bool origin) => ScalarAsync<Guid>(origin ? Origin : Collecting, sql);

    public async Task<decimal> NetCashAsync(ReversalSeed seed)
    {
        var collected = await ScalarAsync<decimal>(Collecting, $"SELECT Amount FROM dbo.BranchDebtCollections WHERE CollectionOperationID = '{seed.CollectionId}';");
        var reversed = await ScalarAsync<decimal>(Collecting, $"SELECT COALESCE(SUM(Amount), 0) FROM dbo.BranchCollectionReversals WHERE CollectionOperationID = '{seed.CollectionId}' AND Status = N'Completed';");
        return collected - reversed;
    }

    private async Task<T> ScalarAsync<T>(string database, string sql)
    {
        await using var connection = new SqlConnection(Connection(database));
        await connection.OpenAsync();
        var value = await new SqlCommand(sql, connection).ExecuteScalarAsync();
        return (T)Convert.ChangeType(value, typeof(T))!;
    }

    private async Task SeedBranchAsync()
    {
        var adminHash = new PasswordService().Hash(new AppUser { UserId = AdminId, Username = "admin" }, Password);
        var supervisorHash = new PasswordService().Hash(new AppUser { UserId = SupervisorId, Username = "supervisor" }, Password);
        var collectorHash = new PasswordService().Hash(new AppUser { UserId = CollectorId, Username = "collector" }, Password);
        foreach (var (database, branchId, code) in new[] { (Collecting, CollectingBranchId, "02"), (Origin, OriginBranchId, "01") })
        {
            await ExecuteAsync(database, """
                INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Reversal', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt) VALUES (@branch, @business, @code, N'Branch', N'Retail', 1, SYSUTCDATETIME());
                INSERT INTO dbo.POSStations (StationID, BranchID, StationNumber, StationName, IsActive, CreatedAt) VALUES (@station, @branch, 1, N'Till', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Devices (DeviceID, BranchID, StationID, DeviceName, DeviceType, IsActive, RegisteredAt, CreatedAt) VALUES (@device, @branch, @station, N'Till', N'POS', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive) VALUES
                    (@admin, N'admin', N'ADMIN', N'Admin', N'pending', 1),
                    (@supervisor, N'supervisor', N'SUPERVISOR', N'Supervisor', N'pending', 1),
                    (@collector, N'collector', N'COLLECTOR', N'Collector', N'pending', 1);
                INSERT INTO dbo.UserBranchAccess (UserID, BranchID) VALUES (@admin, @branch), (@supervisor, @branch), (@collector, @branch);
                INSERT INTO dbo.UserRoles (UserID, RoleID) SELECT @admin, RoleID FROM dbo.Roles WHERE RoleCode = N'Admin';
                INSERT INTO dbo.UserRoles (UserID, RoleID) SELECT @supervisor, RoleID FROM dbo.Roles WHERE RoleCode = N'Supervisor';
                INSERT INTO dbo.UserRoles (UserID, RoleID) SELECT @collector, RoleID FROM dbo.Roles WHERE RoleCode = N'User';
                INSERT INTO dbo.POSCashSessions (CashSessionID, BusinessID, BranchID, POSStationID, DeviceID, OpenedByUserID, OpenedAt, OpeningCashAmount, Status)
                VALUES (@session, @business, @branch, @station, @device, @admin, SYSUTCDATETIME(), 0, N'Open');
                INSERT INTO dbo.SyncEndpoints (EndpointID, DatabaseRole, LocationKind, ServerName, DatabaseName, AuthenticationType, EncryptConnection, TrustServerCertificate, Status)
                VALUES (NEWID(), N'Central', N'Local', N'localhost', @central, N'Integrated', 1, 1, N'Ready');
                """, ("@business", BusinessId), ("@branch", branchId), ("@code", Guid.Empty), ("@station", StationId), ("@device", DeviceId),
                ("@admin", AdminId), ("@supervisor", SupervisorId), ("@collector", CollectorId), ("@session", SessionId), ("@central", Guid.Empty));
            await using var connection = new SqlConnection(Connection(database));
            await connection.OpenAsync();
            await using var command = new SqlCommand("UPDATE dbo.Branches SET BranchCode = @code WHERE BranchID = @branch; UPDATE dbo.Users SET PasswordHash = @adminHash WHERE UserID = @admin; UPDATE dbo.Users SET PasswordHash = @supervisorHash WHERE UserID = @supervisor; UPDATE dbo.Users SET PasswordHash = @collectorHash WHERE UserID = @collector; UPDATE dbo.Devices SET DeviceTokenHash = @token WHERE DeviceID = @device; UPDATE dbo.SyncEndpoints SET DatabaseName = @central WHERE DatabaseRole = N'Central';", connection);
            command.Parameters.AddWithValue("@code", code);
            command.Parameters.AddWithValue("@branch", branchId);
            command.Parameters.AddWithValue("@adminHash", adminHash);
            command.Parameters.AddWithValue("@supervisorHash", supervisorHash);
            command.Parameters.AddWithValue("@collectorHash", collectorHash);
            command.Parameters.AddWithValue("@admin", AdminId);
            command.Parameters.AddWithValue("@supervisor", SupervisorId);
            command.Parameters.AddWithValue("@collector", CollectorId);
            command.Parameters.Add("@token", System.Data.SqlDbType.VarBinary, 64).Value = DeviceTokenService.Hash(DeviceToken);
            command.Parameters.AddWithValue("@device", DeviceId);
            command.Parameters.AddWithValue("@central", Central);
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task ExecuteAsync(string database, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new SqlConnection(Connection(database));
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var parameter in parameters)
        {
            var value = parameter.Value;
            if (parameter.Name == "@central" && value is Guid empty && empty == Guid.Empty) value = Central;
            if (parameter.Name == "@code" && value is Guid) value = "00";
            if (value is decimal money)
            {
                var moneyParameter = command.Parameters.Add(parameter.Name, System.Data.SqlDbType.Decimal);
                moneyParameter.Precision = 19;
                moneyParameter.Scale = 4;
                moneyParameter.Value = money;
                continue;
            }
            command.Parameters.AddWithValue(parameter.Name, value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private static async Task MigrateAsync(string database)
    {
        var connections = new SqlConnectionFactory(Microsoft.Extensions.Options.Options.Create(new XtraPOS.Api.Configuration.SqlServerOptions { Server = "localhost", Database = database, ConnectTimeoutSeconds = 15 }));
        await new XtraPOS.Api.Data.Migrations.MigrationRunner(connections, Microsoft.Extensions.Logging.Abstractions.NullLogger<XtraPOS.Api.Data.Migrations.MigrationRunner>.Instance).ApplyPendingAsync(CancellationToken.None);
    }

    public static string Connection(string name) => new SqlConnectionStringBuilder { DataSource = "localhost", InitialCatalog = name, IntegratedSecurity = true, Encrypt = true, TrustServerCertificate = true, ApplicationName = "XtraPOS.Tests" }.ConnectionString;

    private static async Task CreateAsync(string name)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder { DataSource = "localhost", InitialCatalog = "master", IntegratedSecurity = true, Encrypt = true, TrustServerCertificate = true }.ConnectionString);
        await connection.OpenAsync();
        await new SqlCommand($"CREATE DATABASE [{name}] COLLATE Arabic_100_CI_AI;", connection).ExecuteNonQueryAsync();
    }

    private static async Task DropAsync(string name)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder { DataSource = "localhost", InitialCatalog = "master", IntegratedSecurity = true, Encrypt = true, TrustServerCertificate = true }.ConnectionString);
        await connection.OpenAsync();
        await new SqlCommand($"IF DB_ID(N'{name}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END", connection).ExecuteNonQueryAsync();
    }
}

public sealed record ReversalSeed(Guid CustomerId, Guid ReceivableId, Guid SaleId, Guid CollectionId, Guid AllocationId, Guid ReversalId, Guid BranchId, Guid SessionId);
