using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Central;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class MembershipEnrollmentSqlTests
{
    private const string DatabaseName = "XtraPOS_Central_Test_MembershipEnroll";

    [Fact]
    public async Task Prove_and_commit_bind_one_identity_and_reject_unsafe_retries()
    {
        CentralDatabase.Require(DatabaseName);
        var cs = Connection(DatabaseName);
        await DropAsync();
        try
        {
            await MasterAsync($"IF DB_ID(N'{DatabaseName}') IS NULL CREATE DATABASE [{DatabaseName}] COLLATE Arabic_CI_AS;");
            await new CentralMigrationRunner().ApplyAsync(cs, CancellationToken.None);
            Assert.Equal(1, await ScalarAsync(cs, "SELECT COUNT(*) FROM dbo.DatabaseSchemaHistory WHERE MigrationID = N'022_MembershipPendingEnrollment';"));

            var business = Guid.NewGuid();
            var customer = Guid.NewGuid();
            var other = Guid.NewGuid();
            var attempt = Guid.NewGuid();
            var duplicate = Guid.NewGuid();
            var expired = Guid.NewGuid();
            var unproved = Guid.NewGuid();
            await ExecuteAsync(cs, """
                INSERT INTO dbo.Businesses (BusinessID, BusinessName, IsActive, CreatedAt)
                VALUES (@business, N'Enrollment', 1, SYSUTCDATETIME());
                INSERT INTO dbo.Customers (CustomerID, BusinessID, Name, IsActive, CreatedAt, UpdatedAt)
                VALUES (@customer, @business, N'New', 1, SYSUTCDATETIME(), SYSUTCDATETIME()),
                       (@other, @business, N'Other', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
                INSERT INTO dbo.MembershipAuthAttempts (AttemptID, Provider, StateHash, Nonce, PkceVerifier, ProviderSubject, Status, ExpiresAt, CreatedAt)
                VALUES
                    (@attempt, N'Google', HASHBYTES('SHA2_256', N'state-1'), N'nonce', N'verifier', N'subject-new', N'Authenticated', DATEADD(minute, 10, SYSUTCDATETIME()), SYSUTCDATETIME()),
                    (@duplicate, N'Google', HASHBYTES('SHA2_256', N'state-2'), N'nonce', N'verifier', N'subject-new', N'Authenticated', DATEADD(minute, 10, SYSUTCDATETIME()), SYSUTCDATETIME()),
                    (@expired, N'Google', HASHBYTES('SHA2_256', N'state-3'), N'nonce', N'verifier', N'subject-old', N'Authenticated', DATEADD(minute, -1, SYSUTCDATETIME()), SYSUTCDATETIME()),
                    (@unproved, N'Google', HASHBYTES('SHA2_256', N'state-4'), N'nonce', N'verifier', N'subject-open', N'Authenticated', DATEADD(minute, 10, SYSUTCDATETIME()), SYSUTCDATETIME());
                """,
                ("@business", business), ("@customer", customer), ("@other", other), ("@attempt", attempt), ("@duplicate", duplicate), ("@expired", expired), ("@unproved", unproved));

            Assert.Equal(0, await CommitAsync(cs, unproved, customer, business));
            Assert.Equal(0, await ScalarAsync(cs, "SELECT COUNT(*) FROM dbo.CustomerMembershipIdentities;"));

            Assert.Equal(0, await ProveAsync(cs, expired));
            Assert.Equal(1, await ProveAsync(cs, attempt));
            Assert.NotEqual(DBNull.Value, await ScalarValueAsync(cs, "SELECT OwnershipVerifiedAt FROM dbo.MembershipAuthAttempts WHERE AttemptID = @id;", attempt));

            async Task<int> Once()
            {
                try { return await CommitAsync(cs, attempt, customer, business); }
                catch (SqlException) { return 0; }
            }
            var counts = await Task.WhenAll(Once(), Once());
            Assert.Equal(1, counts.Sum());
            Assert.Equal(1, await ScalarAsync(cs, "SELECT COUNT(*) FROM dbo.CustomerMembershipIdentities WHERE CustomerID = @id AND BusinessID = @business AND Status = N'Active';", customer, business));
            Assert.Equal("Consumed", await TextAsync(cs, "SELECT Status FROM dbo.MembershipAuthAttempts WHERE AttemptID = @id;", attempt));
            Assert.Equal(0, await CommitAsync(cs, attempt, customer, business));
            Assert.Equal(1, await ScalarAsync(cs, "SELECT COUNT(*) FROM dbo.CustomerMembershipIdentities WHERE Status = N'Active';"));

            Assert.Equal(1, await ProveAsync(cs, duplicate));
            await Assert.ThrowsAnyAsync<SqlException>(() => CommitAsync(cs, duplicate, other, business));
            Assert.Equal(0, await ScalarAsync(cs, "SELECT COUNT(*) FROM dbo.CustomerMembershipIdentities WHERE CustomerID = @id;", other));
            Assert.Equal("Authenticated", await TextAsync(cs, "SELECT Status FROM dbo.MembershipAuthAttempts WHERE AttemptID = @id;", duplicate));
        }
        finally
        {
            await DropAsync();
        }
    }

    private static async Task<int> ProveAsync(string cs, Guid attemptId)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new SqlCommand("""
            UPDATE dbo.MembershipAuthAttempts
            SET OwnershipVerifiedAt = SYSUTCDATETIME(), ExpiresAt = DATEADD(minute, 15, SYSUTCDATETIME())
            WHERE AttemptID = @id AND Status = N'Authenticated' AND CustomerID IS NULL AND ProviderSubject IS NOT NULL AND ExpiresAt > SYSUTCDATETIME();
            """, connection);
        command.Parameters.AddWithValue("@id", attemptId);
        return await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> CommitAsync(string cs, Guid attemptId, Guid customerId, Guid businessId)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable);
        string? provider = null;
        string? subject = null;
        await using (var read = new SqlCommand("SELECT Provider, ProviderSubject, OwnershipVerifiedAt, Status FROM dbo.MembershipAuthAttempts WHERE AttemptID = @id;", connection, transaction))
        {
            read.Parameters.AddWithValue("@id", attemptId);
            await using var reader = await read.ExecuteReaderAsync();
            var found = await reader.ReadAsync();
            var proved = found && !reader.IsDBNull(2) && reader.GetString(3) == "Authenticated";
            if (proved)
            {
                provider = reader.GetString(0);
                subject = reader.GetString(1);
            }
            await reader.CloseAsync();
            if (!proved)
            {
                await transaction.RollbackAsync();
                return 0;
            }
        }
        var identity = Guid.NewGuid();
        await using (var insert = new SqlCommand("""
            INSERT INTO dbo.CustomerMembershipIdentities
                (IdentityID, BusinessID, CustomerID, Provider, ProviderSubject, LinkedAt, LastAuthenticatedAt, Status, CreatedAt, UpdatedAt)
            VALUES
                (@id, @business, @customer, @provider, @subject, SYSUTCDATETIME(), SYSUTCDATETIME(), N'Active', SYSUTCDATETIME(), SYSUTCDATETIME());
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@id", identity);
            insert.Parameters.AddWithValue("@business", businessId);
            insert.Parameters.AddWithValue("@customer", customerId);
            insert.Parameters.AddWithValue("@provider", provider);
            insert.Parameters.AddWithValue("@subject", subject);
            await insert.ExecuteNonQueryAsync();
        }
        await using var done = new SqlCommand("""
            UPDATE dbo.MembershipAuthAttempts
            SET Status = N'Consumed', CustomerID = @customer, BusinessID = @business
            WHERE AttemptID = @id AND Status = N'Authenticated' AND CustomerID IS NULL AND OwnershipVerifiedAt IS NOT NULL;
            """, connection, transaction);
        done.Parameters.AddWithValue("@customer", customerId);
        done.Parameters.AddWithValue("@business", businessId);
        done.Parameters.AddWithValue("@id", attemptId);
        var updated = await done.ExecuteNonQueryAsync();
        if (updated != 1)
        {
            await transaction.RollbackAsync();
            return 0;
        }
        await transaction.CommitAsync();
        return updated;
    }

    private static async Task ExecuteAsync(string cs, string sql, params (string Name, Guid Value)[] parameters)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        foreach (var parameter in parameters) command.Parameters.Add(parameter.Name, SqlDbType.UniqueIdentifier).Value = parameter.Value;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<int> ScalarAsync(string cs, string sql, Guid? id = null, Guid? business = null)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        if (id is Guid value) command.Parameters.AddWithValue("@id", value);
        if (business is Guid businessId) command.Parameters.AddWithValue("@business", businessId);
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<object> ScalarValueAsync(string cs, string sql, Guid id)
    {
        await using var connection = new SqlConnection(cs);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", id);
        return await command.ExecuteScalarAsync() ?? DBNull.Value;
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
        if (!DatabaseName.Contains("_Test_MembershipEnroll", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing to drop a database outside the enrollment test.");
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
