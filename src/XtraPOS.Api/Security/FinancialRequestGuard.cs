namespace XtraPOS.Api.Security;

public static class FinancialRequestGuard
{
    public static IResult? RejectUntrusted(HttpContext http, IConfiguration configuration)
    {
        var fetchSite = http.Request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite.Equals("cross-site", StringComparison.OrdinalIgnoreCase))
        {
            return Rejected("CROSS_SITE_REJECTED");
        }

        var origin = http.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            return Rejected("MISSING_ORIGIN");
        }

        var allowed = AllowedOrigins(http, configuration);
        allowed.Add($"{http.Request.Scheme}://{http.Request.Host.Value}");
        if (!allowed.Contains(origin))
        {
            return Rejected("UNTRUSTED_ORIGIN");
        }

        return null;
    }

    private static IResult Rejected(string code) =>
        Results.Json(new { code }, statusCode: StatusCodes.Status403Forbidden);

    private static HashSet<string> AllowedOrigins(HttpContext http, IConfiguration configuration)
    {
        _ = http;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "Financial:TrustedOrigins", "CreditCollections:TrustedOrigins", "CreditReversals:TrustedOrigins" })
        {
            foreach (var item in configuration.GetSection(key).Get<string[]>() ?? [])
            {
                if (!string.IsNullOrWhiteSpace(item)) allowed.Add(item);
            }
        }

        return allowed;
    }
}
