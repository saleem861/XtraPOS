namespace XtraPOS.Api.Data;

public static class BranchDatabase
{
    public const string Name = "XtraPOS_Branch";
    public const string TestNamePrefix = "XtraPOS_Branch_Test";
    public const string Collation = "Arabic_CI_AS";

    private static readonly HashSet<string> ForbiddenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "master",
        "model",
        "msdb",
        "tempdb",
        "saleemPOSDB",
        "Shop1",
        "Shop2",
        "swstore",
    };

    public static bool IsAllowedName(string? name)
    {
        return IsSafeIdentifier(name)
            && !ForbiddenNames.Contains(name!)
            && (string.Equals(name, Name, StringComparison.Ordinal) || IsTestName(name) || IsOperationalName(name));
    }

    public static bool IsOperationalName(string? name)
    {
        const string prefix = Name + "_";
        return IsSafeIdentifier(name)
            && name!.StartsWith(prefix, StringComparison.Ordinal)
            && name.Length >= prefix.Length + 2
            && name[prefix.Length..].All(char.IsAsciiDigit);
    }

    public static bool IsTestName(string? name)
    {
        return IsSafeIdentifier(name)
            && name!.StartsWith(TestNamePrefix + "_", StringComparison.Ordinal)
            && !string.Equals(name, Name, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSafeIdentifier(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 128)
        {
            return false;
        }

        if (!char.IsAsciiLetter(name[0]))
        {
            return false;
        }

        return name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }
}
