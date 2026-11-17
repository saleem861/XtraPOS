using System.Text.Json;
using System.Text.Json.Nodes;

namespace XtraPOS.Api.Setup;

public static class BranchAgentMembershipFile
{
    public const string GoogleClientId = "998725042091-t1p0fv71s7ajkbs104s62if1c1oviqmd.apps.googleusercontent.com";
    public const string DefaultPhoneOrigin = "https://test-pos.sharbelfashion.com";

    public static void Enable(string appsettingsPath, string? publicOrigin)
    {
        var root = JsonNode.Parse(File.ReadAllText(appsettingsPath)) as JsonObject ?? new JsonObject();
        var membership = root["Membership"] as JsonObject ?? new JsonObject();
        membership["Enabled"] = true;
        membership["GoogleLoginEnabled"] = true;
        membership["AppleLoginEnabled"] = false;
        membership["RequireVerificationForSpecialPrices"] = false;
        if (string.IsNullOrWhiteSpace(membership["GoogleClientId"]?.GetValue<string>()))
        {
            membership["GoogleClientId"] = GoogleClientId;
        }

        var phoneOrigin = PhoneOrigin(membership["PublicOrigin"]?.GetValue<string>(), publicOrigin);
        if (phoneOrigin is not null)
        {
            membership["PublicOrigin"] = phoneOrigin;
        }

        root["Membership"] = membership;
        File.WriteAllText(appsettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string? PhoneOrigin(string? configured, string? pairedServer)
    {
        if (IsPhoneReachable(pairedServer)) return pairedServer!.Trim().TrimEnd('/');
        if (IsPhoneReachable(configured)) return configured!.Trim().TrimEnd('/');
        return null;
    }

    private static bool IsPhoneReachable(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;
        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri)) return false;
        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !uri.IsLoopback;
    }
}
