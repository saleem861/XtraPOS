using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Devices;

namespace XtraPOS.Api.Security;

public sealed class AuthService(
    BranchDbContext db,
    DeviceService devices,
    PasswordService passwords,
    PermissionService permissions,
    UserAdministrationService users,
    IOptions<LoginProtectionOptions> protection,
    ILogger<AuthService> logger)
{
    public const string InvalidCredentialsMessage = "اسم المستخدم أو كلمة المرور غير صحيحة.";
    public const string LockedMessage = "تعذر تسجيل الدخول حالياً. حاول مرة أخرى لاحقاً.";
    public const string DeviceMessage = "تعذر تسجيل الدخول من هذا الجهاز.";

    public async Task<LoginResult> LoginAsync(
        HttpContext http,
        LoginRequest? request,
        CancellationToken cancellationToken)
    {
        var username = request?.Username?.Trim() ?? "";
        var password = request?.Password ?? "";
        if (username.Length == 0 || password.Length == 0)
        {
            return LoginResult.Fail("يرجى إدخال اسم المستخدم وكلمة المرور.");
        }

        if (username.Length > AccountIdentity.UsernameMaxLength)
        {
            username = username[..AccountIdentity.UsernameMaxLength];
        }

        var access = await devices.EvaluateAccessAsync(http.Request, cancellationToken);
        if (access.Problem != DeviceAccessProblem.None || access.Device is null)
        {
            var deviceReason = access.Problem switch
            {
                DeviceAccessProblem.Revoked => LoginFailureReasons.RevokedDevice,
                DeviceAccessProblem.Inactive => LoginFailureReasons.InactiveDevice,
                DeviceAccessProblem.InactiveBranch => LoginFailureReasons.InactiveBranch,
                DeviceAccessProblem.InactiveStation => LoginFailureReasons.InactiveStation,
                _ => LoginFailureReasons.UnregisteredDevice,
            };
            Guid? auditDeviceId = access.Problem is DeviceAccessProblem.NotRegistered ? null : access.Device?.DeviceId;
            await WriteAuditAsync(null, auditDeviceId, username, false, deviceReason, http, cancellationToken);
            return LoginResult.Fail(DeviceMessage);
        }

        var device = access.Device;
        var normalized = AccountIdentity.NormalizeUsername(username);
        var user = await db.Users.SingleOrDefaultAsync(
            item => item.NormalizedUsername == normalized,
            cancellationToken);

        if (user is null)
        {
            await WriteAuditAsync(null, device.DeviceId, username, false, LoginFailureReasons.UnknownUser, http, cancellationToken);
            return LoginResult.Fail(InvalidCredentialsMessage);
        }

        var now = UtcNowSeconds();
        if (user.LockoutEndAt is not null && user.LockoutEndAt > now)
        {
            await WriteAuditAsync(user.UserId, device.DeviceId, username, false, LoginFailureReasons.LockedOut, http, cancellationToken);
            return LoginResult.Fail(LockedMessage);
        }

        if (user.LockoutEndAt is not null && user.LockoutEndAt <= now)
        {
            user.LockoutEndAt = null;
            user.FailedLoginCount = 0;
        }

        if (!user.IsActive)
        {
            await WriteAuditAsync(user.UserId, device.DeviceId, username, false, LoginFailureReasons.InactiveUser, http, cancellationToken);
            return LoginResult.Fail(InvalidCredentialsMessage);
        }

        var verification = passwords.Verify(user, user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            await RegisterFailureAsync(user, now, cancellationToken);
            await WriteAuditAsync(user.UserId, device.DeviceId, username, false, LoginFailureReasons.InvalidPassword, http, cancellationToken);
            return LoginResult.Fail(InvalidCredentialsMessage);
        }

        var hasAccess = await db.UserBranchAccess.AnyAsync(
            item => item.UserId == user.UserId && item.BranchId == device.BranchId && item.IsActive,
            cancellationToken);
        if (!hasAccess)
        {
            await WriteAuditAsync(user.UserId, device.DeviceId, username, false, LoginFailureReasons.NoBranchAccess, http, cancellationToken);
            return LoginResult.Fail(InvalidCredentialsMessage);
        }

        if (verification == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = passwords.Hash(user, password);
        }

        user.FailedLoginCount = 0;
        user.LockoutEndAt = null;
        user.LastLoginAt = now;
        user.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        await WriteAuditAsync(user.UserId, device.DeviceId, username, true, null, http, cancellationToken);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(AuthClaimTypes.UserId, user.UserId.ToString()),
                new Claim(AuthClaimTypes.Username, user.Username),
                new Claim(AuthClaimTypes.DeviceId, device.DeviceId.ToString()),
            ],
            CookieAuthenticationDefaults.AuthenticationScheme));
        await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);

        var profile = await BuildProfileAsync(user, device, cancellationToken);
        return LoginResult.Ok(profile);
    }

    public async Task LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    public async Task<LoginResult?> ChangePasswordAsync(
        HttpContext http,
        ChangePasswordRequest? request,
        CancellationToken cancellationToken)
    {
        var session = await RequireSessionAsync(http, cancellationToken);
        if (session is null)
        {
            return null;
        }

        var currentPassword = request?.CurrentPassword ?? "";
        var newPassword = request?.NewPassword ?? "";
        var confirmPassword = request?.ConfirmPassword ?? "";
        if (currentPassword.Length == 0 || newPassword.Length == 0)
        {
            return LoginResult.Fail("يرجى إدخال كلمة المرور الحالية والجديدة.");
        }

        if (newPassword.Length < AccountIdentity.PasswordMinLength
            || newPassword.Length > AccountIdentity.PasswordMaxLength)
        {
            return LoginResult.Fail("كلمة المرور يجب أن تكون 8 أحرف على الأقل.");
        }

        if (!string.Equals(newPassword, confirmPassword, StringComparison.Ordinal))
        {
            return LoginResult.Fail("تأكيد كلمة المرور غير مطابق.");
        }

        var (user, device) = session.Value;
        var now = UtcNowSeconds();
        if (user.LockoutEndAt is not null && user.LockoutEndAt > now)
        {
            return LoginResult.Fail(LockedMessage);
        }

        if (passwords.Verify(user, user.PasswordHash, currentPassword) == PasswordVerificationResult.Failed)
        {
            await RegisterFailureAsync(user, now, cancellationToken);
            return LoginResult.Fail(InvalidCredentialsMessage);
        }

        user.PasswordHash = passwords.Hash(user, newPassword);
        user.MustChangePassword = false;
        user.FailedLoginCount = 0;
        user.LockoutEndAt = null;
        user.UpdatedAt = now;
        await users.PublishPasswordChangeAsync(user.UserId, user.PasswordHash, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return LoginResult.Ok(await BuildProfileAsync(user, device, cancellationToken));
    }

    public async Task<CurrentUserResponse?> GetCurrentAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var session = await RequireSessionAsync(http, cancellationToken);
        return session is null ? null : await BuildProfileAsync(session.Value.User, session.Value.Device, cancellationToken);
    }

    private async Task<(AppUser User, Device Device)?> RequireSessionAsync(
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var principal = http.User;
        if (principal.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        if (!Guid.TryParse(principal.FindFirstValue(AuthClaimTypes.UserId), out var userId)
            || !Guid.TryParse(principal.FindFirstValue(AuthClaimTypes.DeviceId), out var claimedDeviceId))
        {
            return null;
        }

        var access = await devices.EvaluateAccessAsync(http.Request, cancellationToken);
        if (access.Problem != DeviceAccessProblem.None || access.Device is null || access.Device.DeviceId != claimedDeviceId)
        {
            return null;
        }

        var user = await db.Users.SingleOrDefaultAsync(item => item.UserId == userId && item.IsActive, cancellationToken);
        if (user is null)
        {
            return null;
        }

        var hasAccess = await db.UserBranchAccess.AnyAsync(
            item => item.UserId == user.UserId && item.BranchId == access.Device.BranchId && item.IsActive,
            cancellationToken);
        return hasAccess ? (user, access.Device) : null;
    }

    public async Task<PosDeviceContext?> GetPosDeviceContextAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var session = await RequireSessionAsync(http, cancellationToken);
        if (session is null
            || session.Value.Device.StationId is not Guid stationId
            || session.Value.Device.Branch is null)
        {
            return null;
        }

        return new PosDeviceContext(
            session.Value.User.UserId,
            session.Value.User.DisplayName,
            session.Value.Device.Branch.BusinessId,
            session.Value.Device.BranchId,
            stationId,
            session.Value.Device.DeviceId);
    }

    public async Task<CatalogCaller?> GetCatalogCallerAsync(HttpContext http, CancellationToken cancellationToken)
    {
        var profile = await GetCurrentAsync(http, cancellationToken);
        if (profile is null
            || !Guid.TryParse(http.User.FindFirstValue(AuthClaimTypes.DeviceId), out var deviceId))
        {
            return null;
        }

        var branchId = await db.Devices
            .Where(device => device.DeviceId == deviceId)
            .Select(device => (Guid?)device.BranchId)
            .SingleOrDefaultAsync(cancellationToken);
        if (branchId is null)
        {
            return null;
        }

        var canViewItems = await permissions.HasPermissionAsync(profile.UserId, PermissionCodes.ItemsView, cancellationToken);
        var canEditMaster = await permissions.HasPermissionAsync(profile.UserId, PermissionCodes.ItemsEditMaster, cancellationToken);
        var canEditBranchState = await permissions.HasPermissionAsync(profile.UserId, PermissionCodes.ItemsEditBranchState, cancellationToken);
        var canEditBranchPrice = await permissions.HasPermissionAsync(profile.UserId, PermissionCodes.ItemsEditBranchPrice, cancellationToken);
        return new CatalogCaller(
            profile.UserId,
            branchId.Value,
            canViewItems,
            canEditMaster,
            canEditBranchState,
            canEditBranchPrice);
    }

    private async Task RegisterFailureAsync(AppUser user, DateTime now, CancellationToken cancellationToken)
    {
        var options = protection.Value;
        user.FailedLoginCount += 1;
        user.UpdatedAt = now;
        if (user.FailedLoginCount >= options.ResolvedMaxFailedAttempts)
        {
            user.LockoutEndAt = now.AddMinutes(options.ResolvedLockoutMinutes);
            logger.LogInformation("User {UserId} is locked until {LockoutEndAt}.", user.UserId, user.LockoutEndAt);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task WriteAuditAsync(
        Guid? userId,
        Guid? deviceId,
        string username,
        bool succeeded,
        string? reason,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        var agent = http.Request.Headers.UserAgent.ToString();
        if (agent.Length > 300)
        {
            agent = agent[..300];
        }

        db.UserLoginAudit.Add(new UserLoginAudit
        {
            UserId = userId,
            DeviceId = deviceId,
            UsernameAttempted = username,
            Succeeded = succeeded,
            FailureReason = reason,
            OccurredAt = UtcNowSeconds(),
            ClientInfo = string.IsNullOrWhiteSpace(agent) ? null : agent,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<CurrentUserResponse> BuildProfileAsync(
        AppUser user,
        Device device,
        CancellationToken cancellationToken)
    {
        var roles = await db.UserRoles
            .Where(item => item.UserId == user.UserId && item.Role.IsActive)
            .Select(item => new RoleInfo(item.Role.RoleCode, item.Role.RoleName))
            .ToListAsync(cancellationToken);
        var permissionCodes = await permissions.GetPermissionCodesAsync(user.UserId, cancellationToken);

        return new CurrentUserResponse
        {
            UserId = user.UserId,
            Username = user.Username,
            DisplayName = user.DisplayName,
            FullName = user.FullName,
            Roles = roles,
            Permissions = permissionCodes,
            BusinessName = device.Branch.Business.BusinessName,
            BusinessDisplayName = device.Branch.Business.BusinessDisplayName,
            BranchName = device.Branch.BranchName,
            StationName = device.Station?.StationName ?? "",
            DeviceName = device.DeviceName,
            MustChangePassword = user.MustChangePassword,
        };
    }

    private static DateTime UtcNowSeconds()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }
}
