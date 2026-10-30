namespace XtraPOS.Api.Data;

public static class DatabaseRole
{
    public const string Central = "Central";
    public const string Branch = "Branch";
    public const string BranchRemote = "BranchRemote";

    public static bool IsKnown(string? role) =>
        role is Central or Branch or BranchRemote;
}
