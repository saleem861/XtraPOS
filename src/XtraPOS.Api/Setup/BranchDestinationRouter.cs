using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Setup;

public sealed class BranchDestinationRouter(
    SqlConnectionFactory connections,
    BranchDbContext db,
    SelectedDatabaseProvisioner provisioner,
    OperationalBranchConnectionStore store,
    OperationalBranchConnection current,
    IHostEnvironment environment)
{
    private Sync.SqlEndpointSettings? pending;
    private bool committed;

    public async Task AdoptIfSelectedAsync(
        Sync.SqlEndpointSettings? selected,
        string? dataDirectory,
        CancellationToken cancellationToken)
    {
        if (selected is null || string.IsNullOrWhiteSpace(selected.DatabaseName))
        {
            return;
        }

        if (!OperationalDatabaseGuard.IsProductionCatalog(selected.DatabaseName))
        {
            throw new SetupRejectedException("اسم قاعدة البيانات غير صالح.");
        }

        if (!environment.IsStaging() && connections.MatchesConfigured(selected))
        {
            return;
        }

        await provisioner.ProvisionSchemaAsync(selected, cancellationToken, dataDirectory);
        Bind(selected);
    }

    public void Bind(Sync.SqlEndpointSettings selected)
    {
        if (!OperationalDatabaseGuard.IsProductionCatalog(selected.DatabaseName))
        {
            throw new SetupRejectedException("اسم قاعدة البيانات غير صالح.");
        }

        current.Replace(selected);
        SqlConnection.ClearAllPools();
        db.Database.SetConnectionString(connections.CreateBranchConnectionString());
        pending = selected;
        committed = false;
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (pending is null || committed)
        {
            return;
        }

        try
        {
            await store.SaveAsync(pending, cancellationToken);
            committed = true;
        }
        catch (Exception)
        {
            Abandon();
            throw new SetupRejectedException("تعذر حفظ اتصال قاعدة البيانات. لم يتم تفعيل التوجيه.");
        }
    }

    public void Abandon()
    {
        if (committed)
        {
            return;
        }

        pending = null;
        current.Replace(null);
        SqlConnection.ClearAllPools();
        try
        {
            db.Database.SetConnectionString(connections.CreateBranchConnectionString());
        }
        catch (InvalidOperationException)
        {
            // The bootstrap connection is not usable. The selected route stays inactive.
        }
    }
}
