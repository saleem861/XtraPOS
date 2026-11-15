using XtraPOS.Api.Setup;

namespace XtraPOS.Api.Membership;

public static class MembershipLinks
{
    public static string? PhoneOrigin(string? configured, string? pairedServer) =>
        BranchAgentMembershipFile.PhoneOrigin(configured, pairedServer);

    public static string BranchEnrollmentOrigin(string? configured, string? startupServer) =>
        PhoneOrigin(configured, startupServer) ?? BranchAgentMembershipFile.DefaultPhoneOrigin;

    public static string EnrollmentOrigin(string? publicOrigin, string? localBaseUrl, string requestOrigin)
    {
        var phone = PhoneOrigin(publicOrigin, null);
        if (phone is not null) return phone;
        if (IsPhoneReachable(requestOrigin)) return Trim(requestOrigin);
        if (!string.IsNullOrWhiteSpace(localBaseUrl)) return Trim(localBaseUrl);
        return Trim(requestOrigin);
    }

    public static bool IsPhoneReachable(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;
        if (!Uri.TryCreate(origin.Trim(), UriKind.Absolute, out var uri)) return false;
        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) && !uri.IsLoopback;
    }

    public static bool IsEnrollmentToken(string? token) =>
        token is { Length: >= 20 and <= 128 } && token.All(item => char.IsLetterOrDigit(item) || item is '-' or '_');

    public static string? CounterToken(string? path)
    {
        const string prefix = "/membership/counter/";
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var token = path[prefix.Length..];
        return IsEnrollmentToken(token) ? token : null;
    }

    private static string Trim(string origin) => origin.Trim().TrimEnd('/');
}

public sealed class SharedEnrollmentPublish
{
    public Guid EnrollmentId { get; set; }
    public Guid BusinessId { get; set; }
    public Guid? CustomerId { get; set; }
    public Guid CashierUserId { get; set; }
    public string? Token { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
}

public sealed record SharedEnrollmentView(
    string Status,
    string? Provider,
    string? Display,
    string? Subject,
    string? Email,
    Guid? AttemptId,
    DateTime? AttemptExpiresAtUtc);

public sealed class SharedCounterPublish
{
    public string? Token { get; set; }
}

public sealed record SharedCounterView(string Status, string? Provider, string? Subject, string? Email, string? Display);
