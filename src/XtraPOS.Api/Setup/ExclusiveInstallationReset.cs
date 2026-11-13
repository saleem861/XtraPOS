using XtraPOS.Api.Data;

namespace XtraPOS.Api.Setup;

public static class ExclusiveInstallationReset
{
    public static bool CanDrop(string? databaseName, string? server, string? instanceName)
    {
        var name = databaseName?.Trim() ?? "";
        if (!OperationalDatabaseGuard.IsProductionCatalog(name) || OperationalDatabaseGuard.IsProtected(name))
        {
            return false;
        }

        if (string.Equals(name, BranchDatabase.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var host = (server ?? "").Trim();
        var instance = (instanceName ?? "").Trim();
        var address = instance.Length == 0 ? host : host + "\\" + instance;
        return BranchAgentRules.TryNormalizeLocalInstance(address, out _);
    }
}
