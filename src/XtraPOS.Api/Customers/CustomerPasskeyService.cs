using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Customers;

public sealed record PasskeyStatus(string Status, bool PhoneVerifiedPending);
public sealed record PasskeySessionStart(Guid SessionId, string Token, DateTime ExpiresAt, string UrlPath);
public sealed record PasskeyPublicView(string CustomerName, string MaskedPhone, string Status, int SecondsLeft, bool PhoneVerified, bool HasPasskey);

public sealed class CustomerPasskeyService(SqlConnectionFactory connections)
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(3);

    public async Task<PasskeyStatus> StatusAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var active = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.CustomerPasskeys WHERE CustomerID = @id AND Status = N'Active';", customerId, cancellationToken);
        if (active is > 0) return new PasskeyStatus("Active", false);
        var waiting = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.CustomerPasskeys WHERE CustomerID = @id AND Status = N'PendingCashier';", customerId, cancellationToken);
        if (waiting is > 0) return new PasskeyStatus("Pending", false);
        var revoked = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.CustomerPasskeys WHERE CustomerID = @id AND Status = N'Revoked';", customerId, cancellationToken);
        var pending = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.CustomerPasskeySessions WHERE CustomerID = @id AND Purpose = N'Enroll' AND ConsumedAt IS NULL AND ExpiresAt > SYSUTCDATETIME();", customerId, cancellationToken);
        if (pending is > 0) return new PasskeyStatus("Pending", false);
        return new PasskeyStatus(revoked is > 0 ? "Revoked" : "None", false);
    }

    public async Task<PasskeySessionStart> BeginAsync(
        Guid employeeId,
        Guid customerId,
        string purpose,
        bool replacing,
        CancellationToken cancellationToken,
        string? draftName = null,
        string? draftPhone = null,
        bool requireCustomer = true)
    {
        await using var connection = await OpenAsync(cancellationToken);
        if (requireCustomer)
        {
            await RequireCustomerAsync(connection, customerId, cancellationToken);
        }

        if (purpose == "Enroll" && replacing)
        {
            await ExpireOpenAsync(connection, customerId, "Enroll", cancellationToken);
        }

        var sessionId = Guid.NewGuid();
        var token = PasskeyCeremony.Encode(RandomNumberGenerator.GetBytes(32));
        var hash = PasskeyCeremony.Hash(Encoding.UTF8.GetBytes(token));
        var expires = DateTime.UtcNow.Add(SessionLifetime);
        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.CustomerPasskeySessions
                (SessionID, TokenHash, CustomerID, Purpose, EmployeeUserID, ExpiresAt, Result, DraftName, DraftPhone)
            VALUES (@id, @hash, @customer, @purpose, @employee, @expires, N'Pending', @name, @phone);
            """,
            connection);
        insert.Parameters.AddWithValue("@id", sessionId);
        insert.Parameters.AddWithValue("@hash", hash);
        insert.Parameters.AddWithValue("@customer", customerId);
        insert.Parameters.AddWithValue("@purpose", purpose);
        insert.Parameters.AddWithValue("@employee", employeeId);
        insert.Parameters.AddWithValue("@expires", expires);
        insert.Parameters.AddWithValue("@name", (object?)draftName ?? DBNull.Value);
        insert.Parameters.AddWithValue("@phone", (object?)draftPhone ?? DBNull.Value);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await AuditAsync(connection, customerId, employeeId, purpose == "Enroll" ? "EnrollmentStarted" : "VerificationStarted", cancellationToken);
        var path = purpose == "Enroll" ? $"/passkey/enroll/{token}" : $"/passkey/verify/{token}";
        return new PasskeySessionStart(sessionId, token, expires, path);
    }

    public async Task<bool> CustomerExistsAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = @id;", customerId, cancellationToken) is > 0;
    }

    public async Task<Guid> ClaimEnrollmentAsync(Guid sessionId, Guid employeeId, string? phone, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT CustomerID, EmployeeUserID, Purpose, Result, DraftPhone FROM dbo.CustomerPasskeySessions WHERE SessionID = @id;",
            connection);
        command.Parameters.AddWithValue("@id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new PasskeyRejectedException("NOT_FOUND", "طلب Passkey غير موجود.");
        }

        var customerId = reader.GetGuid(0);
        var owner = reader.GetGuid(1);
        var purpose = reader.GetString(2);
        var result = reader.GetString(3);
        var draftPhone = reader.IsDBNull(4) ? null : reader.GetString(4);
        await reader.CloseAsync();
        if (owner != employeeId || !string.Equals(purpose, "Enroll", StringComparison.Ordinal) || !string.Equals(result, "Verified", StringComparison.Ordinal))
        {
            throw new PasskeyRejectedException("NOT_READY", "لم يكتمل حفظ Passkey لهذا الزبون.");
        }

        if (!PhonesMatch(draftPhone, phone))
        {
            throw new PasskeyRejectedException("PHONE", "رقم الهاتف لا يطابق Passkey الذي تم إنشاؤه.");
        }

        if (await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.CustomerPasskeys WHERE CustomerID = @id AND Status = N'Active';", customerId, cancellationToken) is not > 0)
        {
            throw new PasskeyRejectedException("NO_PASSKEY", "لا يوجد Passkey مفعل.");
        }

        return customerId;
    }

    public async Task<PasskeyPublicView> ViewAsync(string token, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await FindAsync(connection, token, cancellationToken) ?? throw new PasskeyRejectedException("NOT_FOUND", "طلب التفعيل غير موجود.");
        if (row.ExpiresAt <= DateTime.UtcNow || row.ConsumedAt is not null)
        {
            throw new PasskeyRejectedException("EXPIRED", "انتهت صلاحية الطلب.");
        }

        var name = await TextAsync(connection, "SELECT Name FROM dbo.Customers WHERE CustomerID = @id;", row.CustomerId, cancellationToken) ?? row.DraftName ?? "زبون";
        var phone = await TextAsync(connection, "SELECT WhatsAppPhoneE164 FROM dbo.Customers WHERE CustomerID = @id;", row.CustomerId, cancellationToken) ?? row.DraftPhone;
        var has = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.CustomerPasskeys WHERE CustomerID = @id AND Status = N'Active';", row.CustomerId, cancellationToken);
        return new PasskeyPublicView(name, Mask(phone), row.Purpose, (int)Math.Max(0, (row.ExpiresAt - DateTime.UtcNow).TotalSeconds), row.PhoneVerifiedAt is not null, has is > 0);
    }

    public async Task MarkPhoneVerifiedAsync(string token, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await RequireOpenAsync(connection, token, "Enroll", cancellationToken);
        await using var update = new SqlCommand("UPDATE dbo.CustomerPasskeySessions SET PhoneVerifiedAt = SYSUTCDATETIME() WHERE SessionID = @id AND PhoneVerifiedAt IS NULL;", connection);
        update.Parameters.AddWithValue("@id", row.SessionId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await AuditAsync(connection, row.CustomerId, null, "PhoneVerified", cancellationToken);
    }

    public async Task<Guid> CustomerForTokenAsync(string token, string purpose, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await RequireOpenAsync(connection, token, purpose, cancellationToken);
        return row.CustomerId;
    }

    public async Task<(string Challenge, string RpId, string UserHandle)> RegistrationOptionsAsync(string token, string rpId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await RequireOpenAsync(connection, token, "Enroll", cancellationToken);
        if (row.EmployeeId == Guid.Empty)
        {
            throw new PasskeyRejectedException("CASHIER_REQUIRED", "يلزم موافقة موظف مخول قبل ربط Passkey.");
        }

        var challenge = PasskeyCeremony.CreateChallenge();
        await StoreChallengeAsync(connection, row.SessionId, challenge, cancellationToken);
        var handle = PasskeyCeremony.Encode(row.CustomerId.ToByteArray());
        return (PasskeyCeremony.Encode(challenge), rpId, handle);
    }

    public async Task CompleteRegistrationAsync(string token, string clientDataJson, string attestationObject, string rpId, IReadOnlySet<string> origins, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await RequireOpenAsync(connection, token, "Enroll", cancellationToken);
        if (row.EmployeeId == Guid.Empty)
        {
            throw new PasskeyRejectedException("CASHIER_REQUIRED", "يلزم موافقة موظف مخول قبل ربط Passkey.");
        }

        var challenge = await TakeChallengeAsync(connection, row, cancellationToken);
        var checkedKey = PasskeyCeremony.ReadRegistration(clientDataJson, attestationObject, PasskeyCeremony.Encode(challenge), rpId, origins);
        await using var revoke = new SqlCommand("UPDATE dbo.CustomerPasskeys SET Status = N'Revoked', RevokedAt = SYSUTCDATETIME() WHERE CustomerID = @id AND Status IN (N'Active', N'PendingCashier');", connection);
        revoke.Parameters.AddWithValue("@id", row.CustomerId);
        await revoke.ExecuteNonQueryAsync(cancellationToken);
        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.CustomerPasskeys (CredentialId, CustomerID, PublicKeyX, PublicKeyY, SignCount, Status)
            VALUES (@cred, @customer, @x, @y, 0, N'Active');
            """,
            connection);
        insert.Parameters.AddWithValue("@cred", checkedKey.CredentialId);
        insert.Parameters.AddWithValue("@customer", row.CustomerId);
        insert.Parameters.AddWithValue("@x", checkedKey.X);
        insert.Parameters.AddWithValue("@y", checkedKey.Y);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await ConsumeAsync(connection, row.SessionId, "Verified", checkedKey.CredentialId, cancellationToken);
        await AuditAsync(connection, row.CustomerId, row.EmployeeId, "PasskeyActivated", cancellationToken);
    }

    public async Task ConfirmBindingAsync(Guid customerId, Guid employeeId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var update = new SqlCommand("UPDATE dbo.CustomerPasskeys SET Status = N'Active' WHERE CustomerID = @id AND Status = N'PendingCashier';", connection);
        update.Parameters.AddWithValue("@id", customerId);
        var changed = await update.ExecuteNonQueryAsync(cancellationToken);
        if (changed == 0)
        {
            throw new PasskeyRejectedException("NOT_READY", "لا يوجد Passkey بانتظار موافقة الكاشير.");
        }

        await AuditAsync(connection, customerId, employeeId, "PasskeyActivated", cancellationToken);
    }

    public async Task<(string Challenge, string[] CredentialIds)> AssertionOptionsAsync(string token, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await RequireOpenAsync(connection, token, "Verify", cancellationToken);
        var active = await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.CustomerPasskeys WHERE CustomerID = @id AND Status = N'Active';", row.CustomerId, cancellationToken);
        if (active is not > 0)
        {
            throw new PasskeyRejectedException("NO_PASSKEY", "لا يوجد Passkey مفعل.");
        }

        var challenge = PasskeyCeremony.CreateChallenge();
        await StoreChallengeAsync(connection, row.SessionId, challenge, cancellationToken);
        var ids = new List<string>();
        await using var command = new SqlCommand("SELECT CredentialId FROM dbo.CustomerPasskeys WHERE CustomerID = @id AND Status = N'Active';", connection);
        command.Parameters.AddWithValue("@id", row.CustomerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(PasskeyCeremony.Encode((byte[])reader[0]));
        }

        return (PasskeyCeremony.Encode(challenge), ids.ToArray());
    }

    public async Task CompleteAssertionAsync(string token, string credentialId, string clientDataJson, string authenticatorData, string signature, string rpId, IReadOnlySet<string> origins, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await RequireOpenAsync(connection, token, "Verify", cancellationToken);
        var challenge = await TakeChallengeAsync(connection, row, cancellationToken);
        byte[] cred;
        byte[] auth;
        byte[] sig;
        try
        {
            cred = PasskeyCeremony.Decode(credentialId);
            auth = PasskeyCeremony.Decode(authenticatorData);
            sig = PasskeyCeremony.Decode(signature);
        }
        catch (FormatException)
        {
            throw new PasskeyRejectedException("BAD_CLIENT", "بيانات التحقق غير صالحة.");
        }

        await using var key = new SqlCommand("SELECT PublicKeyX, PublicKeyY, Status FROM dbo.CustomerPasskeys WHERE CredentialId = @id AND CustomerID = @customer;", connection);
        key.Parameters.AddWithValue("@id", cred);
        key.Parameters.AddWithValue("@customer", row.CustomerId);
        await using var reader = await key.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new PasskeyRejectedException("WRONG_CUSTOMER", "مفتاح Passkey لا يخص هذا الزبون.");
        }

        var status = reader.GetString(2);
        var x = (byte[])reader[0];
        var y = (byte[])reader[1];
        await reader.CloseAsync();
        if (!string.Equals(status, "Active", StringComparison.Ordinal))
        {
            throw new PasskeyRejectedException("REVOKED", "تم إلغاء تفعيل Passkey.");
        }

        PasskeyCeremony.VerifyAssertion(clientDataJson, auth, sig, x, y, PasskeyCeremony.Encode(challenge), rpId, origins);
        await ConsumeAsync(connection, row.SessionId, "Verified", cred, cancellationToken);
        await AuditAsync(connection, row.CustomerId, row.EmployeeId, "VerificationSucceeded", cancellationToken);
    }

    public async Task<string> PollAsync(Guid employeeId, Guid sessionId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT EmployeeUserID, Result, ExpiresAt, ConsumedAt FROM dbo.CustomerPasskeySessions WHERE SessionID = @id;", connection);
        command.Parameters.AddWithValue("@id", sessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.GetGuid(0) != employeeId)
        {
            throw new PasskeyRejectedException("NOT_FOUND", "طلب التحقق غير موجود.");
        }

        var result = reader.GetString(1);
        var expires = reader.GetDateTime(2);
        var consumed = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3);
        if (result == "Verified" && consumed is not null) return "Verified";
        if (expires <= DateTime.UtcNow) return "Expired";
        return result;
    }

    public async Task RevokeAsync(Guid employeeId, Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var update = new SqlCommand("UPDATE dbo.CustomerPasskeys SET Status = N'Revoked', RevokedAt = SYSUTCDATETIME() WHERE CustomerID = @id AND Status = N'Active';", connection);
        update.Parameters.AddWithValue("@id", customerId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await AuditAsync(connection, customerId, employeeId, "PasskeyRevoked", cancellationToken);
    }

    public async Task<bool> HasActiveAsync(Guid customerId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.CustomerPasskeys WHERE CustomerID = @id AND Status = N'Active';", customerId, cancellationToken) is > 0;
    }

    private async Task<byte[]> TakeChallengeAsync(SqlConnection connection, SessionRow row, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT ChallengeHash, ChallengeExpiresAt FROM dbo.CustomerPasskeySessions WHERE SessionID = @id;", connection);
        command.Parameters.AddWithValue("@id", row.SessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken) || reader.IsDBNull(0) || reader.IsDBNull(1) || reader.GetDateTime(1) <= DateTime.UtcNow)
        {
            throw new PasskeyRejectedException("BAD_CHALLENGE", "انتهت صلاحية تحدي التحقق.");
        }

        var hash = (byte[])reader[0];
        await reader.CloseAsync();
        await using var clear = new SqlCommand("UPDATE dbo.CustomerPasskeySessions SET ChallengeHash = NULL, ChallengeExpiresAt = NULL WHERE SessionID = @id AND ChallengeHash = @hash;", connection);
        clear.Parameters.AddWithValue("@id", row.SessionId);
        clear.Parameters.AddWithValue("@hash", hash);
        if (await clear.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PasskeyRejectedException("REPLAY", "تم استخدام طلب التحقق من قبل.");
        }

        return hash;
    }

    private static async Task StoreChallengeAsync(SqlConnection connection, Guid sessionId, byte[] challenge, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("UPDATE dbo.CustomerPasskeySessions SET ChallengeHash = @hash, ChallengeExpiresAt = @expires WHERE SessionID = @id;", connection);
        command.Parameters.AddWithValue("@hash", challenge);
        command.Parameters.AddWithValue("@expires", DateTime.UtcNow.Add(SessionLifetime));
        command.Parameters.AddWithValue("@id", sessionId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ConsumeAsync(SqlConnection connection, Guid sessionId, string result, byte[] credentialId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("UPDATE dbo.CustomerPasskeySessions SET ConsumedAt = SYSUTCDATETIME(), Result = @result, VerifiedCredentialId = @cred WHERE SessionID = @id AND ConsumedAt IS NULL;", connection);
        command.Parameters.AddWithValue("@result", result);
        command.Parameters.AddWithValue("@cred", credentialId);
        command.Parameters.AddWithValue("@id", sessionId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new PasskeyRejectedException("REPLAY", "تم استخدام الطلب من قبل.");
        }
    }

    private async Task<SessionRow> RequireOpenAsync(SqlConnection connection, string token, string purpose, CancellationToken cancellationToken)
    {
        var row = await FindAsync(connection, token, cancellationToken) ?? throw new PasskeyRejectedException("NOT_FOUND", "طلب التحقق غير موجود.");
        if (!string.Equals(row.Purpose, purpose, StringComparison.Ordinal))
        {
            throw new PasskeyRejectedException("WRONG_FLOW", "هذا الرمز لا يخص هذه العملية.");
        }

        if (row.ConsumedAt is not null)
        {
            throw new PasskeyRejectedException("REPLAY", "تم استخدام الطلب من قبل.");
        }

        if (row.ExpiresAt <= DateTime.UtcNow)
        {
            throw new PasskeyRejectedException("EXPIRED", "انتهت صلاحية الطلب.");
        }

        return row;
    }

    private static async Task<SessionRow?> FindAsync(SqlConnection connection, string token, CancellationToken cancellationToken)
    {
        byte[] hash;
        try
        {
            hash = PasskeyCeremony.Hash(Encoding.UTF8.GetBytes(token));
        }
        catch (FormatException)
        {
            return null;
        }

        await using var command = new SqlCommand(
            "SELECT SessionID, CustomerID, Purpose, EmployeeUserID, ExpiresAt, PhoneVerifiedAt, ConsumedAt, DraftName, DraftPhone FROM dbo.CustomerPasskeySessions WHERE TokenHash = @hash;",
            connection);
        command.Parameters.AddWithValue("@hash", hash);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SessionRow(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetGuid(3),
            reader.GetDateTime(4),
            reader.IsDBNull(5) ? null : reader.GetDateTime(5),
            reader.IsDBNull(6) ? null : reader.GetDateTime(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8));
    }

    private static async Task RequireCustomerAsync(SqlConnection connection, Guid customerId, CancellationToken cancellationToken)
    {
        if (await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.Customers WHERE CustomerID = @id;", customerId, cancellationToken) is not > 0)
        {
            throw new PasskeyRejectedException("NOT_FOUND", "الزبون غير موجود.");
        }
    }

    private static async Task ExpireOpenAsync(SqlConnection connection, Guid customerId, string purpose, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("UPDATE dbo.CustomerPasskeySessions SET ConsumedAt = SYSUTCDATETIME(), Result = N'Cancelled' WHERE CustomerID = @id AND Purpose = @purpose AND ConsumedAt IS NULL;", connection);
        command.Parameters.AddWithValue("@id", customerId);
        command.Parameters.AddWithValue("@purpose", purpose);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task AuditAsync(SqlConnection connection, Guid customerId, Guid? actor, string action, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("INSERT INTO dbo.CustomerPasskeyAudit (AuditID, CustomerID, ActorUserID, Action) VALUES (@id, @customer, @actor, @action);", connection);
        command.Parameters.AddWithValue("@id", Guid.NewGuid());
        command.Parameters.AddWithValue("@customer", customerId);
        command.Parameters.AddWithValue("@actor", (object?)actor ?? DBNull.Value);
        command.Parameters.AddWithValue("@action", action);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int?> ScalarAsync(SqlConnection connection, string sql, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", customerId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is int number ? number : Convert.ToInt32(value);
    }

    private static async Task<string?> TextAsync(SqlConnection connection, string sql, Guid customerId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@id", customerId);
        return await command.ExecuteScalarAsync(cancellationToken) as string;
    }

    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static bool PhonesMatch(string? stored, string? submitted)
    {
        var left = Digits(stored);
        var right = Digits(submitted);
        if (left.Length == 0 || right.Length == 0) return false;
        return left.EndsWith(right, StringComparison.Ordinal) || right.EndsWith(left, StringComparison.Ordinal);
    }

    private static string Digits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return new string(value.Where(char.IsDigit).ToArray());
    }

    private static string Mask(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone) || phone.Length < 4) return "هاتف الزبون المسجّل";
        return phone[..3] + new string('*', Math.Max(0, phone.Length - 5)) + phone[^2..];
    }

    private sealed record SessionRow(Guid SessionId, Guid CustomerId, string Purpose, Guid EmployeeId, DateTime ExpiresAt, DateTime? PhoneVerifiedAt, DateTime? ConsumedAt, string? DraftName, string? DraftPhone);
}
