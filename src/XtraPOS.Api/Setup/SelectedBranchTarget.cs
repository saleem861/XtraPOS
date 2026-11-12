using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public static class SelectedBranchTarget
{
    public static SqlEndpointSettings? FromRequest(CreateBranchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BranchDatabaseName))
        {
            return null;
        }

        var remote = string.Equals(request.BranchLocation, "Remote", StringComparison.OrdinalIgnoreCase);
        var server = request.BranchServer?.Trim() ?? "";
        var instance = string.IsNullOrWhiteSpace(request.BranchInstance) ? null : request.BranchInstance.Trim();
        var port = request.BranchPort;
        var authentication = string.Equals(request.BranchAuthentication, SqlEndpointSettings.SqlLogin, StringComparison.Ordinal)
            ? SqlEndpointSettings.SqlLogin
            : SqlEndpointSettings.Integrated;
        var username = request.BranchUsername;
        var password = request.BranchPassword;

        if (string.IsNullOrWhiteSpace(server))
        {
            server = "localhost";
        }

        if (server.Contains('\\', StringComparison.Ordinal))
        {
            instance = null;
        }

        if (server.Contains(',', StringComparison.Ordinal))
        {
            port = null;
        }

        return new SqlEndpointSettings(
            DatabaseRole.Branch,
            remote ? SqlEndpointSettings.Remote : SqlEndpointSettings.Local,
            server,
            instance,
            port,
            request.BranchDatabaseName.Trim(),
            authentication,
            username,
            password,
            true,
            true);
    }
}
