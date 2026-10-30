using Microsoft.EntityFrameworkCore;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Setup;

namespace XtraPOS.Api.Devices;

public sealed class SecondaryDeviceRegistration(BranchDbContext db, DevicePairingService pairing)
{
    public async Task<IReadOnlyList<SecondaryStationResponse>> ListStationsAsync(
        Guid branchId,
        string? pairingToken,
        CancellationToken cancellationToken)
    {
        pairing.Peek(pairingToken, branchId);
        await RequireBranchAsync(branchId, cancellationToken);
        var stations = await db.PosStations
            .Where(station => station.BranchId == branchId && station.IsActive)
            .OrderBy(station => station.StationNumber)
            .Select(station => new SecondaryStationResponse
            {
                StationId = station.StationId,
                StationNumber = station.StationNumber,
                StationName = station.StationName,
            })
            .ToListAsync(cancellationToken);
        return stations;
    }

    public async Task<SecondaryStationResponse> CreateStationAsync(
        SecondaryStationRequest request,
        CancellationToken cancellationToken)
    {
        var grant = pairing.Peek(request.PairingToken, request.BranchId);
        if (!grant.CanCreateStation)
        {
            throw new PairingForbiddenException(DevicePairingService.ForbiddenStationMessage);
        }

        await RequireBranchAsync(request.BranchId, cancellationToken);
        var name = (request.StationName ?? "").Trim();
        if (request.StationNumber <= 0 || name.Length == 0 || name.Length > 100)
        {
            throw new PairingRejectedException("بيانات نقطة البيع غير صالحة.");
        }

        var duplicate = await db.PosStations.AnyAsync(
            station => station.BranchId == request.BranchId && station.StationNumber == request.StationNumber,
            cancellationToken);
        if (duplicate)
        {
            throw new PairingRejectedException("رقم نقطة البيع مستخدم في هذا الفرع.");
        }

        var now = UtcNowSeconds();
        var station = new PosStation
        {
            BranchId = request.BranchId,
            StationNumber = request.StationNumber,
            StationName = name,
            IsActive = true,
            CreatedAt = now,
        };
        db.PosStations.Add(station);
        await db.SaveChangesAsync(cancellationToken);
        if (station.StationId == Guid.Empty)
        {
            throw new InvalidOperationException("StationID was not generated.");
        }

        return new SecondaryStationResponse
        {
            StationId = station.StationId,
            StationNumber = station.StationNumber,
            StationName = station.StationName,
        };
    }

    public async Task<DeviceRegistrationResponse> RegisterAsync(
        SecondaryRegisterRequest request,
        CancellationToken cancellationToken)
    {
        pairing.Consume(request.PairingToken, request.BranchId);
        var branch = await db.Branches
            .Include(item => item.Business)
            .SingleOrDefaultAsync(item => item.BranchId == request.BranchId && item.IsActive, cancellationToken);
        if (branch is null)
        {
            throw new PairingRejectedException(DevicePairingService.InvalidCodeMessage);
        }

        var deviceName = (request.DeviceName ?? "").Trim();
        if (request.StationId == Guid.Empty || deviceName.Length == 0 || deviceName.Length > 150)
        {
            throw new PairingRejectedException("بيانات الجهاز غير صالحة.");
        }

        var station = await db.PosStations.SingleOrDefaultAsync(
            item => item.StationId == request.StationId && item.BranchId == branch.BranchId && item.IsActive,
            cancellationToken);
        if (station is null)
        {
            throw new PairingRejectedException("نقطة البيع غير متاحة في هذا الفرع.");
        }

        var now = UtcNowSeconds();
        var token = DeviceTokenService.Generate();
        var device = new Device
        {
            BranchId = branch.BranchId,
            StationId = station.StationId,
            DeviceName = deviceName,
            DeviceType = DeviceTokenService.DeviceTypePos,
            DeviceTokenHash = DeviceTokenService.Hash(token),
            IsActive = true,
            RegisteredAt = now,
            CreatedAt = now,
        };
        db.Devices.Add(device);
        await db.SaveChangesAsync(cancellationToken);
        await DeviceConfigurationWriter.SaveAsync(db, device.DeviceId, branch.BranchId, deviceName, station.StationNumber, cancellationToken);
        if (device.DeviceId == Guid.Empty)
        {
            throw new InvalidOperationException("DeviceID was not generated.");
        }

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

    private async Task RequireBranchAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var exists = await db.Branches.AnyAsync(branch => branch.BranchId == branchId && branch.IsActive, cancellationToken);
        if (!exists)
        {
            throw new PairingRejectedException(DevicePairingService.InvalidCodeMessage);
        }
    }

    private static DateTime UtcNowSeconds()
    {
        var now = DateTime.UtcNow;
        return new DateTime(now.Ticks - (now.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }
}

public sealed class SecondaryStationRequest
{
    public Guid BranchId { get; set; }
    public int StationNumber { get; set; }
    public string? StationName { get; set; }
    public string? PairingToken { get; set; }
}

public sealed class SecondaryRegisterRequest
{
    public Guid BranchId { get; set; }
    public Guid StationId { get; set; }
    public string? DeviceName { get; set; }
    public string? PairingToken { get; set; }
}

public sealed class SecondaryStationResponse
{
    public Guid StationId { get; init; }
    public int StationNumber { get; init; }
    public string StationName { get; init; } = "";
}

public sealed class PairingForbiddenException(string message) : Exception(message);
