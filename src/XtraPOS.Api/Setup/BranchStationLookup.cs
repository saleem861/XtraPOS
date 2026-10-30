using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public sealed record BranchStationRow(Guid StationId, int StationNumber, string StationName);

public sealed record BranchStationList(bool Ok, string? Message, IReadOnlyList<BranchStationRow> Stations);

public sealed class BranchStationLookup
{
    public async Task<BranchStationList> ListAsync(
        SqlProbeRequest? request,
        CancellationToken cancellationToken,
        IHostEnvironment? environment = null,
        IConfiguration? configuration = null)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DatabaseName))
        {
            return Fail("يرجى إدخال اسم قاعدة البيانات.");
        }

        if (string.Equals(request.Intent, "new", StringComparison.OrdinalIgnoreCase))
        {
            return Fail("لا يمكن قراءة نقاط البيع قبل إنشاء الفرع.");
        }

        var databaseName = request.DatabaseName.Trim();
        if (!BranchDatabase.IsAllowedName(databaseName) || CentralDatabase.IsAllowedName(databaseName))
        {
            return Fail("اسم قاعدة البيانات غير مسموح.");
        }

        var server = string.IsNullOrWhiteSpace(request.Server) ? "localhost" : request.Server.Trim();
        var settings = new SqlEndpointSettings(
            DatabaseRole.Branch,
            string.Equals(request.LocationKind, SqlEndpointSettings.Remote, StringComparison.Ordinal)
                ? SqlEndpointSettings.Remote
                : SqlEndpointSettings.Local,
            server,
            request.InstanceName,
            request.Port,
            databaseName,
            string.Equals(request.AuthenticationType, SqlEndpointSettings.SqlLogin, StringComparison.Ordinal)
                ? SqlEndpointSettings.SqlLogin
                : SqlEndpointSettings.Integrated,
            request.Username,
            request.Password,
            request.Encrypt ?? true,
            request.TrustServerCertificate ?? true);

        try
        {
            settings = StagingSetupSql.Apply(environment, configuration, settings);
            await using var master = new SqlConnection(settings.BuildConnectionString("master"));
            await master.OpenAsync(cancellationToken);
            await using var exists = new SqlCommand("SELECT DB_ID(@name);", master);
            exists.Parameters.AddWithValue("@name", databaseName);
            var databaseId = await exists.ExecuteScalarAsync(cancellationToken);
            if (databaseId is null or DBNull)
            {
                return Fail("قاعدة البيانات غير موجودة.");
            }
        }
        catch (Exception exception)
        {
            return Fail(environment is null
                ? SqlEndpointSettings.DescribeConnectionFailure(exception)
                : SetupLog.Fail(environment.ContentRootPath, environment.EnvironmentName, "branch-stations", exception, request.Password));
        }

        try
        {
            await using var connection = new SqlConnection(settings.BuildConnectionString(databaseName));
            await connection.OpenAsync(cancellationToken);
            await using var table = new SqlCommand("SELECT CASE WHEN OBJECT_ID(N'dbo.POSStations', N'U') IS NULL THEN 0 ELSE 1 END;", connection);
            if ((int)(await table.ExecuteScalarAsync(cancellationToken) ?? 0) != 1)
            {
                return Fail("تعذر قراءة نقاط البيع.");
            }

            var stations = new List<BranchStationRow>();
            await using var command = new SqlCommand(
                """
                SELECT StationID, StationNumber, StationName
                FROM dbo.POSStations
                WHERE IsActive = 1
                ORDER BY StationNumber;
                """,
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                stations.Add(new BranchStationRow(reader.GetGuid(0), reader.GetInt32(1), reader.GetString(2)));
            }

            return new BranchStationList(true, null, stations);
        }
        catch (Exception exception)
        {
            return Fail(environment is null
                ? SqlEndpointSettings.DescribeConnectionFailure(exception)
                : SetupLog.Fail(environment.ContentRootPath, environment.EnvironmentName, "branch-stations", exception, request.Password));
        }
    }

    private static BranchStationList Fail(string message) => new(false, message, []);
}
