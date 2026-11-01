using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using XtraPOS.Api.Configuration;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Setup;

namespace XtraPOS.Api.Devices;

public sealed class LocalRecoveryStation
{
    public Guid StationId { get; init; }
    public int StationNumber { get; init; }
    public string StationName { get; init; } = "";
}

public sealed class LocalRecoveryContext
{
    public Guid BusinessId { get; init; }
    public string BusinessName { get; init; } = "";
    public string? BusinessDisplayName { get; init; }
    public Guid BranchId { get; init; }
    public string BranchName { get; init; } = "";
    public string? BranchCode { get; init; }
    public string BranchType { get; init; } = "";
    public string SuggestedDeviceName { get; init; } = "";
    public string? ExistingDeviceName { get; init; }
    public Guid? ExistingDeviceId { get; init; }
    public IReadOnlyList<LocalRecoveryStation> Stations { get; init; } = [];
}

public sealed class LocalDeviceRecoveryRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public Guid? StationId { get; set; }
    public int? StationNumber { get; set; }
    public string? StationName { get; set; }
    public string? DeviceName { get; set; }
}

public sealed class LocalDeviceRecovery(
    BranchDbContext db,
    PasswordService passwords,
    IOptions<LoginProtectionOptions> protection,
    SqlConnectionFactory connections,
    DeviceDatabaseDirectory directory)
{
    public const string LockedMessage = "الحساب مقفل مؤقتًا.";
    public const string NotAuthorizedMessage = "لا يملك هذا المستخدم صلاحية ربط جهاز.";
    public const string ServerFailureMessage = "تعذر ربط الجهاز بسبب خطأ في الخادم.";
    public async Task<LocalRecoveryContext> GetContextAsync(CancellationToken cancellationToken)
    {
        var branch = await SingleBranchAsync(cancellationToken);
        var stations = await StationListAsync(branch.BranchId, cancellationToken);
        var device = await db.Devices
            .Where(item => item.BranchId == branch.BranchId && item.IsActive)
            .OrderBy(item => item.RevokedAt)
            .ThenByDescending(item => item.RegisteredAt)
            .Select(item => new { item.DeviceId, item.DeviceName })
            .FirstOrDefaultAsync(cancellationToken);

        return new LocalRecoveryContext
        {
            BusinessId = branch.BusinessId,
            BusinessName = branch.Business.BusinessName,
            BusinessDisplayName = branch.Business.BusinessDisplayName,
            BranchId = branch.BranchId,
            BranchName = branch.BranchName,
            BranchCode = branch.BranchCode,
            BranchType = branch.BranchType,
            SuggestedDeviceName = device?.DeviceName ?? Environment.MachineName,
            ExistingDeviceName = device?.DeviceName,
            ExistingDeviceId = device?.DeviceId,
            Stations = stations,
        };
    }

    public async Task<DeviceRegistrationResponse> RecoverAsync(
        LocalDeviceRecoveryRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            throw new SetupRejectedException("بيانات الربط غير صالحة.");
        }

        var deviceName = (request.DeviceName ?? "").Trim();
        if (deviceName.Length == 0 || deviceName.Length > 150)
        {
            throw new SetupRejectedException("يرجى إدخال اسم الجهاز.");
        }

        var branch = await SingleBranchAsync(cancellationToken);
        await ConfirmAdminAsync(request.Username, request.Password, branch.BranchId, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var station = await ResolveStationAsync(branch.BranchId, request, cancellationToken);
            if (station.StationId == Guid.Empty)
            {
                await db.SaveChangesAsync(cancellationToken);
            }

            var now = UtcNowSeconds();
            var token = DeviceTokenService.Generate();
            var device = await ResolveDeviceAsync(branch.BranchId, station.StationId, deviceName, token, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            directory.Remember(device.DeviceId, connections.EffectiveDatabase);

            return new DeviceRegistrationResponse
            {
                DeviceId = device.DeviceId,
                BusinessId = branch.BusinessId,
                BranchId = branch.BranchId,
                StationId = station.StationId,
                DeviceName = device.DeviceName,
                DeviceType = device.DeviceType,
                DeviceToken = token,
                RegisteredAt = device.RegisteredAt,
                BusinessName = branch.Business.BusinessName,
                BusinessDisplayName = branch.Business.BusinessDisplayName,
                BranchName = branch.BranchName,
                BranchCode = branch.BranchCode,
                BranchType = branch.BranchType,
                StationName = station.StationName,
                StationNumber = station.StationNumber,
            };
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    private async Task ConfirmAdminAsync(
        string? username,
        string? password,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        var trimmed = username?.Trim() ?? "";
        var secret = password ?? "";
        if (trimmed.Length == 0 || secret.Length == 0)
        {
            throw new SetupRejectedException("يرجى إدخال اسم المستخدم وكلمة المرور.");
        }

        var normalized = AccountIdentity.NormalizeUsername(trimmed);
        var user = await db.Users.SingleOrDefaultAsync(item => item.NormalizedUsername == normalized, cancellationToken);
        if (user is null || !user.IsActive)
        {
            throw new SetupRejectedException(AuthService.InvalidCredentialsMessage);
        }

        var now = UtcNowSeconds();
        if (user.LockoutEndAt is not null && user.LockoutEndAt > now)
        {
            throw new SetupRejectedException(LockedMessage);
        }

        if (user.LockoutEndAt is not null)
        {
            user.LockoutEndAt = null;
            user.FailedLoginCount = 0;
        }

        if (passwords.Verify(user, user.PasswordHash, secret) == PasswordVerificationResult.Failed)
        {
            var options = protection.Value;
            user.FailedLoginCount += 1;
            user.UpdatedAt = now;
            if (user.FailedLoginCount >= options.ResolvedMaxFailedAttempts)
            {
                user.LockoutEndAt = now.AddMinutes(options.ResolvedLockoutMinutes);
            }

            await db.SaveChangesAsync(cancellationToken);
            throw new SetupRejectedException(AuthService.InvalidCredentialsMessage);
        }

        var isAdmin = await db.UserRoles.AnyAsync(
            item => item.UserId == user.UserId && item.Role.RoleCode == RoleCodes.Admin && item.Role.IsActive,
            cancellationToken);
        var hasAccess = await db.UserBranchAccess.AnyAsync(
            item => item.UserId == user.UserId && item.BranchId == branchId && item.IsActive,
            cancellationToken);
        if (!isAdmin || !hasAccess)
        {
            throw new SetupRejectedException(NotAuthorizedMessage);
        }

        user.FailedLoginCount = 0;
        user.LockoutEndAt = null;
    }

    private async Task<Branch> SingleBranchAsync(CancellationToken cancellationToken)
    {
        var configured = await db.SystemSettings
            .Where(item => item.SettingKey == "BranchID")
            .Select(item => item.SettingValue)
            .SingleOrDefaultAsync(cancellationToken);
        if (Guid.TryParse(configured, out var branchId))
        {
            var selected = await db.Branches
                .Include(item => item.Business)
                .SingleOrDefaultAsync(
                    item => item.BranchId == branchId && item.IsActive && item.Business.IsActive,
                    cancellationToken);
            if (selected is null)
            {
                throw new SetupRejectedException("تعذر تحديد الفرع الحالي على هذا الخادم.");
            }

            return selected;
        }

        var branches = await db.Branches
            .Include(item => item.Business)
            .Where(item => item.IsActive && item.Business.IsActive)
            .ToListAsync(cancellationToken);
        if (branches.Count != 1)
        {
            throw new SetupRejectedException("تعذر تحديد الفرع الحالي على هذا الخادم.");
        }

        return branches[0];
    }

    private async Task<List<LocalRecoveryStation>> StationListAsync(Guid branchId, CancellationToken cancellationToken)
    {
        return await db.PosStations
            .Where(item => item.BranchId == branchId && item.IsActive)
            .OrderBy(item => item.StationNumber)
            .Select(item => new LocalRecoveryStation
            {
                StationId = item.StationId,
                StationNumber = item.StationNumber,
                StationName = item.StationName,
            })
            .ToListAsync(cancellationToken);
    }

    private async Task<PosStation> ResolveStationAsync(
        Guid branchId,
        LocalDeviceRecoveryRequest request,
        CancellationToken cancellationToken)
    {
        if (request.StationId is Guid stationId && stationId != Guid.Empty)
        {
            var selected = await db.PosStations.SingleOrDefaultAsync(
                item => item.StationId == stationId && item.BranchId == branchId && item.IsActive,
                cancellationToken);
            if (selected is null)
            {
                throw new SetupRejectedException("نقطة البيع غير متاحة في هذا الفرع.");
            }

            return selected;
        }

        if (await db.PosStations.AnyAsync(item => item.BranchId == branchId, cancellationToken))
        {
            throw new SetupRejectedException("يرجى اختيار نقطة البيع.");
        }

        var number = request.StationNumber.GetValueOrDefault(1);
        var name = string.IsNullOrWhiteSpace(request.StationName) ? "كاونتر 1" : request.StationName.Trim();
        if (number <= 0 || name.Length == 0 || name.Length > 100)
        {
            throw new SetupRejectedException("بيانات نقطة البيع غير صالحة.");
        }

        var station = new PosStation
        {
            BranchId = branchId,
            StationNumber = number,
            StationName = name,
            IsActive = true,
            CreatedAt = UtcNowSeconds(),
        };
        db.PosStations.Add(station);
        return station;
    }

    private async Task<Device> ResolveDeviceAsync(
        Guid branchId,
        Guid stationId,
        string deviceName,
        string token,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var devices = await db.Devices
            .Where(item => item.BranchId == branchId && item.IsActive)
            .ToListAsync(cancellationToken);
        var active = devices.Where(item => item.RevokedAt == null).ToList();
        var pool = active.Count > 0 ? active : devices;
        var existing = pool.FirstOrDefault(item => item.DeviceName == deviceName) ?? (pool.Count == 1 ? pool[0] : null);
        if (pool.Count > 1 && existing is null)
        {
            throw new SetupRejectedException("يوجد أكثر من جهاز على هذا الفرع. أدخل اسم الجهاز المسجل لإعادة ربطه.");
        }

        var hash = DeviceTokenService.Hash(token);
        if (existing is null)
        {
            var created = new Device
            {
                BranchId = branchId,
                StationId = stationId,
                DeviceName = deviceName,
                DeviceType = DeviceTokenService.DeviceTypePos,
                DeviceTokenHash = hash,
                IsActive = true,
                RegisteredAt = now,
                CreatedAt = now,
            };
            db.Devices.Add(created);
            return created;
        }

        existing.StationId = stationId;
        existing.DeviceName = deviceName;
        existing.DeviceType = DeviceTokenService.DeviceTypePos;
        existing.DeviceTokenHash = hash;
        existing.IsActive = true;
        existing.RevokedAt = null;
        existing.RegisteredAt = now;
        existing.UpdatedAt = now;
        return existing;
    }

    private static DateTime UtcNowSeconds()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }
}
