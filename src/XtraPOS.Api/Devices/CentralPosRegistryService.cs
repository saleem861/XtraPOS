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
