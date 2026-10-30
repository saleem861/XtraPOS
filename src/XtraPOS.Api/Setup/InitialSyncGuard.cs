using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public static class InitialSyncGuard
{
    public static bool SkipProvisioning(bool organizationConfigured) => organizationConfigured;

    public static bool NeedsInitialSync(string? replicaState) =>
        replicaState is not SharedDataSync.ReplicaReady and not SharedDataSync.ReplicaStale;

    public const string ApprovedExistingCentral = "XtraPOS_Central";

    public static string? StagingCentralRejection(bool staging, string? databaseName)
    {
        if (!staging || string.Equals(databaseName, ApprovedExistingCentral, StringComparison.Ordinal))
        {
            return null;
        }

        return "بيئة التجربة تتصل فقط بقاعدة XtraPOS_Central الموجودة. لن يتم إنشاء قاعدة مركزية أخرى.";
    }

    public static string? RejectCentralCreation(bool staging, bool creating, string? databaseName)
    {
        if (!staging || !creating)
        {
            return null;
        }

        return databaseName == ApprovedExistingCentral
            ? "قاعدة XtraPOS_Central موجودة ولن يتم إنشاؤها أو إعادة تهيئتها."
            : StagingCentralRejection(true, databaseName);
    }

    public static bool CanInitializeExistingEmpty(bool creating, bool exists, int userTableCount, bool testName) =>
        creating && exists && userTableCount == 0 && testName;

    public static void RefuseOperationalCentral(bool staging, string? databaseName)
    {
        var rejection = StagingCentralRejection(staging, databaseName);
        if (rejection is not null)
        {
            throw new SetupRejectedException(rejection);
        }
    }
}
