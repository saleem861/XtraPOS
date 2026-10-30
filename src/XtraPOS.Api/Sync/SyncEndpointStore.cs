using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Sync;

public sealed record SyncEndpointView(
    Guid EndpointId,
    string DatabaseRole,
    string LocationKind,
    string ServerName,
    string? InstanceName,
    int? Port,
    string DatabaseName,
    string AuthenticationType,
    string? Username,
    string Status,
    DateTime? LastSuccessfulSyncAt,
    string? LastError);

public sealed class SyncEndpointStore(SqlConnectionFactory connections, IDataProtectionProvider protection, CentralConnectionFileStore centralFile)
{
    private const string CredentialPurpose = "XtraPOS.SqlCredentials";
    private readonly IDataProtector _protector = protection.CreateProtector(CredentialPurpose);

    private string UnprotectPassword(string protectedPassword)
    {
        try
        {
            return _protector.Unprotect(protectedPassword);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            var keys = new DirectoryInfo(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ASP.NET",
                "DataProtection-Keys"));
            foreach (var name in new[]
            {
                @"C:\Users\Saleem Jayousi\OneDrive\Projects\XtraPOS\src\XtraPOS.Api",
                @"D:\Google\XtraPOS\src\XtraPOS.Api",
                @"C:\Users\Saleem Jayousi\OneDrive\Projects\XtraPOS",
                "XtraPOS",
            })
            {
                try
                {
                    var provider = DataProtectionProvider.Create(keys, builder => builder.SetApplicationName(name));
                    return provider.CreateProtector(CredentialPurpose).Unprotect(protectedPassword);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                }
            }

            throw;
        }
    }

    public async Task<SyncEndpointView> SaveAsync(SqlEndpointSettings settings, string status, CancellationToken cancellationToken)
    {
        if (!DatabaseRole.IsKnown(settings.DatabaseRole) || string.Equals(settings.DatabaseRole, DatabaseRole.Branch, StringComparison.Ordinal))
        {
            throw new CentralRejectedException("دور قاعدة البيانات غير صالح.");
        }

        var protectedPassword = string.IsNullOrEmpty(settings.Password) ? null : _protector.Protect(settings.Password);
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var delete = new SqlCommand(
            "DELETE FROM dbo.SyncCheckpoints WHERE EndpointID IN (SELECT EndpointID FROM dbo.SyncEndpoints WHERE DatabaseRole = @role); DELETE FROM dbo.SyncInbox WHERE EndpointID IN (SELECT EndpointID FROM dbo.SyncEndpoints WHERE DatabaseRole = @role); DELETE FROM dbo.SyncOutbox WHERE EndpointID IN (SELECT EndpointID FROM dbo.SyncEndpoints WHERE DatabaseRole = @role); DELETE FROM dbo.SyncEndpoints WHERE DatabaseRole = @role;",
            connection);
        delete.Parameters.AddWithValue("@role", settings.DatabaseRole);
        await delete.ExecuteNonQueryAsync(cancellationToken);

        var id = Guid.NewGuid();
        await using var insert = new SqlCommand(
            """
            INSERT INTO dbo.SyncEndpoints
                (EndpointID, DatabaseRole, LocationKind, ServerName, InstanceName, Port, DatabaseName, AuthenticationType, Username, ProtectedPassword, EncryptConnection, TrustServerCertificate, Status)
            VALUES
                (@id, @role, @location, @server, @instance, @port, @database, @auth, @username, @password, @encrypt, @trust, @status);
            """,
            connection);
        insert.Parameters.AddWithValue("@id", id);
        insert.Parameters.AddWithValue("@role", settings.DatabaseRole);
        insert.Parameters.AddWithValue("@location", settings.LocationKind);
        insert.Parameters.AddWithValue("@server", settings.Server.Trim());
        insert.Parameters.AddWithValue("@instance", (object?)settings.InstanceName ?? DBNull.Value);
        insert.Parameters.AddWithValue("@port", (object?)settings.Port ?? DBNull.Value);
        insert.Parameters.AddWithValue("@database", settings.DatabaseName.Trim());
        insert.Parameters.AddWithValue("@auth", settings.AuthenticationType);
        insert.Parameters.AddWithValue("@username", (object?)settings.Username ?? DBNull.Value);
        insert.Parameters.AddWithValue("@password", (object?)protectedPassword ?? DBNull.Value);
        insert.Parameters.AddWithValue("@encrypt", settings.Encrypt);
        insert.Parameters.AddWithValue("@trust", settings.TrustServerCertificate);
        insert.Parameters.AddWithValue("@status", status);
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return (await GetAsync(settings.DatabaseRole, cancellationToken))!;
    }

    public async Task<SyncEndpointView?> GetAsync(string databaseRole, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT EndpointID, DatabaseRole, LocationKind, ServerName, InstanceName, Port, DatabaseName, AuthenticationType, Username, Status, LastSuccessfulSyncAt, LastError
            FROM dbo.SyncEndpoints
            WHERE DatabaseRole = @role;
            """,
            connection);
        command.Parameters.AddWithValue("@role", databaseRole);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SyncEndpointView(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetDateTime(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));
    }

    public async Task<SqlEndpointSettings?> ReadSettingsAsync(string databaseRole, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(
            """
            SELECT DatabaseRole, LocationKind, ServerName, InstanceName, Port, DatabaseName, AuthenticationType, Username, ProtectedPassword, EncryptConnection, TrustServerCertificate
            FROM dbo.SyncEndpoints
            WHERE DatabaseRole = @role;
            """,
            connection);
        command.Parameters.AddWithValue("@role", databaseRole);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        string? password = null;
        var role = reader.GetString(0);
        var location = reader.GetString(1);
        var server = reader.GetString(2);
        var instance = reader.IsDBNull(3) ? null : reader.GetString(3);
        int? port = reader.IsDBNull(4) ? null : reader.GetInt32(4);
        var database = reader.GetString(5);
        var authentication = reader.GetString(6);
        var username = reader.IsDBNull(7) ? null : reader.GetString(7);
        var encrypt = reader.GetBoolean(9);
        var trust = reader.GetBoolean(10);
        if (!reader.IsDBNull(8))
        {
            try
            {
                password = UnprotectPassword(reader.GetString(8));
            }
            catch (System.Security.Cryptography.CryptographicException) when (string.Equals(role, DatabaseRole.Central, StringComparison.Ordinal))
            {
                var saved = await centralFile.ReadAsync(cancellationToken);
                if (saved?.Password is null)
                {
                    throw;
                }

                password = saved.Password;
                await reader.CloseAsync();
                await RewriteProtectedPasswordAsync(connection, role, password, cancellationToken);
            }
        }

        return new SqlEndpointSettings(
            role,
            location,
            server,
            instance,
            port,
            database,
            authentication,
            username,
            password,
            encrypt,
            trust);
    }

    private async Task RewriteProtectedPasswordAsync(SqlConnection connection, string role, string password, CancellationToken cancellationToken)
    {
        await using var update = new SqlCommand(
            "UPDATE dbo.SyncEndpoints SET ProtectedPassword = @password WHERE DatabaseRole = @role;",
            connection);
        update.Parameters.AddWithValue("@password", _protector.Protect(password));
        update.Parameters.AddWithValue("@role", role);
        await update.ExecuteNonQueryAsync(cancellationToken);
    }
}
