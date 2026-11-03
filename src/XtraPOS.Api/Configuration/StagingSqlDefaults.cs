namespace XtraPOS.Api.Configuration;

public static class StagingSqlDefaults
{
    public const string BranchDatabaseName = "XtraPOS_Branch_Test_Staging";
    public const string CentralDatabaseName = "XtraPOS_Central_Test_Staging";

    public static Dictionary<string, string?> MissingValues(string? database)
    {
        var values = new Dictionary<string, string?>();
        var trimmedDatabase = database?.Trim();
        if (string.IsNullOrWhiteSpace(trimmedDatabase)
            || string.Equals(trimmedDatabase, Data.BranchDatabase.Name, StringComparison.Ordinal))
        {
            values["SqlServer:Database"] = BranchDatabaseName;
        }

        return values;
    }
}
