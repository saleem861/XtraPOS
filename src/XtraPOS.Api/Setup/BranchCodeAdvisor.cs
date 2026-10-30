using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public sealed class BranchCodeRequest
{
    public string? Source { get; set; }
    public string? Intent { get; set; }
    public string? LocationKind { get; set; }
    public string? Server { get; set; }
    public string? InstanceName { get; set; }
    public int? Port { get; set; }
    public string? DatabaseName { get; set; }
    public string? AuthenticationType { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? BusinessName { get; set; }
    public string? BranchCode { get; set; }
    public string? BranchId { get; set; }
}

public sealed record BranchCodeResponse(bool Ok, string? SuggestedCode, bool Available, string? Message);

public sealed class BranchCodeAdvisor
{
    public async Task<BranchCodeResponse> AdviseAsync(
        BranchCodeRequest? request,
        string? configuredBranchConnection,
        CancellationToken cancellationToken,
        IHostEnvironment? environment = null,
        IConfiguration? configuration = null)
    {
        if (request is null)
        {
            return Reject("بيانات رمز الفرع غير صالحة.");
        }

        var source = request.Source?.Trim() ?? "";
        var central = string.Equals(source, DatabaseRole.Central, StringComparison.OrdinalIgnoreCase);
        var branch = string.Equals(source, DatabaseRole.Branch, StringComparison.OrdinalIgnoreCase);
        if (!central && !branch)
        {
            return Reject("مصدر رمز الفرع غير معروف.");
        }

        IReadOnlyList<string?> codes;
        if (string.Equals(request.Intent, "new", StringComparison.OrdinalIgnoreCase))
        {
            codes = [];
        }
        else if (central && !CentralDatabase.IsAllowedName(request.DatabaseName))
        {
            return Reject("اسم القاعدة المركزية غير مسموح.");
        }
        else
        {
            try
            {
                codes = central
                    ? await ReadCentralCodesAsync(request, environment, configuration, cancellationToken)
                    : await ReadBranchCodesAsync(request, configuredBranchConnection, environment, configuration, cancellationToken);
            }
            catch (CentralRejectedException exception)
            {
                return Reject(Reported(environment, "branch-code", exception, request.Password));
            }
            catch (Exception exception)
            {
                return Reject(Reported(environment, "branch-code", exception, request.Password));
            }
        }

        var suggested = BranchCodeSequence.Suggest(codes);
        if (BranchCodeSequence.IsDuplicate(codes, request.BranchCode))
        {
            return new BranchCodeResponse(false, suggested, false, BranchCodeSequence.DuplicateMessage);
        }

        return new BranchCodeResponse(true, suggested, true, null);
    }

    private static async Task<IReadOnlyList<string?>> ReadCentralCodesAsync(
        BranchCodeRequest request,
        IHostEnvironment? environment,
        IConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        var databaseName = CentralDatabase.Require(request.DatabaseName);
        var settings = Settings(request, DatabaseRole.Central, databaseName, environment, configuration);
        return await ReadCodesAsync(
            settings.BuildConnectionString(databaseName),
            DatabaseRole.Central,
            request.BusinessName,
            request.BranchId,
            cancellationToken);
    }

    private static async Task<IReadOnlyList<string?>> ReadBranchCodesAsync(
        BranchCodeRequest request,
        string? configuredBranchConnection,
        IHostEnvironment? environment,
        IConfiguration? configuration,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.DatabaseName))
        {
            if (string.IsNullOrWhiteSpace(configuredBranchConnection))
            {
                return [];
            }

            return await ReadCodesAsync(configuredBranchConnection, DatabaseRole.Branch, request.BusinessName, request.BranchId, cancellationToken);
        }

        if (!BranchDatabase.IsSafeIdentifier(request.DatabaseName))
        {
            throw new CentralRejectedException("اسم قاعدة البيانات غير مسموح.");
        }

        var settings = Settings(request, DatabaseRole.Branch, request.DatabaseName.Trim(), environment, configuration);
        return await ReadCodesAsync(settings.BuildConnectionString(request.DatabaseName.Trim()), DatabaseRole.Branch, request.BusinessName, request.BranchId, cancellationToken);
    }

    private static async Task<IReadOnlyList<string?>> ReadCodesAsync(
        string connectionString,
        string expectedRole,
        string? businessName,
        string? branchId,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var roleCommand = new SqlCommand(
            """
            IF OBJECT_ID(N'dbo.SystemSettings', N'U') IS NULL
                SELECT CAST(NULL AS nvarchar(100));
            ELSE
                SELECT SettingValue FROM dbo.SystemSettings WHERE SettingKey = N'DatabaseRole';
            """,
            connection);
        var role = await roleCommand.ExecuteScalarAsync(cancellationToken) as string;
        if (!string.Equals(role, expectedRole, StringComparison.Ordinal))
        {
            throw new CentralRejectedException("دور قاعدة البيانات لا يطابق الاختيار.");
        }

        await using var command = new SqlCommand(
            """
            SELECT BranchCode
            FROM dbo.Branches
            WHERE BranchCode IS NOT NULL
              AND (@branchId IS NULL OR BranchID <> @branchId)
              AND (
                    @business IS NULL
                    OR BusinessID = (
                        SELECT TOP (1) BusinessID
                        FROM dbo.Businesses
                        WHERE BusinessName = @business
                        ORDER BY CreatedAt)
                  );
            """,
            connection);
        command.Parameters.AddWithValue("@business", string.IsNullOrWhiteSpace(businessName) ? DBNull.Value : businessName.Trim());
        command.Parameters.AddWithValue("@branchId", Guid.TryParse(branchId, out var parsedBranchId) ? parsedBranchId : DBNull.Value);
        var codes = new List<string?>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            codes.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
        }

        return codes;
    }

    private static string Reported(IHostEnvironment? environment, string step, Exception exception, string? secret)
    {
        if (environment is null)
        {
            return Scrub(exception is CentralRejectedException ? exception.Message : SqlEndpointSettings.DescribeConnectionFailure(exception), secret);
        }

        return SetupLog.Fail(environment.ContentRootPath, environment.EnvironmentName, step, exception, secret);
    }

    private static SqlEndpointSettings Settings(
        BranchCodeRequest request,
        string role,
        string databaseName,
        IHostEnvironment? environment,
        IConfiguration? configuration)
    {
        var settings = new SqlEndpointSettings(
            role,
            string.Equals(request.LocationKind, SqlEndpointSettings.Remote, StringComparison.OrdinalIgnoreCase)
                ? SqlEndpointSettings.Remote
                : SqlEndpointSettings.Local,
            string.IsNullOrWhiteSpace(request.Server) ? "localhost" : request.Server.Trim(),
            request.InstanceName,
            request.Port,
            databaseName,
            string.Equals(request.AuthenticationType, SqlEndpointSettings.SqlLogin, StringComparison.OrdinalIgnoreCase)
                ? SqlEndpointSettings.SqlLogin
                : SqlEndpointSettings.Integrated,
            request.Username,
            request.Password,
            true,
            true);
        return StagingSetupSql.Apply(environment, configuration, settings);
    }

    private static BranchCodeResponse Reject(string message) =>
        new(false, null, false, message);

    private static string Scrub(string message, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return message;
        }

        return message.Replace(password, "", StringComparison.Ordinal);
    }
}
