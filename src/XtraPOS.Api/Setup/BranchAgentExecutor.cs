using XtraPOS.Api.Data;
using XtraPOS.Api.Data.Migrations;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public sealed class BranchAgentExecutor(
    SelectedDatabaseProvisioner provisioner,
    MigrationRunner migrations,
    OperationalBranchConnectionStore store)
{
    public async Task ProvisionAsync(
        ClaimedBranchJob job,
        Func<BranchAgentProgress, CancellationToken, Task> report,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(job.Operation, BranchAgentRules.ProvisionOperation, StringComparison.Ordinal))
        {
            throw new BranchAgentRejectedException("العملية غير مسموحة.");
        }

        if (!BranchAgentRules.TryNormalizeLocalInstance(job.Instance, out var instance) || !string.Equals(instance, job.Instance, StringComparison.OrdinalIgnoreCase))
        {
            throw new BranchAgentRejectedException("نسخة SQL المحلية غير مسموحة.");
        }

        if (!OperationalDatabaseGuard.IsProductionCatalog(job.DatabaseName))
        {
            throw new BranchAgentRejectedException("اسم قاعدة البيانات غير صالح.");
        }

        var settings = LocalSettings(instance, job.DatabaseName);
        await report(new BranchAgentProgress("progress", SetupSteps.CheckServer, "started", null, false), cancellationToken);
        await provisioner.ProbeAsync(settings, cancellationToken);
        await report(new BranchAgentProgress("progress", SetupSteps.CheckServer, "succeeded", null, false), cancellationToken);

        await report(new BranchAgentProgress("progress", SetupSteps.CreateDatabase, "started", null, false), cancellationToken);
        await provisioner.EnsureCatalogAsync(settings, cancellationToken);
        await report(new BranchAgentProgress("progress", SetupSteps.CreateDatabase, "succeeded", null, false), cancellationToken);

        await report(new BranchAgentProgress("progress", SetupSteps.CreateTables, "started", null, false), cancellationToken);
        await migrations.ApplyPendingToAsync(settings.BuildConnectionString(settings.DatabaseName), cancellationToken);
        await report(new BranchAgentProgress("progress", SetupSteps.CreateTables, "succeeded", null, false), cancellationToken);

        await store.SaveAsync(settings, cancellationToken);
        await report(new BranchAgentProgress("completed", null, null, "تم إنشاء قاعدة الفرع على جهاز الفرع.", true), cancellationToken);
    }

    public static SqlEndpointSettings LocalSettings(string instance, string databaseName)
    {
        var separator = instance.IndexOf('\\');
        var server = separator < 0 ? instance : instance[..separator];
        var name = separator < 0 ? null : instance[(separator + 1)..];
        return new SqlEndpointSettings(
            DatabaseRole.Branch,
            SqlEndpointSettings.Local,
            server,
            name,
            null,
            databaseName,
            SqlEndpointSettings.Integrated,
            null,
            null,
            true,
            true);
    }
}
