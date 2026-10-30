namespace XtraPOS.Api.Data;

public static class BranchDatabaseOverride
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? Database => Current.Value;

    public static void Set(string? databaseName)
    {
        Current.Value = string.IsNullOrWhiteSpace(databaseName) ? null : databaseName.Trim();
    }
}
