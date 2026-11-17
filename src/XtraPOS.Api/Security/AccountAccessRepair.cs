using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Security;

/// <summary>
/// Keeps every user active on every branch, and restores the requested cashier account.
/// Central sync copies password and activity from the center, so both databases are repaired.
/// </summary>
public static class AccountAccessRepair
{
    public const string Username = "905017083";
    public const string Password = "05925947077";

    public const string ActivateUsersSql =
        """
        IF OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
            UPDATE dbo.Users
            SET IsActive = 1,
                FailedLoginCount = 0,
                LockoutEndAt = NULL
            WHERE IsActive = 0 OR FailedLoginCount <> 0 OR LockoutEndAt IS NOT NULL;
        """;

    public const string RestoreAccountSql =
        """
        IF OBJECT_ID(N'dbo.Users', N'U') IS NOT NULL
            UPDATE dbo.Users
            SET IsActive = 1,
                FailedLoginCount = 0,
                LockoutEndAt = NULL,
                MustChangePassword = 0,
                PasswordHash = @hash,
                UpdatedAt = SYSUTCDATETIME()
            WHERE NormalizedUsername = @name
               OR Username = @name
               OR EmployeeNumber = @name;
        """;

    public const string ActivateAccessSql =
        """
        IF OBJECT_ID(N'dbo.UserBranchAccess', N'U') IS NOT NULL
            UPDATE dbo.UserBranchAccess SET IsActive = 1 WHERE IsActive = 0;
        """;

    public static async Task ApplyAsync(SqlConnection connection, SqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using (var activate = new SqlCommand(ActivateUsersSql, connection, transaction))
        {
            await activate.ExecuteNonQueryAsync(cancellationToken);
        }

        var hash = new PasswordService().Hash(new Data.AppUser(), Password);
        await using (var restore = new SqlCommand(RestoreAccountSql, connection, transaction))
        {
            restore.Parameters.Add("@hash", System.Data.SqlDbType.NVarChar, 1000).Value = hash;
            restore.Parameters.Add("@name", System.Data.SqlDbType.NVarChar, 100).Value = AccountIdentity.NormalizeUsername(Username);
            await restore.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var access = new SqlCommand(ActivateAccessSql, connection, transaction))
        {
            await access.ExecuteNonQueryAsync(cancellationToken);
        }

        await BranchAccessGrant.ApplyAsync(connection, transaction, cancellationToken);
    }
}
