using XtraPOS.Api.Data;

namespace XtraPOS.Api.Catalog;

public static class StagingCatalogSeed
{
    public static Task<int> EnsureAsync(SqlConnectionFactory connections, CancellationToken cancellationToken)
    {
        _ = connections;
        _ = cancellationToken;
        return Task.FromResult(0);
    }
}
