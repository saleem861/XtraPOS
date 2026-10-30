namespace XtraPOS.Api.Customers;

public interface IWhatsAppVerificationProvider
{
    Task<WhatsAppSendResult> SendAuthenticationCodeAsync(string destinationE164, string code, CancellationToken cancellationToken);
}

public sealed record WhatsAppSendResult(bool Delivered, string? ProviderMessageId, string? Error);

public sealed class UnconfiguredWhatsAppProvider : IWhatsAppVerificationProvider
{
    public Task<WhatsAppSendResult> SendAuthenticationCodeAsync(string destinationE164, string code, CancellationToken cancellationToken)
    {
        return Task.FromResult(new WhatsAppSendResult(false, null, "WHATSAPP_NOT_CONFIGURED"));
    }
}

public sealed class MetaWhatsAppCloudProvider(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<MetaWhatsAppCloudProvider> logger)
    : IWhatsAppVerificationProvider
{
    public async Task<WhatsAppSendResult> SendAuthenticationCodeAsync(string destinationE164, string code, CancellationToken cancellationToken)
    {
        // SenderE164 (+12027739269) identifies the business handset. Meta PhoneNumberId is a separate value and is never derived from that number.
        var token = configuration["WhatsApp:AccessToken"];
        var phoneNumberId = configuration["WhatsApp:PhoneNumberId"];
        var template = configuration["WhatsApp:TemplateName"];
        var language = configuration["WhatsApp:TemplateLanguage"] ?? "ar";
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(phoneNumberId) || string.IsNullOrWhiteSpace(template))
        {
            return new WhatsAppSendResult(false, null, "WHATSAPP_NOT_CONFIGURED");
        }

        var payload = new
        {
            messaging_product = "whatsapp",
            to = destinationE164.TrimStart('+'),
            type = "template",
            template = new
            {
                name = template,
                language = new { code = language },
                components = new object[]
                {
                    new { type = "body", parameters = new object[] { new { type = "text", text = code } } },
                    new { type = "button", sub_type = "url", index = "0", parameters = new object[] { new { type = "text", text = code } } },
                },
            },
        };

        var client = httpClientFactory.CreateClient("WhatsApp");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://graph.facebook.com/v20.0/{phoneNumberId}/messages");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(payload);
        using var response = await client.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("WhatsApp authentication template was rejected with status {StatusCode}.", (int)response.StatusCode);
            return new WhatsAppSendResult(false, null, "WHATSAPP_DELIVERY_FAILED");
        }

        string? messageId = null;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("messages", out var messages) && messages.GetArrayLength() > 0)
            {
                messageId = messages[0].GetProperty("id").GetString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
            messageId = null;
        }

        return new WhatsAppSendResult(true, messageId, null);
    }
}
