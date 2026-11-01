using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Devices;

public sealed class CentralPosReplicaSync(CentralConnectionFileStore centrals, SqlConnectionFactory branches, SyncStatusStore status, IHostEnvironment environment)
{
    public async Task<int> PullAsync(CancellationToken cancellationToken)
    {
        var settings = await centrals.ReadAsync(cancellationToken);
        if (settings is null || !await branches.DatabaseExistsAsync(cancellationToken))
        {
            return 0;
        }

        try
        {
            var rows = new List<(int Number, Guid BranchId, string Name, string State)>();
            await using (var remote = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName)))
            {
                await remote.OpenAsync(cancellationToken);
                if (!await TableExistsAsync(remote, "CentralPosRegistry", cancellationToken))
                {
                    return 0;
                }

                status.Report("InProgress", "pos-replica", null, null);
                await using var read = new SqlCommand("SELECT PosNumber, BranchID, PosName, Status FROM dbo.CentralPosRegistry;", remote);
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    rows.Add((reader.GetInt32(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3)));
                }
            }

            await using var local = new SqlConnection(branches.CreateBranchConnectionString());
            await local.OpenAsync(cancellationToken);
            if (!await TableExistsAsync(local, "CentralPosReplica", cancellationToken))
            {
                return 0;
            }
            foreach (var row in rows)
            {
                await using var merge = new SqlCommand(
                    """
                    MERGE dbo.CentralPosReplica AS target
                    USING (SELECT @number AS PosNumber) AS source
                    ON target.PosNumber = source.PosNumber
                    WHEN MATCHED THEN UPDATE SET BranchID = @branch, PosName = @name, Status = @status, SyncedAtUtc = SYSUTCDATETIME()
                    WHEN NOT MATCHED THEN INSERT (PosNumber, BranchID, PosName, Status) VALUES (@number, @branch, @name, @status);
                    """,
                    local);
                merge.Parameters.Add("@number", SqlDbType.Int).Value = row.Number;
                merge.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = row.BranchId;
                merge.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = row.Name;
                merge.Parameters.Add("@status", SqlDbType.NVarChar, 20).Value = row.State;
                await merge.ExecuteNonQueryAsync(cancellationToken);
            }

            status.Report("Success", "pos-replica", null, null);
            return rows.Count;
        }
        catch (Exception exception)
        {
            var id = SetupLog.Fail(environment.ContentRootPath, environment.EnvironmentName, "pos-replica", exception);
            var detail = SqlEndpointSettings.DescribeConnectionFailure(exception);
            status.Report("Error", "pos-replica", detail, id);
            return 0;
        }
    }

    private static async Task<bool> TableExistsAsync(SqlConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT CASE WHEN OBJECT_ID(@name, N'U') IS NULL THEN 0 ELSE 1 END;", connection);
        command.Parameters.Add("@name", SqlDbType.NVarChar, 128).Value = "dbo." + tableName;
        var found = await command.ExecuteScalarAsync(cancellationToken);
        return found is not null && found is not DBNull && Convert.ToInt32(found) == 1;
    }
}
