using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public static class SetupTopologyBuilder
{
    public static TopologyPlan Build(CreateBranchRequest request, SqlEndpointSettings? operationalBranchOverride = null)
    {
        SqlEndpointSettings? central = null;
        if (!string.IsNullOrWhiteSpace(request.CentralDatabaseName))
        {
            central = CentralRoute.Bind(
                request.CentralLocation,
                request.CentralServer,
                request.CentralInstance,
                request.CentralPort,
                request.CentralDatabaseName,
                request.CentralAuthentication,
                request.CentralUsername,
                request.CentralPassword,
                request.CentralEncrypt ?? true,
                request.CentralTrustServerCertificate ?? true);
        }

        SqlEndpointSettings? remote = null;
        if (string.Equals(request.BranchRemoteChoice, "yes", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(request.BranchRemoteDatabaseName))
        {
            remote = new SqlEndpointSettings(
                Data.DatabaseRole.BranchRemote,
                SqlEndpointSettings.Remote,
                string.IsNullOrWhiteSpace(request.BranchRemoteServer) ? "localhost" : request.BranchRemoteServer.Trim(),
                request.BranchRemoteInstance,
                request.BranchRemotePort,
                request.BranchRemoteDatabaseName.Trim(),
                string.Equals(request.BranchRemoteAuthentication, SqlEndpointSettings.SqlLogin, StringComparison.Ordinal)
                    ? SqlEndpointSettings.SqlLogin
                    : SqlEndpointSettings.Integrated,
                request.BranchRemoteUsername,
                request.BranchRemotePassword,
                true,
                true);
        }

        return new TopologyPlan(
            request.OperatingMode ?? "Standalone",
            string.Equals(request.CentralAction, "create", StringComparison.OrdinalIgnoreCase),
            string.Equals(request.LinkToCentral, "yes", StringComparison.OrdinalIgnoreCase),
            central,
            remote,
            BranchSetupValidator.ReusesCentralAdmin(request, out var centralAdminId) ? centralAdminId : null,
            request.SyncPosNumber,
            request.CashboxNumber,
            operationalBranchOverride ?? SelectedBranchTarget.FromRequest(request),
            request.BranchDataDirectory,
            request.RemoteDataDirectory);
    }
}
