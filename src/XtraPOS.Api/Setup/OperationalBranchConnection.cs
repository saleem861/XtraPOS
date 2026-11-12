using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using XtraPOS.Api.Data;
using XtraPOS.Api.Sync;

namespace XtraPOS.Api.Setup;

public sealed class OperationalBranchConnection
{
    private readonly object _gate = new();
    private SqlEndpointSettings? _current;

    public SqlEndpointSettings? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Replace(SqlEndpointSettings? settings)
    {
        lock (_gate)
        {
            _current = settings;
        }
    }
}

public sealed class OperationalBranchConnectionStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly IDataProtector _protector;
    private readonly string _path;

    public OperationalBranchConnectionStore(IDataProtectionProvider protection, IHostEnvironment environment)
        : this(protection, Path.Combine(environment.ContentRootPath, "App_Data"))
    {
    }

    public OperationalBranchConnectionStore(IDataProtectionProvider protection, string directory)
    {
        _protector = protection.CreateProtector("XtraPOS.OperationalBranch");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "operational-branch.json");
    }

    public async Task SaveAsync(SqlEndpointSettings settings, CancellationToken cancellationToken)
    {
        var document = new StoredOperationalBranch
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
        var temporary = _path + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(document, Json), cancellationToken);
        File.Move(temporary, _path, overwrite: true);
    }

    public async Task<SqlEndpointSettings?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        var document = JsonSerializer.Deserialize<StoredOperationalBranch>(await File.ReadAllTextAsync(_path, cancellationToken), Json);
        if (document is null || string.IsNullOrWhiteSpace(document.DatabaseName) || string.IsNullOrWhiteSpace(document.Server))
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
            catch (System.Security.Cryptography.CryptographicException)
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

    private sealed class StoredOperationalBranch
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
