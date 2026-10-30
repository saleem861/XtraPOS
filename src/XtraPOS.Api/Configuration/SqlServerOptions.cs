using XtraPOS.Api.Data;

namespace XtraPOS.Api.Configuration;

public sealed class SqlServerOptions
{
    public const string SectionName = "SqlServer";

    public string Server { get; set; } = "";

    public string Database { get; set; } = BranchDatabase.Name;

    public string? User { get; set; }

    public string? Password { get; set; }

    public int ConnectTimeoutSeconds { get; set; } = 15;
}
