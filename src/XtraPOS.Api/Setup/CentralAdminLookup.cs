using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public sealed record CentralAdminRow(
    Guid UserId,
    string Username,
    string DisplayName,
    string? FullName,
    string? EmployeeNumber,
    string RoleCode,
    string RoleName);

public sealed record CentralAdminList(bool Ok, string? Message, IReadOnlyList<CentralAdminRow> Admins);

public sealed class CentralAdminLookup
{
    public async Task<CentralAdminList> ListAsync(
        SqlProbeRequest? request,
        CancellationToken cancellationToken,
        IHostEnvironment? environment = null,
        IConfiguration? configuration = null)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.DatabaseName))
        {
            return Fail("يرجى إدخال اسم قاعدة البيانات.");
        }

        var databaseName = request.DatabaseName.Trim();
        if (!CentralDatabase.IsAllowedName(databaseName))
        {
            return Fail("اسم القاعدة المركزية غير مسموح.");
        }

        var server = string.IsNullOrWhiteSpace(request.Server) ? "localhost" : request.Server.Trim();
        var settings = new SqlEndpointSettings(
            DatabaseRole.Central,
            SqlEndpointSettings.Local,
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
            await using var connection = new SqlConnection(settings.BuildConnectionString(databaseName));
            await connection.OpenAsync(cancellationToken);
            var admins = new List<CentralAdminRow>();
            await using var command = new SqlCommand(
                """
                SELECT u.UserID, u.Username, u.DisplayName, u.FullName, u.EmployeeNumber, r.RoleCode, r.RoleName
                FROM dbo.Users AS u
                INNER JOIN dbo.UserRoles AS ur ON ur.UserID = u.UserID
                INNER JOIN dbo.Roles AS r ON r.RoleID = ur.RoleID AND r.RoleCode = N'Admin'
                WHERE u.IsActive = 1
                ORDER BY u.Username;
                """,
                connection);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                admins.Add(new CentralAdminRow(
                    reader.GetGuid(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6)));
            }

            return new CentralAdminList(true, null, admins);
        }
        catch (Exception exception)
        {
            return Fail(environment is null
                ? SqlEndpointSettings.DescribeConnectionFailure(exception)
                : SetupLog.Fail(environment.ContentRootPath, environment.EnvironmentName, "central-admins", exception, request.Password));
        }
    }

    public async Task<(bool Ok, string? Message, CentralAdminRow? Admin)> VerifyAsync(
        SqlProbeRequest? request,
        string? adminUsername,
        string? adminPassword,
        Func<string, string, bool> passwordMatches,
        CancellationToken cancellationToken,
        IHostEnvironment? environment = null,
        IConfiguration? configuration = null)
    {
        var listed = await ListAsync(request, cancellationToken, environment, configuration);
        if (!listed.Ok || request is null)
        {
            return (false, listed.Message ?? "تعذر قراءة مديري الإدارة المركزية.", null);
        }

        var normalized = AccountIdentity.NormalizeUsername(adminUsername ?? "");
        var admin = listed.Admins.FirstOrDefault(item =>
            string.Equals(AccountIdentity.NormalizeUsername(item.Username), normalized, StringComparison.Ordinal));
        if (admin is null || string.IsNullOrEmpty(adminPassword))
        {
            return (false, "اسم المستخدم أو كلمة المرور غير صحيحة.", null);
        }

        var settings = new SqlEndpointSettings(
            DatabaseRole.Central,
            SqlEndpointSettings.Local,
            string.IsNullOrWhiteSpace(request.Server) ? "localhost" : request.Server.Trim(),
            request.InstanceName,
            request.Port,
            request.DatabaseName!.Trim(),
            string.Equals(request.AuthenticationType, SqlEndpointSettings.SqlLogin, StringComparison.Ordinal)
                ? SqlEndpointSettings.SqlLogin
                : SqlEndpointSettings.Integrated,
            request.Username,
            request.Password,
            request.Encrypt ?? true,
            request.TrustServerCertificate ?? true);
        settings = StagingSetupSql.Apply(environment, configuration, settings);
        await using var connection = new SqlConnection(settings.BuildConnectionString(settings.DatabaseName));
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            "SELECT PasswordHash FROM dbo.Users WHERE UserID = @id AND IsActive = 1;",
            connection);
        command.Parameters.AddWithValue("@id", admin.UserId);
        var hash = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (hash is null || !passwordMatches(hash, adminPassword))
        {
            return (false, "اسم المستخدم أو كلمة المرور غير صحيحة.", null);
        }

        return (true, null, admin);
    }

    private static CentralAdminList Fail(string message) => new(false, message, []);
}
