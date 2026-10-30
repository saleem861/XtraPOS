using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public static class StagingSetupSql
{
    public static SqlEndpointSettings Apply(IHostEnvironment? environment, IConfiguration? configuration, SqlEndpointSettings settings)
    {
        if (environment?.IsStaging() != true || configuration is null)
        {
            return settings;
        }

        var central = string.Equals(settings.DatabaseRole, DatabaseRole.Central, StringComparison.Ordinal)
            || string.Equals(settings.DatabaseName, InitialSyncGuard.ApprovedExistingCentral, StringComparison.Ordinal);
        var prefix = central ? "CentralSql" : "SqlServer";
        var server = configuration[$"{prefix}:Server"];
        var user = configuration[$"{prefix}:User"];
        var password = configuration[$"{prefix}:Password"];
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(password))
        {
            throw new SetupRejectedException("اتصال قاعدة البيانات غير مكتمل على الخادم.");
        }

        return settings with
        {
            Server = server.Trim(),
            InstanceName = null,
            Port = null,
            AuthenticationType = SqlEndpointSettings.SqlLogin,
            Username = user,
            Password = password,
        };
    }
}
