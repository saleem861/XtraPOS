using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Security;

public sealed class UserAdministrationException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public static class UserAdminActions
{
    public const string Created = "UserCreated";
    public const string Updated = "UserUpdated";
    public const string Activated = "UserActivated";
    public const string Deactivated = "UserDeactivated";
    public const string RoleChanged = "RoleChanged";
    public const string BranchAccessChanged = "BranchAccessChanged";
    public const string PasswordReset = "PasswordReset";
}

public sealed record UserListFilter(string? Search, bool? IsActive, string? RoleCode, Guid? BranchId);

public sealed record ManagedBranch(Guid BranchId, string BranchName, string? BranchCode);

public sealed record ManagedRole(string RoleCode, string RoleName);

public sealed record ManagedUser(
    Guid UserId,
    string FullName,
    string? EmployeeNumber,
    string Username,
    string? RoleCode,
    string? RoleName,
    bool IsActive,
    bool MustChangePassword,
    DateTime? LastLoginAt,
    IReadOnlyList<ManagedBranch> Branches);

public sealed record ManagedUserPage(string Authority, int Count, IReadOnlyList<ManagedUser> Users);

public sealed record UserAdminCatalog(string Authority, IReadOnlyList<ManagedRole> Roles, IReadOnlyList<ManagedBranch> Branches);

public sealed record IssuedCredential(Guid UserId, string TemporaryPassword);

public sealed class CreateManagedUserRequest
{
    public string? FullName { get; set; }
    public string? EmployeeNumber { get; set; }
    public string? Username { get; set; }
    public string? RoleCode { get; set; }
    public Guid[]? BranchIds { get; set; }
    public bool IsActive { get; set; } = true;
    public string? PasswordMode { get; set; }
    public string? Password { get; set; }
}

public sealed class UpdateManagedUserRequest
{
    public string? FullName { get; set; }
    public string? EmployeeNumber { get; set; }
    public string? Username { get; set; }
}

public sealed class UserAdministrationService(
    SqlConnectionFactory connections,
    SyncEndpointStore endpoints,
    PasswordService passwords,
    ILogger<UserAdministrationService> logger)
{
    public const string CentralAuthority = "Central";
    public const string BranchAuthority = "Branch";
    private const int ResetLimit = 8;

    public async Task<ManagedUserPage> ListAsync(UserListFilter filter, CancellationToken cancellationToken)
    {
        var central = await LinkedCentralAsync(cancellationToken);
        var users = central is null
            ? await ReadUsersAsync(connections.CreateBranchConnectionString(), filter, branchSchema: true, cancellationToken)
            : await ReadUsersAsync(central.BuildConnectionString(central.DatabaseName), filter, branchSchema: false, cancellationToken);
        if (central is not null)
        {
            var logins = await ReadLastLoginsAsync(cancellationToken);
            users = users.Select(user => user with { LastLoginAt = logins.GetValueOrDefault(user.UserId) }).ToArray();
        }

        return new ManagedUserPage(central is null ? BranchAuthority : CentralAuthority, users.Count, users);
    }

    public async Task<ManagedUser?> GetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var page = await ListAsync(new UserListFilter(null, null, null, null), cancellationToken);
        return page.Users.FirstOrDefault(user => user.UserId == userId);
    }

    public async Task<UserAdminCatalog> CatalogAsync(CancellationToken cancellationToken)
    {
        var central = await LinkedCentralAsync(cancellationToken);
        var connectionString = central?.BuildConnectionString(central.DatabaseName) ?? connections.CreateBranchConnectionString();
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var roles = new List<ManagedRole>();
        await using (var command = new SqlCommand(
            """
            SELECT RoleCode, RoleName
            FROM dbo.Roles
            WHERE IsActive = 1 AND RoleCode IN (N'Admin', N'Supervisor', N'User')
            ORDER BY CASE RoleCode WHEN N'Admin' THEN 0 WHEN N'Supervisor' THEN 1 ELSE 2 END;
            """,
            connection))
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                roles.Add(new ManagedRole(reader.GetString(0), reader.GetString(1)));
            }
        }

        var branches = await ReadBranchesAsync(connection, null, cancellationToken);
        return new UserAdminCatalog(central is null ? BranchAuthority : CentralAuthority, roles, branches);
    }

    public async Task<IssuedCredential> CreateAsync(Guid actorUserId, CreateManagedUserRequest? request, CancellationToken cancellationToken)
    {
        var fullName = Required(request?.FullName, "يرجى إدخال الاسم الكامل.", 300);
        var employeeNumber = Required(request?.EmployeeNumber, "يرجى إدخال رقم الموظف.", 50);
        var username = Required(request?.Username, "يرجى إدخال اسم المستخدم.", AccountIdentity.UsernameMaxLength);
        if (!AccountIdentity.IsAcceptableUsername(username))
        {
            throw new UserAdministrationException("اسم المستخدم غير صالح.");
        }

        var roleCode = NormalizeRole(request?.RoleCode);
        var branchIds = DistinctBranches(request?.BranchIds);
        if (branchIds.Count == 0)
        {
            throw new UserAdministrationException("يرجى اختيار فرع واحد على الأقل.");
        }

        var temporary = ResolvePassword(request?.PasswordMode, request?.Password);
        var userId = Guid.NewGuid();
        var hash = HashPassword(userId, temporary);
        await WriteAsync(
            actorUserId,
            userId,
            clearLockout: false,
            async (connection, transaction, branchSchema) =>
            {
                await EnsureActorAsync(connection, transaction, actorUserId, cancellationToken);
                await EnsureMirrorReadyAsync(branchSchema, roleCode, branchIds, cancellationToken);
                await EnsureAvailableAsync(connection, transaction, userId, username, employeeNumber, cancellationToken);
                await EnsureBranchesAsync(connection, transaction, branchIds, cancellationToken);
                await InsertUserAsync(connection, transaction, branchSchema, userId, username, fullName, employeeNumber, hash, request?.IsActive ?? true, cancellationToken);
                await ReplaceRoleAsync(connection, transaction, userId, roleCode, cancellationToken);
                await ReplaceBranchesAsync(connection, transaction, userId, branchIds, cancellationToken);
                await AuditAsync(connection, transaction, actorUserId, userId, UserAdminActions.Created, cancellationToken);
            },
            cancellationToken);
        return new IssuedCredential(userId, temporary);
    }

    public Task UpdateAsync(Guid actorUserId, Guid userId, UpdateManagedUserRequest? request, CancellationToken cancellationToken)
    {
        var fullName = Required(request?.FullName, "يرجى إدخال الاسم الكامل.", 300);
        var employeeNumber = Required(request?.EmployeeNumber, "يرجى إدخال رقم الموظف.", 50);
        var username = Required(request?.Username, "يرجى إدخال اسم المستخدم.", AccountIdentity.UsernameMaxLength);
        if (!AccountIdentity.IsAcceptableUsername(username))
        {
            throw new UserAdministrationException("اسم المستخدم غير صالح.");
        }

        return WriteAsync(
            actorUserId,
            userId,
            clearLockout: false,
            async (connection, transaction, _) =>
            {
                await EnsureActorAsync(connection, transaction, actorUserId, cancellationToken);
                await RequireUserAsync(connection, transaction, userId, cancellationToken);
                await EnsureAvailableAsync(connection, transaction, userId, username, employeeNumber, cancellationToken);
                await using var command = Command(
                    connection,
                    transaction,
                    """
                    UPDATE dbo.Users
                    SET Username = @username,
                        NormalizedUsername = @normalized,
                        DisplayName = @displayName,
                        FullName = @fullName,
                        EmployeeNumber = @employeeNumber,
                        UpdatedAt = SYSUTCDATETIME()
                    WHERE UserID = @id;
                    """);
                AddIdentity(command, userId, username, fullName, employeeNumber);
                await command.ExecuteNonQueryAsync(cancellationToken);
                await AuditAsync(connection, transaction, actorUserId, userId, UserAdminActions.Updated, cancellationToken);
            },
            cancellationToken);
    }

    public Task SetActiveAsync(Guid actorUserId, Guid userId, bool isActive, CancellationToken cancellationToken)
    {
        return WriteAsync(
            actorUserId,
            userId,
            clearLockout: false,
            async (connection, transaction, _) =>
            {
                await EnsureActorAsync(connection, transaction, actorUserId, cancellationToken);
                if (!isActive && await IsLastActiveAdminAsync(connection, transaction, userId, cancellationToken))
                {
                    throw new UserAdministrationException("لا يمكن إيقاف آخر مدير نظام فعال.");
                }

                await using var command = Command(
                    connection,
                    transaction,
                    "UPDATE dbo.Users SET IsActive = @active, UpdatedAt = SYSUTCDATETIME() WHERE UserID = @id;");
                command.Parameters.AddWithValue("@active", isActive);
                command.Parameters.AddWithValue("@id", userId);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new UserAdministrationException("المستخدم غير موجود.", StatusCodes.Status404NotFound);
                }

                await AuditAsync(connection, transaction, actorUserId, userId, isActive ? UserAdminActions.Activated : UserAdminActions.Deactivated, cancellationToken);
            },
            cancellationToken);
    }

    public Task SetRoleAsync(Guid actorUserId, Guid userId, string? roleCode, CancellationToken cancellationToken)
    {
        var role = NormalizeRole(roleCode);
        return WriteAsync(
            actorUserId,
            userId,
            clearLockout: false,
            async (connection, transaction, branchSchema) =>
            {
                await EnsureActorAsync(connection, transaction, actorUserId, cancellationToken);
                await RequireUserAsync(connection, transaction, userId, cancellationToken);
                await EnsureMirrorReadyAsync(branchSchema, role, [], cancellationToken);
                if (!string.Equals(role, RoleCodes.Admin, StringComparison.Ordinal)
                    && await IsLastActiveAdminAsync(connection, transaction, userId, cancellationToken))
                {
                    throw new UserAdministrationException("يجب أن يبقى مدير نظام فعال واحد على الأقل.");
                }

                await ReplaceRoleAsync(connection, transaction, userId, role, cancellationToken);
                await TouchAsync(connection, transaction, userId, cancellationToken);
                await AuditAsync(connection, transaction, actorUserId, userId, UserAdminActions.RoleChanged, cancellationToken);
            },
            cancellationToken);
    }

    public Task SetBranchesAsync(Guid actorUserId, Guid userId, IReadOnlyCollection<Guid>? branchIds, CancellationToken cancellationToken)
    {
        var branches = DistinctBranches(branchIds);
        return WriteAsync(
            actorUserId,
            userId,
            clearLockout: false,
            async (connection, transaction, branchSchema) =>
            {
                await EnsureActorAsync(connection, transaction, actorUserId, cancellationToken);
                await RequireUserAsync(connection, transaction, userId, cancellationToken);
                await EnsureMirrorReadyAsync(branchSchema, null, branches, cancellationToken);
                await EnsureBranchesAsync(connection, transaction, branches, cancellationToken);
                await ReplaceBranchesAsync(connection, transaction, userId, branches, cancellationToken);
                await TouchAsync(connection, transaction, userId, cancellationToken);
                await AuditAsync(connection, transaction, actorUserId, userId, UserAdminActions.BranchAccessChanged, cancellationToken);
            },
            cancellationToken);
    }

    public async Task<IssuedCredential> ResetPasswordAsync(Guid actorUserId, Guid userId, string? passwordMode, string? password, CancellationToken cancellationToken)
    {
        var temporary = ResolvePassword(passwordMode, password);
        var hash = HashPassword(userId, temporary);
        await WriteAsync(
            actorUserId,
            userId,
            clearLockout: true,
            async (connection, transaction, branchSchema) =>
            {
                await EnsureActorAsync(connection, transaction, actorUserId, cancellationToken);
                await RequireUserAsync(connection, transaction, userId, cancellationToken);
                await using var limit = Command(
                    connection,
                    transaction,
                    """
                    SELECT COUNT(*)
                    FROM dbo.UserAdministrationAudit
                    WHERE ActorUserID = @actor
                      AND Action = N'PasswordReset'
                      AND OccurredAt >= DATEADD(MINUTE, -15, SYSUTCDATETIME());
                    """);
                limit.Parameters.AddWithValue("@actor", actorUserId);
                var recent = (int)(await limit.ExecuteScalarAsync(cancellationToken) ?? 0);
                if (recent >= ResetLimit)
                {
                    throw new UserAdministrationException("تم إيقاف إعادة التعيين مؤقتاً. حاول بعد قليل.", StatusCodes.Status429TooManyRequests);
                }

                var sql = branchSchema
                    ? """
                      UPDATE dbo.Users
                      SET PasswordHash = @hash,
                          MustChangePassword = 1,
                          FailedLoginCount = 0,
                          LockoutEndAt = NULL,
                          UpdatedAt = SYSUTCDATETIME()
                      WHERE UserID = @id;
                      """
                    : """
                      UPDATE dbo.Users
                      SET PasswordHash = @hash,
                          MustChangePassword = 1,
                          UpdatedAt = SYSUTCDATETIME()
                      WHERE UserID = @id;
                      """;
                await using var command = Command(connection, transaction, sql);
                command.Parameters.AddWithValue("@hash", hash);
                command.Parameters.AddWithValue("@id", userId);
                if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
                {
                    throw new UserAdministrationException("المستخدم غير موجود.", StatusCodes.Status404NotFound);
                }

                await AuditAsync(connection, transaction, actorUserId, userId, UserAdminActions.PasswordReset, cancellationToken);
            },
            cancellationToken);
        return new IssuedCredential(userId, temporary);
    }

    public async Task PublishPasswordChangeAsync(Guid userId, string passwordHash, CancellationToken cancellationToken)
    {
        var central = await LinkedCentralAsync(cancellationToken);
        if (central is null)
        {
            return;
        }

        await using var connection = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            UPDATE dbo.Users
            SET PasswordHash = @hash,
                MustChangePassword = 0,
                UpdatedAt = SYSUTCDATETIME()
            WHERE UserID = @id;
            """,
            connection);
        command.Parameters.AddWithValue("@hash", passwordHash);
        command.Parameters.AddWithValue("@id", userId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            throw new UserAdministrationException("تعذر حفظ كلمة المرور في الإدارة المركزية.", StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation("User administration password change published for {TargetUserId}.", userId);
    }

    private async Task WriteAsync(
        Guid actorUserId,
        Guid targetUserId,
        bool clearLockout,
        Func<SqlConnection, SqlTransaction, bool, Task> change,
        CancellationToken cancellationToken)
    {
        var central = await LinkedCentralAsync(cancellationToken);
        try
        {
            if (central is null)
            {
                await using var branch = new SqlConnection(connections.CreateBranchConnectionString());
                await branch.OpenAsync(cancellationToken);
                await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
                await change(branch, transaction, true);
                await transaction.CommitAsync(cancellationToken);
            }
            else
            {
                await using var remote = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
                await remote.OpenAsync(cancellationToken);
                await using var transaction = (SqlTransaction)await remote.BeginTransactionAsync(cancellationToken);
                await change(remote, transaction, false);
                await transaction.CommitAsync(cancellationToken);
                await CopyToBranchAsync(central, targetUserId, clearLockout, cancellationToken);
            }
        }
        catch (UserAdministrationException)
        {
            throw;
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            throw UniqueMessage(exception);
        }
        catch (SqlException exception)
        {
            logger.LogWarning("User administration save failed with SQL {SqlNumber} for {TargetUserId}.", exception.Number, targetUserId);
            throw new UserAdministrationException("تعذر حفظ بيانات المستخدم.", StatusCodes.Status503ServiceUnavailable);
        }

        logger.LogInformation("User administration completed for {TargetUserId} by {ActorUserId}.", targetUserId, actorUserId);
    }

    private async Task CopyToBranchAsync(SqlEndpointSettings central, Guid userId, bool clearLockout, CancellationToken cancellationToken)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await CopyOnceAsync(central, userId, clearLockout, cancellationToken);
                return;
            }
            catch (Exception exception) when (exception is not UserAdministrationException)
            {
                last = exception;
            }
        }

        logger.LogWarning(last, "Branch user copy failed for {TargetUserId}.", userId);
        throw new UserAdministrationException("تم الحفظ في الإدارة المركزية، وتعذر تحديث نسخة الفرع.", StatusCodes.Status503ServiceUnavailable);
    }

    private async Task CopyOnceAsync(SqlEndpointSettings central, Guid userId, bool clearLockout, CancellationToken cancellationToken)
    {
        await using var remote = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await remote.OpenAsync(cancellationToken);
        await using var read = new SqlCommand(
            """
            SELECT u.Username, u.NormalizedUsername, u.DisplayName, u.FullName, u.EmployeeNumber, u.PasswordHash,
                   u.IsActive, u.MustChangePassword, u.CreatedAt, u.UpdatedAt, role.RoleCode
            FROM dbo.Users AS u
            OUTER APPLY (
                SELECT TOP (1) r.RoleCode
                FROM dbo.UserRoles AS ur
                INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID
                WHERE ur.UserID = u.UserID
                ORDER BY CASE r.RoleCode WHEN N'Admin' THEN 0 WHEN N'Supervisor' THEN 1 ELSE 2 END, ur.AssignedAt
            ) AS role
            WHERE u.UserID = @id;
            """,
            remote);
        read.Parameters.AddWithValue("@id", userId);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new UserAdministrationException("المستخدم غير موجود في الإدارة المركزية.", StatusCodes.Status503ServiceUnavailable);
        }

        var username = reader.GetString(0);
        var normalized = reader.GetString(1);
        var displayName = reader.GetString(2);
        var fullName = reader.IsDBNull(3) ? null : reader.GetString(3);
        var employeeNumber = reader.IsDBNull(4) ? null : reader.GetString(4);
        var hash = reader.GetString(5);
        var isActive = reader.GetBoolean(6);
        var mustChange = reader.GetBoolean(7);
        var createdAt = reader.GetDateTime(8);
        var updatedAt = reader.IsDBNull(9) ? (DateTime?)null : reader.GetDateTime(9);
        var roleCode = reader.IsDBNull(10) ? null : reader.GetString(10);
        await reader.CloseAsync();

        var branches = new List<(Guid BranchId, bool IsActive)>();
        await using (var access = new SqlCommand(
            "SELECT BranchID, IsActive FROM dbo.UserBranchAccess WHERE UserID = @id;",
            remote))
        {
            access.Parameters.AddWithValue("@id", userId);
            await using var accessReader = await access.ExecuteReaderAsync(cancellationToken);
            while (await accessReader.ReadAsync(cancellationToken))
            {
                branches.Add((accessReader.GetGuid(0), accessReader.GetBoolean(1)));
            }
        }

        var audits = new List<(Guid AuditId, Guid ActorUserId, string Action, DateTime OccurredAt)>();
        await using (var audit = new SqlCommand(
            "SELECT AuditID, ActorUserID, Action, OccurredAt FROM dbo.UserAdministrationAudit WHERE TargetUserID = @id;",
            remote))
        {
            audit.Parameters.AddWithValue("@id", userId);
            await using var auditReader = await audit.ExecuteReaderAsync(cancellationToken);
            while (await auditReader.ReadAsync(cancellationToken))
            {
                audits.Add((auditReader.GetGuid(0), auditReader.GetGuid(1), auditReader.GetString(2), auditReader.GetDateTime(3)));
            }
        }

        await using var branch = new SqlConnection(connections.CreateBranchConnectionString());
        await branch.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await branch.BeginTransactionAsync(cancellationToken);
        await using (var upsert = Command(
            branch,
            transaction,
            """
            IF EXISTS (SELECT 1 FROM dbo.Users WHERE UserID = @id)
                UPDATE dbo.Users
                SET Username = @username,
                    NormalizedUsername = @normalized,
                    DisplayName = @displayName,
                    FullName = @fullName,
                    EmployeeNumber = @employeeNumber,
                    PasswordHash = @hash,
                    IsActive = @active,
                    MustChangePassword = @mustChange,
                    FailedLoginCount = CASE WHEN @clearLockout = 1 THEN 0 ELSE FailedLoginCount END,
                    LockoutEndAt = CASE WHEN @clearLockout = 1 THEN NULL ELSE LockoutEndAt END,
                    UpdatedAt = @updatedAt
                WHERE UserID = @id;
            ELSE
                INSERT INTO dbo.Users
                    (UserID, Username, NormalizedUsername, DisplayName, FullName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, FailedLoginCount, LockoutEndAt, CreatedAt, UpdatedAt)
                VALUES
                    (@id, @username, @normalized, @displayName, @fullName, @employeeNumber, @hash, @active, @mustChange, 0, NULL, @createdAt, @updatedAt);
            """))
        {
            upsert.Parameters.AddWithValue("@id", userId);
            upsert.Parameters.AddWithValue("@username", username);
            upsert.Parameters.AddWithValue("@normalized", normalized);
            upsert.Parameters.AddWithValue("@displayName", displayName);
            upsert.Parameters.AddWithValue("@fullName", (object?)fullName ?? DBNull.Value);
            upsert.Parameters.AddWithValue("@employeeNumber", (object?)employeeNumber ?? DBNull.Value);
            upsert.Parameters.AddWithValue("@hash", hash);
            upsert.Parameters.AddWithValue("@active", isActive);
            upsert.Parameters.AddWithValue("@mustChange", mustChange);
            upsert.Parameters.AddWithValue("@clearLockout", clearLockout);
            upsert.Parameters.AddWithValue("@createdAt", createdAt);
            upsert.Parameters.AddWithValue("@updatedAt", (object?)updatedAt ?? DBNull.Value);
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        if (!string.IsNullOrWhiteSpace(roleCode))
        {
            await ReplaceRoleAsync(branch, transaction, userId, roleCode, cancellationToken);
        }

        foreach (var access in branches)
        {
            await using var grant = Command(
                branch,
                transaction,
                """
                IF EXISTS (SELECT 1 FROM dbo.UserBranchAccess WHERE UserID = @userId AND BranchID = @branchId)
                    UPDATE dbo.UserBranchAccess SET IsActive = @active WHERE UserID = @userId AND BranchID = @branchId;
                ELSE
                    INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
                    VALUES (@userId, @branchId, @active, SYSUTCDATETIME());
                """);
            grant.Parameters.AddWithValue("@userId", userId);
            grant.Parameters.AddWithValue("@branchId", access.BranchId);
            grant.Parameters.AddWithValue("@active", access.IsActive);
            await grant.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var audit in audits)
        {
            await using var insert = Command(
                branch,
                transaction,
                """
                IF NOT EXISTS (SELECT 1 FROM dbo.UserAdministrationAudit WHERE AuditID = @id)
                    INSERT INTO dbo.UserAdministrationAudit (AuditID, ActorUserID, TargetUserID, Action, OccurredAt)
                    VALUES (@id, @actor, @target, @action, @occurred);
                """);
            insert.Parameters.AddWithValue("@id", audit.AuditId);
            insert.Parameters.AddWithValue("@actor", audit.ActorUserId);
            insert.Parameters.AddWithValue("@target", userId);
            insert.Parameters.AddWithValue("@action", audit.Action);
            insert.Parameters.AddWithValue("@occurred", audit.OccurredAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<ManagedUser>> ReadUsersAsync(
        string connectionString,
        UserListFilter filter,
        bool branchSchema,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(filter.RoleCode))
        {
            NormalizeRole(filter.RoleCode);
        }

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var loginColumn = branchSchema ? "u.LastLoginAt" : "CAST(NULL AS datetime2(0))";
        await using var command = new SqlCommand(
            $"""
            SELECT u.UserID, COALESCE(NULLIF(LTRIM(RTRIM(u.FullName)), N''), u.DisplayName), u.EmployeeNumber, u.Username,
                   role.RoleCode, role.RoleName, u.IsActive, u.MustChangePassword, {loginColumn}
            FROM dbo.Users AS u
            OUTER APPLY (
                SELECT TOP (1) r.RoleCode, r.RoleName
                FROM dbo.UserRoles AS ur
                INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID
                WHERE ur.UserID = u.UserID
                ORDER BY CASE r.RoleCode WHEN N'Admin' THEN 0 WHEN N'Supervisor' THEN 1 ELSE 2 END, ur.AssignedAt
            ) AS role
            WHERE (@search IS NULL
                    OR u.DisplayName LIKE @search
                    OR u.FullName LIKE @search
                    OR u.EmployeeNumber LIKE @search
                    OR u.Username LIKE @search)
              AND (@active IS NULL OR u.IsActive = @active)
              AND (@role IS NULL OR role.RoleCode = @role)
              AND (@branchId IS NULL OR EXISTS (
                    SELECT 1 FROM dbo.UserBranchAccess AS access
                    WHERE access.UserID = u.UserID AND access.BranchID = @branchId AND access.IsActive = 1))
            ORDER BY u.FullName, u.Username;
            """,
            connection);
        var search = string.IsNullOrWhiteSpace(filter.Search) ? null : Like(filter.Search.Trim());
        command.Parameters.AddWithValue("@search", (object?)search ?? DBNull.Value);
        command.Parameters.AddWithValue("@active", (object?)filter.IsActive ?? DBNull.Value);
        command.Parameters.AddWithValue("@role", (object?)(string.IsNullOrWhiteSpace(filter.RoleCode) ? null : filter.RoleCode!.Trim()) ?? DBNull.Value);
        command.Parameters.AddWithValue("@branchId", (object?)filter.BranchId ?? DBNull.Value);
        var users = new List<ManagedUser>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                users.Add(new ManagedUser(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.GetBoolean(6),
                    reader.GetBoolean(7),
                    reader.IsDBNull(8) ? null : reader.GetDateTime(8),
                    []));
            }
        }

        var access = await ReadAccessAsync(connection, cancellationToken);
        return users.Select(user => user with { Branches = access.GetValueOrDefault(user.UserId) ?? [] }).ToArray();
    }

    private async Task<Dictionary<Guid, DateTime?>> ReadLastLoginsAsync(CancellationToken cancellationToken)
    {
        var logins = new Dictionary<Guid, DateTime?>();
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT UserID, LastLoginAt FROM dbo.Users;", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            logins[reader.GetGuid(0)] = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
        }

        return logins;
    }

    private static async Task<Dictionary<Guid, IReadOnlyList<ManagedBranch>>> ReadAccessAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var map = new Dictionary<Guid, List<ManagedBranch>>();
        await using var command = new SqlCommand(
            """
            SELECT a.UserID, a.BranchID, b.BranchName, b.BranchCode
            FROM dbo.UserBranchAccess AS a
            INNER JOIN dbo.Branches AS b ON b.BranchID = a.BranchID
            WHERE a.IsActive = 1
            ORDER BY b.BranchName;
            """,
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var userId = reader.GetGuid(0);
            if (!map.TryGetValue(userId, out var list))
            {
                list = [];
                map[userId] = list;
            }

            list.Add(new ManagedBranch(reader.GetGuid(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return map.ToDictionary(item => item.Key, item => (IReadOnlyList<ManagedBranch>)item.Value);
    }

    private static async Task<List<ManagedBranch>> ReadBranchesAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var branches = new List<ManagedBranch>();
        await using var command = Command(
            connection,
            transaction,
            "SELECT BranchID, BranchName, BranchCode FROM dbo.Branches WHERE IsActive = 1 ORDER BY BranchName;");
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            branches.Add(new ManagedBranch(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return branches;
    }

    private async Task EnsureMirrorReadyAsync(bool branchSchema, string? roleCode, IReadOnlyCollection<Guid> branchIds, CancellationToken cancellationToken)
    {
        if (branchSchema)
        {
            return;
        }

        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        if (!string.IsNullOrWhiteSpace(roleCode))
        {
            await using var role = new SqlCommand("SELECT COUNT(*) FROM dbo.Roles WHERE RoleCode = @code AND IsActive = 1;", connection);
            role.Parameters.AddWithValue("@code", roleCode);
            if ((int)(await role.ExecuteScalarAsync(cancellationToken) ?? 0) != 1)
            {
                throw new UserAdministrationException("الدور غير متاح في الفرع.", StatusCodes.Status503ServiceUnavailable);
            }
        }

        foreach (var branchId in branchIds)
        {
            await using var branch = new SqlCommand("SELECT COUNT(*) FROM dbo.Branches WHERE BranchID = @id;", connection);
            branch.Parameters.AddWithValue("@id", branchId);
            if ((int)(await branch.ExecuteScalarAsync(cancellationToken) ?? 0) != 1)
            {
                throw new UserAdministrationException("أحد الفروع غير موجود في نسخة الفرع.", StatusCodes.Status503ServiceUnavailable);
            }
        }
    }

    private static async Task EnsureActorAsync(SqlConnection connection, SqlTransaction transaction, Guid actorUserId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT COUNT(*) FROM dbo.Users WHERE UserID = @id;");
        command.Parameters.AddWithValue("@id", actorUserId);
        if ((int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) != 1)
        {
            throw new UserAdministrationException("حسابك غير موجود في جهة حفظ المستخدمين.", StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task RequireUserAsync(SqlConnection connection, SqlTransaction transaction, Guid userId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "SELECT COUNT(*) FROM dbo.Users WHERE UserID = @id;");
        command.Parameters.AddWithValue("@id", userId);
        if ((int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) != 1)
        {
            throw new UserAdministrationException("المستخدم غير موجود.", StatusCodes.Status404NotFound);
        }
    }

    private static async Task<bool> IsLastActiveAdminAsync(SqlConnection connection, SqlTransaction transaction, Guid userId, CancellationToken cancellationToken)
    {
        await using var self = Command(
            connection,
            transaction,
            """
            SELECT COUNT(*)
            FROM dbo.Users AS u
            INNER JOIN dbo.UserRoles AS ur ON ur.UserID = u.UserID
            INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID
            WHERE u.UserID = @id AND u.IsActive = 1 AND r.RoleCode = N'Admin';
            """);
        self.Parameters.AddWithValue("@id", userId);
        if ((int)(await self.ExecuteScalarAsync(cancellationToken) ?? 0) == 0)
        {
            return false;
        }

        await using var others = Command(
            connection,
            transaction,
            """
            SELECT COUNT(*)
            FROM dbo.Users AS u
            INNER JOIN dbo.UserRoles AS ur ON ur.UserID = u.UserID
            INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID
            WHERE u.UserID <> @id AND u.IsActive = 1 AND r.RoleCode = N'Admin';
            """);
        others.Parameters.AddWithValue("@id", userId);
        return (int)(await others.ExecuteScalarAsync(cancellationToken) ?? 0) == 0;
    }

    private static async Task EnsureAvailableAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid userId,
        string username,
        string employeeNumber,
        CancellationToken cancellationToken)
    {
        await using var usernameCommand = Command(
            connection,
            transaction,
            "SELECT COUNT(*) FROM dbo.Users WHERE NormalizedUsername = @normalized AND UserID <> @id;");
        usernameCommand.Parameters.AddWithValue("@normalized", AccountIdentity.NormalizeUsername(username));
        usernameCommand.Parameters.AddWithValue("@id", userId);
        if ((int)(await usernameCommand.ExecuteScalarAsync(cancellationToken) ?? 0) > 0)
        {
            throw new UserAdministrationException("اسم المستخدم مستخدم بالفعل.");
        }

        await using var employeeCommand = Command(
            connection,
            transaction,
            "SELECT COUNT(*) FROM dbo.Users WHERE EmployeeNumber = @employeeNumber AND UserID <> @id;");
        employeeCommand.Parameters.AddWithValue("@employeeNumber", employeeNumber);
        employeeCommand.Parameters.AddWithValue("@id", userId);
        if ((int)(await employeeCommand.ExecuteScalarAsync(cancellationToken) ?? 0) > 0)
        {
            throw new UserAdministrationException("رقم الموظف مستخدم بالفعل.");
        }
    }

    private static async Task EnsureBranchesAsync(SqlConnection connection, SqlTransaction transaction, IReadOnlyCollection<Guid> branchIds, CancellationToken cancellationToken)
    {
        foreach (var branchId in branchIds)
        {
            await using var command = Command(connection, transaction, "SELECT COUNT(*) FROM dbo.Branches WHERE BranchID = @id AND IsActive = 1;");
            command.Parameters.AddWithValue("@id", branchId);
            if ((int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0) != 1)
            {
                throw new UserAdministrationException("أحد الفروع المختارة غير متاح.");
            }
        }
    }

    private static async Task InsertUserAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        bool branchSchema,
        Guid userId,
        string username,
        string fullName,
        string employeeNumber,
        string hash,
        bool isActive,
        CancellationToken cancellationToken)
    {
        var sql = branchSchema
            ? """
              INSERT INTO dbo.Users
                  (UserID, Username, NormalizedUsername, DisplayName, FullName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, FailedLoginCount, CreatedAt, UpdatedAt)
              VALUES
                  (@id, @username, @normalized, @displayName, @fullName, @employeeNumber, @hash, @active, 1, 0, SYSUTCDATETIME(), SYSUTCDATETIME());
              """
            : """
              INSERT INTO dbo.Users
                  (UserID, Username, NormalizedUsername, DisplayName, FullName, EmployeeNumber, PasswordHash, IsActive, MustChangePassword, CreatedAt, UpdatedAt)
              VALUES
                  (@id, @username, @normalized, @displayName, @fullName, @employeeNumber, @hash, @active, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
              """;
        await using var command = Command(connection, transaction, sql);
        AddIdentity(command, userId, username, fullName, employeeNumber);
        command.Parameters.AddWithValue("@hash", hash);
        command.Parameters.AddWithValue("@active", isActive);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ReplaceRoleAsync(SqlConnection connection, SqlTransaction transaction, Guid userId, string roleCode, CancellationToken cancellationToken)
    {
        await using var remove = Command(
            connection,
            transaction,
            """
            DELETE ur
            FROM dbo.UserRoles AS ur
            INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID
            WHERE ur.UserID = @id AND r.RoleCode <> @code;
            """);
        remove.Parameters.AddWithValue("@id", userId);
        remove.Parameters.AddWithValue("@code", roleCode);
        await remove.ExecuteNonQueryAsync(cancellationToken);

        await using var insert = Command(
            connection,
            transaction,
            """
            INSERT INTO dbo.UserRoles (UserID, RoleID, AssignedAt)
            SELECT @id, r.RoleID, SYSUTCDATETIME()
            FROM dbo.Roles AS r
            WHERE r.RoleCode = @code AND r.IsActive = 1
              AND NOT EXISTS (SELECT 1 FROM dbo.UserRoles AS existing WHERE existing.UserID = @id AND existing.RoleID = r.RoleID);
            """);
        insert.Parameters.AddWithValue("@id", userId);
        insert.Parameters.AddWithValue("@code", roleCode);
        if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            await using var existing = Command(
                connection,
                transaction,
                """
                SELECT COUNT(*)
                FROM dbo.UserRoles AS ur
                INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID
                WHERE ur.UserID = @id AND r.RoleCode = @code;
                """);
            existing.Parameters.AddWithValue("@id", userId);
            existing.Parameters.AddWithValue("@code", roleCode);
            if ((int)(await existing.ExecuteScalarAsync(cancellationToken) ?? 0) != 1)
            {
                throw new UserAdministrationException("الدور غير متاح.");
            }
        }
    }

    private static async Task ReplaceBranchesAsync(SqlConnection connection, SqlTransaction transaction, Guid userId, IReadOnlyCollection<Guid> branchIds, CancellationToken cancellationToken)
    {
        await using (var clear = Command(
            connection,
            transaction,
            "UPDATE dbo.UserBranchAccess SET IsActive = 0 WHERE UserID = @id;"))
        {
            clear.Parameters.AddWithValue("@id", userId);
            await clear.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var branchId in branchIds)
        {
            await using var grant = Command(
                connection,
                transaction,
                """
                IF EXISTS (SELECT 1 FROM dbo.UserBranchAccess WHERE UserID = @userId AND BranchID = @branchId)
                    UPDATE dbo.UserBranchAccess SET IsActive = 1 WHERE UserID = @userId AND BranchID = @branchId;
                ELSE
                    INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
                    VALUES (@userId, @branchId, 1, SYSUTCDATETIME());
                """);
            grant.Parameters.AddWithValue("@userId", userId);
            grant.Parameters.AddWithValue("@branchId", branchId);
            await grant.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task AuditAsync(SqlConnection connection, SqlTransaction transaction, Guid actorUserId, Guid targetUserId, string action, CancellationToken cancellationToken)
    {
        await using var command = Command(
            connection,
            transaction,
            """
            INSERT INTO dbo.UserAdministrationAudit (AuditID, ActorUserID, TargetUserID, Action, OccurredAt)
            VALUES (NEWID(), @actor, @target, @action, SYSUTCDATETIME());
            """);
        command.Parameters.AddWithValue("@actor", actorUserId);
        command.Parameters.AddWithValue("@target", targetUserId);
        command.Parameters.AddWithValue("@action", action);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task TouchAsync(SqlConnection connection, SqlTransaction transaction, Guid userId, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, "UPDATE dbo.Users SET UpdatedAt = SYSUTCDATETIME() WHERE UserID = @id;");
        command.Parameters.AddWithValue("@id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqlEndpointSettings?> LinkedCentralAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var syncTable = new SqlCommand("SELECT OBJECT_ID(N'dbo.SyncEndpoints', N'U');", connection);
        if (await syncTable.ExecuteScalarAsync(cancellationToken) is null or DBNull)
        {
            return null;
        }

        await using var roleCommand = new SqlCommand(
            "SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';",
            connection);
        var role = await roleCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (string.Equals(role, DatabaseRole.Central, StringComparison.Ordinal))
        {
            return null;
        }

        var endpoint = await endpoints.GetAsync(DatabaseRole.Central, cancellationToken);
        if (endpoint is null || !string.Equals(endpoint.Status, SqlEndpointSettings.Linked, StringComparison.Ordinal))
        {
            return null;
        }

        var settings = await endpoints.ReadSettingsAsync(DatabaseRole.Central, cancellationToken);
        if (settings is null
            || !string.Equals(settings.DatabaseRole, DatabaseRole.Central, StringComparison.Ordinal)
            || !CentralDatabase.IsAllowedName(settings.DatabaseName))
        {
            throw new UserAdministrationException("اتصال الإدارة المركزية غير صالح.", StatusCodes.Status503ServiceUnavailable);
        }

        return settings;
    }

    private string HashPassword(Guid userId, string password)
    {
        var probe = new AppUser { UserId = userId };
        var hash = passwords.Hash(probe, password);
        if (passwords.Verify(probe, hash, password) == Microsoft.AspNetCore.Identity.PasswordVerificationResult.Failed
            || string.Equals(hash, password, StringComparison.Ordinal))
        {
            throw new UserAdministrationException("تعذر تجهيز كلمة المرور.", StatusCodes.Status500InternalServerError);
        }

        return hash;
    }

    private static string ResolvePassword(string? mode, string? password)
    {
        if (string.Equals(mode, "manual", StringComparison.OrdinalIgnoreCase))
        {
            var manual = password ?? "";
            if (manual.Length < AccountIdentity.PasswordMinLength || manual.Length > AccountIdentity.PasswordMaxLength)
            {
                throw new UserAdministrationException("كلمة المرور يجب أن تكون 8 أحرف على الأقل.");
            }

            return manual;
        }

        if (string.Equals(mode, "auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(mode))
        {
            return UserPasswordResetCommand.CreateTemporaryPassword();
        }

        throw new UserAdministrationException("طريقة كلمة المرور غير صالحة.");
    }

    private static string NormalizeRole(string? roleCode)
    {
        var role = roleCode?.Trim() ?? "";
        if (role is not (RoleCodes.Admin or RoleCodes.Supervisor or RoleCodes.User))
        {
            throw new UserAdministrationException("الدور يجب أن يكون مديراً أو مشرفاً أو مستخدماً.");
        }

        return role;
    }

    private static List<Guid> DistinctBranches(IReadOnlyCollection<Guid>? branchIds)
    {
        return branchIds?.Where(id => id != Guid.Empty).Distinct().ToList() ?? [];
    }

    private static string Required(string? value, string message, int maxLength)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Length > maxLength)
        {
            throw new UserAdministrationException(message);
        }

        return trimmed;
    }

    private static void AddIdentity(SqlCommand command, Guid userId, string username, string fullName, string employeeNumber)
    {
        var displayName = fullName.Length <= 150 ? fullName : fullName[..150];
        command.Parameters.AddWithValue("@id", userId);
        command.Parameters.AddWithValue("@username", username);
        command.Parameters.AddWithValue("@normalized", AccountIdentity.NormalizeUsername(username));
        command.Parameters.AddWithValue("@displayName", displayName);
        command.Parameters.AddWithValue("@fullName", fullName);
        command.Parameters.AddWithValue("@employeeNumber", employeeNumber);
    }

    private static string Like(string value)
    {
        return "%" + value.Replace("[", "[[]", StringComparison.Ordinal).Replace("%", "[%]", StringComparison.Ordinal).Replace("_", "[_]", StringComparison.Ordinal) + "%";
    }

    private static UserAdministrationException UniqueMessage(SqlException exception)
    {
        if (exception.Message.Contains("EmployeeNumber", StringComparison.OrdinalIgnoreCase))
        {
            return new UserAdministrationException("رقم الموظف مستخدم بالفعل.");
        }

        return new UserAdministrationException("اسم المستخدم مستخدم بالفعل.");
    }

    private static SqlCommand Command(SqlConnection connection, SqlTransaction? transaction, string sql)
    {
        return new SqlCommand(sql, connection, transaction) { CommandTimeout = 60 };
    }
}
