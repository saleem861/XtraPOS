using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Customers;

public sealed record PhoneVerificationDispatch(
    Guid VerificationId,
    DateTime ExpiresAt,
    DateTime ResendAvailableAt,
    string MaskedDestination);

public sealed class PhoneVerificationService(
    SqlConnectionFactory connections,
    SyncEndpointStore endpoints,
    IPhoneVerificationProvider provider,
    IConfiguration configuration,
    ILogger<PhoneVerificationService> logger)
{
    private readonly int codeLength = ReadCodeLength(configuration);

    private static int ReadCodeLength(IConfiguration configuration)
    {
        var configured = configuration.GetValue("PhoneVerification:CodeLength", PhoneVerificationCode.Length);
        if (configured != PhoneVerificationCode.Length)
        {
            throw new InvalidOperationException("PhoneVerification:CodeLength must be 4.");
        }

        return configured;
    }
    public const int LifetimeMinutes = 5;
    public const int ResendSeconds = 60;
    public const int MaxAttempts = 5;

    public async Task<PhoneVerificationDispatch> SendRegistrationAsync(string? customerNumber, string? destinationE164, CancellationToken cancellationToken)
    {
        var number = customerNumber?.Trim();
        var destination = destinationE164?.Trim();
        if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(destination))
        {
            throw new CustomerMasterException("يرجى إدخال رقم الزبون ورقم الهاتف.");
        }

        await using var connection = await OpenAuthorityAsync(cancellationToken);
        var businessId = await BusinessIdAsync(connection, cancellationToken);
        await using (var existing = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.Customers WHERE BusinessID = @business AND CustomerNumber = @number;",
            connection))
        {
            existing.Parameters.AddWithValue("@business", businessId);
            existing.Parameters.AddWithValue("@number", number);
            if ((int)(await existing.ExecuteScalarAsync(cancellationToken) ?? 0) > 0)
            {
                throw new CustomerMasterException("رقم الزبون مستخدم بالفعل.");
            }
        }

        var now = DateTime.UtcNow;
        await using (var pending = new SqlCommand(
            """
            SELECT TOP (1) ResendAvailableAt
            FROM dbo.CustomerPhoneVerificationChallenges
            WHERE CustomerID IS NULL AND CustomerNumber = @number AND DestinationE164 = @destination AND Status = N'Pending'
            ORDER BY CreatedAt DESC;
            """,
            connection))
        {
            pending.Parameters.AddWithValue("@number", number);
            pending.Parameters.AddWithValue("@destination", destination);
            if (await pending.ExecuteScalarAsync(cancellationToken) is DateTime ready && PhoneVerificationRules.ResendBlocked(ready, now))
            {
                throw new CustomerMasterException(PhoneVerificationMessages.ForCategory(PhoneVerificationCategories.RateLimit));
            }
        }

        return await InsertChallengeAsync(connection, businessId, null, number, destination, now, cancellationToken);
    }

    public async Task VerifyRegistrationAsync(Guid verificationId, string? destinationE164, string? otp, CancellationToken cancellationToken)
    {
        var destination = destinationE164?.Trim();
        if (string.IsNullOrWhiteSpace(destination))
        {
            throw new CustomerMasterException("رقم الهاتف غير صالح.");
        }

        await VerifyAsync(verificationId, customerId: null, requiredDestination: destination, otp, cancellationToken);
    }

    public async Task<PhoneVerificationDispatch> SendAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAuthorityAsync(cancellationToken);
        var customer = await ReadDestinationAsync(connection, customerId, cancellationToken);
        if (customer.Destination is null)
        {
            throw new CustomerMasterException("لا يوجد رقم هاتف لهذا الزبون.");
        }

        var now = DateTime.UtcNow;
        var pending = await LatestPendingAsync(connection, customerId, cancellationToken);
        if (PhoneVerificationRules.ResendBlocked(pending, now))
        {
            throw new CustomerMasterException(PhoneVerificationMessages.ForCategory(PhoneVerificationCategories.RateLimit));
        }

        var dispatch = await InsertChallengeAsync(connection, customer.BusinessId, customerId, null, customer.Destination, now, cancellationToken);
        await using (var mark = new SqlCommand(
            """
            UPDATE dbo.Customers SET PhoneVerificationStatus = N'Pending', UpdatedAt = SYSUTCDATETIME()
            WHERE CustomerID = @customer AND PhoneVerificationStatus <> N'Verified';
            """,
            connection))
        {
            mark.Parameters.AddWithValue("@customer", customerId);
            await mark.ExecuteNonQueryAsync(cancellationToken);
        }

        return dispatch;
    }

    private async Task<PhoneVerificationDispatch> InsertChallengeAsync(
        SqlConnection connection,
        Guid businessId,
        Guid? customerId,
        string? customerNumber,
        string destination,
        DateTime now,
        CancellationToken cancellationToken)
    {
        string? code = provider.OwnsCode ? OtpHasher.CreateCode() : null;
        var id = Guid.NewGuid();
        var expires = now.AddMinutes(LifetimeMinutes);
        var resend = now.AddSeconds(ResendSeconds);
        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.CustomerPhoneVerificationChallenges (
                VerificationID, BusinessID, CustomerID, CustomerNumber, DestinationE164, Channel, Provider, OtpHash, Status,
                CreatedAt, ExpiresAt, LastSentAt, ResendAvailableAt, AttemptCount, MaxAttempts)
            VALUES (
                @id, @business, @customer, @number, @destination, @channel, @provider, @hash, N'Pending',
                @created, @expires, @created, @resend, 0, @max);
            """,
            connection);
        insert.Parameters.AddWithValue("@id", id);
        insert.Parameters.AddWithValue("@business", businessId);
        insert.Parameters.AddWithValue("@customer", (object?)customerId ?? DBNull.Value);
        insert.Parameters.AddWithValue("@number", (object?)customerNumber ?? DBNull.Value);
        insert.Parameters.AddWithValue("@destination", destination);
        insert.Parameters.AddWithValue("@channel", provider.Name == "MetaWhatsApp" ? "WhatsApp" : "SMS");
        insert.Parameters.AddWithValue("@provider", provider.Name);
        insert.Parameters.AddWithValue("@hash", code is null ? DBNull.Value : OtpHasher.Hash(code));
        insert.Parameters.AddWithValue("@created", now);
        insert.Parameters.AddWithValue("@expires", expires);
        insert.Parameters.AddWithValue("@resend", resend);
        insert.Parameters.AddWithValue("@max", MaxAttempts);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        var sent = await provider.StartAsync(destination, code, cancellationToken);
        await using var update = new SqlCommand(
            """
            UPDATE dbo.CustomerPhoneVerificationChallenges
            SET Status = @status, ProviderMessageID = @message
            WHERE VerificationID = @id;
            """,
            connection);
        update.Parameters.AddWithValue("@status", sent.Success ? "Pending" : "Failed");
        update.Parameters.AddWithValue("@message", (object?)sent.ExternalId ?? DBNull.Value);
        update.Parameters.AddWithValue("@id", id);
        await update.ExecuteNonQueryAsync(cancellationToken);
        if (!sent.Success)
        {
            logger.LogWarning("Phone verification was not delivered. Category {Category}.", sent.ErrorCategory);
            throw new CustomerMasterException(PhoneVerificationMessages.ForCategory(sent.ErrorCategory), StatusCodes.Status503ServiceUnavailable);
        }

        return new PhoneVerificationDispatch(id, expires, resend, WhatsAppPhoneFormatter.Mask(destination));
    }

    public Task VerifyAsync(Guid customerId, Guid verificationId, string? otp, CancellationToken cancellationToken) =>
        VerifyAsync(verificationId, customerId, requiredDestination: null, otp, cancellationToken);

    private async Task VerifyAsync(Guid verificationId, Guid? customerId, string? requiredDestination, string? otp, CancellationToken cancellationToken)
    {
        var code = PhoneNormalizer.Normalize(otp);
        var lengthError = PhoneVerificationCode.Rejection(code, codeLength);
        if (lengthError is not null)
        {
            throw new CustomerMasterException(lengthError);
        }

        await using var connection = await OpenAuthorityAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT DestinationE164, OtpHash, Status, ExpiresAt, AttemptCount, MaxAttempts, ProviderMessageID
            FROM dbo.CustomerPhoneVerificationChallenges WITH (UPDLOCK, ROWLOCK)
            WHERE VerificationID = @id
              AND ((@customer IS NULL AND CustomerID IS NULL) OR CustomerID = @customer)
              AND (@requiredDestination IS NULL OR DestinationE164 = @requiredDestination);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", verificationId);
        command.Parameters.AddWithValue("@customer", (object?)customerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@requiredDestination", (object?)requiredDestination ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CustomerMasterException("طلب التحقق غير موجود.", StatusCodes.Status404NotFound);
        }

        var destination = reader.GetString(0);
        var hash = reader.IsDBNull(1) ? null : reader.GetString(1);
        var status = reader.GetString(2);
        var expires = reader.GetDateTime(3);
        var attempts = reader.GetInt32(4);
        var max = reader.GetInt32(5);
        var externalId = reader.IsDBNull(6) ? null : reader.GetString(6);
        await reader.CloseAsync();
        if (status is not "Pending")
        {
            throw new CustomerMasterException("رمز التحقق لم يعد صالحاً.");
        }

        var utcNow = DateTime.UtcNow;
        if (PhoneVerificationRules.Expired(expires, utcNow))
        {
            await SetStatusAsync(connection, transaction, verificationId, "Expired", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new CustomerMasterException(PhoneVerificationMessages.ForCategory(PhoneVerificationCategories.Expired));
        }

        if (PhoneVerificationRules.AttemptsExhausted(attempts, max))
        {
            await SetStatusAsync(connection, transaction, verificationId, "Locked", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new CustomerMasterException(PhoneVerificationMessages.ForCategory(PhoneVerificationCategories.Attempts));
        }

        var approved = hash is not null
            ? OtpHasher.Matches(code, hash)
            : (await provider.CheckAsync(destination, code, externalId, cancellationToken)).Approved;
        if (!approved)
        {
            var next = attempts + 1;
            await using var fail = new SqlCommand(
                """
                UPDATE dbo.CustomerPhoneVerificationChallenges
                SET AttemptCount = @attempts, Status = CASE WHEN @attempts >= MaxAttempts THEN N'Locked' ELSE Status END
                WHERE VerificationID = @id;
                """,
                connection,
                transaction);
            fail.Parameters.AddWithValue("@attempts", next);
            fail.Parameters.AddWithValue("@id", verificationId);
            await fail.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new CustomerMasterException(next >= max
                ? PhoneVerificationMessages.ForCategory(PhoneVerificationCategories.Attempts)
                : PhoneVerificationMessages.ForCategory(PhoneVerificationCategories.Rejected));
        }

        await using var success = new SqlCommand(
            """
            UPDATE dbo.CustomerPhoneVerificationChallenges
            SET Status = N'Verified', VerifiedAt = SYSUTCDATETIME()
            WHERE VerificationID = @id;
            UPDATE dbo.Customers
            SET PhoneVerificationStatus = N'Verified',
                PhoneVerifiedAt = SYSUTCDATETIME(),
                PhoneVerifiedE164 = @destination,
                UpdatedAt = SYSUTCDATETIME()
            WHERE @customer IS NOT NULL AND CustomerID = @customer;
            """,
            connection,
            transaction);
        success.Parameters.AddWithValue("@id", verificationId);
        success.Parameters.AddWithValue("@destination", destination);
        success.Parameters.AddWithValue("@customer", (object?)customerId ?? DBNull.Value);
        await success.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task SetStatusAsync(SqlConnection connection, SqlTransaction transaction, Guid id, string status, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "UPDATE dbo.CustomerPhoneVerificationChallenges SET Status = @status WHERE VerificationID = @id;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<DateTime?> LatestPendingAsync(SqlConnection connection, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT TOP (1) ResendAvailableAt
            FROM dbo.CustomerPhoneVerificationChallenges
            WHERE CustomerID = @customer AND Status = N'Pending'
            ORDER BY CreatedAt DESC;
            """,
            connection);
        command.Parameters.AddWithValue("@customer", customerId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DateTime time ? time : null;
    }

    private static async Task<(Guid BusinessId, string? Destination, DateTime? OptInAt)> ReadDestinationAsync(
        SqlConnection connection,
        Guid customerId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT BusinessID, WhatsAppPhoneE164, WhatsAppOptInAt FROM dbo.Customers WHERE CustomerID = @id;",
            connection);
        command.Parameters.AddWithValue("@id", customerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new CustomerMasterException("الزبون غير موجود.", StatusCodes.Status404NotFound);
        }

        return (
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetDateTime(2));
    }

    private static async Task<Guid> BusinessIdAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var setting = new SqlCommand(
            "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'BusinessID';",
            connection);
        if (Guid.TryParse(await setting.ExecuteScalarAsync(cancellationToken) as string, out var configured))
        {
            return configured;
        }

        await using var command = new SqlCommand(
            "SELECT TOP (1) BusinessID FROM dbo.Businesses WHERE IsActive = 1 ORDER BY CreatedAt, BusinessID;",
            connection);
        return await command.ExecuteScalarAsync(cancellationToken) is Guid businessId
            ? businessId
            : throw new CustomerMasterException("هوية المنشأة غير متاحة.", StatusCodes.Status503ServiceUnavailable);
    }

    private async Task<SqlConnection> OpenAuthorityAsync(CancellationToken cancellationToken)
    {
        var endpoint = await endpoints.GetAsync(DatabaseRole.Central, cancellationToken);
        var settings = endpoint is not null && string.Equals(endpoint.Status, SqlEndpointSettings.Linked, StringComparison.Ordinal)
            ? await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken)
            : null;
        var connection = new SqlConnection(settings?.BuildConnectionString(settings.DatabaseName) ?? connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
