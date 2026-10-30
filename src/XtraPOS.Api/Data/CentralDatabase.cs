namespace XtraPOS.Api.Data;

public static class CentralDatabase
{
    public const string Name = "XtraPOS_Central";
    public const string TestNamePrefix = "XtraPOS_Central_Test";
    public const string Collation = BranchDatabase.Collation;
    public const string RoleSettingKey = "DatabaseRole";

    public static bool IsAllowedName(string? name)
    {
        return BranchDatabase.IsSafeIdentifier(name)
            && (string.Equals(name, Name, StringComparison.Ordinal) || IsTestName(name));
    }

    public static bool IsTestName(string? name)
    {
        return BranchDatabase.IsSafeIdentifier(name)
            && name!.StartsWith(TestNamePrefix + "_", StringComparison.Ordinal)
            && !string.Equals(name, Name, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(name, BranchDatabase.Name, StringComparison.OrdinalIgnoreCase);
    }

    public static string Require(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (!IsAllowedName(trimmed))
        {
            throw new InvalidOperationException("Refusing a central database name that is not XtraPOS_Central or a XtraPOS_Central_Test database.");
        }

        return trimmed;
    }
}
