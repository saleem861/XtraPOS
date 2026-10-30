using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Devices;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class DevicePairingServiceTests
{
    [Fact]
    public void IssuedCode_IsTemporaryBranchScopedAndContainsNoSql()
    {
        var pairing = new DevicePairingService();
        var now = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
        pairing.UtcNow = () => now;
        var branchId = Guid.NewGuid();
        var issued = pairing.Issue(branchId, canCreateStation: true);

        Assert.Equal(8, issued.Code.Length);
        Assert.DoesNotContain('=', issued.Code);
        Assert.DoesNotContain(';', issued.Code);
        Assert.DoesNotContain("Password", issued.Code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Server", issued.Code, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(now.Add(DevicePairingService.CodeLifetime), issued.ExpiresAt);

        var verified = pairing.Verify(branchId, issued.Code);
        Assert.True(verified.CanCreateStation);
        Assert.NotEqual(issued.Code, verified.PairingToken);
        Assert.Throws<PairingRejectedException>(() => pairing.Verify(branchId, issued.Code));
    }

    [Fact]
    public void WrongBranchExpiredAndReplay_AreRejected()
    {
        var pairing = new DevicePairingService();
        var now = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
        pairing.UtcNow = () => now;
        var branchId = Guid.NewGuid();
        var issued = pairing.Issue(branchId, canCreateStation: false);

        Assert.Throws<PairingRejectedException>(() => pairing.Verify(Guid.NewGuid(), issued.Code));

        var second = pairing.Issue(branchId, canCreateStation: false);
        now = now.Add(DevicePairingService.CodeLifetime).AddSeconds(1);
        Assert.Throws<PairingRejectedException>(() => pairing.Verify(branchId, second.Code));

        now = new DateTime(2026, 10, 7, 8, 0, 0, DateTimeKind.Utc);
        var third = pairing.Issue(branchId, canCreateStation: false);
        var verified = pairing.Verify(branchId, third.Code);
        Assert.False(verified.CanCreateStation);
        pairing.Peek(verified.PairingToken, branchId);
        pairing.Consume(verified.PairingToken, branchId);
        Assert.Throws<PairingRejectedException>(() => pairing.Consume(verified.PairingToken, branchId));
        Assert.Throws<PairingRejectedException>(() => pairing.Consume(null, branchId));
    }
}

[Collection("Branch")]
public sealed class SecondaryDeviceRegistrationTests(BranchApiFixture fixture)
{
    [Fact]
    public async Task PairingIsRequired_AndInvalidOrExpiredCodesAreRejected()
    {
        var pairing = fixture.Factory.Services.GetRequiredService<DevicePairingService>();
        var now = new DateTime(2026, 10, 7, 9, 0, 0, DateTimeKind.Utc);
        pairing.UtcNow = () => now;
        try
        {
            var (branchId, stationId) = await BranchAndStationAsync();
            using var anonymous = fixture.CreateClient();
            var denied = await anonymous.PostAsJsonAsync("/api/devices/pairing-codes", new { branchId, password = "Secret123", connectionString = "Server=localhost" });
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

            using var admin = await AdminClientAsync();
            var issued = await admin.PostAsJsonAsync("/api/devices/pairing-codes", new
            {
                branchId,
                password = "Secret123",
                connectionString = "Server=localhost;Initial Catalog=XtraPOS_Branch;User ID=sa",
            });
            var issuedBody = await issued.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, issued.StatusCode);
            Assert.DoesNotContain("Secret123", issuedBody);
            Assert.DoesNotContain("connectionString", issuedBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Initial Catalog", issuedBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("User ID", issuedBody, StringComparison.OrdinalIgnoreCase);
            var issuedJson = JsonSerializer.Deserialize<IssuedCodeDto>(issuedBody, JsonOptions);
            Assert.False(string.IsNullOrWhiteSpace(issuedJson!.Code));

            var devicesBefore = await DeviceCountAsync();
            var missing = await anonymous.PostAsJsonAsync("/api/devices/register", new
            {
                branchId,
                stationId,
                deviceName = "جهاز اختبار بدون رمز",
                password = "Secret123",
            });
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            Assert.Contains(DevicePairingService.MissingTokenMessage, await missing.Content.ReadAsStringAsync());
            Assert.Equal(devicesBefore, await DeviceCountAsync());

            var invalid = await anonymous.PostAsJsonAsync("/api/devices/pairing-codes/verify", new { branchId, code = "NOPE1234" });
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains(DevicePairingService.InvalidCodeMessage, await invalid.Content.ReadAsStringAsync());

            var wrongBranch = await anonymous.PostAsJsonAsync("/api/devices/pairing-codes/verify", new { branchId = Guid.NewGuid(), code = issuedJson.Code });
            Assert.Equal(HttpStatusCode.BadRequest, wrongBranch.StatusCode);

            var expiredIssue = await admin.PostAsJsonAsync("/api/devices/pairing-codes", new { branchId });
            var expiredCode = (await expiredIssue.Content.ReadFromJsonAsync<IssuedCodeDto>())!.Code;
            now = now.Add(DevicePairingService.CodeLifetime).AddSeconds(1);
            var expired = await anonymous.PostAsJsonAsync("/api/devices/pairing-codes/verify", new { branchId, code = expiredCode });
            Assert.Equal(HttpStatusCode.BadRequest, expired.StatusCode);
            Assert.Contains(DevicePairingService.InvalidCodeMessage, await expired.Content.ReadAsStringAsync());
        }
        finally
        {
            pairing.UtcNow = () => DateTime.UtcNow;
            await CleanupAsync();
        }
    }

    [Fact]
    public async Task Register_UsesBranchApi_AndCreateStationRequiresPermission()
    {
        var pairing = fixture.Factory.Services.GetRequiredService<DevicePairingService>();
        pairing.UtcNow = () => DateTime.UtcNow;
        try
        {
            var (branchId, stationId) = await BranchAndStationAsync();
            using var admin = await AdminClientAsync();
            using var device = fixture.CreateClient();
            var issued = await admin.PostAsJsonAsync("/api/devices/pairing-codes", new { branchId });
            var code = (await issued.Content.ReadFromJsonAsync<IssuedCodeDto>())!.Code;
            var verified = await device.PostAsJsonAsync("/api/devices/pairing-codes/verify", new { branchId, code });
            var verifiedBody = await verified.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, verified.StatusCode);
            Assert.DoesNotContain("Password", verifiedBody, StringComparison.OrdinalIgnoreCase);
            var token = JsonSerializer.Deserialize<VerifiedDto>(verifiedBody, JsonOptions)!.PairingToken;

            var replay = await device.PostAsJsonAsync("/api/devices/pairing-codes/verify", new { branchId, code });
            Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);

            var restricted = pairing.Issue(branchId, canCreateStation: false);
            var restrictedVerified = pairing.Verify(branchId, restricted.Code);
            var forbidden = await device.PostAsJsonAsync("/api/devices/stations", new
            {
                branchId,
                stationNumber = 81,
                stationName = "كاونتر مرفوض",
                pairingToken = restrictedVerified.PairingToken,
                password = "Secret123",
                connectionString = "Server=localhost",
            });
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            Assert.Contains(DevicePairingService.ForbiddenStationMessage, await forbidden.Content.ReadAsStringAsync());
            Assert.Equal(0, await StationCountAsync(81));

            var created = await device.PostAsJsonAsync("/api/devices/stations", new
            {
                branchId,
                stationNumber = 82,
                stationName = "كاونتر مسموح",
                pairingToken = token,
            });
            var createdBody = await created.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            Assert.DoesNotContain("connectionString", createdBody, StringComparison.OrdinalIgnoreCase);
            var station = JsonSerializer.Deserialize<StationDto>(createdBody, JsonOptions);
            Assert.Equal("كاونتر مسموح", station!.StationName);
            Assert.NotEqual("جهاز اختبار مسموح", station.StationName);

            var registered = await device.PostAsJsonAsync("/api/devices/register", new
            {
                branchId,
                stationId = station.StationId,
                deviceName = "جهاز اختبار مسموح",
                pairingToken = token,
                password = "Secret123",
                connectionString = "Server=localhost;Initial Catalog=XtraPOS_Branch;User ID=sa",
            });
            var registeredBody = await registered.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
            Assert.Contains("deviceToken", registeredBody);
            Assert.DoesNotContain("Secret123", registeredBody);
            Assert.DoesNotContain("connectionString", registeredBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Initial Catalog", registeredBody, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("User ID", registeredBody, StringComparison.OrdinalIgnoreCase);
            var profile = JsonSerializer.Deserialize<RegisteredDto>(registeredBody, JsonOptions);
            Assert.Equal("جهاز اختبار مسموح", profile!.DeviceName);
            Assert.Equal("كاونتر مسموح", profile.StationName);
            Assert.NotEqual(profile.DeviceName, profile.StationName);

            var again = await device.PostAsJsonAsync("/api/devices/register", new
            {
                branchId,
                stationId,
                deviceName = "جهاز اختبار مكرر",
                pairingToken = token,
            });
            Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        }
        finally
        {
            pairing.UtcNow = () => DateTime.UtcNow;
            await CleanupAsync();
        }
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Device-Id", fixture.DeviceId.ToString());
        client.DefaultRequestHeaders.Add("X-Device-Token", fixture.DeviceToken);
        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = fixture.Username, password = "Admin1234" });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        return client;
    }

    private async Task<(Guid BranchId, Guid StationId)> BranchAndStationAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BranchDbContext>();
        var branch = await db.Branches.SingleAsync();
        var station = await db.PosStations.SingleAsync(item => item.StationNumber == 1);
        return (branch.BranchId, station.StationId);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private async Task<int> DeviceCountAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BranchDbContext>();
        return await db.Devices.CountAsync();
    }

    private async Task<int> StationCountAsync(int stationNumber)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BranchDbContext>();
        return await db.PosStations.CountAsync(station => station.StationNumber == stationNumber);
    }

    private async Task CleanupAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BranchDbContext>();
        await db.Devices.Where(device => device.DeviceName.StartsWith("جهاز اختبار")).ExecuteDeleteAsync();
        await db.PosStations.Where(station => station.StationNumber >= 80).ExecuteDeleteAsync();
    }

    private sealed class IssuedCodeDto
    {
        public string Code { get; set; } = "";
    }

    private sealed class VerifiedDto
    {
        public string PairingToken { get; set; } = "";
    }

    private sealed class StationDto
    {
        public Guid StationId { get; set; }
        public string StationName { get; set; } = "";
    }

    private sealed class RegisteredDto
    {
        public string DeviceName { get; set; } = "";
        public string StationName { get; set; } = "";
    }
}
