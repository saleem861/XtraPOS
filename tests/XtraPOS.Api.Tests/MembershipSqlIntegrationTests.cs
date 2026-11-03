using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using XtraPOS.Api.Membership;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class MembershipSqlIntegrationTests
{
    private const string DatabaseName = "XtraPOS_Central_Test_Membership";

    [Fact]
    public async Task Membership_requests_are_single_use_and_sale_scoped()
    {
        CentralDatabase.Require(DatabaseName);
        var cs = Connection(DatabaseName);
        await DropAsync();
        try
        {
            await MasterAsync($"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}] COLLATE Arabic_CI_AS;");
            await new CentralMigrationRunner().ApplyAsync(cs, CancellationToken.None);
            var business = Guid.NewGuid();
            var otherBusiness = Guid.NewGuid();
            var customer = Guid.NewGuid();
            var otherCustomer = Guid.NewGuid();
            var branch = Guid.NewGuid();
            var counter = Guid.NewGuid();
            var otherCounter = Guid.NewGuid();
            var sale = Guid.NewGuid();
            var nextSale = Guid.NewGuid();
            var cashier = Guid.NewGuid();
            var identity = Guid.NewGuid();
            var session = Guid.NewGuid();
            var request = Guid.NewGuid();
            await ExecuteAsync(cs, """
                INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt) VALUES (@business, N'Membership', 1, SYSUTCDATETIME()), (@otherBusiness, N'Other', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt) VALUES (@customer, @business, N'Member', 1, SYSUTCDATETIME(), SYSUTCDATETIME()), (@otherCustomer, @business, N'Other', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CustomerMembershipIdentities (IdentityID, BusinessID, CustomerID, Provider, ProviderSubject, LinkedAt, Status, CreatedAt, UpdatedAt)
                VALUES (@identity, @business, @customer, N'Google', N'subject-1', SYSUTCDATETIME(), N'Active', SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.CustomerMembershipSessions (SessionID, BusinessID, CustomerID, IdentityID, SessionTokenHash, CreatedAt, LastUsedAt, IdleExpiresAt, AbsoluteExpiresAt, LastSeenAt)
                VALUES (@session, @business, @customer, @identity, HASHBYTES('SHA2_256', N'session-secret'), SYSUTCDATETIME(), SYSUTCDATETIME(), DATEADD(day, 1, SYSUTCDATETIME()), DATEADD(day, 30, SYSUTCDATETIME()), SYSUTCDATETIME());
                INSERT INTO dbo.MembershipVerificationRequests (VerificationRequestID, BusinessID, BranchID, CounterID, CustomerID, CashierUserID, SaleContextID, QRTokenHash, Status, CreatedAt, ExpiresAt, CorrelationID)
                VALUES (@request, @business, @branch, @counter, @customer, @cashier, @sale, HASHBYTES('SHA2_256', N'qr-secret'), N'Pending', SYSUTCDATETIME(), DATEADD(second, 60, SYSUTCDATETIME()), NEWID());
                """,
                ("@business", business), ("@otherBusiness", otherBusiness), ("@customer", customer), ("@otherCustomer", otherCustomer),
                ("@identity", identity), ("@session", session), ("@request", request), ("@branch", branch), ("@counter", counter), ("@cashier", cashier), ("@sale", sale));
            await using (var hash = new SqlConnection(cs))
            {
                await hash.OpenAsync();
                await using var set = new SqlCommand("UPDATE dbo.CustomerMembershipSessions SET SessionTokenHash = @hash WHERE SessionID = @session; UPDATE dbo.MembershipVerificationRequests SET QRTokenHash = @qr WHERE VerificationRequestID = @request;", hash);
                set.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = MembershipTokens.Hash("session-secret");
                set.Parameters.Add("@qr", SqlDbType.Binary, 32).Value = MembershipTokens.Hash("qr-secret");
                set.Parameters.AddWithValue("@session", session);
                set.Parameters.AddWithValue("@request", request);
                Assert.Equal(2, await set.ExecuteNonQueryAsync());
            }

            Assert.Equal(1, await ScalarAsync(cs, "SELECT COUNT(*) FROM dbo.CustomerMembershipSessions WHERE SessionID = @id AND RevokedAt IS NULL;", session));
            await ExecuteAsync(cs, "UPDATE dbo.CustomerMembershipSessions SET RevokedAt = SYSUTCDATETIME(), RevocationReason = N'StaffRevokeAll' WHERE CustomerID = @customer AND RevokedAt IS NULL;", ("@customer", customer));
            Assert.Equal(0, await ScalarAsync(cs, "SELECT COUNT(*) FROM dbo.CustomerMembershipSessions WHERE SessionID = @id AND RevokedAt IS NULL;", session));

            await ExecuteAsync(cs, "UPDATE dbo.CustomerMembershipSessions SET RevokedAt = NULL WHERE SessionID = @session;", ("@session", session));
            var first = VerifyAsync(cs, request, session);
            var second = VerifyAsync(cs, request, session);
            var counts = await Task.WhenAll(first, second);
            Assert.Equal(1, counts.Sum());
            Assert.Equal("Verified", await TextAsync(cs, "SELECT Status FROM dbo.MembershipVerificationRequests WHERE VerificationRequestID = @id;", request));
            Assert.Equal(0, await VerifyAsync(cs, request, session));

            var expired = Guid.NewGuid();
            await ExecuteAsync(cs, """
                INSERT INTO dbo.MembershipVerificationRequests (VerificationRequestID, BusinessID, BranchID, CounterID, CustomerID, CashierUserID, SaleContextID, QRTokenHash, Status, CreatedAt, ExpiresAt, CorrelationID)
                VALUES (@request, @business, @branch, @counter, @customer, @cashier, @sale, HASHBYTES('SHA2_256', N'expired-qr'), N'Pending', DATEADD(minute, -2, SYSUTCDATETIME()), DATEADD(second, -1, SYSUTCDATETIME()), NEWID());
                """,
                ("@request", expired), ("@business", business), ("@branch", branch), ("@counter", counter), ("@customer", customer), ("@cashier", cashier), ("@sale", sale));
            await using (var hash = new SqlConnection(cs))
            {
                await hash.OpenAsync();
                await using var set = new SqlCommand("UPDATE dbo.MembershipVerificationRequests SET QRTokenHash = @qr WHERE VerificationRequestID = @request;", hash);
                set.Parameters.Add("@qr", SqlDbType.Binary, 32).Value = MembershipTokens.Hash("expired-qr");
                set.Parameters.AddWithValue("@request", expired);
                await set.ExecuteNonQueryAsync();
            }
            Assert.Equal(0, await VerifyAsync(cs, expired, session));
            Assert.Equal("Pending", await TextAsync(cs, "SELECT Status FROM dbo.MembershipVerificationRequests WHERE VerificationRequestID = @id;", expired));

            var wrong = MembershipRules.RejectionCode(Sample(customer, otherCustomer, business, business, counter, counter, sale, sale));
            Assert.Equal("MEMBERSHIP_WRONG_CUSTOMER", wrong);
            Assert.Equal("MEMBERSHIP_WRONG_BUSINESS", MembershipRules.RejectionCode(Sample(customer, customer, business, otherBusiness, counter, counter, sale, sale)));
            Assert.Equal("MEMBERSHIP_WRONG_BUSINESS", MembershipRules.RejectionCode(Sample(customer, customer, business, business, counter, otherCounter, sale, sale)));

            await ExecuteAsync(cs, """
                UPDATE dbo.MembershipVerificationRequests SET Status = N'Cancelled', CancelledAt = SYSUTCDATETIME()
                WHERE BusinessID = @business AND BranchID = @branch AND CounterID = @counter AND SaleContextID = @sale AND Status = N'Pending';
                INSERT INTO dbo.MembershipVerificationRequests (VerificationRequestID, BusinessID, BranchID, CounterID, CustomerID, CashierUserID, SaleContextID, QRTokenHash, Status, CreatedAt, ExpiresAt, CorrelationID)
                VALUES (@next, @business, @branch, @counter, @customer, @cashier, @nextSale, HASHBYTES('SHA2_256', N'next-sale'), N'Pending', SYSUTCDATETIME(), DATEADD(second, 60, SYSUTCDATETIME()), NEWID());
                """,
                ("@business", business), ("@branch", branch), ("@counter", counter), ("@sale", sale), ("@next", Guid.NewGuid()), ("@nextSale", nextSale), ("@customer", customer), ("@cashier", cashier));
            Assert.Equal("Verified", await TextAsync(cs, "SELECT Status FROM dbo.MembershipVerificationRequests WHERE SaleContextID = @id AND Status = N'Verified';", sale));
            Assert.Equal(0, await ScalarAsync(cs, "SELECT COUNT(*) FROM dbo.MembershipVerificationRequests WHERE SaleContextID = @id AND Status = N'Verified';", nextSale));
            Assert.Equal(0, await ScalarAsync(cs, "SELECT COUNT(*) FROM dbo.MembershipVerificationRequests WHERE SaleContextID = @id AND Status = N'Pending';", sale));
        }
        finally
        {
            await DropAsync();
        }
    }

    private static MembershipVerificationFacts Sample(Guid requestCustomer, Guid sessionCustomer, Guid requestBusiness, Guid sessionBusiness, Guid requestCounter, Guid expectedCounter, Guid requestSale, Guid expectedSale)
    {
        var branch = Guid.NewGuid();
        return new MembershipVerificationFacts(true, true, false, false, sessionCustomer, sessionBusiness, requestCustomer, requestBusiness, branch, requestCounter, requestSale, branch, expectedCounter, expectedSale, MembershipRequestStatus.Pending, DateTime.UtcNow.AddSeconds(60), DateTime.UtcNow, true, true);
    }

    private static async Task<int> VerifyAsync(string cs, Guid requestId, Guid sessionId)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            UPDATE dbo.MembershipVerificationRequests
            SET Status = N'Verified', VerifiedAt = SYSUTCDATETIME(), ConsumedAt = SYSUTCDATETIME(), VerifiedByMembershipSessionID = @session
            WHERE VerificationRequestID = @id AND Status = N'Pending' AND ExpiresAt > SYSUTCDATETIME();
            """, connection);
        command.Parameters.AddWithValue("@session", sessionId);
        command.Parameters.AddWithValue("@id", requestId);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAsync(string cs, string sql, params (string Name, Guid Value)[] parameters)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.Add(parameter.Name, SqlDbType.UniqueIdentifier).Value = parameter.Value;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string cs, string sql, Guid id)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<string> TextAsync(string cs, string sql, Guid id)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToString(await command.ExecuteScalarAsync()) ?? "";
    }

    private static string Connection(string name) => new SqlConnectionStringBuilder { DataSource = "localhost", InitialCatalog = name, IntegratedSecurity = true, Encrypt = true, TrustServerCertificate = true, ApplicationName = "XtraPOS.Tests" }.ConnectionString;

    private static async Task DropAsync()
    {
        if (!DatabaseName.Contains("_Test_Membership", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing to drop a database outside the membership test.");
        SqlConnection.ClearAllPools();
        await MasterAsync($"IF DB_ID(N'{DatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}]; END");
    }

    private static async Task MasterAsync(string sql)
    {
        await using var connection = new SqlConnection("Server=localhost;Database=master;Integrated Security=True;Encrypt=True;TrustServerCertificate=True;");
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }
}
