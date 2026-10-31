using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Security;

public static class UserAdministrationLiveCommand
{
    public const string CommandName = "--user-admin-live-check";
    public const string TestUsername = "xtrapos.test.6h";
    public const string TestEmployeeNumber = "XTRAPOS-TEST-6H";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(arg => string.Equals(arg, CommandName, StringComparison.Ordinal));

    public static async Task<int> ExecuteAsync(
        UserAdministrationService users,
        SqlConnectionFactory connections,
        string? configuredDatabase,
        string? confirmedDatabase,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(confirmedDatabase, "XtraPOS_Branch_01", StringComparison.Ordinal)
            || !string.Equals(configuredDatabase, confirmedDatabase, StringComparison.Ordinal))
        {
            await output.WriteLineAsync("User administration check requires --confirm-database XtraPOS_Branch_01 on that configured database.");
            return 1;
        }

        var actorId = await ScalarGuidAsync(connections, "SELECT UserID FROM dbo.Users WHERE EmployeeNumber = N'905017083';", cancellationToken);
        if (actorId is null)
        {
            await output.WriteLineAsync("The live check could not find the active admin actor.");
            return 1;
        }

        var before = await users.ListAsync(new UserListFilter(null, null, null, null), cancellationToken);
        await output.WriteLineAsync($"Authority={before.Authority}");
        await output.WriteLineAsync($"UsersBefore={before.Count}");
        foreach (var employee in new[] { "905017083", "402120067", "421600008" })
        {
            var match = before.Users.FirstOrDefault(user => string.Equals(user.EmployeeNumber, employee, StringComparison.Ordinal));
            await output.WriteLineAsync($"KnownUser={employee}\tRole={match?.RoleCode ?? "missing"}\tName={match?.FullName ?? "missing"}");
        }

        var existing = before.Users.FirstOrDefault(user => string.Equals(user.Username, TestUsername, StringComparison.Ordinal));
        var catalog = await users.CatalogAsync(cancellationToken);
        var branchId = catalog.Branches.FirstOrDefault()?.BranchId ?? Guid.Empty;
        if (existing is null)
        {
            if (branchId == Guid.Empty)
            {
                await output.WriteLineAsync("No branch is available for the disposable user.");
                return 1;
            }

            var created = await users.CreateAsync(
                actorId.Value,
                new CreateManagedUserRequest
                {
                    FullName = "مستخدم تجربة إدارة",
                    EmployeeNumber = TestEmployeeNumber,
                    Username = TestUsername,
                    RoleCode = RoleCodes.User,
                    BranchIds = [branchId],
                    IsActive = true,
                    PasswordMode = "auto",
                },
                cancellationToken);
            existing = await users.GetAsync(created.UserId, cancellationToken);
            await output.WriteLineAsync($"Created={TestUsername}\tMustChangePassword={existing?.MustChangePassword}\tPasswordStoredAsPlaintext={await PasswordStoredAsync(connections, created.UserId, created.TemporaryPassword, cancellationToken)}");
        }

        if (existing is null || !string.Equals(existing.Username, TestUsername, StringComparison.Ordinal))
        {
            await output.WriteLineAsync("Refusing the live check because the disposable user was not found.");
            return 1;
        }

        var reset = await users.ResetPasswordAsync(actorId.Value, existing.UserId, "auto", null, cancellationToken);
        var afterReset = await users.GetAsync(existing.UserId, cancellationToken);
        var stored = await PasswordStoredAsync(connections, existing.UserId, reset.TemporaryPassword, cancellationToken);
        await output.WriteLineAsync($"ResetTarget={TestUsername}\tRole={afterReset?.RoleCode}\tActive={afterReset?.IsActive}\tMustChangePassword={afterReset?.MustChangePassword}\tPasswordStoredAsPlaintext={stored}");
        await users.SetActiveAsync(actorId.Value, existing.UserId, false, cancellationToken);
        var after = await users.ListAsync(new UserListFilter(null, null, null, null), cancellationToken);
        await output.WriteLineAsync($"UsersAfter={after.Count}");
        await output.WriteLineAsync("DisposableUserDeactivated=yes");
        return stored ? 1 : 0;
    }

    private static async Task<bool> PasswordStoredAsync(SqlConnectionFactory connections, Guid userId, string password, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT CASE
                WHEN u.PasswordHash = @password THEN 1
                WHEN EXISTS (SELECT 1 FROM dbo.UserAdministrationAudit WHERE TargetUserID = @id AND CHARINDEX(@password, Action) > 0) THEN 1
                ELSE 0
            END
            FROM dbo.Users AS u
            WHERE u.UserID = @id;
            """,
            connection);
        command.Parameters.AddWithValue("@id", userId);
        command.Parameters.AddWithValue("@password", password);
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 1) == 1;
    }

    public static async Task PrintCountsAsync(string connectionString, string label, bool branchOperations, TextWriter output, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var values = branchOperations
            ? new[]
            {
                await CountRowsAsync(connection, "dbo.Items", null, null, cancellationToken),
                await CountRowsAsync(connection, "dbo.ItemUnits", null, null, cancellationToken),
                await CountRowsAsync(connection, "dbo.ItemBranchInventory", null, null, cancellationToken),
                await CountRowsAsync(connection, "dbo.BranchStockBalances", null, null, cancellationToken),
                await CountRowsAsync(connection, "dbo.StockMovements", null, null, cancellationToken),
                await CountRowsAsync(connection, "dbo.ItemPrices", "BranchID", "BranchID IS NOT NULL", cancellationToken),
                await CountRowsAsync(connection, "dbo.Users", null, null, cancellationToken),
                await CountRowsAsync(connection, "dbo.Permissions", null, null, cancellationToken),
                await CountRowsAsync(connection, "dbo.DatabaseSchemaHistory", null, null, cancellationToken),
                await CountDuplicateEmployeesAsync(connection, cancellationToken),
            }
            : new[]
            {
                await CountRowsAsync(connection, "dbo.Businesses", null, null, cancellationToken),
                await CountRowsAsync(connection, "dbo.Branches", null, null, cancellationToken),
                await CountRowsAsync(connection, "dbo.Items", null, null, cancellationToken),
                await CountRowsAsync(connection, "dbo.ItemUnits", null, null, cancellationToken),
                await CountRowsAsync(connection, "dbo.ItemPrices", null, null, cancellationToken),
                await CountRowsAsync(connection, "dbo.Users", null, null, cancellationToken),
                await CountRowsAsync(connection, "dbo.Permissions", null, null, cancellationToken),
                await CountRowsAsync(connection, "dbo.DatabaseSchemaHistory", null, null, cancellationToken),
                await CountDuplicateEmployeesAsync(connection, cancellationToken),
            };

        await output.WriteLineAsync(branchOperations
            ? $"{label} Items={values[0]} Units={values[1]} Inventory={values[2]} Balances={values[3]} Movements={values[4]} Overrides={values[5]} Users={values[6]} Permissions={values[7]} Migrations={values[8]} DuplicateEmployees={values[9]}"
            : $"{label} Businesses={values[0]} Branches={values[1]} Items={values[2]} Units={values[3]} Prices={values[4]} Users={values[5]} Permissions={values[6]} Migrations={values[7]} DuplicateEmployees={values[8]}");
    }

    private static async Task<int> CountRowsAsync(
        SqlConnection connection,
        string table,
        string? requiredColumn,
        string? predicate,
        CancellationToken cancellationToken)
    {
        await using var probe = new SqlCommand(
            """
            SELECT CASE
                WHEN OBJECT_ID(@table, N'U') IS NULL THEN 0
                WHEN @column IS NOT NULL AND COL_LENGTH(@table, @column) IS NULL THEN 0
                ELSE 1
            END;
            """,
            connection);
        probe.Parameters.Add("@table", SqlDbType.NVarChar, 128).Value = table;
        probe.Parameters.Add("@column", SqlDbType.NVarChar, 128).Value = (object?)requiredColumn ?? DBNull.Value;
        if ((int)(await probe.ExecuteScalarAsync(cancellationToken) ?? 0) == 0)
        {
            return 0;
        }

        var sql = predicate is null
            ? $"SELECT COUNT(*) FROM {table};"
            : $"SELECT COUNT(*) FROM {table} WHERE {predicate};";
        await using var count = new SqlCommand(sql, connection);
        return (int)(await count.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task<int> CountDuplicateEmployeesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            IF OBJECT_ID(N'dbo.Users', N'U') IS NULL OR COL_LENGTH(N'dbo.Users', N'EmployeeNumber') IS NULL
                SELECT 0;
            ELSE
                EXEC(N'SELECT COUNT(*) FROM (SELECT EmployeeNumber FROM dbo.Users WHERE EmployeeNumber IS NOT NULL GROUP BY EmployeeNumber HAVING COUNT(*) > 1) AS duplicates');
            """,
            connection);
        return (int)(await command.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    private static async Task<Guid?> ScalarGuidAsync(SqlConnectionFactory connections, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }
}
