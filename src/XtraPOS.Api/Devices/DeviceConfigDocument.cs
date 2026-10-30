using System.Text.Json.Nodes;

namespace XtraPOS.Api.Devices;

public static class DeviceConfigDocument
{
    public const int SchemaVersion = 1;

    public static string Merge(string? existingJson, IReadOnlyDictionary<string, string?> changes)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(existingJson)
                ? new JsonObject()
                : JsonNode.Parse(existingJson) as JsonObject ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            root = new JsonObject();
        }

        foreach (var (key, value) in changes)
        {
            if (value is null)
            {
                root.Remove(key);
            }
            else
            {
                root[key] = value;
            }
        }

        var version = root["schemaVersion"]?.GetValue<int?>() ?? 0;
        root["schemaVersion"] = version < 1 ? SchemaVersion : version;
        var configVersion = root["configVersion"]?.GetValue<int?>() ?? 0;
        root["configVersion"] = configVersion + 1;
        return root.ToJsonString();
    }
}
