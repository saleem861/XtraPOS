using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

public sealed class TestDatabaseGuardTests
{
    [Fact]
    public void Guard_AllowsOnlyADedicatedTestDatabase()
    {
        Assert.Equal(TestDatabase.Name, TestDatabase.Require(TestDatabase.Name));
        Assert.StartsWith(BranchDatabase.TestNamePrefix + "_", TestDatabase.Require(TestDatabase.Name), StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => TestDatabase.Require(BranchDatabase.Name));
        Assert.Throws<InvalidOperationException>(() => TestDatabase.Require(CentralDatabase.Name));
        Assert.Throws<InvalidOperationException>(() => TestDatabase.Require("xtrapos_branch"));
        Assert.Throws<InvalidOperationException>(() => TestDatabase.Require("XtraPOS_Branch_Test"));
        Assert.Throws<InvalidOperationException>(() => TestDatabase.Require("saleemPOSDB"));
        Assert.Throws<InvalidOperationException>(() => TestDatabase.Require("master"));
        Assert.Throws<InvalidOperationException>(() => TestDatabase.Require("XtraPOS_Branch_Test_Integration]; DROP DATABASE [XtraPOS_Branch"));
        Assert.True(BranchDatabase.IsOperationalName("XtraPOS_Branch_01"));
        Assert.True(BranchDatabase.IsAllowedName("XtraPOS_Branch_01"));
        Assert.False(BranchDatabase.IsOperationalName(BranchDatabase.Name));
        Assert.False(BranchDatabase.IsOperationalName("XtraPOS_Branch_Test_MainSetup"));
        Assert.False(BranchDatabase.IsAllowedName("Shop1"));
        Assert.DoesNotContain("XtraPOS_Branch;", TestDatabase.ConnectionString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(TestDatabase.Name, TestDatabase.ConnectionString(), StringComparison.Ordinal);
    }

    [Fact]
    public void BranchRemote_IsOneWayUntilAnExplicitRestore()
    {
        Assert.Equal("BranchLocalToBranchRemote", BranchRemoteSync.NormalDirection);
        Assert.Equal("RestoreOnly", BranchRemoteSync.ReverseDirection);
        Assert.False(BranchRemoteSync.IsImplemented);
        Assert.False(BranchRemoteSync.ReverseIsNormalSync);
        var replicate = Assert.Throws<InvalidOperationException>(BranchRemoteSync.Replicate);
        var restore = Assert.Throws<InvalidOperationException>(BranchRemoteSync.Restore);
        Assert.Contains("Branch Local", replicate.Message, StringComparison.Ordinal);
        Assert.Contains("النسخة البعيدة", restore.Message, StringComparison.Ordinal);
    }
}
