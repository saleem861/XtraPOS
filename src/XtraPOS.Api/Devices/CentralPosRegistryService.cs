using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Setup;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Devices;

public sealed class CentralPosRegistryService(CentralConnectionFileStore centrals, IHostEnvironment environment)
{
    public async Task<int> SuggestAsync(CancellationToken cancellationToken)
    {
        var used = await ReadNumbersAsync(cancellationToken);
        return PosNumberAllocator.SuggestNext(used);
    }

    public async Task<(bool Ok, string Message, int Number)> ReserveAsync(int requested, Guid branchId, string posName, CancellationToken cancellationToken)
    {
        if (requested <= 0)
        {
            return (false, "رقم نقطة البيع يجب أن يكون عدداً صحيحاً موجباً.", 0);
        }

        var settings = await centrals.ReadAsync(cancellationToken);
        if (settings is null)
        {
            return (false, "المركزية غير متاحة. لا يُعتمد رقم جديد من النسخة المحلية.", 0);
        }

        await using var connection = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var used = new List<int>();
        await using (var read = new SqlCommand("SELECT PosNumber FROM dbo.CentralPosRegistry WITH (UPDLOCK, HOLDLOCK);", connection, transaction))
        {
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                used.Add(reader.GetInt32(0));
            }
        }

        if (!PosNumberAllocator.IsAvailable(used, requested))
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, "رقم نقطة البيع مستخدم في المركزية.", requested);
        }

        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.CentralPosRegistry (PosNumber, BranchID, PosName, Status)
            VALUES (@number, @branch, @name, N'Active');
            """,
            connection,
            transaction);
        insert.Parameters.Add("@number", SqlDbType.Int).Value = requested;
        insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        insert.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = posName;
        try
        {
            await insert.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (true, "تم حجز الرقم.", requested);
        }
        catch (SqlException exception)
        {
            var id = SetupLog.Fail(environment.ContentRootPath, environment.EnvironmentName, "pos-reserve", exception);
            return (false, id, requested);
        }
    }

    public const string SynchronizationPoint = "SynchronizationDatabase";

    public async Task<(bool Ok, string Message, int Number)> RegisterSynchronizationDatabaseAsync(
        int requested,
        Guid branchId,
        string databaseName,
        CancellationToken cancellationToken)
    {
        var settings = await centrals.ReadAsync(cancellationToken);
        if (settings is null)
        {
            return (false, "المركزية غير متاحة. لم تُسجل قاعدة المزامنة.", 0);
        }

        await using var connection = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await EnsureSyncColumnsAsync(connection, cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var existing = new SqlCommand(
            """
            SELECT PosNumber
            FROM dbo.CentralPosRegistry WITH (UPDLOCK, HOLDLOCK)
            WHERE PointType = N'SynchronizationDatabase' AND DatabaseName = @database;
            """,
            connection,
            transaction))
        {
            existing.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = databaseName;
            var found = await existing.ExecuteScalarAsync(cancellationToken);
            if (found is int number)
            {
                await transaction.CommitAsync(cancellationToken);
                return (true, "قاعدة المزامنة مسجلة مسبقاً.", number);
            }
        }

        var used = new List<int>();
        await using (var read = new SqlCommand("SELECT PosNumber FROM dbo.CentralPosRegistry WITH (UPDLOCK, HOLDLOCK);", connection, transaction))
        {
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                used.Add(reader.GetInt32(0));
            }
        }

        var allocated = requested > 0 && PosNumberAllocator.IsAvailable(used, requested)
            ? requested
            : PosNumberAllocator.SuggestNext(used);
        if (requested > 0 && allocated != requested)
        {
            await transaction.RollbackAsync(cancellationToken);
            return (false, $"رقم نقطة المزامنة مستخدم. الرقم المتاح هو {allocated}.", allocated);
        }

        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.CentralPosRegistry (PosNumber, BranchID, PosName, Status, PointType, DatabaseName)
            VALUES (@number, @branch, @name, N'Active', N'SynchronizationDatabase', @database);
            """,
            connection,
            transaction);
        insert.Parameters.Add("@number", SqlDbType.Int).Value = allocated;
        insert.Parameters.Add("@branch", SqlDbType.UniqueIdentifier).Value = branchId;
        insert.Parameters.Add("@name", SqlDbType.NVarChar, 200).Value = databaseName;
        insert.Parameters.Add("@database", SqlDbType.NVarChar, 128).Value = databaseName;
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return (true, "تم تسجيل قاعدة المزامنة.", allocated);
    }

    private static async Task EnsureSyncColumnsAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            IF COL_LENGTH(N'dbo.CentralPosRegistry', N'PointType') IS NULL
                ALTER TABLE dbo.CentralPosRegistry ADD PointType nvarchar(40) NOT NULL
                    CONSTRAINT DF_CentralPosRegistry_PointType DEFAULT (N'Sales');
            IF COL_LENGTH(N'dbo.CentralPosRegistry', N'DatabaseName') IS NULL
                ALTER TABLE dbo.CentralPosRegistry ADD DatabaseName nvarchar(128) NULL;
            """,
            connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReleaseBranchAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var settings = await centrals.ReadAsync(cancellationToken);
        if (settings is null || branchId == Guid.Empty)
        {
            return;
        }

        await using var connection = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SET XACT_ABORT ON;
            BEGIN TRANSACTION;
            IF OBJECT_ID(N'dbo.CentralPosReplica', N'U') IS NOT NULL
                DELETE replica FROM dbo.CentralPosReplica AS replica WHERE replica.BranchID = @target;
            IF OBJECT_ID(N'dbo.CentralPosRegistry', N'U') IS NOT NULL
                DELETE registry FROM dbo.CentralPosRegistry AS registry WHERE registry.BranchID = @target;
            COMMIT TRANSACTION;

            BEGIN TRANSACTION;
            DECLARE @childSql nvarchar(max) = N'';
            SELECT @childSql = @childSql
                + N'DELETE child FROM ' + QUOTENAME(SCHEMA_NAME(child.schema_id)) + N'.' + QUOTENAME(child.name)
                + N' AS child WHERE child.' + QUOTENAME(columnName.name) + N' = @target AND child.' + QUOTENAME(columnName.name) + N' IS NOT NULL;'
            FROM sys.foreign_keys AS link
            INNER JOIN sys.foreign_key_columns AS pair ON pair.constraint_object_id = link.object_id
            INNER JOIN sys.tables AS child ON child.object_id = link.parent_object_id
            INNER JOIN sys.columns AS columnName ON columnName.object_id = child.object_id AND columnName.column_id = pair.parent_column_id
            INNER JOIN sys.tables AS parent ON parent.object_id = link.referenced_object_id
            INNER JOIN sys.columns AS parentColumn ON parentColumn.object_id = parent.object_id AND parentColumn.column_id = pair.referenced_column_id
            WHERE parent.name = N'Branches' AND parentColumn.name = N'BranchID'
              AND child.name NOT IN (N'Sales', N'SaleItems', N'SalePayments', N'POSCashSessions', N'POSStations', N'Devices', N'Branches');
            EXEC sys.sp_executesql @childSql, N'@target uniqueidentifier', @target = @target;

            IF OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL
                DELETE session FROM dbo.POSCashSessions AS session
                WHERE session.BranchID = @target
                  AND NOT EXISTS (SELECT 1 FROM dbo.Sales AS sale WHERE sale.CashSessionID = session.CashSessionID)
                  AND NOT EXISTS (SELECT 1 FROM dbo.SalePayments AS payment WHERE payment.CashSessionID = session.CashSessionID);

            IF OBJECT_ID(N'dbo.Devices', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Devices', N'BranchID') IS NOT NULL
                DELETE device FROM dbo.Devices AS device
                WHERE device.BranchID = @target
                  AND NOT EXISTS (SELECT 1 FROM dbo.Sales AS sale WHERE sale.DeviceID = device.DeviceID)
                  AND NOT EXISTS (SELECT 1 FROM dbo.POSCashSessions AS session WHERE session.DeviceID = device.DeviceID);

            DELETE station FROM dbo.POSStations AS station
            WHERE station.BranchID = @target
              AND NOT EXISTS (SELECT 1 FROM dbo.Sales AS sale WHERE sale.POSStationID = station.StationID)
              AND NOT EXISTS (SELECT 1 FROM dbo.POSCashSessions AS session WHERE session.POSStationID = station.StationID);

            DELETE branch FROM dbo.Branches AS branch
            WHERE branch.BranchID = @target
              AND NOT EXISTS (SELECT 1 FROM dbo.Sales AS sale WHERE sale.BranchID = branch.BranchID)
              AND NOT EXISTS (SELECT 1 FROM dbo.POSStations AS station WHERE station.BranchID = branch.BranchID)
              AND NOT EXISTS (SELECT 1 FROM dbo.POSCashSessions AS session WHERE session.BranchID = branch.BranchID)
              AND NOT EXISTS (SELECT 1 FROM dbo.CentralPosRegistry AS registry WHERE registry.BranchID = branch.BranchID);
            COMMIT TRANSACTION;
            """,
            connection);
        command.Parameters.Add("@target", SqlDbType.UniqueIdentifier).Value = branchId;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(int posNumber, CancellationToken cancellationToken)
    {
        var settings = await centrals.ReadAsync(cancellationToken);
        if (settings is null || posNumber <= 0)
        {
            return false;
        }

        await using var connection = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            DELETE FROM dbo.CentralPosRegistry WHERE PosNumber = @number;
            IF OBJECT_ID(N'dbo.CentralPosReplica', N'U') IS NOT NULL
                DELETE FROM dbo.CentralPosReplica WHERE PosNumber = @number;
            """,
            connection);
        command.Parameters.Add("@number", SqlDbType.Int).Value = posNumber;
        await command.ExecuteNonQueryAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RevokeAsync(int posNumber, CancellationToken cancellationToken)
    {
        var settings = await centrals.ReadAsync(cancellationToken);
        if (settings is null)
        {
            return false;
        }

        await using var connection = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "UPDATE dbo.CentralPosRegistry SET Status = N'Revoked' WHERE PosNumber = @number AND Status = N'Active';",
            connection);
        command.Parameters.Add("@number", SqlDbType.Int).Value = posNumber;
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private async Task<List<int>> ReadNumbersAsync(CancellationToken cancellationToken)
    {
        var settings = await centrals.ReadAsync(cancellationToken);
        if (settings is null)
        {
            throw new InvalidOperationException("Central connection is not configured.");
        }

        await using var connection = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT PosNumber FROM dbo.CentralPosRegistry;",
            connection);
        var used = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            used.Add(reader.GetInt32(0));
        }

        return used;
    }
}
