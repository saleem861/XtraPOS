using Microsoft.Data.SqlClient;
using Microsoft.AspNetCore.Identity;
using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public sealed class NewTestBranchRequest
{
    public string? AdminUsername { get; set; }
    public string? AdminPassword { get; set; }
    public string? AdminDisplayName { get; set; }
    public string? CentralUsername { get; set; }
    public string? CentralPassword { get; set; }
    public string? BranchName { get; set; }
    public string? BranchCode { get; set; }
    public string? DeviceName { get; set; }
    public bool LinkToCentral { get; set; }
}

public sealed class NewTestBranchService(
    SqlConnectionFactory connections,
    MigrationRunner migrations,
    PasswordService passwords,
    SyncEndpointStore endpoints,
    SharedDataSync sharedData,
    DeviceDatabaseDirectory directory,
    BranchDatabaseProvisioner provisioner,
    IConfiguration configuration,
    IHostEnvironment environment)
{
    public async Task<DeviceRegistrationResponse> CreateAsync(NewTestBranchRequest? request, string? onboardingAuthorization, string? clientKey, CancellationToken cancellationToken)
    {
        if (!environment.IsStaging())
        {
            throw new SetupRejectedException("إنشاء فرع تجربة إضافي متاح في بيئة التجربة فقط.");
        }

        var branchName = (request?.BranchName ?? "").Trim();
        var branchCode = (request?.BranchCode ?? "").Trim();
        var deviceName = (request?.DeviceName ?? "").Trim();
        if (branchName.Length is < 2 or > 150 || !IsCode(branchCode) || deviceName.Length is < 2 or > 150)
        {
            throw new SetupRejectedException("أدخل اسم الفرع ورمزه واسم الجهاز.");
        }

        var databaseName = "XtraPOS_Branch_Test_" + branchCode;
        if (!BranchDatabase.IsTestName(databaseName) || string.Equals(databaseName, connections.ConfiguredDatabase, StringComparison.OrdinalIgnoreCase))
        {
            throw new SetupRejectedException("اسم قاعدة الفرع الجديد غير مسموح، ولن تُستبدل قاعدة الفرع الحالية.");
        }

        var adminUsername = (request?.AdminUsername ?? "").Trim();
        var adminPassword = request?.AdminPassword ?? "";
        var adminDisplay = (request?.AdminDisplayName ?? adminUsername).Trim();
        if (!IsAdminName(adminUsername) || adminPassword.Length < AccountIdentity.PasswordMinLength || adminDisplay.Length is < 2 or > 150)
        {
            throw new SetupRejectedException("أدخل حساب المدير الأول وكلمة مرور من 8 أحرف على الأقل.");
        }

        Guid? centralAdminId = null;
        if (request!.LinkToCentral)
        {
            centralAdminId = await ConfirmCentralAdminAsync(request.CentralUsername, request.CentralPassword, cancellationToken);
        }
        else if (!IndependentOnboarding.TryConsume(environment.ContentRootPath, onboardingAuthorization, clientKey ?? "", out var onboardingMessage))
        {
            throw new SetupRejectedException(onboardingMessage);
        }

        var rejected = BranchProvisionPolicy.RejectDatabaseName(databaseName, connections.ConfiguredDatabase);
        if (rejected is not null)
        {
            throw new SetupRejectedException(rejected);
        }

        await provisioner.EnsureAsync(databaseName, cancellationToken);

        BranchDatabaseOverride.Set(databaseName);
        try
        {
            await migrations.ApplyPendingToAsync(connections.CreateNamedBranchConnectionString(databaseName), cancellationToken);
            var created = await InstallAsync(databaseName, branchName, branchCode, deviceName, adminUsername, adminDisplay, adminPassword, request.LinkToCentral, cancellationToken);
            if (!request.LinkToCentral)
            {
                directory.Remember(created.DeviceId, databaseName);
                return created;
            }

            var central = CentralSettings();
            var endpoint = await endpoints.SaveAsync(central, SqlEndpointSettings.Linked, cancellationToken);
            await sharedData.BootstrapAsync(central, cancellationToken);
            var state = await sharedData.EnsureCustomerReplicaAsync(endpoint.EndpointId, central, cancellationToken);
            if (InitialSyncGuard.NeedsInitialSync(state))
            {
                throw new SetupRejectedException("تم إنشاء الفرع، لكن المزامنة الأولية لم تكتمل. لم يُعلَّم الإعداد ناجحاً.");
            }

            await provisioner.RegisterCentralBranchAsync(created.BranchId, created.BusinessId, branchCode, branchName, centralAdminId!.Value, cancellationToken);
            directory.Remember(created.DeviceId, databaseName);
            return created;
        }
        finally
        {
            BranchDatabaseOverride.Set(null);
        }
    }

    private async Task<Guid> ConfirmCentralAdminAsync(string? username, string? password, CancellationToken cancellationToken)
    {
        var normalized = AccountIdentity.NormalizeUsername(username ?? "");
        if (normalized.Length == 0 || string.IsNullOrEmpty(password))
        {
            throw new SetupRejectedException("ربط الفرع بالمركز يحتاج حساب مدير الإدارة المركزية.");
        }

        var central = CentralSettings();
        await using var connection = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT u.UserID, u.PasswordHash
            FROM dbo.Users AS u
            INNER JOIN dbo.UserRoles AS ur ON ur.UserID = u.UserID
            INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID AND r.RoleCode = N'Admin'
            WHERE u.NormalizedUsername = @name AND u.IsActive = 1;
            """,
            connection);
        command.Parameters.AddWithValue("@name", normalized);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new SetupRejectedException(AuthService.InvalidCredentialsMessage);
        }

        var userId = reader.GetGuid(0);
        var hash = reader.GetString(1);
        if (passwords.Verify(new AppUser(), hash, password) == PasswordVerificationResult.Failed)
        {
            throw new SetupRejectedException(AuthService.InvalidCredentialsMessage);
        }

        return userId;
    }

    private async Task<DeviceRegistrationResponse> InstallAsync(
        string databaseName,
        string branchName,
        string branchCode,
        string deviceName,
        string username,
        string displayName,
        string password,
        bool linkToCentral,
        CancellationToken cancellationToken)
    {
        Guid businessId;
        string businessName;
        string? shortName;
        if (linkToCentral)
        {
            var central = CentralSettings();
            await using var remote = new SqlConnection(central.BuildConnectionString(central.DatabaseName));
            await remote.OpenAsync(cancellationToken);
            await using var business = new SqlCommand("SELECT TOP (1) BusinessID, BusinessName, BusinessDisplayName FROM dbo.Businesses;", remote);
            await using var businessReader = await business.ExecuteReaderAsync(cancellationToken);
            if (!await businessReader.ReadAsync(cancellationToken))
            {
                throw new SetupRejectedException("القاعدة المركزية لا تحتوي على هوية منشأة.");
            }

            businessId = businessReader.GetGuid(0);
            businessName = businessReader.GetString(1);
            shortName = businessReader.IsDBNull(2) ? null : businessReader.GetString(2);
        }
        else
        {
            businessId = Guid.NewGuid();
            businessName = branchName;
            shortName = null;
        }

        var branchId = Guid.NewGuid();
        var stationId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var token = DeviceTokenService.Generate();
        await using var branch = new SqlConnection(connections.CreateBranchConnectionString());
        await branch.OpenAsync(cancellationToken);
        await using var existing = new SqlCommand("SELECT COUNT(*) FROM dbo.Branches WHERE BranchCode = @code;", branch);
        existing.Parameters.AddWithValue("@code", branchCode);
        if ((int)(await existing.ExecuteScalarAsync(cancellationToken) ?? 0) > 0)
        {
            throw new SetupRejectedException("هذا الفرع موجود مسبقاً. لم يتم إنشاء منشأة أو جهاز آخر.");
        }

        await using var install = new SqlCommand(
            $"""
            INSERT INTO dbo.Businesses (BusinessID, BusinessName, BusinessDisplayName, IsActive, CreatedAt)
            VALUES (@businessId, @businessName, @shortName, 1, SYSUTCDATETIME());
            INSERT INTO dbo.Branches (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
            VALUES (@branchId, @businessId, @code, @branchName, N'Retail', 1, SYSUTCDATETIME());
            INSERT INTO dbo.POSStations (StationID, BranchID, StationNumber, StationName, IsActive, CreatedAt)
            VALUES (@stationId, @branchId, 1, N'كاونتر 1', 1, SYSUTCDATETIME());
            INSERT INTO dbo.Devices (DeviceID, BranchID, StationID, DeviceName, DeviceType, DeviceTokenHash, IsActive, RegisteredAt, CreatedAt)
            VALUES (@deviceId, @branchId, @stationId, @deviceName, N'POS', @hash, 1, SYSUTCDATETIME(), SYSUTCDATETIME());
            INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (N'DatabaseRole', N'Branch');
            INSERT INTO dbo.SystemSettings (SettingKey, SettingValue) VALUES (N'BranchID', @branchText);
            INSERT INTO dbo.Users (UserID, Username, NormalizedUsername, DisplayName, PasswordHash, IsActive, MustChangePassword, FailedLoginCount, CreatedAt)
            VALUES (@userId, @username, @normalized, @displayName, @passwordHash, 1, 0, 0, SYSUTCDATETIME());
            INSERT INTO dbo.UserRoles (UserID, RoleID, AssignedAt)
            SELECT @userId, RoleID, SYSUTCDATETIME() FROM dbo.Roles WHERE RoleCode = N'Admin';
            INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
            VALUES (@userId, @branchId, 1, SYSUTCDATETIME());
            """,
            branch);
        install.Parameters.AddWithValue("@businessId", businessId);
        install.Parameters.AddWithValue("@businessName", businessName);
        install.Parameters.AddWithValue("@shortName", (object?)shortName ?? DBNull.Value);
        install.Parameters.AddWithValue("@branchId", branchId);
        install.Parameters.AddWithValue("@code", branchCode);
        install.Parameters.AddWithValue("@branchName", branchName);
        install.Parameters.AddWithValue("@stationId", stationId);
        install.Parameters.AddWithValue("@deviceId", deviceId);
        install.Parameters.AddWithValue("@deviceName", deviceName);
        install.Parameters.AddWithValue("@hash", DeviceTokenService.Hash(token));
        install.Parameters.AddWithValue("@branchText", branchId.ToString("D"));
        var userId = Guid.NewGuid();
        install.Parameters.AddWithValue("@userId", userId);
        install.Parameters.AddWithValue("@username", username);
        install.Parameters.AddWithValue("@displayName", displayName);
        install.Parameters.AddWithValue("@passwordHash", passwords.Hash(new AppUser(), password));
        install.Parameters.AddWithValue("@normalized", AccountIdentity.NormalizeUsername(username));
        await install.ExecuteNonQueryAsync(cancellationToken);

        return new DeviceRegistrationResponse
        {
            DeviceId = deviceId,
            BusinessId = businessId,
            BranchId = branchId,
            StationId = stationId,
            DeviceName = deviceName,
            DeviceType = DeviceTokenService.DeviceTypePos,
            DeviceToken = token,
            RegisteredAt = DateTime.UtcNow,
            BusinessName = businessName,
            BusinessDisplayName = shortName,
            BranchName = branchName,
            BranchCode = branchCode,
            BranchType = "Retail",
            StationName = "كاونتر 1",
            StationNumber = 1,
        };
    }

    private SqlEndpointSettings CentralSettings()
    {
        var server = configuration["CentralSql:Server"];
        var user = configuration["CentralSql:User"];
        var password = configuration["CentralSql:Password"];
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            throw new SetupRejectedException("اتصال القاعدة المركزية غير مكتمل على الخادم.");
        }

        return new SqlEndpointSettings(
            DatabaseRole.Central,
            SqlEndpointSettings.Remote,
            server,
            null,
            null,
            InitialSyncGuard.ApprovedExistingCentral,
            SqlEndpointSettings.SqlLogin,
            user,
            password,
            true,
            true);
    }

    private static bool IsAdminName(string username)
    {
        var normalized = AccountIdentity.NormalizeUsername(username);
        return normalized.Length is >= 3 and <= 100 && normalized.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    private static bool IsCode(string code)
    {
        return code.Length is >= 2 and <= 12 && code.All(character => char.IsAsciiLetterOrDigit(character));
    }
}
