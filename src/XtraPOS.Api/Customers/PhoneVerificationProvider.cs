namespace XtraPOS.Api.Customers;

public interface IPhoneVerificationProvider
{
    string Name { get; }

    bool OwnsCode { get; }

    Task<SendVerificationResult> StartAsync(string destinationE164, string? localCode, CancellationToken cancellationToken);

    Task<CheckVerificationResult> CheckAsync(string destinationE164, string code, string? externalId, CancellationToken cancellationToken);
}

public sealed record SendVerificationResult(bool Success, string? ExternalId, string? ErrorCode, string ErrorCategory);

public sealed record CheckVerificationResult(bool Approved, string? ErrorCode, string ErrorCategory);

public static class PhoneVerificationCategories
{
    public const string None = "None";
    public const string Configuration = "Configuration";
    public const string Authentication = "Authentication";
    public const string Destination = "Destination";
    public const string RateLimit = "RateLimit";
    public const string Attempts = "Attempts";
    public const string Expired = "Expired";
    public const string Rejected = "Rejected";
    public const string Delivery = "Delivery";
    public const string TrialRecipient = "TrialRecipient";
    public const string ComplianceProfileRequired = "ComplianceProfileRequired";
}

public static class RegistrationVerification
{
    public static bool SaveRequiresChallenge(string? countryCode) => !string.IsNullOrWhiteSpace(countryCode);

    public static string? Rejection(
        string status,
        Guid? linkedCustomerId,
        string challengeDestination,
        string? submittedDestination,
        DateTime? verifiedAt,
        DateTime utcNow)
    {
        if (linkedCustomerId is not null)
        {
            return "طلب التحقق مستخدم بالفعل.";
        }

        if (!string.Equals(status, "Verified", StringComparison.Ordinal))
        {
            return "يجب التحقق من رقم الهاتف قبل حفظ الزبون.";
        }

        if (!string.Equals(challengeDestination, submittedDestination, StringComparison.Ordinal))
        {
            return "رقم الهاتف لا يطابق التحقق.";
        }

        if (verifiedAt is null || verifiedAt < utcNow.AddMinutes(-30))
        {
            return "رمز التحقق لم يعد صالحاً.";
        }

        return null;
    }
}

public static class PhoneVerificationCode
{
    public const int Length = 4;

    public static string? Rejection(string? code, int length = Length) =>
        code is null || code.Length != length ? $"رمز التحقق يجب أن يكون {length} أرقام." : null;
}

public static class PhoneVerificationRules
{
    public static bool Expired(DateTime expiresAt, DateTime utcNow) => expiresAt < utcNow;

    public static bool ResendBlocked(DateTime? resendAvailableAt, DateTime utcNow) =>
        resendAvailableAt is DateTime ready && ready > utcNow;

    public static bool AttemptsExhausted(int attempts, int maxAttempts) => attempts >= maxAttempts;

    public static bool AlternatePrefixAllowed(string errorCategory, bool alreadyRetried) =>
        !alreadyRetried && errorCategory == PhoneVerificationCategories.Destination;
}

public static class PhoneVerificationMessages
{
    public const string Destination = "تعذر إرسال رمز التحقق إلى رقم الهاتف.";

    public static string ForCategory(string category) => category switch
    {
        PhoneVerificationCategories.Configuration => "خدمة التحقق عبر الرسائل غير مهيأة بعد.",
        PhoneVerificationCategories.Authentication => "خدمة التحقق عبر الرسائل غير مهيأة بعد.",
        PhoneVerificationCategories.Destination => Destination,
        PhoneVerificationCategories.RateLimit => "يرجى الانتظار قبل طلب رمز جديد.",
        PhoneVerificationCategories.Attempts => "تم تجاوز عدد محاولات التحقق المسموح بها.",
        PhoneVerificationCategories.Expired => "انتهت صلاحية رمز التحقق. أرسل رمزًا جديدًا.",
        PhoneVerificationCategories.Rejected => "رمز التحقق غير صحيح.",
        PhoneVerificationCategories.TrialRecipient => "لا يمكن إرسال رمز التحقق لهذا الرقم لأن حساب Twilio الحالي تجريبي ويسمح فقط بأرقام الاختبار الموثقة. بعد ترقية حساب Twilio سيتم السماح بالإرسال للأرقام العادية.",
        PhoneVerificationCategories.ComplianceProfileRequired => "تعذر إرسال رمز التحقق حاليًا بسبب إعدادات حساب خدمة الرسائل.",
        _ => "تعذر إرسال رمز التحقق. حاول مرة أخرى.",
    };
}
