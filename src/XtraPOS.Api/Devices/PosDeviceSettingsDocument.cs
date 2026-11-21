using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XtraPOS.Api.Devices;

/// <summary>
/// تعريف إعداد واحد من «القيم الأولية للنقطة الحالية».
/// لإضافة إعداد جديد لاحقاً: أضف تعريفاً إلى <see cref="PosDeviceSettingsCatalog.All"/> فقط.
/// </summary>
public sealed record PosDeviceSettingDefinition(string Key, string Label, string Kind, JsonNode DefaultValue);

public static class PosDeviceSettingsCatalog
{
    public const string BooleanKind = "boolean";
    public const string CustomerDisplayEnabled = "customerDisplay.enabled";

    public static readonly IReadOnlyList<PosDeviceSettingDefinition> All =
    [
        new(CustomerDisplayEnabled, "تفعيل شاشة الزبون", BooleanKind, JsonValue.Create(false)!),
    ];

    public static PosDeviceSettingDefinition? Find(string key) =>
        All.FirstOrDefault(definition => string.Equals(definition.Key, key, StringComparison.Ordinal));
}

public static class PosDeviceSettingSources
{
    public const string Branch = "Branch";
    public const string Central = "Central";
}

/// <summary>
/// قيمة إعداد مع من عدّله ومتى ومن أين. هذه البيانات هي أساس حل التعارض.
/// </summary>
public sealed record PosDeviceSettingEntry(
    JsonNode? Value,
    DateTime UpdatedAtUtc,
    Guid? UpdatedByUserId,
    string? UpdatedByName,
    string Source);

/// <summary>
/// مستند الإعدادات المخزّن في POSDeviceConfigurations.ConfigurationJson["settings"]
/// وفي CentralPosDeviceSettings.SettingsJson.
///
/// قاعدة الدمج (Last-Writer-Wins لكل مفتاح على حدة):
/// - لكل مفتاح يُعتمد التعديل الأحدث UpdatedAtUtc.
/// - عند تساوي الوقت يفوز تعديل المركز، ثم المقارنة النصية للقيمة؛ لذا الدمج تبادلي ونتيجته واحدة في الطرفين.
/// - تعديل الفرع دون اتصال لمفتاح، وتعديل المركز لمفتاح آخر، يبقيان معاً ولا يمحو أحدهما الآخر.
/// </summary>
public static class PosDeviceSettingsDocument
{
    public const string RootKey = "settings";

    public static SortedDictionary<string, PosDeviceSettingEntry> Parse(string? json)
    {
        var result = new SortedDictionary<string, PosDeviceSettingEntry>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return result;
        }

        return FromObject(root);
    }

    public static SortedDictionary<string, PosDeviceSettingEntry> FromObject(JsonObject? settings)
    {
        var result = new SortedDictionary<string, PosDeviceSettingEntry>(StringComparer.Ordinal);
        if (settings is null)
        {
            return result;
        }

        foreach (var (key, node) in settings)
        {
            if (node is not JsonObject entry
                || !TryReadTime(entry["updatedAtUtc"], out var updatedAt))
            {
                continue;
            }

            Guid? userId = Guid.TryParse(Text(entry["updatedBy"]), out var parsed) ? parsed : null;
            var source = Text(entry["source"]) == PosDeviceSettingSources.Central
                ? PosDeviceSettingSources.Central
                : PosDeviceSettingSources.Branch;
            result[key] = new PosDeviceSettingEntry(
                entry["value"]?.DeepClone(),
                updatedAt,
                userId,
                Text(entry["updatedByName"]),
                source);
        }

        return result;
    }

    public static JsonObject ToObject(IReadOnlyDictionary<string, PosDeviceSettingEntry> settings)
    {
        var root = new JsonObject();
        foreach (var (key, entry) in settings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            root[key] = new JsonObject
            {
                ["value"] = entry.Value?.DeepClone(),
                ["updatedAtUtc"] = FormatTime(entry.UpdatedAtUtc),
                ["updatedBy"] = entry.UpdatedByUserId?.ToString("D"),
                ["updatedByName"] = entry.UpdatedByName,
                ["source"] = entry.Source,
            };
        }

        return root;
    }

    public static string Serialize(IReadOnlyDictionary<string, PosDeviceSettingEntry> settings) =>
        ToObject(settings).ToJsonString();

    public static SortedDictionary<string, PosDeviceSettingEntry> Merge(
        IReadOnlyDictionary<string, PosDeviceSettingEntry> left,
        IReadOnlyDictionary<string, PosDeviceSettingEntry> right)
    {
        var result = new SortedDictionary<string, PosDeviceSettingEntry>(StringComparer.Ordinal);
        foreach (var key in left.Keys.Union(right.Keys, StringComparer.Ordinal))
        {
            left.TryGetValue(key, out var a);
            right.TryGetValue(key, out var b);
            result[key] = a is null ? b! : b is null ? a : Newer(a, b);
        }

        return result;
    }

    public static bool SameContent(
        IReadOnlyDictionary<string, PosDeviceSettingEntry> left,
        IReadOnlyDictionary<string, PosDeviceSettingEntry> right) =>
        string.Equals(Serialize(left), Serialize(right), StringComparison.Ordinal);

    /// <summary>
    /// يحوّل طلب التعديل إلى مدخلات موقّعة بالوقت والمستخدم بعد التحقق من المفاتيح والأنواع.
    /// </summary>
    public static SortedDictionary<string, PosDeviceSettingEntry> BuildChanges(
        IReadOnlyDictionary<string, JsonElement>? values,
        DateTime nowUtc,
        Guid actorUserId,
        string? actorName,
        string source)
    {
        if (values is null || values.Count == 0)
        {
            throw new PosDeviceSettingsException("لا توجد قيم للحفظ.");
        }

        var result = new SortedDictionary<string, PosDeviceSettingEntry>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            var definition = PosDeviceSettingsCatalog.Find(key)
                ?? throw new PosDeviceSettingsException($"الإعداد «{key}» غير معروف.");
            if (definition.Kind == PosDeviceSettingsCatalog.BooleanKind
                && value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new PosDeviceSettingsException($"قيمة «{definition.Label}» يجب أن تكون نعم أو لا.");
            }

            result[key] = new PosDeviceSettingEntry(
                JsonNode.Parse(value.GetRawText()),
                Truncate(nowUtc),
                actorUserId,
                string.IsNullOrWhiteSpace(actorName) ? null : actorName.Trim(),
                source);
        }

        return result;
    }

    /// <summary>القيم الفعّالة: القيمة المحفوظة أو الافتراضية لكل إعداد معروف.</summary>
    public static IReadOnlyList<PosDeviceSettingValue> Effective(IReadOnlyDictionary<string, PosDeviceSettingEntry> settings)
    {
        return PosDeviceSettingsCatalog.All
            .Select(definition =>
            {
                settings.TryGetValue(definition.Key, out var entry);
                return new PosDeviceSettingValue(
                    definition.Key,
                    definition.Label,
                    definition.Kind,
                    (entry?.Value ?? definition.DefaultValue).DeepClone(),
                    entry is null,
                    entry?.UpdatedAtUtc,
                    entry?.UpdatedByName,
                    entry?.Source);
            })
            .ToArray();
    }

    public static string? ReadFromConfiguration(string? configurationJson)
    {
        if (string.IsNullOrWhiteSpace(configurationJson))
        {
            return null;
        }

        try
        {
            return (JsonNode.Parse(configurationJson) as JsonObject)?[RootKey]?.ToJsonString();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>يضع الإعدادات تحت "settings" ويحافظ على باقي محتوى ملف إعداد الجهاز كما هو.</summary>
    public static string WriteIntoConfiguration(string? configurationJson, IReadOnlyDictionary<string, PosDeviceSettingEntry> settings)
    {
        JsonObject root;
        try
        {
            root = string.IsNullOrWhiteSpace(configurationJson)
                ? new JsonObject()
                : JsonNode.Parse(configurationJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject();
        }

        root[RootKey] = ToObject(settings);
        if (root["schemaVersion"] is null)
        {
            root["schemaVersion"] = DeviceConfigDocument.SchemaVersion;
        }

        return root.ToJsonString();
    }

    private static PosDeviceSettingEntry Newer(PosDeviceSettingEntry a, PosDeviceSettingEntry b)
    {
        var byTime = a.UpdatedAtUtc.CompareTo(b.UpdatedAtUtc);
        if (byTime != 0)
        {
            return byTime > 0 ? a : b;
        }

        var aCentral = a.Source == PosDeviceSettingSources.Central;
        var bCentral = b.Source == PosDeviceSettingSources.Central;
        if (aCentral != bCentral)
        {
            return aCentral ? a : b;
        }

        var aText = a.Value?.ToJsonString() ?? "null";
        var bText = b.Value?.ToJsonString() ?? "null";
        return string.CompareOrdinal(aText, bText) >= 0 ? a : b;
    }

    private static DateTime Truncate(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerMillisecond), DateTimeKind.Utc);
    }

    private static string FormatTime(DateTime value) =>
        Truncate(value).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string? Text(JsonNode? node) =>
        node is JsonValue json && json.TryGetValue<string>(out var text) ? text : null;

    private static bool TryReadTime(JsonNode? node, out DateTime value)
    {
        value = default;
        var text = Text(node);
        if (text is null)
        {
            return false;
        }

        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
        {
            return false;
        }

        value = Truncate(DateTime.SpecifyKind(parsed, DateTimeKind.Utc));
        return true;
    }
}

public sealed record PosDeviceSettingValue(
    string Key,
    string Label,
    string Kind,
    JsonNode Value,
    bool IsDefault,
    DateTime? UpdatedAtUtc,
    string? UpdatedByName,
    string? Source);

public sealed class PosDeviceSettingsException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
