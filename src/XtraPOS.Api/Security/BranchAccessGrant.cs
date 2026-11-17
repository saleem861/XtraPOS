using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Security;

public static class BranchAccessGrant
{
    public const string InsertMissingSql =
        """
        IF OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
           AND OBJECT_ID(N'dbo.Branches', N'U') IS NOT NULL
           AND OBJECT_ID(N'dbo.UserBranchAccess', N'U') IS NOT NULL
            INSERT INTO dbo.UserBranchAccess (UserID, BranchID, IsActive, GrantedAt)
            SELECT users.UserID, branch.BranchID, 1, SYSUTCDATETIME()
            FROM dbo.Users AS users
            CROSS JOIN dbo.Branches AS branch
            WHERE users.IsActive = 1
              AND NOT EXISTS (
                  SELECT 1
                  FROM dbo.UserBranchAccess AS access
                  WHERE access.UserID = users.UserID AND access.BranchID = branch.BranchID);
        """;

    public static async Task ApplyAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(InsertMissingSql, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
