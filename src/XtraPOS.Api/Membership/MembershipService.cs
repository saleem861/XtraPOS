using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Membership;

public sealed class MembershipException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed record MembershipVerificationCreated(Guid VerificationRequestId, string Token, DateTime ExpiresAtUtc, int PollingIntervalMs);
public sealed record MembershipVerificationView(Guid VerificationRequestId, string Status, DateTime ExpiresAtUtc, Guid CustomerId, Guid CounterId, Guid SaleContextId);
public sealed record MembershipPublicStatus(string Status, DateTime? ExpiresAtUtc, string? Code);
public sealed record MembershipSummary(bool Configured, int ActiveSessions, IReadOnlyList<string> Providers, DateTime? LastVerifiedAtUtc);

public sealed class MembershipService(
    SqlConnectionFactory connections,
    SyncEndpointStore endpoints,
    IOptions<MembershipOptions> options)
{
    public const string SessionCookie = "xtrapos-membership";
    public const string CsrfCookie = "xtrapos-membership-csrf";

    public MembershipOptions Options => options.Value;

    public async Task<MembershipVerificationCreated> CreateAsync(Guid businessId, Guid branchId, Guid counterId, Guid cashierUserId, Guid customerId, Guid saleContextId, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled) throw new MembershipException("MEMBERSHIP_SERVER_UNAVAILABLE");
        var connectionString = await CentralConnectionStringAsync(cancellationToken) ?? throw new MembershipException("MEMBERSHIP_SERVER_UNAVAILABLE");
        var token = MembershipTokens.CreateOpaqueToken();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var expires = now.AddSeconds(Math.Clamp(options.Value.QRExpirySeconds, 15, 300));
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var cancel = new SqlCommand(
            """
            UPDATE dbo.MembershipVerificationRequests
            SET Status = N'Cancelled', CancelledAt = SYSUTCDATETIME()
            WHERE BusinessID = @business AND BranchID = @branch AND CounterID = @counter
              AND Status = N'Pending' AND (SaleContextID = @sale OR CustomerID <> @customer);
            """, connection, transaction))
        {
            cancel.Parameters.AddWithValue("@business", businessId);
            cancel.Parameters.AddWithValue("@branch", branchId);
            cancel.Parameters.AddWithValue("@counter", counterId);
            cancel.Parameters.AddWithValue("@sale", saleContextId);
            cancel.Parameters.AddWithValue("@customer", customerId);
            await cancel.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = new SqlCommand(
            """
            INSERT INTO dbo.MembershipVerificationRequests
                (VerificationRequestID, BusinessID, BranchID, CounterID, CustomerID, CashierUserID, SaleContextID, QRTokenHash, Status, CreatedAt, ExpiresAt, CorrelationID)
            VALUES
                (@id, @business, @branch, @counter, @customer, @cashier, @sale, @hash, N'Pending', SYSUTCDATETIME(), @expires, @correlation);
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@id", id);
            insert.Parameters.AddWithValue("@business", businessId);
            insert.Parameters.AddWithValue("@branch", branchId);
            insert.Parameters.AddWithValue("@counter", counterId);
            insert.Parameters.AddWithValue("@customer", customerId == Guid.Empty ? DBNull.Value : customerId);
            insert.Parameters.AddWithValue("@cashier", cashierUserId);
            insert.Parameters.AddWithValue("@sale", saleContextId);
            insert.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = MembershipTokens.Hash(token);
            insert.Parameters.AddWithValue("@expires", expires);
            insert.Parameters.AddWithValue("@correlation", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await AuditAsync(connection, transaction, businessId, "VerificationRequested", customerId, id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new MembershipVerificationCreated(id, token, expires, options.Value.VerificationPollingIntervalMs);
    }

    public async Task<MembershipVerificationView?> ReadForPosAsync(Guid businessId, Guid branchId, Guid counterId, Guid verificationId, CancellationToken cancellationToken)
    {
        var connectionString = await CentralConnectionStringAsync(cancellationToken);
        if (connectionString is null) return null;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT Status, ExpiresAt, CustomerID, CounterID, SaleContextID
            FROM dbo.MembershipVerificationRequests
            WHERE VerificationRequestID = @id AND BusinessID = @business AND BranchID = @branch AND CounterID = @counter;
            """, connection);
        command.Parameters.AddWithValue("@id", verificationId);
        command.Parameters.AddWithValue("@business", businessId);
        command.Parameters.AddWithValue("@branch", branchId);
        command.Parameters.AddWithValue("@counter", counterId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var status = reader.GetString(0);
        var expires = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
        if (status == "Pending" && expires <= DateTime.UtcNow) status = "Expired";
        return new MembershipVerificationView(verificationId, status, expires, reader.IsDBNull(2) ? Guid.Empty : reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4));
    }

    public async Task CancelAsync(Guid businessId, Guid branchId, Guid counterId, Guid verificationId, CancellationToken cancellationToken)
    {
        var connectionString = await CentralConnectionStringAsync(cancellationToken) ?? throw new MembershipException("MEMBERSHIP_SERVER_UNAVAILABLE");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            UPDATE dbo.MembershipVerificationRequests
            SET Status = N'Cancelled', CancelledAt = SYSUTCDATETIME()
            WHERE VerificationRequestID = @id AND BusinessID = @business AND BranchID = @branch AND CounterID = @counter AND Status = N'Pending';
            """, connection);
        command.Parameters.AddWithValue("@id", verificationId);
        command.Parameters.AddWithValue("@business", businessId);
        command.Parameters.AddWithValue("@branch", branchId);
        command.Parameters.AddWithValue("@counter", counterId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CancelSaleAsync(Guid businessId, Guid branchId, Guid counterId, Guid saleContextId, CancellationToken cancellationToken)
    {
        var connectionString = await CentralConnectionStringAsync(cancellationToken);
        if (connectionString is null) return;
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            UPDATE dbo.MembershipVerificationRequests
            SET Status = N'Cancelled', CancelledAt = SYSUTCDATETIME()
            WHERE BusinessID = @business AND BranchID = @branch AND CounterID = @counter
              AND SaleContextID = @sale AND Status = N'Pending';
            """, connection);
        command.Parameters.AddWithValue("@business", businessId);
        command.Parameters.AddWithValue("@branch", branchId);
        command.Parameters.AddWithValue("@counter", counterId);
        command.Parameters.AddWithValue("@sale", saleContextId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<MembershipPublicStatus> PublicStatusAsync(string token, CancellationToken cancellationToken)
    {
        var connectionString = await CentralConnectionStringAsync(cancellationToken);
        if (connectionString is null) return new MembershipPublicStatus("Unavailable", null, "MEMBERSHIP_SERVER_UNAVAILABLE");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT Status, ExpiresAt FROM dbo.MembershipVerificationRequests WHERE QRTokenHash = @hash;", connection);
        command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = MembershipTokens.Hash(token);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new MembershipPublicStatus("Missing", null, "MEMBERSHIP_REQUEST_NOT_FOUND");
        var status = reader.GetString(0);
        var expires = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
        if (status == "Pending" && expires <= DateTime.UtcNow) return new MembershipPublicStatus("Expired", expires, "MEMBERSHIP_QR_EXPIRED");
        return new MembershipPublicStatus(status, expires, null);
    }

    public async Task CompleteAsync(string token, string? sessionToken, CancellationToken cancellationToken)
    {
        var connectionString = await CentralConnectionStringAsync(cancellationToken) ?? throw new MembershipException("MEMBERSHIP_SERVER_UNAVAILABLE");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var request = await ReadRequestAsync(connection, transaction, token, cancellationToken) ?? throw new MembershipException("MEMBERSHIP_REQUEST_NOT_FOUND");
        var session = await ReadSessionAsync(connection, transaction, sessionToken, cancellationToken);
        var active = session is not null && await CustomerActiveAsync(connection, transaction, request.CustomerId, cancellationToken);
        var facts = new MembershipVerificationFacts(
            options.Value.Enabled,
            session is not null,
            session?.Revoked ?? false,
            session is not null && (session.IdleExpiresAt <= DateTime.UtcNow || session.AbsoluteExpiresAt <= DateTime.UtcNow),
            session?.CustomerId,
            session?.BusinessId,
            request.CustomerId,
            request.BusinessId,
            request.BranchId,
            request.CounterId,
            request.SaleContextId,
            request.BranchId,
            request.CounterId,
            request.SaleContextId,
            Enum.Parse<MembershipRequestStatus>(request.Status),
            request.ExpiresAt,
            DateTime.UtcNow,
            active || session is null,
            true);
        var code = MembershipRules.RejectionCode(facts);
        if (code is not null)
        {
            await AuditAsync(connection, transaction, request.BusinessId, "VerificationRejected", request.CustomerId, request.Id, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new MembershipException(code);
        }

        await using var update = new SqlCommand(
            """
            UPDATE dbo.MembershipVerificationRequests
            SET Status = N'Verified', VerifiedAt = SYSUTCDATETIME(), ConsumedAt = SYSUTCDATETIME(), VerifiedByMembershipSessionID = @session
            WHERE VerificationRequestID = @id AND Status = N'Pending' AND ExpiresAt > SYSUTCDATETIME();
            """, connection, transaction);
        update.Parameters.AddWithValue("@session", session!.SessionId);
        update.Parameters.AddWithValue("@id", request.Id);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            throw new MembershipException("MEMBERSHIP_QR_ALREADY_USED");
        }

        await AuditAsync(connection, transaction, request.BusinessId, "VerificationSucceeded", request.CustomerId, request.Id, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<MembershipSummary> SummaryAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var connectionString = await CentralConnectionStringAsync(cancellationToken);
        if (connectionString is null) return new MembershipSummary(false, 0, [], null);
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var providers = new List<string>();
        await using (var command = new SqlCommand(
            "SELECT Provider FROM dbo.CustomerMembershipIdentities WHERE CustomerID = @customer AND Status = N'Active';", connection))
        {
            command.Parameters.AddWithValue("@customer", customerId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) providers.Add(reader.GetString(0));
        }

        await using var sessions = new SqlCommand(
            """
            SELECT COUNT(*) FROM dbo.CustomerMembershipSessions
            WHERE CustomerID = @customer AND RevokedAt IS NULL AND IdleExpiresAt > SYSUTCDATETIME() AND AbsoluteExpiresAt > SYSUTCDATETIME();
            """, connection);
        sessions.Parameters.AddWithValue("@customer", customerId);
        var count = (int)(await sessions.ExecuteScalarAsync(cancellationToken) ?? 0);
        DateTime? lastVerified = null;
        await using (var last = new SqlCommand(
            """
            SELECT MAX(VerifiedAt) FROM dbo.MembershipVerificationRequests
            WHERE CustomerID = @customer AND Status = N'Verified';
            """, connection))
        {
            last.Parameters.AddWithValue("@customer", customerId);
            var value = await last.ExecuteScalarAsync(cancellationToken);
            if (value is DateTime verified) lastVerified = DateTime.SpecifyKind(verified, DateTimeKind.Utc);
        }
        return new MembershipSummary(options.Value.GoogleLoginEnabled || options.Value.AppleLoginEnabled, count, providers, lastVerified);
    }

    public async Task<(string Token, DateTime ExpiresAtUtc)> CreateEnrollmentQrAsync(Guid businessId, Guid cashierUserId, Guid? customerId, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled) throw new MembershipException("MEMBERSHIP_SERVER_UNAVAILABLE");
        var token = MembershipTokens.CreateOpaqueToken();
        var expires = DateTime.UtcNow.AddSeconds(Math.Clamp(options.Value.QRExpirySeconds, 15, 300));
        await using var connection = await OpenCentralAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.MembershipEnrollmentRequests
                (EnrollmentID, BusinessID, CustomerID, TokenHash, Status, ExpiresAt, CreatedAt, CashierUserID)
            VALUES
                (NEWID(), @business, @customer, @hash, N'Pending', @expires, SYSUTCDATETIME(), @cashier);
            """, connection);
        command.Parameters.AddWithValue("@business", businessId);
        command.Parameters.AddWithValue("@customer", (object?)customerId ?? DBNull.Value);
        command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = MembershipTokens.Hash(token);
        command.Parameters.AddWithValue("@expires", expires);
        command.Parameters.AddWithValue("@cashier", cashierUserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return (token, expires);
    }

    public async Task<(string Status, string? Provider, string? Display, Guid? AttemptId)> ReadEnrollmentQrAsync(string token, CancellationToken cancellationToken)
    {
        await using var connection = await OpenCentralAsync(cancellationToken);
        string? provider = null;
        string? subject = null;
        string? display = null;
        string status = "Pending";
        DateTime expires = DateTime.UtcNow;
        await using (var command = new SqlCommand(
            """
            SELECT Status, ExpiresAt, Provider, ProviderSubject, ProviderAccountDisplay, ProviderEmail
            FROM dbo.MembershipEnrollmentRequests
            WHERE TokenHash = @hash;
            """, connection))
        {
            command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = MembershipTokens.Hash(token);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return ("Missing", null, null, null);
            status = reader.GetString(0);
            expires = DateTime.SpecifyKind(reader.GetDateTime(1), DateTimeKind.Utc);
            provider = reader.IsDBNull(2) ? null : reader.GetString(2);
            subject = reader.IsDBNull(3) ? null : reader.GetString(3);
            display = reader.IsDBNull(4) ? null : reader.GetString(4);
            if (string.IsNullOrWhiteSpace(display) && !reader.IsDBNull(5)) display = reader.GetString(5);
        }
        if (status == "Pending" && expires <= DateTime.UtcNow) status = "Expired";
        if (string.IsNullOrWhiteSpace(subject)) return (status, provider, display, null);
        Guid? attemptId = null;
        await using (var attempt = new SqlCommand(
            """
            SELECT TOP (1) AttemptID
            FROM dbo.MembershipAuthAttempts
            WHERE Provider = @provider AND ProviderSubject = @subject AND Status = N'Authenticated' AND ExpiresAt > SYSUTCDATETIME()
            ORDER BY CreatedAt DESC;
            """, connection))
        {
            attempt.Parameters.AddWithValue("@provider", provider ?? "Google");
            attempt.Parameters.AddWithValue("@subject", subject);
            var value = await attempt.ExecuteScalarAsync(cancellationToken);
            if (value is Guid id) attemptId = id;
        }
        return (attemptId is null ? status : "Ready", provider, display, attemptId);
    }

    public async Task UnlinkAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var connectionString = await CentralConnectionStringAsync(cancellationToken) ?? throw new MembershipException("MEMBERSHIP_SERVER_UNAVAILABLE");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var identities = new SqlCommand(
            """
            UPDATE dbo.CustomerMembershipIdentities
            SET Status = N'Revoked', UpdatedAt = SYSUTCDATETIME()
            WHERE CustomerID = @customer AND Status = N'Active';
            """, connection, transaction))
        {
            identities.Parameters.AddWithValue("@customer", customerId);
            await identities.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var sessions = new SqlCommand(
            """
            UPDATE dbo.CustomerMembershipSessions
            SET RevokedAt = SYSUTCDATETIME(), RevocationReason = N'StaffUnlink'
            WHERE CustomerID = @customer AND RevokedAt IS NULL;
            """, connection, transaction))
        {
            sessions.Parameters.AddWithValue("@customer", customerId);
            await sessions.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task RevokeAllAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var connectionString = await CentralConnectionStringAsync(cancellationToken) ?? throw new MembershipException("MEMBERSHIP_SERVER_UNAVAILABLE");
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            UPDATE dbo.CustomerMembershipSessions
            SET RevokedAt = SYSUTCDATETIME(), RevocationReason = N'StaffRevokeAll'
            WHERE CustomerID = @customer AND RevokedAt IS NULL;
            """, connection);
        command.Parameters.AddWithValue("@customer", customerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task<SqlConnection> OpenCentralAsync(CancellationToken cancellationToken)
    {
        var connectionString = await CentralConnectionStringAsync(cancellationToken) ?? throw new MembershipException("MEMBERSHIP_SERVER_UNAVAILABLE");
        var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    internal async Task<string> CreateSessionAsync(SqlConnection connection, SqlTransaction transaction, Guid businessId, Guid customerId, Guid identityId, CancellationToken cancellationToken)
    {
        var secret = MembershipTokens.CreateOpaqueToken();
        var now = DateTime.UtcNow;
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.CustomerMembershipSessions
                (SessionID, BusinessID, CustomerID, IdentityID, SessionTokenHash, CreatedAt, LastUsedAt, IdleExpiresAt, AbsoluteExpiresAt, DeviceDisplayName, BrowserFamily, Platform, LastSeenAt)
            VALUES
                (NEWID(), @business, @customer, @identity, @hash, SYSUTCDATETIME(), SYSUTCDATETIME(), @idle, @absolute, NULL, NULL, NULL, SYSUTCDATETIME());
            """, connection, transaction);
        command.Parameters.AddWithValue("@business", businessId);
        command.Parameters.AddWithValue("@customer", customerId);
        command.Parameters.AddWithValue("@identity", identityId);
        command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = MembershipTokens.Hash(secret);
        command.Parameters.AddWithValue("@idle", now.AddDays(Math.Clamp(options.Value.SessionIdleDays, 1, 365)));
        command.Parameters.AddWithValue("@absolute", now.AddDays(Math.Clamp(options.Value.SessionAbsoluteDays, 1, 400)));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await AuditAsync(connection, transaction, businessId, "SessionCreated", customerId, null, cancellationToken);
        return secret;
    }

    private async Task<string?> CentralConnectionStringAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var roleCommand = new SqlCommand("SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';", connection);
        var role = await roleCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (string.Equals(role, DatabaseRole.Central, StringComparison.Ordinal))
        {
            return connections.CreateBranchConnectionString();
        }

        var endpoint = await endpoints.GetAsync(DatabaseRole.Central, cancellationToken);
        if (endpoint is null || !string.Equals(endpoint.Status, SqlEndpointSettings.Linked, StringComparison.Ordinal)) return null;
        var settings = await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        return settings?.BuildConnectionString(settings.DatabaseName);
    }

    private static async Task<RequestRow?> ReadRequestAsync(SqlConnection connection, SqlTransaction transaction, string token, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT VerificationRequestID, BusinessID, BranchID, CounterID, CustomerID, SaleContextID, Status, ExpiresAt
            FROM dbo.MembershipVerificationRequests WHERE QRTokenHash = @hash;
            """, connection, transaction);
        command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = MembershipTokens.Hash(token);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new RequestRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3), reader.GetGuid(4), reader.GetGuid(5), reader.GetString(6), DateTime.SpecifyKind(reader.GetDateTime(7), DateTimeKind.Utc));
    }

    private static async Task<SessionRow?> ReadSessionAsync(SqlConnection connection, SqlTransaction transaction, string? sessionToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken)) return null;
        await using var command = new SqlCommand(
            """
            SELECT SessionID, BusinessID, CustomerID, RevokedAt, IdleExpiresAt, AbsoluteExpiresAt
            FROM dbo.CustomerMembershipSessions WHERE SessionTokenHash = @hash;
            """, connection, transaction);
        command.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = MembershipTokens.Hash(sessionToken);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SessionRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), !reader.IsDBNull(3), DateTime.SpecifyKind(reader.GetDateTime(4), DateTimeKind.Utc), DateTime.SpecifyKind(reader.GetDateTime(5), DateTimeKind.Utc));
    }

    private static async Task<bool> CustomerActiveAsync(SqlConnection connection, SqlTransaction transaction, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT IsActive FROM dbo.Customers WHERE CustomerID = @id;", connection, transaction);
        command.Parameters.AddWithValue("@id", customerId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is bool active && active;
    }

    private static async Task AuditAsync(SqlConnection connection, SqlTransaction transaction, Guid businessId, string code, Guid? customerId, Guid? requestId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.MembershipVerificationAudit (AuditID, BusinessID, EventCode, CustomerID, VerificationRequestID, CorrelationID, CreatedAt)
            VALUES (NEWID(), @business, @code, @customer, @request, NEWID(), SYSUTCDATETIME());
            """, connection, transaction);
        command.Parameters.AddWithValue("@business", businessId);
        command.Parameters.AddWithValue("@code", code);
        command.Parameters.AddWithValue("@customer", (object?)customerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@request", (object?)requestId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private sealed record RequestRow(Guid Id, Guid BusinessId, Guid BranchId, Guid CounterId, Guid CustomerId, Guid SaleContextId, string Status, DateTime ExpiresAt);
    private sealed record SessionRow(Guid SessionId, Guid BusinessId, Guid CustomerId, bool Revoked, DateTime IdleExpiresAt, DateTime AbsoluteExpiresAt);
}
