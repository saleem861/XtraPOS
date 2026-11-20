using System.Text.Json;

namespace XtraPOS.Api.Setup;

/// <summary>
/// The branch service talks to the separate updater (deploy/updater/xtrapos-updater.ps1, scheduled task
/// XtraPOSUpdater) through files in C:\ProgramData\XtraPOS\Updater. The service never stops or replaces itself.
/// </summary>
public static class UpdaterChannel
{
    public static readonly TimeSpan AliveWindow = TimeSpan.FromMinutes(3);

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XtraPOS", "Updater");

    public static bool IsAlive(DateTime? heartbeatUtc, DateTime nowUtc)
    {
        if (heartbeatUtc is not { } beat)
        {
            return false;
        }

        var age = nowUtc - beat;
        return age < AliveWindow && age > -AliveWindow;
    }

    public static DateTime? ReadHeartbeat(string folder)
    {
        var file = Path.Combine(folder, "heartbeat.txt");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(file).Trim();
            if (DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }

            return File.GetLastWriteTimeUtc(file);
        }
        catch (IOException)
        {
            return File.GetLastWriteTimeUtc(file);
        }
    }

    public static string Request(string folder, DateTime nowUtc)
    {
        var id = Guid.NewGuid().ToString("N");
        var json = JsonSerializer.Serialize(new { id, requestedAt = nowUtc.ToString("o") });
        var temp = Path.Combine(folder, "request.json.tmp");
        File.WriteAllText(temp, json);
        File.Move(temp, Path.Combine(folder, "request.json"), overwrite: true);
        return id;
    }

    public static UpdaterStatus? ReadStatus(string folder)
    {
        var file = Path.Combine(folder, "status.json");
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return ParseStatus(File.ReadAllText(file));
        }
        catch (IOException)
        {
            return null;
        }
    }

    public static UpdaterStatus? ParseStatus(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return new UpdaterStatus(
                Text(root, "requestId"),
                Text(root, "state"),
                Text(root, "stage"),
                Text(root, "detail"),
                Text(root, "from"),
                Text(root, "to"),
                Text(root, "at"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string InstalledSerial(string contentRoot)
    {
        var file = Path.Combine(contentRoot, "release.json");
        if (!File.Exists(file))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            return Text(document.RootElement, "serial");
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return "";
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}

public sealed record UpdaterStatus(string RequestId, string State, string Stage, string Detail, string From, string To, string At);
