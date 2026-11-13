using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace XtraPOS.Api.Setup;

public sealed class LocalPosSessionStore(IDataProtectionProvider protection, IHostEnvironment environment)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public void Save(DeviceRegistrationResponse device)
    {
        if (string.IsNullOrWhiteSpace(device.DeviceToken))
        {
            return;
        }

        var protector = protection.CreateProtector("XtraPOS.LocalPosSession");
        var path = SessionPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, protector.Protect(JsonSerializer.Serialize(device, Json)));
        File.Move(temporary, path, overwrite: true);
    }

    public DeviceRegistrationResponse? Read()
    {
        var path = SessionPath();
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var protector = protection.CreateProtector("XtraPOS.LocalPosSession");
            return JsonSerializer.Deserialize<DeviceRegistrationResponse>(protector.Unprotect(File.ReadAllText(path)), Json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Clear()
    {
        var path = SessionPath();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string SessionPath() => Path.Combine(environment.ContentRootPath, "App_Data", "local-pos-session.json");
}
