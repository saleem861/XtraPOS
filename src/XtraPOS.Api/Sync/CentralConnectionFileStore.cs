using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;

namespace XtraPOS.Api.Sync;

public sealed class CentralConnectionFileStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IDataProtector _protector;
    private readonly string _path;

    public CentralConnectionFileStore(IDataProtectionProvider protection, IHostEnvironment environment)
        : this(protection, Path.Combine(environment.ContentRootPath, "App_Data"))
    {
    }

    public CentralConnectionFileStore(IDataProtectionProvider protection, string directory)
    {
        _protector = protection.CreateProtector("XtraPOS.CentralSqlCredentials");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "central-connection.json");
    }

    public async Task SaveAsync(SqlEndpointSettings settings, CancellationToken cancellationToken)
    {
        var document = new StoredCentralConnection
        {
            DatabaseRole = settings.DatabaseRole,
            LocationKind = settings.LocationKind,
            Server = settings.Server,
            InstanceName = settings.InstanceName,
            Port = settings.Port,
            DatabaseName = settings.DatabaseName,
            AuthenticationType = settings.AuthenticationType,
            Username = settings.Username,
            ProtectedPassword = string.IsNullOrEmpty(settings.Password) ? null : _protector.Protect(settings.Password),
            Encrypt = settings.Encrypt,
            TrustServerCertificate = settings.TrustServerCertificate,
        };
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(document, Json), cancellationToken);
    }

    public async Task<SqlEndpointSettings?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var document = JsonSerializer.Deserialize<StoredCentralConnection>(await File.ReadAllTextAsync(_path, cancellationToken), Json);
        if (document is null || string.IsNullOrWhiteSpace(document.DatabaseName))
        {
            return null;
        }

        string? password = null;
        if (!string.IsNullOrEmpty(document.ProtectedPassword))
        {
            try
            {
                password = _protector.Unprotect(document.ProtectedPassword);
            }
            catch (CryptographicException)
            {
                return null;
            }
        }

        return new SqlEndpointSettings(
            document.DatabaseRole,
            document.LocationKind,
            document.Server,
            document.InstanceName,
            document.Port,
            document.DatabaseName,
            document.AuthenticationType,
            document.Username,
            password,
            document.Encrypt,
            document.TrustServerCertificate);
    }

    public string FilePath => _path;

    private sealed class StoredCentralConnection
    {
        public string DatabaseRole { get; set; } = "";
        public string LocationKind { get; set; } = "";
        public string Server { get; set; } = "";
        public string? InstanceName { get; set; }
        public int? Port { get; set; }
        public string DatabaseName { get; set; } = "";
        public string AuthenticationType { get; set; } = "";
        public string? Username { get; set; }
        public string? ProtectedPassword { get; set; }
        public bool Encrypt { get; set; }
        public bool TrustServerCertificate { get; set; }
    }
}
