using System.Security.Cryptography;
using System.Text;

namespace XtraPOS.Api.Setup;

public static class IndependentOnboarding
{
    public const string TokenFileName = "onboarding-authorization.token";
    public const string HeaderName = "X-Onboarding-Authorization";
    public const int MinimumTokenLength = 32;
    public const int AttemptLimit = 5;
    public static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(10);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Queue<DateTime>> Attempts = new();

    public static string TokenPath(string contentRoot) =>
        Path.Combine(contentRoot, "App_Data", TokenFileName);

    public static bool TryConsume(string contentRoot, string? presented, string clientKey, out string message)
    {
        var key = string.IsNullOrWhiteSpace(clientKey) ? "unknown" : clientKey.Trim();
        if (!AllowAttempt(key))
        {
            SetupLog.Write(contentRoot, "onboarding", "independent-onboarding", "rate-limited " + key);
            message = "تم إيقاف محاولات إنشاء الفرع المستقل مؤقتاً. لم يتم استخدام التفويض.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(presented) || presented.Trim().Length < MinimumTokenLength)
        {
            SetupLog.Write(contentRoot, "onboarding", "independent-onboarding", "missing-authorization " + key);
            message = BranchProvisionPolicy.OnboardingClosedMessage;
            return false;
        }

        var path = TokenPath(contentRoot);
        var claimed = path + ".claimed";
        lock (Gate)
        {
            if (!File.Exists(path))
            {
                SetupLog.Write(contentRoot, "onboarding", "independent-onboarding", "no-token " + key);
                message = BranchProvisionPolicy.OnboardingClosedMessage;
                return false;
            }

            var expected = File.ReadAllText(path).Trim();
            if (!FixedEquals(expected, presented.Trim()))
            {
                SetupLog.Write(contentRoot, "onboarding", "independent-onboarding", "rejected " + key);
                message = BranchProvisionPolicy.OnboardingClosedMessage;
                return false;
            }

            try
            {
                File.Move(path, claimed);
            }
            catch (IOException)
            {
                SetupLog.Write(contentRoot, "onboarding", "independent-onboarding", "concurrent-rejected " + key);
                message = "تفويض إنشاء الفرع المستقل مستخدم بالفعل. لم يتم إنشاء قاعدة.";
                return false;
            }

            File.Delete(claimed);
            SetupLog.Write(contentRoot, "onboarding", "independent-onboarding", "consumed " + key);
            message = "";
            return true;
        }
    }

    public static void ResetAttempts()
    {
        lock (Gate)
        {
            Attempts.Clear();
        }
    }

    private static bool AllowAttempt(string clientKey)
    {
        var now = DateTime.UtcNow;
        lock (Gate)
        {
            if (!Attempts.TryGetValue(clientKey, out var attempts))
            {
                attempts = new Queue<DateTime>();
                Attempts[clientKey] = attempts;
            }

            while (attempts.Count > 0 && now - attempts.Peek() > AttemptWindow)
            {
                attempts.Dequeue();
            }

            if (attempts.Count >= AttemptLimit)
            {
                return false;
            }

            attempts.Enqueue(now);
            return true;
        }
    }

    private static bool FixedEquals(string expected, string presented)
    {
        var left = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var right = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(left, right);
    }
}
