using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using XtraPOS.Api.Customers;

namespace XtraPOS.Api.Tests;

public class TwilioVerifyTests
{
    [Fact]
    public async Task Send_posts_sms_to_the_verify_service()
    {
        string? url = null;
        string? body = null;
        string? scheme = null;
        var provider = Provider(request =>
        {
            url = request.RequestUri!.AbsoluteUri;
            body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            scheme = request.Headers.Authorization!.Scheme;
            return Json(HttpStatusCode.Created, """{"sid":"VE123","status":"pending"}""");
        }, apiKey: false);
        var result = await provider.StartAsync("+972592594707", "should-not-be-sent", CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal("VE123", result.ExternalId);
        Assert.Contains("/v2/Services/VA123/Verifications", url);
        Assert.Contains("To=%2B972592594707", body);
        Assert.Contains("Channel=sms", body);
        Assert.DoesNotContain("should-not-be-sent", body);
        Assert.Equal("Basic", scheme);
    }

    [Fact]
    public async Task Check_accepts_only_approved()
    {
        var approved = Provider(_ => Json(HttpStatusCode.OK, """{"status":"approved"}"""));
        var pending = Provider(_ => Json(HttpStatusCode.OK, """{"status":"pending"}"""));
        Assert.True((await approved.CheckAsync("+972592594707", "1234", "VE123", CancellationToken.None)).Approved);
        Assert.False((await pending.CheckAsync("+972592594707", "1234", "VE123", CancellationToken.None)).Approved);
    }

    [Fact]
    public void Recipient_restriction_distinguishes_trial_from_compliance_and_does_not_retry()
    {
        var trial = TwilioVerifyErrors.Category(21608, 403, "During trial, the recipient must be a verified tester.");
        Assert.Equal(PhoneVerificationCategories.TrialRecipient, trial);
        Assert.False(PhoneVerificationRules.AlternatePrefixAllowed(trial, false));
        Assert.Contains("تجريبي", PhoneVerificationMessages.ForCategory(trial));

        var compliance = TwilioVerifyErrors.Category(21608, 403);
        Assert.Equal(PhoneVerificationCategories.ComplianceProfileRequired, compliance);
        Assert.False(PhoneVerificationRules.AlternatePrefixAllowed(compliance, false));
        var message = PhoneVerificationMessages.ForCategory(compliance);
        Assert.Equal("تعذر إرسال رمز التحقق حاليًا بسبب إعدادات حساب خدمة الرسائل.", message);
        Assert.DoesNotContain("تجريبي", message);
        Assert.DoesNotContain("SID", message);
        Assert.NotEqual(PhoneVerificationMessages.Destination, message);
    }

    [Fact]
    public void Configuration_and_auth_errors_do_not_allow_prefix_retry()
    {
        Assert.Equal(PhoneVerificationCategories.Authentication, TwilioVerifyErrors.Category(20003, 401));
        Assert.Equal(PhoneVerificationCategories.Destination, TwilioVerifyErrors.Category(21211, 400));
        Assert.False(PhoneVerificationRules.AlternatePrefixAllowed(PhoneVerificationCategories.Authentication, false));
        Assert.False(PhoneVerificationRules.AlternatePrefixAllowed(PhoneVerificationCategories.Configuration, false));
        Assert.True(PhoneVerificationRules.AlternatePrefixAllowed(PhoneVerificationCategories.Destination, false));
        Assert.False(PhoneVerificationRules.AlternatePrefixAllowed(PhoneVerificationCategories.Destination, true));
    }

    [Fact]
    public void Local_expiry_resend_and_attempt_limits_stay_in_force()
    {
        var created = new DateTime(2026, 10, 25, 0, 0, 0, DateTimeKind.Utc);
        Assert.False(PhoneVerificationRules.Expired(created.AddMinutes(5), created.AddMinutes(4)));
        Assert.True(PhoneVerificationRules.Expired(created.AddMinutes(5), created.AddMinutes(5).AddSeconds(1)));
        Assert.True(PhoneVerificationRules.ResendBlocked(created.AddSeconds(60), created.AddSeconds(30)));
        Assert.False(PhoneVerificationRules.ResendBlocked(created.AddSeconds(60), created.AddSeconds(60)));
        Assert.False(PhoneVerificationRules.AttemptsExhausted(4, 5));
        Assert.True(PhoneVerificationRules.AttemptsExhausted(5, 5));
    }

    private static TwilioVerifyProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> respond, bool apiKey = true)
    {
        var values = new Dictionary<string, string?>
        {
            ["TwilioVerify:AccountSid"] = "ACaccount",
            ["TwilioVerify:ServiceSid"] = "VA123",
            ["TwilioVerify:Channel"] = "sms",
        };
        if (apiKey)
        {
            values["TwilioVerify:ApiKeySid"] = "SKkey";
            values["TwilioVerify:ApiKeySecret"] = "secret";
        }
        else
        {
            values["TwilioVerify:AuthToken"] = "token";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new TwilioVerifyProvider(new SingleClientFactory(new HttpClient(new StubHandler(respond))), configuration, NullLogger<TwilioVerifyProvider>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
