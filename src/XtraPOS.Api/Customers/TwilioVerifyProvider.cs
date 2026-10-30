using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace XtraPOS.Api.Customers;

public sealed class TwilioVerifyProvider(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<TwilioVerifyProvider> logger)
    : IPhoneVerificationProvider
{
    public string Name => "TwilioVerify";

    public bool OwnsCode => false;

    public async Task<SendVerificationResult> StartAsync(string destinationE164, string? localCode, CancellationToken cancellationToken)
    {
        var settings = TwilioVerifySettings.Read(configuration);
        if (!settings.IsConfigured)
        {
            return new SendVerificationResult(false, null, "TWILIO_NOT_CONFIGURED", PhoneVerificationCategories.Configuration);
        }

        var response = await PostAsync(settings, $"{settings.ServiceSid}/Verifications", new Dictionary<string, string>
        {
            ["To"] = destinationE164,
            ["Channel"] = settings.Channel,
        }, cancellationToken);
        if (!response.Success)
        {
            return new SendVerificationResult(false, null, response.ErrorCode, response.Category);
        }

        return new SendVerificationResult(true, response.Sid, null, PhoneVerificationCategories.None);
    }

    public async Task<CheckVerificationResult> CheckAsync(string destinationE164, string code, string? externalId, CancellationToken cancellationToken)
    {
        var settings = TwilioVerifySettings.Read(configuration);
        if (!settings.IsConfigured)
        {
            return new CheckVerificationResult(false, "TWILIO_NOT_CONFIGURED", PhoneVerificationCategories.Configuration);
        }

        var response = await PostAsync(settings, $"{settings.ServiceSid}/VerificationCheck", new Dictionary<string, string>
        {
            ["To"] = destinationE164,
            ["Code"] = code,
        }, cancellationToken);
        if (!response.Success)
        {
            return new CheckVerificationResult(false, response.ErrorCode, response.Category);
        }

        return string.Equals(response.Status, "approved", StringComparison.OrdinalIgnoreCase)
            ? new CheckVerificationResult(true, null, PhoneVerificationCategories.None)
            : new CheckVerificationResult(false, response.Status, PhoneVerificationCategories.Rejected);
    }

    private async Task<TwilioCall> PostAsync(TwilioVerifySettings settings, string path, Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient("TwilioVerify");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://verify.twilio.com/v2/Services/{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", settings.BasicCredential);
        request.Content = new FormUrlEncodedContent(form);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        string? sid = null;
        string? status = null;
        string? providerMessage = null;
        int? code = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("sid", out var sidValue)) sid = sidValue.GetString();
            if (document.RootElement.TryGetProperty("status", out var statusValue)) status = statusValue.GetString();
            if (document.RootElement.TryGetProperty("code", out var codeValue) && codeValue.TryGetInt32(out var parsed)) code = parsed;
            if (document.RootElement.TryGetProperty("message", out var messageValue)) providerMessage = messageValue.GetString();
        }
        catch (JsonException)
        {
            sid = null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var category = TwilioVerifyErrors.Category(code, (int)response.StatusCode, providerMessage);
            logger.LogWarning("Twilio Verify rejected the request with status {StatusCode}, Twilio code {TwilioCode}, and category {Category}.", (int)response.StatusCode, code, category);
            return new TwilioCall(false, null, null, code?.ToString() ?? ((int)response.StatusCode).ToString(), category);
        }

        return new TwilioCall(true, sid, status, null, PhoneVerificationCategories.None);
    }

    private sealed record TwilioCall(bool Success, string? Sid, string? Status, string? ErrorCode, string Category);
}

public sealed record TwilioVerifySettings(string AccountSid, string Username, string Secret, string ServiceSid, string Channel)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(AccountSid)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Secret)
        && !string.IsNullOrWhiteSpace(ServiceSid)
        && ServiceSid.StartsWith("VA", StringComparison.Ordinal);

    public string BasicCredential => Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Username}:{Secret}"));

    public static TwilioVerifySettings Read(IConfiguration configuration)
    {
        var accountSid = configuration["TwilioVerify:AccountSid"] ?? "";
        var apiKeySid = configuration["TwilioVerify:ApiKeySid"];
        var apiKeySecret = configuration["TwilioVerify:ApiKeySecret"];
        var authToken = configuration["TwilioVerify:AuthToken"];
        var useApiKey = !string.IsNullOrWhiteSpace(apiKeySid) && !string.IsNullOrWhiteSpace(apiKeySecret);
        return new TwilioVerifySettings(
            accountSid,
            useApiKey ? apiKeySid! : accountSid,
            useApiKey ? apiKeySecret! : authToken ?? "",
            configuration["TwilioVerify:ServiceSid"] ?? "",
            string.IsNullOrWhiteSpace(configuration["TwilioVerify:Channel"]) ? "sms" : configuration["TwilioVerify:Channel"]!);
    }
}

public static class TwilioVerifyErrors
{
    public static string Category(int? twilioCode, int httpStatus, string? providerMessage = null)
    {
        if (twilioCode is 20003 or 20008 or 20403) return PhoneVerificationCategories.Authentication;
        if (twilioCode is 20404) return PhoneVerificationCategories.Configuration;
        if (twilioCode is 60200) return PhoneVerificationCategories.Rejected;
        if (twilioCode is 20429 or 60212) return PhoneVerificationCategories.RateLimit;
        if (twilioCode is 60202 or 60203) return PhoneVerificationCategories.Attempts;
        if (twilioCode is 20404) return PhoneVerificationCategories.Expired;
        if (twilioCode is 21608)
        {
            return providerMessage is not null && providerMessage.Contains("trial", StringComparison.OrdinalIgnoreCase)
                ? PhoneVerificationCategories.TrialRecipient
                : PhoneVerificationCategories.ComplianceProfileRequired;
        }
        if (twilioCode is 21211 or 21217 or 21408 or 21612 or 21614 or 30003 or 30005 or 30006)
        {
            return PhoneVerificationCategories.Destination;
        }

        if (httpStatus is 401 or 403) return PhoneVerificationCategories.Authentication;
        return PhoneVerificationCategories.Delivery;
    }
}

public sealed class MetaPhoneVerificationProvider(IWhatsAppVerificationProvider whatsApp) : IPhoneVerificationProvider
{
    public string Name => "MetaWhatsApp";

    public bool OwnsCode => true;

    public async Task<SendVerificationResult> StartAsync(string destinationE164, string? localCode, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(localCode))
        {
            return new SendVerificationResult(false, null, "WHATSAPP_NOT_CONFIGURED", PhoneVerificationCategories.Configuration);
        }

        var sent = await whatsApp.SendAuthenticationCodeAsync(destinationE164, localCode, cancellationToken);
        if (!sent.Delivered)
        {
            var category = sent.Error == "WHATSAPP_NOT_CONFIGURED"
                ? PhoneVerificationCategories.Configuration
                : PhoneVerificationCategories.Delivery;
            return new SendVerificationResult(false, null, sent.Error, category);
        }

        return new SendVerificationResult(true, sent.ProviderMessageId, null, PhoneVerificationCategories.None);
    }

    public Task<CheckVerificationResult> CheckAsync(string destinationE164, string code, string? externalId, CancellationToken cancellationToken)
    {
        return Task.FromResult(new CheckVerificationResult(false, "LOCAL_CODE", PhoneVerificationCategories.Rejected));
    }
}
