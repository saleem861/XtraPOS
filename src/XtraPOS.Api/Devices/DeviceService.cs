using Microsoft.EntityFrameworkCore;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Setup;

namespace XtraPOS.Api.Devices;

public sealed class DeviceService(BranchDbContext db)
{
    public async Task<DeviceRegistrationResponse?> GetCurrentAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var device = await AuthenticateAsync(request, cancellationToken);
        if (device is null)
        {
            return null;
        }

        return ToProfile(device);
    }

    public async Task<bool> HeartbeatAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var device = await AuthenticateAsync(request, cancellationToken);
        if (device is null)
        {
            return false;
        }

        var now = UtcNowSeconds();
        device.LastSeenAt = now;
        device.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<DeviceAccess> EvaluateAccessAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.Headers["X-Device-Id"], out var deviceId))
        {
            return new DeviceAccess(null, DeviceAccessProblem.NotRegistered);
        }

        var token = request.Headers["X-Device-Token"].ToString();
        if (string.IsNullOrWhiteSpace(token))
        {
            return new DeviceAccess(null, DeviceAccessProblem.NotRegistered);
        }

        var device = await db.Devices
            .Include(item => item.Branch)
            .ThenInclude(item => item.Business)
            .Include(item => item.Station)
            .SingleOrDefaultAsync(item => item.DeviceId == deviceId, cancellationToken);

        if (device is null || !DeviceTokenService.Matches(device.DeviceTokenHash, token))
        {
            return new DeviceAccess(null, DeviceAccessProblem.NotRegistered);
        }

        if (device.RevokedAt is not null)
        {
            return new DeviceAccess(device, DeviceAccessProblem.Revoked);
        }

        if (!device.IsActive)
        {
            return new DeviceAccess(device, DeviceAccessProblem.Inactive);
        }

        if (!device.Branch.IsActive)
        {
            return new DeviceAccess(device, DeviceAccessProblem.InactiveBranch);
        }

        if (device.StationId is not null && (device.Station is null || !device.Station.IsActive))
        {
            return new DeviceAccess(device, DeviceAccessProblem.InactiveStation);
        }

        return new DeviceAccess(device, DeviceAccessProblem.None);
    }

    private async Task<Device?> AuthenticateAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(request.Headers["X-Device-Id"], out var deviceId))
        {
            return null;
        }

        var token = request.Headers["X-Device-Token"].ToString();
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var device = await db.Devices
            .Include(item => item.Branch)
            .ThenInclude(item => item.Business)
            .Include(item => item.Station)
            .SingleOrDefaultAsync(
                item => item.DeviceId == deviceId && item.IsActive && item.RevokedAt == null,
                cancellationToken);

        if (device is null || !DeviceTokenService.Matches(device.DeviceTokenHash, token))
        {
            return null;
        }

        return device;
    }

    public async Task<bool> RevokeCurrentAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var device = await AuthenticateAsync(request, cancellationToken);
        if (device is null)
        {
            return false;
        }

        var now = UtcNowSeconds();
        device.RevokedAt = now;
        device.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public static DeviceRegistrationResponse ToProfile(Device device)
    {
        return new DeviceRegistrationResponse
        {
            DeviceId = device.DeviceId,
            BusinessId = device.Branch.BusinessId,
            BranchId = device.BranchId,
            StationId = device.StationId ?? Guid.Empty,
            DeviceName = device.DeviceName,
            DeviceType = device.DeviceType,
            RegisteredAt = device.RegisteredAt,
            BusinessName = device.Branch.Business.BusinessName,
            BusinessDisplayName = device.Branch.Business.BusinessDisplayName,
            BranchName = device.Branch.BranchName,
            BranchCode = device.Branch.BranchCode,
            BranchType = device.Branch.BranchType,
            StationName = device.Station?.StationName ?? "",
            StationNumber = device.Station?.StationNumber ?? 0,
        };
    }

    private static DateTime UtcNowSeconds()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }
}

public enum DeviceAccessProblem
{
    None,
    NotRegistered,
    Inactive,
    Revoked,
    InactiveBranch,
    InactiveStation,
}

public sealed record DeviceAccess(Device? Device, DeviceAccessProblem Problem);
