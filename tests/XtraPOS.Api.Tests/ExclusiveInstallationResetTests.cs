using XtraPOS.Api.Setup;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class ExclusiveInstallationResetTests
{
    [Fact]
    public void Reset_drops_only_a_local_exclusive_branch_database()
    {
        Assert.True(ExclusiveInstallationReset.CanDrop("XtraPOS_Branch2", "localhost", null));
        Assert.True(ExclusiveInstallationReset.CanDrop("XtraPOS_Branch2", "localhost", "BARTENDER"));
        Assert.True(ExclusiveInstallationReset.CanDrop("XtraPOS_Branch2", ".", null));
        Assert.False(ExclusiveInstallationReset.CanDrop("XtraPOS_Branch2", "69.30.235.148", null));
        Assert.False(ExclusiveInstallationReset.CanDrop("Shop1", "localhost", null));
        Assert.False(ExclusiveInstallationReset.CanDrop("XtraPOS_Central", "localhost", null));
        Assert.False(ExclusiveInstallationReset.CanDrop("XtraPOS_Branch_01", "localhost", null));
        Assert.False(ExclusiveInstallationReset.CanDrop("XtraPOS_Branch", "localhost", null));
        Assert.False(ExclusiveInstallationReset.CanDrop("XtraPOS_Branch_Test_Staging", "localhost", null));
    }
}
