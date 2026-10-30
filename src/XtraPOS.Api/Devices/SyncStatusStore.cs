using System.Text.Json;

namespace XtraPOS.Api.Devices;

public sealed class SyncStatusStore
{
    private readonly string _path;
    private readonly object _gate = new();
    private SyncSnapshot _current = new("Success", null, null, null, DateTime.UtcNow);

    public SyncStatusStore(IHostEnvironment environment)
    {
        var directory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "sync-status.json");
        if (File.Exists(_path))
        {
            try
            {
                var loaded = JsonSerializer.Deserialize<SyncSnapshot>(File.ReadAllText(_path));
                if (loaded is not null)
                {
                    _current = loaded;
                }
            }
            catch (JsonException)
            {
                // A corrupt status file must not block the API.
            }
        }
    }

    public SyncSnapshot Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public void Report(string state, string? operation, string? detail, string? diagnosticId)
    {
        var snapshot = new SyncSnapshot(state, operation, detail, diagnosticId, DateTime.UtcNow);
        lock (_gate)
        {
            _current = snapshot;
            File.WriteAllText(_path, JsonSerializer.Serialize(snapshot));
        }
    }
}

public sealed record SyncSnapshot(string State, string? Operation, string? Detail, string? DiagnosticId, DateTime UpdatedAtUtc);
