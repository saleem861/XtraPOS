using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Customers;
using XtraPOS.Api.Diagnostics;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Membership;

public sealed record MembershipAuthStart(string AuthorizeUrl, string State);
public sealed record MembershipAuthResult(string? SessionSecret, string? Code, string? ReturnPath);

public sealed class MembershipAuthService(
    MembershipService membership,
    PhoneVerificationService phones,
    IMembershipIdTokenValidator tokens,
    MembershipCodeExchange exchange,
    IOptions<MembershipOptions> options,
    IHostEnvironment environment)
{
    public async Task<MembershipAuthStart> BeginAsync(string provider, string redirectUri, CancellationToken cancellationToken, string? enrollmentToken = null, string? returnPath = null)
    {
        var settings = options.Value;
        var clientId = provider == "Apple" ? settings.AppleClientId : settings.GoogleClientId;
        var enabled = provider == "Apple" ? settings.AppleLoginEnabled : settings.GoogleLoginEnabled;
        if (!enabled || string.IsNullOrWhiteSpace(clientId)) throw new MembershipException("BLOCKED_EXTERNAL_CONFIG");
        var (verifier, challenge) = MembershipPkce.Create();
        var state = string.IsNullOrWhiteSpace(enrollmentToken)
            ? MembershipTokens.CreateOpaqueToken()
            : enrollmentToken + "." + MembershipTokens.CreateOpaqueToken();
        if (!string.IsNullOrWhiteSpace(returnPath)) state += "|" + returnPath;
        var nonce = MembershipTokens.CreateOpaqueToken();
        await using var connection = await membership.OpenCentralAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.MembershipAuthAttempts
                (AttemptID, Provider, StateHash, Nonce, PkceVerifier, Status, ExpiresAt, CreatedAt)
            VALUES
                (NEWID(), @provider, @state, @nonce, @verifier, N'Pending', DATEADD(minute, 10, SYSUTCDATETIME()), SYSUTCDATETIME());
            """, connection);
        command.Parameters.AddWithValue("@provider", provider);
        command.Parameters.Add("@state", SqlDbType.Binary, 32).Value = MembershipTokens.Hash(state);
        command.Parameters.AddWithValue("@nonce", nonce);
        command.Parameters.AddWithValue("@verifier", verifier);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return new MembershipAuthStart(MembershipPkce.AuthorizeUrl(provider, clientId, redirectUri, state, nonce, challenge), state);
    }

    public async Task<MembershipAuthResult> FinishAsync(string provider, string? code, string? idToken, string? state, string redirectUri, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state)) throw new MembershipException("MEMBERSHIP_PROVIDER_STATE");
        var settings = options.Value;
        var clientId = provider == "Apple" ? settings.AppleClientId : settings.GoogleClientId;
        var secret = provider == "Apple" ? settings.AppleClientSecret : settings.GoogleClientSecret;
        if (string.IsNullOrWhiteSpace(clientId)) throw new MembershipException("BLOCKED_EXTERNAL_CONFIG");
        await using var connection = await membership.OpenCentralAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        AttemptRow? attempt;
        await using (var read = new SqlCommand(
            """
            SELECT AttemptID, Nonce, PkceVerifier, BusinessID, CustomerID
            FROM dbo.MembershipAuthAttempts
            WHERE StateHash = @state AND Provider = @provider AND Status = N'Pending' AND ExpiresAt > SYSUTCDATETIME();
            """, connection, transaction))
        {
            read.Parameters.Add("@state", SqlDbType.Binary, 32).Value = MembershipTokens.Hash(state);
            read.Parameters.AddWithValue("@provider", provider);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            attempt = await reader.ReadAsync(cancellationToken)
                ? new AttemptRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetGuid(4))
                : null;
        }
        if (attempt is null) throw new MembershipException("MEMBERSHIP_PROVIDER_STATE");
        var jwt = idToken;
        if (string.IsNullOrWhiteSpace(jwt))
        {
            if (string.IsNullOrWhiteSpace(code)) throw new MembershipException("MEMBERSHIP_PROVIDER_SIGNATURE");
            jwt = await exchange.ExchangeAsync(provider, clientId, secret ?? "", code, attempt.Verifier, redirectUri, cancellationToken);
        }
        var claims = await tokens.ValidateAsync(provider, jwt, clientId, attempt.Nonce, cancellationToken);
        Guid? linkedCustomer = null;
        Guid? linkedBusiness = null;
        Guid? identityId = null;
        var linkedCount = 0;
        await using (var linked = new SqlCommand(
            """
            SELECT IdentityID, BusinessID, CustomerID
            FROM dbo.CustomerMembershipIdentities
            WHERE Provider = @provider AND ProviderSubject = @subject AND Status = N'Active';
            """, connection, transaction))
        {
            linked.Parameters.AddWithValue("@provider", provider);
            linked.Parameters.AddWithValue("@subject", claims.Subject);
            await using var reader = await linked.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                linkedCount++;
                identityId = reader.GetGuid(0);
                linkedBusiness = reader.GetGuid(1);
                linkedCustomer = reader.GetGuid(2);
            }
        }
        if (linkedCount > 1) throw new MembershipException("MEMBERSHIP_WRONG_BUSINESS");

        await using (var consume = new SqlCommand(
            """
            UPDATE dbo.MembershipAuthAttempts
            SET Status = N'Authenticated', ProviderSubject = @subject
            WHERE AttemptID = @id AND Status = N'Pending';
            """, connection, transaction))
        {
            consume.Parameters.AddWithValue("@subject", claims.Subject);
            consume.Parameters.AddWithValue("@id", attempt.Id);
            if (await consume.ExecuteNonQueryAsync(cancellationToken) != 1) throw new MembershipException("MEMBERSHIP_PROVIDER_STATE");
        }

        var dot = state.IndexOf('.');
        Guid? enrollmentCustomer = null;
        if (dot > 0)
        {
            await using var enroll = new SqlCommand(
                """
                UPDATE dbo.MembershipEnrollmentRequests
                SET Provider = @provider, ProviderSubject = @subject, ProviderEmail = @email, ProviderAccountDisplay = @display
                OUTPUT inserted.CustomerID
                WHERE TokenHash = @hash AND Status = N'Pending' AND ExpiresAt > SYSUTCDATETIME();
                """, connection, transaction);
            enroll.Parameters.AddWithValue("@provider", provider);
            enroll.Parameters.AddWithValue("@subject", claims.Subject);
            var email = string.IsNullOrWhiteSpace(claims.Email) ? null : claims.Email;
            enroll.Parameters.AddWithValue("@email", (object?)email ?? DBNull.Value);
            enroll.Parameters.AddWithValue("@display", email ?? claims.Subject);
            enroll.Parameters.Add("@hash", SqlDbType.Binary, 32).Value = MembershipTokens.Hash(state[..dot]);
            var enrolled = await enroll.ExecuteScalarAsync(cancellationToken);
            if (enrolled is Guid enrolledCustomer) enrollmentCustomer = enrolledCustomer;
            DiagnosticLog.Write(environment, "enrollment-link", enrolled is null ? "not-found" : "linked");
        }
        else
        {
            DiagnosticLog.Write(environment, "enrollment-link", "missing-token");
        }

        if (linkedCustomer is Guid customer && linkedBusiness is Guid business && identityId is Guid identity)
        {
            if (dot > 0 && enrollmentCustomer != customer)
            {
                await transaction.CommitAsync(cancellationToken);
                return new MembershipAuthResult(null, "MEMBERSHIP_PROVIDER_CONFLICT", attempt.Id.ToString());
            }
            var session = await membership.CreateSessionAsync(connection, transaction, business, customer, identity, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new MembershipAuthResult(session, null, null);
        }

        await transaction.CommitAsync(cancellationToken);
        return new MembershipAuthResult(null, "MEMBERSHIP_OWNERSHIP_PROOF_REQUIRED", attempt.Id.ToString());
    }

    public async Task ProveOwnershipAsync(Guid attemptId, Guid verificationId, string? destination, string? otp, CancellationToken cancellationToken)
    {
        await phones.VerifyRegistrationAsync(verificationId, destination, otp, cancellationToken);
        await using var connection = await membership.OpenCentralAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            UPDATE dbo.MembershipAuthAttempts
            SET OwnershipVerifiedAt = SYSUTCDATETIME(), ExpiresAt = DATEADD(minute, 15, SYSUTCDATETIME())
            WHERE AttemptID = @id AND Status = N'Authenticated' AND CustomerID IS NULL AND ProviderSubject IS NOT NULL AND ExpiresAt > SYSUTCDATETIME();
            """, connection);
        command.Parameters.AddWithValue("@id", attemptId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new MembershipException("MEMBERSHIP_ENROLLMENT_EXPIRED");
    }

    public async Task<bool> AttemptIdentityTakenAsync(Guid attemptId, CancellationToken cancellationToken)
    {
        await using var connection = await membership.OpenCentralAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT CASE WHEN EXISTS (
                SELECT 1
                FROM dbo.MembershipAuthAttempts AS attempt
                INNER JOIN dbo.CustomerMembershipIdentities AS linked
                    ON linked.Provider = attempt.Provider
                   AND linked.ProviderSubject = attempt.ProviderSubject
                   AND linked.Status = N'Active'
                WHERE attempt.AttemptID = @id
                  AND attempt.ProviderSubject IS NOT NULL
            ) THEN 1 ELSE 0 END;
            """,
            connection);
        command.Parameters.AddWithValue("@id", attemptId);
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) == 1;
    }

    public async Task CommitNewCustomerAsync(Guid attemptId, Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = await membership.OpenCentralAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        string? provider = null;
        string? subject = null;
        DateTime expires = DateTime.MinValue;
        string status = "";
        Guid? attemptCustomer = null;
        await using (var read = new SqlCommand(
            """
            SELECT Provider, ProviderSubject, ExpiresAt, Status, CustomerID
            FROM dbo.MembershipAuthAttempts WHERE AttemptID = @id;
            """, connection, transaction))
        {
            read.Parameters.AddWithValue("@id", attemptId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new MembershipException("MEMBERSHIP_PROVIDER_STATE");
            provider = reader.GetString(0);
            subject = reader.IsDBNull(1) ? null : reader.GetString(1);
            expires = DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc);
            status = reader.GetString(3);
            attemptCustomer = reader.IsDBNull(4) ? null : reader.GetGuid(4);
        }
        if (string.IsNullOrWhiteSpace(subject)) throw new MembershipException("MEMBERSHIP_PROVIDER_STATE");
        string? email = null;
        string? display = null;
        await using (var shown = new SqlCommand(
            """
            SELECT TOP (1) ProviderEmail, ProviderAccountDisplay
            FROM dbo.MembershipEnrollmentRequests
            WHERE Provider = @provider AND ProviderSubject = @subject
            ORDER BY CreatedAt DESC;
            """, connection, transaction))
        {
            shown.Parameters.AddWithValue("@provider", provider);
            shown.Parameters.AddWithValue("@subject", subject);
            await using var reader = await shown.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                email = reader.IsDBNull(0) ? null : reader.GetString(0);
                display = reader.IsDBNull(1) ? null : reader.GetString(1);
            }
        }
        if (string.IsNullOrWhiteSpace(email)) email = null;
        display = string.IsNullOrWhiteSpace(email) ? (string.IsNullOrWhiteSpace(display) ? subject : display) : email;
        Guid? owner = null;
        await using (var existing = new SqlCommand(
            "SELECT CustomerID FROM dbo.CustomerMembershipIdentities WHERE Provider = @provider AND ProviderSubject = @subject AND Status = N'Active';", connection, transaction))
        {
            existing.Parameters.AddWithValue("@provider", provider);
            existing.Parameters.AddWithValue("@subject", subject ?? "");
            var value = await existing.ExecuteScalarAsync(cancellationToken);
            if (value is Guid id) owner = id;
        }
        var block = MembershipEnrollmentRules.CommitBlock(expires, DateTime.UtcNow, owner ?? attemptCustomer, customerId, status == "Consumed");
        if (block is not null) throw new MembershipException(block);
        if (status == "Consumed")
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }
        var business = await CustomerBusinessAsync(connection, transaction, customerId, cancellationToken);
        var identity = Guid.NewGuid();
        await using (var insert = new SqlCommand(
            """
            INSERT INTO dbo.CustomerMembershipIdentities
                (IdentityID, BusinessID, CustomerID, Provider, ProviderSubject, ProviderAccountDisplay, ProviderEmail, LinkedAt, LastAuthenticatedAt, Status, CreatedAt, UpdatedAt)
            VALUES
                (@id, @business, @customer, @provider, @subject, @display, @email, SYSUTCDATETIME(), SYSUTCDATETIME(), N'Active', SYSUTCDATETIME(), SYSUTCDATETIME());
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@id", identity);
            insert.Parameters.AddWithValue("@business", business);
            insert.Parameters.AddWithValue("@customer", customerId);
            insert.Parameters.AddWithValue("@provider", provider);
            insert.Parameters.AddWithValue("@subject", subject);
            insert.Parameters.AddWithValue("@display", display);
            insert.Parameters.AddWithValue("@email", (object?)email ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        var completedEnrollments = new List<Guid>();
        await using (var enroll = new SqlCommand(
            """
            UPDATE dbo.MembershipEnrollmentRequests
            SET Status = N'Completed', CustomerID = @customer
            OUTPUT inserted.EnrollmentID
            WHERE Provider = @provider AND ProviderSubject = @subject AND Status = N'Pending';
            """, connection, transaction))
        {
            enroll.Parameters.AddWithValue("@customer", customerId);
            enroll.Parameters.AddWithValue("@provider", provider);
            enroll.Parameters.AddWithValue("@subject", subject);
            await using var completed = await enroll.ExecuteReaderAsync(cancellationToken);
            while (await completed.ReadAsync(cancellationToken))
            {
                completedEnrollments.Add(completed.GetGuid(0));
            }
        }
        await using (var done = new SqlCommand(
            """
            UPDATE dbo.MembershipAuthAttempts
            SET Status = N'Consumed', CustomerID = @customer, BusinessID = @business
            WHERE AttemptID = @id AND Status = N'Authenticated' AND CustomerID IS NULL;
            """, connection, transaction))
        {
            done.Parameters.AddWithValue("@customer", customerId);
            done.Parameters.AddWithValue("@business", business);
            done.Parameters.AddWithValue("@id", attemptId);
            if (await done.ExecuteNonQueryAsync(cancellationToken) != 1) throw new MembershipException("MEMBERSHIP_PROVIDER_STATE");
        }
        await membership.CreateSessionAsync(connection, transaction, business, customerId, identity, cancellationToken);
        if (membership.RecordsBranchOperations)
        {
            await MembershipReplica.EnqueueAsync(
                connection,
                transaction,
                MembershipReplica.IdentityOperation,
                identity,
                Guid.Empty,
                "ربط عضوية",
                cancellationToken);
            foreach (var enrollmentId in completedEnrollments)
            {
                await MembershipReplica.EnqueueAsync(
                    connection,
                    transaction,
                    MembershipReplica.EnrollmentOperation,
                    enrollmentId,
                    Guid.Empty,
                    "إكمال تسجيل العضوية",
                    cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        DiagnosticLog.Write(environment, "customer-identity", "stored");
    }

    public async Task<string> LinkAsync(Guid attemptId, Guid customerId, Guid verificationId, string? otp, CancellationToken cancellationToken)
    {
        await using var connection = await membership.OpenCentralAsync(cancellationToken);
        string? subject = null;
        string? provider = null;
        await using (var read = new SqlCommand(
            "SELECT Provider, ProviderSubject, Status FROM dbo.MembershipAuthAttempts WHERE AttemptID = @id;", connection))
        {
            read.Parameters.AddWithValue("@id", attemptId);
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetString(2) != "Authenticated") throw new MembershipException("MEMBERSHIP_PROVIDER_STATE");
            provider = reader.GetString(0);
            subject = reader.IsDBNull(1) ? null : reader.GetString(1);
        }
        if (string.IsNullOrWhiteSpace(subject) || provider is null) throw new MembershipException("MEMBERSHIP_PROVIDER_SUBJECT");
        try
        {
            await phones.VerifyCustomerAsync(verificationId, customerId, otp, cancellationToken);
        }
        catch (CustomerMasterException)
        {
            throw new MembershipException("MEMBERSHIP_OWNERSHIP_PROOF_REQUIRED");
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        Guid? owner = null;
        await using (var existing = new SqlCommand(
            """
            SELECT CustomerID FROM dbo.CustomerMembershipIdentities
            WHERE Provider = @provider AND ProviderSubject = @subject AND Status = N'Active';
            """, connection, transaction))
        {
            existing.Parameters.AddWithValue("@provider", provider);
            existing.Parameters.AddWithValue("@subject", subject);
            var value = await existing.ExecuteScalarAsync(cancellationToken);
            if (value is Guid id) owner = id;
        }
        var decision = MembershipLinkPolicy.Decide(owner, customerId, true);
        if (decision is not null) throw new MembershipException(decision);
        var business = await CustomerBusinessAsync(connection, transaction, customerId, cancellationToken);
        var identity = Guid.NewGuid();
        await using (var insert = new SqlCommand(
            """
            INSERT INTO dbo.CustomerMembershipIdentities
                (IdentityID, BusinessID, CustomerID, Provider, ProviderSubject, LinkedAt, LastAuthenticatedAt, Status, CreatedAt, UpdatedAt)
            VALUES
                (@id, @business, @customer, @provider, @subject, SYSUTCDATETIME(), SYSUTCDATETIME(), N'Active', SYSUTCDATETIME(), SYSUTCDATETIME());
            """, connection, transaction))
        {
            insert.Parameters.AddWithValue("@id", identity);
            insert.Parameters.AddWithValue("@business", business);
            insert.Parameters.AddWithValue("@customer", customerId);
            insert.Parameters.AddWithValue("@provider", provider);
            insert.Parameters.AddWithValue("@subject", subject);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var done = new SqlCommand("UPDATE dbo.MembershipAuthAttempts SET Status = N'Consumed', CustomerID = @customer, BusinessID = @business WHERE AttemptID = @id AND Status = N'Authenticated';", connection, transaction))
        {
            done.Parameters.AddWithValue("@customer", customerId);
            done.Parameters.AddWithValue("@business", business);
            done.Parameters.AddWithValue("@id", attemptId);
            if (await done.ExecuteNonQueryAsync(cancellationToken) != 1) throw new MembershipException("MEMBERSHIP_PROVIDER_STATE");
        }
        var session = await membership.CreateSessionAsync(connection, transaction, business, customerId, identity, cancellationToken);
        if (membership.RecordsBranchOperations)
        {
            await MembershipReplica.EnqueueAsync(
                connection,
                transaction,
                MembershipReplica.IdentityOperation,
                identity,
                Guid.Empty,
                "ربط عضوية",
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return session;
    }

    private static async Task<Guid> CustomerBusinessAsync(SqlConnection connection, SqlTransaction transaction, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT BusinessID FROM dbo.Customers WHERE CustomerID = @id AND IsActive = 1;", connection, transaction);
        command.Parameters.AddWithValue("@id", customerId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is not Guid business) throw new MembershipException("MEMBERSHIP_CUSTOMER_INACTIVE");
        return business;
    }

    private sealed record AttemptRow(Guid Id, string Nonce, string Verifier, Guid? BusinessId, Guid? CustomerId);
}
