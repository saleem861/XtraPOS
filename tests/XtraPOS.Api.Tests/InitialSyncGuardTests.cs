using XtraPOS.Api.Data;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;
using Xunit;

namespace XtraPOS.Api.Tests;

public class InitialSyncGuardTests
{
    [Fact]
    public void Configured_branch_does_not_provision_again()
    {
        Assert.True(InitialSyncGuard.SkipProvisioning(organizationConfigured: true));
        Assert.False(InitialSyncGuard.SkipProvisioning(organizationConfigured: false));
    }

    [Fact]
    public void Ready_or_stale_replica_skips_initial_bootstrap()
    {
        Assert.False(InitialSyncGuard.NeedsInitialSync(SharedDataSync.ReplicaReady));
        Assert.False(InitialSyncGuard.NeedsInitialSync(SharedDataSync.ReplicaStale));
        Assert.True(InitialSyncGuard.NeedsInitialSync(SharedDataSync.ReplicaNotInitialized));
        Assert.True(InitialSyncGuard.NeedsInitialSync(SharedDataSync.ReplicaBootstrapping));
        Assert.True(InitialSyncGuard.NeedsInitialSync(null));
    }

    [Fact]
    public void Staging_accepts_the_central_database_entered_during_setup()
    {
        InitialSyncGuard.RefuseOperationalCentral(staging: true, "XtraPOS_Central");
        InitialSyncGuard.RefuseOperationalCentral(staging: true, "XtraPOS_Central_Test_Staging");
        InitialSyncGuard.RefuseOperationalCentral(staging: false, "XtraPOS_Central");
    }

    [Fact]
    public void Setup_can_create_a_missing_central_database()
    {
        Assert.Null(InitialSyncGuard.StagingCentralRejection(true, "XtraPOS_Central"));
        Assert.Null(InitialSyncGuard.RejectCentralCreation(true, true, "XtraPOS_Central"));
        Assert.Null(InitialSyncGuard.StagingCentralRejection(true, "XtraPOS_Central_Test_Staging"));
        Assert.True(InitialSyncGuard.CanInitializeExistingEmpty(true, true, 0, true));
        Assert.False(InitialSyncGuard.CanInitializeExistingEmpty(true, true, 12, true));
        Assert.False(InitialSyncGuard.CanInitializeExistingEmpty(true, true, 0, false));
    }
}
