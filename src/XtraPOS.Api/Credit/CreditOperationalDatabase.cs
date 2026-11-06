using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Credit;

public static class CreditOperationalDatabase
{
    public const string MissingBranchMessage = "جهاز الإدارة المركزية لا يملك قاعدة فرع صالحة لحفظ الفاتورة. لم يُسجَّل دين.";

    public static async Task<bool> HasSalesAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'dbo.Sales', N'U') IS NULL THEN 0 ELSE 1 END;", connection);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 1;
    }

    public static async Task<SqlConnection?> OpenRoutedBranchAsync(
        SqlConnectionFactory factory,
        SqlConnection central,
        Guid branchId,
        CancellationToken cancellationToken)
    {
        await using var read = new SqlCommand(
            """
            SELECT DatabaseName
            FROM dbo.BranchSqlEndpoints
            WHERE BranchID = @branch AND Status = N'Active';
            """,
            central);
        read.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        var name = await read.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(name) || !BranchDatabase.IsAllowedName(name))
        {
            return null;
        }

        try
        {
            var routed = new SqlConnection(factory.CreateNamedBranchConnectionString(name));
            await routed.OpenAsync(cancellationToken);
            if (await HasSalesAsync(routed, cancellationToken))
            {
                return routed;
            }

            await routed.DisposeAsync();
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
