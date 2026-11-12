using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public static class StagingSetupSql
{
    public static SqlEndpointSettings Apply(IHostEnvironment? environment, IConfiguration? configuration, SqlEndpointSettings settings)
    {
        return settings;
    }
}
