using System.Security.Cryptography;
using System.Text;

namespace XtraPOS.Api.Membership;

public static class MembershipTokens
{
    public static string CreateOpaqueToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static byte[] Hash(string token)
    {
        return SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }
}

public sealed class MembershipOptions
{
    public const string SectionName = "Membership";
    public bool Enabled { get; set; }
    public bool GoogleLoginEnabled { get; set; }
    public bool AppleLoginEnabled { get; set; }
    public bool RequireVerificationForSpecialPrices { get; set; }
    public int QRExpirySeconds { get; set; } = 60;
    public int SessionIdleDays { get; set; } = 30;
    public int SessionAbsoluteDays { get; set; } = 90;
    public int MaximumActiveSessions { get; set; } = 5;
    public int VerificationPollingIntervalMs { get; set; } = 1000;
    public string? GoogleClientId { get; set; }
    public string? GoogleClientSecret { get; set; }
    public string? AppleClientId { get; set; }
    public string? AppleClientSecret { get; set; }
    public string? PublicOrigin { get; set; }
}

public sealed record MembershipProviderClaims(
    string Provider,
    string Subject,
    string Issuer,
    string Audience,
    DateTime ExpiresAtUtc,
    string? Nonce,
    string? Email,
    bool SignatureValid);

public static class MembershipProviderRules
{
    public static string? Rejection(MembershipProviderClaims claims, string expectedIssuer, string expectedAudience, string? expectedNonce, DateTime nowUtc)
    {
        if (!claims.SignatureValid) return "MEMBERSHIP_PROVIDER_SIGNATURE";
        if (!string.Equals(claims.Issuer, expectedIssuer, StringComparison.Ordinal)) return "MEMBERSHIP_PROVIDER_ISSUER";
        if (!string.Equals(claims.Audience, expectedAudience, StringComparison.Ordinal)) return "MEMBERSHIP_PROVIDER_AUDIENCE";
        if (claims.ExpiresAtUtc <= nowUtc) return "MEMBERSHIP_PROVIDER_EXPIRED";
        if (expectedNonce is not null && !string.Equals(claims.Nonce, expectedNonce, StringComparison.Ordinal)) return "MEMBERSHIP_PROVIDER_NONCE";
        if (string.IsNullOrWhiteSpace(claims.Subject)) return "MEMBERSHIP_PROVIDER_SUBJECT";
        return null;
    }
}
