using XtraPOS.Api.Devices;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class DeviceSetupPolicyTests
{
    [Fact]
    public void SuggestsTheSmallestMissingPositiveNumber()
    {
        Assert.Equal(4, PosNumberAllocator.SuggestNext([1, 2, 3, 5]));
        Assert.False(PosNumberAllocator.IsAvailable([4], 4));
    }

    [Fact]
    public void RefusesProtectedAndSharedDatabaseDrops()
    {
        Assert.NotNull(ProtectedDatabasePolicy.RefuseDrop("XtraPOS_Central", true, true, false));
        Assert.NotNull(ProtectedDatabasePolicy.RefuseDrop("المقاطعة", true, true, false));
        Assert.NotNull(ProtectedDatabasePolicy.RefuseDrop("XtraPOS_Branch_Test", false, true, false));
        Assert.Null(ProtectedDatabasePolicy.RefuseDrop("XtraPOS_Local_Device", true, true, false));
    }

    [Fact]
    public void MergesUnknownConfigurationFields()
    {
        var merged = DeviceConfigDocument.Merge("{\"schemaVersion\":1,\"extensions\":{\"keep\":true}}", new Dictionary<string, string?> { ["posName"] = "كاونتر 4" });
        Assert.Contains("\"keep\":true", merged);
        Assert.Contains("posName", merged);
        Assert.Contains("\"configVersion\":1", merged);
    }
}
