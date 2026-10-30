namespace XtraPOS.Api.Configuration;

public sealed class LegacyImportOptions
{
    public const string SectionName = "LegacyImport";

    public const string ExpectedDatabase = "saleemPOSDB";

    public string Server { get; set; } = "";

    public string Database { get; set; } = "";

    public int ConnectTimeoutSeconds { get; set; } = 15;
}
