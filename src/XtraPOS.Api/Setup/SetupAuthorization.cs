using Microsoft.Extensions.Configuration;

namespace XtraPOS.Api.Setup;

public static class SetupAuthorization
{
    public const string HeaderName = "X-Setup-Authorization";
    public const string TokenKey = "Bootstrap:SetupToken";
    public const string TokenFileName = "setup-authorization.token";
    public const string ReplacementDeniedMessage = "استبدال الفرع وحذف بياناته غير متاح من شاشة الإعداد. استخدم الفرع الموجود أو إعداد قاعدة جديدة.";

    public static string TokenPath(string contentRoot) =>
        Path.Combine(contentRoot, "App_Data", TokenFileName);

    public static void Invalidate(string contentRoot)
    {
        var path = TokenPath(contentRoot);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static bool TryAuthorize(HttpRequest request, IConfiguration configuration, string contentRoot, bool alreadyConfigured, out int status, out string message, bool replaceExisting = false)
    {
        // Setup has no verified backup/ownership workflow. Never authorize destructive replacement.
        if (replaceExisting)
        {
            status = StatusCodes.Status403Forbidden;
            message = ReplacementDeniedMessage;
            return false;
        }

        if (alreadyConfigured)
        {
            status = StatusCodes.Status403Forbidden;
            message = "الإعداد الأول اكتمل. لا يمكن إنشاء المنظمة مرة أخرى.";
            return false;
        }

        status = StatusCodes.Status200OK;
        message = "";
        return true;
    }

    private static bool CryptographicEquals(string expected, string provided)
    {
        var left = System.Text.Encoding.UTF8.GetBytes(expected);
        var right = System.Text.Encoding.UTF8.GetBytes(provided);
        return left.Length == right.Length
            && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
    }
}
