using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace XtraPOS.Api.Membership;

public static class MembershipPkce
{
    public static (string Verifier, string Challenge) Create()
    {
        var verifier = MembershipTokens.CreateOpaqueToken();
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    public static string AuthorizeUrl(string provider, string clientId, string redirectUri, string state, string nonce, string challenge)
    {
        var endpoint = provider == "Apple"
            ? "https://appleid.apple.com/auth/authorize"
            : "https://accounts.google.com/o/oauth2/v2/auth";
        var query = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["response_type"] = provider == "Apple" ? "code id_token" : "code",
            ["scope"] = "openid",
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256"
        };
        if (provider == "Apple") query["response_mode"] = "form_post";
        return endpoint + "?" + string.Join("&", query.Select(pair => $"{pair.Key}={Uri.EscapeDataString(pair.Value)}"));
    }
}

public interface IMembershipIdTokenValidator
{
    Task<MembershipProviderClaims> ValidateAsync(string provider, string idToken, string audience, string nonce, CancellationToken cancellationToken);
}

public sealed class MembershipIdTokenValidator : IMembershipIdTokenValidator
{
    public async Task<MembershipProviderClaims> ValidateAsync(string provider, string idToken, string audience, string nonce, CancellationToken cancellationToken)
    {
        var metadata = provider == "Apple"
            ? "https://appleid.apple.com/.well-known/openid-configuration"
            : "https://accounts.google.com/.well-known/openid-configuration";
        var issuer = provider == "Apple" ? "https://appleid.apple.com" : "https://accounts.google.com";
        var manager = new ConfigurationManager<OpenIdConnectConfiguration>(metadata, new OpenIdConnectConfigurationRetriever());
        var configuration = await manager.GetConfigurationAsync(cancellationToken);
        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = issuer,
            ValidAudience = audience,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true
        });
        if (!result.IsValid || result.ClaimsIdentity is null)
        {
            return new MembershipProviderClaims(provider, "", "", audience, DateTime.UtcNow, null, null, false);
        }

        string Claim(string name) => result.Claims.TryGetValue(name, out var value) ? value?.ToString() ?? "" : "";
        var expires = result.SecurityToken is JsonWebToken jwt ? jwt.ValidTo : DateTime.UtcNow;
        var claims = new MembershipProviderClaims(provider, Claim("sub"), issuer, audience, expires, Claim("nonce"), Claim("email"), true);
        var code = MembershipProviderRules.Rejection(claims, issuer, audience, nonce, DateTime.UtcNow);
        if (code is not null) throw new MembershipException(code);
        return claims;
    }
}

public static class MembershipEnrollmentRules
{
    public static string? CommitBlock(bool ownershipVerified, DateTime expiresAtUtc, DateTime nowUtc, Guid? linkedCustomerId, Guid requestedCustomerId, bool consumed)
    {
        if (consumed && linkedCustomerId == requestedCustomerId) return null;
        if (consumed) return "MEMBERSHIP_PROVIDER_CONFLICT";
        if (!ownershipVerified) return "MEMBERSHIP_OWNERSHIP_PROOF_REQUIRED";
        if (nowUtc >= expiresAtUtc) return "MEMBERSHIP_ENROLLMENT_EXPIRED";
        if (linkedCustomerId is Guid owner && owner != requestedCustomerId) return "MEMBERSHIP_PROVIDER_CONFLICT";
        return null;
    }
}

public static class MembershipLinkPolicy
{
    public static string? Decide(Guid? existingCustomerId, Guid requestedCustomerId, bool ownershipProved)
    {
        if (existingCustomerId == requestedCustomerId) return null;
        if (existingCustomerId is not null) return "MEMBERSHIP_PROVIDER_CONFLICT";
        if (!ownershipProved) return "MEMBERSHIP_OWNERSHIP_PROOF_REQUIRED";
        return null;
    }
}

public sealed class MembershipCodeExchange(IHttpClientFactory httpClientFactory)
{
    public async Task<string> ExchangeAsync(string provider, string clientId, string clientSecret, string code, string verifier, string redirectUri, CancellationToken cancellationToken)
    {
        var endpoint = provider == "Apple" ? "https://appleid.apple.com/auth/token" : "https://oauth2.googleapis.com/token";
        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier
        };
        if (!string.IsNullOrWhiteSpace(clientSecret)) body["client_secret"] = clientSecret;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(body)
        };
        var client = httpClientFactory.CreateClient("MembershipOidc");
        using var response = await client.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new MembershipException("MEMBERSHIP_PROVIDER_UNAVAILABLE");
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("id_token", out var token))
        {
            throw new MembershipException("MEMBERSHIP_PROVIDER_SIGNATURE");
        }

        return token.GetString() ?? throw new MembershipException("MEMBERSHIP_PROVIDER_SIGNATURE");
    }
}
