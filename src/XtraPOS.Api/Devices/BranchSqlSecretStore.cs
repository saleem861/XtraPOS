using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace XtraPOS.Api.Devices;

public sealed class BranchSqlSecretStore
{
    private readonly IDataProtector _protector;
    private readonly string _path;

    public BranchSqlSecretStore(IDataProtectionProvider protection, IHostEnvironment environment)
    {
        _protector = protection.CreateProtector("XtraPOS.BranchSqlSecrets");
        var directory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "branch-sql-secrets.json");
    }

    public async Task SaveAsync(Guid branchId, string password, CancellationToken cancellationToken)
    {
        var map = await ReadAllAsync(cancellationToken);
        map[branchId.ToString()] = _protector.Protect(password);
        await File.WriteAllTextAsync(_path, JsonSerializer.Serialize(map), cancellationToken);
    }

    public async Task<string?> ReadAsync(Guid branchId, CancellationToken cancellationToken)
    {
        var map = await ReadAllAsync(cancellationToken);
        return map.TryGetValue(branchId.ToString(), out var protectedValue) && !string.IsNullOrEmpty(protectedValue)
            ? _protector.Unprotect(protectedValue)
            : null;
    }

    private async Task<Dictionary<string, string>> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(_path, cancellationToken))
                ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }
}
