using System.Text.Json;

namespace XtraPOS.Api.Data;

public sealed class DeviceDatabaseDirectory(IHostEnvironment environment)
{
    private readonly string _path = Path.Combine(environment.ContentRootPath, "App_Data", "device-databases.json");
    private readonly object _gate = new();

    public void Remember(Guid deviceId, string databaseName)
    {
        if (!BranchDatabase.IsTestName(databaseName))
        {
            return;
        }

        lock (_gate)
        {
            var map = Read();
            map[deviceId.ToString("D")] = databaseName;
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(map));
        }
    }

    public string? Find(Guid deviceId)
    {
        lock (_gate)
        {
            return Read().TryGetValue(deviceId.ToString("D"), out var database) ? database : null;
        }
    }

    public IReadOnlyList<string> Databases()
    {
        lock (_gate)
        {
            return Read().Values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(_path))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path))
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
