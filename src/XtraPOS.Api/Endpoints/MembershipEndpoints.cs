using XtraPOS.Api.Membership;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

public static class MembershipEndpoints
{
    public static void MapMembershipEndpoints(this WebApplication app)
    {
        app.MapGet("/api/membership/config", (MembershipService membership) => Results.Ok(new
        {
            enabled = membership.Options.Enabled,
            googleLoginEnabled = membership.Options.GoogleLoginEnabled && !string.IsNullOrWhiteSpace(membership.Options.GoogleClientId),
            appleLoginEnabled = membership.Options.AppleLoginEnabled && !string.IsNullOrWhiteSpace(membership.Options.AppleClientId),
            requireVerificationForSpecialPrices = membership.Options.RequireVerificationForSpecialPrices,
            pollingIntervalMs = membership.Options.VerificationPollingIntervalMs
        }));

        app.MapGet("/api/membership/auth/google/start", (HttpContext http, MembershipAuthService auth, CancellationToken cancellationToken) => StartAsync(http, auth, "Google", cancellationToken));
        app.MapGet("/api/membership/auth/google/callback", (HttpContext http, MembershipAuthService auth, CancellationToken cancellationToken) => CallbackAsync(http, auth, "Google", cancellationToken));
        app.MapGet("/api/membership/auth/apple/start", (HttpContext http, MembershipAuthService auth, CancellationToken cancellationToken) => StartAsync(http, auth, "Apple", cancellationToken));
        app.MapPost("/api/membership/auth/apple/callback", (HttpContext http, MembershipAuthService auth, CancellationToken cancellationToken) => CallbackAsync(http, auth, "Apple", cancellationToken));
        app.MapPost("/api/membership/enrollment/link", LinkAsync);

        app.MapGet("/api/membership/verify/{token}", (string token, HttpContext http, MembershipService membership, CancellationToken cancellationToken) =>
        {
            var csrf = MembershipTokens.CreateOpaqueToken();
            http.Response.Cookies.Append(MembershipService.CsrfCookie, csrf, new CookieOptions { HttpOnly = false, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, IsEssential = true });
            return membership.PublicStatusAsync(token, cancellationToken);
        });

        app.MapPost("/api/membership/verify/{token}/complete", async (string token, HttpContext http, MembershipService membership, CancellationToken cancellationToken) =>
        {
            var origin = http.Request.Headers.Origin.ToString();
            if (!string.IsNullOrWhiteSpace(origin) && !string.Equals(origin, $"{http.Request.Scheme}://{http.Request.Host}", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new { code = "MEMBERSHIP_CSRF" }, statusCode: StatusCodes.Status400BadRequest);
            }
            var csrf = http.Request.Headers["X-Membership-CSRF"].ToString();
            if (string.IsNullOrWhiteSpace(csrf) || !http.Request.Cookies.TryGetValue(MembershipService.CsrfCookie, out var cookie) || !string.Equals(csrf, cookie, StringComparison.Ordinal))
            {
                return Results.Json(new { code = "MEMBERSHIP_CSRF" }, statusCode: StatusCodes.Status400BadRequest);
            }

            http.Request.Cookies.TryGetValue(MembershipService.SessionCookie, out var session);
            try
            {
                await membership.CompleteAsync(token, session, cancellationToken);
                return Results.Ok(new { status = "Verified" });
            }
            catch (MembershipException exception)
            {
                return Results.Json(new { code = exception.Code, message = Arabic(exception.Code) }, statusCode: StatusCodes.Status409Conflict);
            }
        });

        var pos = app.MapGroup("/api/pos/membership-verifications");
        pos.MapPost("", CreateAsync);
        pos.MapGet("/{id:guid}", ReadAsync);
        pos.MapPost("/{id:guid}/cancel", CancelAsync);
        pos.MapPost("/cancel-sale", CancelSaleAsync);

        var staff = app.MapGroup("/api/customers/{id:guid}/membership");
        staff.MapGet("", SummaryAsync);
        staff.MapPost("/revoke-all", RevokeAllAsync);
        staff.MapPost("/unlink", UnlinkAsync);
    }

    private static async Task<IResult> StartAsync(HttpContext http, MembershipAuthService auth, string provider, CancellationToken cancellationToken)
    {
        try
        {
            var start = await auth.BeginAsync(provider, RedirectUri(http, provider), cancellationToken);
            return Results.Redirect(start.AuthorizeUrl);
        }
        catch (MembershipException exception)
        {
            return Results.Json(new { code = exception.Code, provider }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> CallbackAsync(HttpContext http, MembershipAuthService auth, string provider, CancellationToken cancellationToken)
    {
        var form = http.Request.HasFormContentType ? await http.Request.ReadFormAsync(cancellationToken) : null;
        var code = http.Request.Query["code"].ToString();
        var state = http.Request.Query["state"].ToString();
        var idToken = http.Request.Query["id_token"].ToString();
        if (form is not null)
        {
            if (string.IsNullOrWhiteSpace(code)) code = form["code"].ToString();
            if (string.IsNullOrWhiteSpace(state)) state = form["state"].ToString();
            if (string.IsNullOrWhiteSpace(idToken)) idToken = form["id_token"].ToString();
        }
        try
        {
            var result = await auth.FinishAsync(provider, code, idToken, state, RedirectUri(http, provider), cancellationToken);
            if (!string.IsNullOrWhiteSpace(result.SessionSecret))
            {
                http.Response.Cookies.Append(MembershipService.SessionCookie, result.SessionSecret, SessionCookie(http));
                return Results.Redirect("/membership/ready");
            }
            return Results.Json(new { code = result.Code ?? "MEMBERSHIP_OWNERSHIP_PROOF_REQUIRED" }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (MembershipException exception)
        {
            return Results.Json(new { code = exception.Code }, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> LinkAsync(HttpContext http, MembershipAuthService auth, MembershipLinkRequest? request, CancellationToken cancellationToken)
    {
        if (request is null) return Results.BadRequest(new { code = "MEMBERSHIP_OWNERSHIP_PROOF_REQUIRED" });
        try
        {
            var secret = await auth.LinkAsync(request.AttemptId, request.CustomerId, request.VerificationId, request.Otp, cancellationToken);
            http.Response.Cookies.Append(MembershipService.SessionCookie, secret, SessionCookie(http));
            return Results.Ok(new { linked = true });
        }
        catch (MembershipException exception)
        {
            var status = exception.Code == "BLOCKED_EXTERNAL_CONFIG" ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status409Conflict;
            return Results.Json(new { code = exception.Code }, statusCode: status);
        }
    }

    private static string RedirectUri(HttpContext http, string provider)
    {
        var configured = http.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptions<MembershipOptions>>().Value.PublicOrigin;
        var origin = string.IsNullOrWhiteSpace(configured) ? $"{http.Request.Scheme}://{http.Request.Host}" : configured.TrimEnd('/');
        var path = provider == "Apple" ? "/api/membership/auth/apple/callback" : "/api/membership/auth/google/callback";
        return origin + path;
    }

    private static CookieOptions SessionCookie(HttpContext http) => new()
    {
        HttpOnly = true,
        Secure = http.Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = "/"
    };

    private static IResult ProviderUnavailable(bool enabled, string? clientId, string provider)
    {
        if (enabled && !string.IsNullOrWhiteSpace(clientId))
        {
            return Results.Json(new { code = "BLOCKED_EXTERNAL_CONFIG", provider, message = "Redirect URI and signing keys are not registered." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        return Results.Json(new { code = "BLOCKED_EXTERNAL_CONFIG", provider }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }

    private static async Task<IResult> CreateAsync(HttpContext http, AuthService auth, MembershipService membership, MembershipCreateRequest? request, CancellationToken cancellationToken)
    {
        var device = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        if (device is null || request is null || request.CustomerId == Guid.Empty || request.SaleContextId == Guid.Empty)
        {
            return Results.Unauthorized();
        }

        try
        {
            var created = await membership.CreateAsync(device.BusinessId, device.BranchId, device.StationId, device.UserId, request.CustomerId, request.SaleContextId, cancellationToken);
            return Results.Ok(new { verificationRequestId = created.VerificationRequestId, token = created.Token, expiresAtUtc = created.ExpiresAtUtc, pollingIntervalMs = created.PollingIntervalMs });
        }
        catch (MembershipException exception)
        {
            return Results.Json(new { code = exception.Code, message = Arabic(exception.Code) }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> ReadAsync(Guid id, HttpContext http, AuthService auth, MembershipService membership, CancellationToken cancellationToken)
    {
        var device = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        if (device is null) return Results.Unauthorized();
        var view = await membership.ReadForPosAsync(device.BusinessId, device.BranchId, device.StationId, id, cancellationToken);
        return view is null ? Results.NotFound(new { code = "MEMBERSHIP_REQUEST_NOT_FOUND" }) : Results.Ok(view);
    }

    private static async Task<IResult> CancelAsync(Guid id, HttpContext http, AuthService auth, MembershipService membership, CancellationToken cancellationToken)
    {
        var device = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        if (device is null) return Results.Unauthorized();
        await membership.CancelAsync(device.BusinessId, device.BranchId, device.StationId, id, cancellationToken);
        return Results.Ok(new { status = "Cancelled" });
    }

    private static async Task<IResult> CancelSaleAsync(HttpContext http, AuthService auth, MembershipService membership, MembershipSaleCancelRequest? request, CancellationToken cancellationToken)
    {
        var device = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        if (device is null || request is null || request.SaleContextId == Guid.Empty) return Results.Unauthorized();
        await membership.CancelSaleAsync(device.BusinessId, device.BranchId, device.StationId, request.SaleContextId, cancellationToken);
        return Results.Ok(new { status = "Cancelled" });
    }

    private static async Task<IResult> SummaryAsync(Guid id, HttpContext http, AuthService auth, PermissionService permissions, MembershipService membership, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null) return Results.Unauthorized();
        if (!current.Permissions.Contains(PermissionCodes.CustomersView, StringComparer.Ordinal)) return Results.Forbid();
        return Results.Ok(await membership.SummaryAsync(id, cancellationToken));
    }

    private static async Task<IResult> RevokeAllAsync(Guid id, HttpContext http, AuthService auth, PermissionService permissions, MembershipService membership, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null) return Results.Unauthorized();
        if (!await permissions.HasPermissionAsync(current.UserId, PermissionCodes.CustomersManage, cancellationToken)) return Results.Forbid();
        await membership.RevokeAllAsync(id, cancellationToken);
        return Results.Ok(new { revoked = true });
    }

    private static async Task<IResult> UnlinkAsync(Guid id, HttpContext http, AuthService auth, PermissionService permissions, MembershipService membership, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null) return Results.Unauthorized();
        if (!await permissions.HasPermissionAsync(current.UserId, PermissionCodes.CustomersManage, cancellationToken)) return Results.Forbid();
        try
        {
            await membership.UnlinkAsync(id, cancellationToken);
            return Results.Ok(new { unlinked = true });
        }
        catch (MembershipException exception)
        {
            return Results.Json(new { code = exception.Code, message = Arabic(exception.Code) }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static string Arabic(string code) => code switch
    {
        "MEMBERSHIP_QR_EXPIRED" => "انتهت صلاحية الرمز. اطلب رمزًا جديدًا من الكاشير.",
        "MEMBERSHIP_WRONG_CUSTOMER" => "هذا الحساب لا يطابق الزبون المحدد.",
        "MEMBERSHIP_SESSION_REQUIRED" => "يلزم تسجيل الدخول إلى العضوية.",
        "MEMBERSHIP_SERVER_UNAVAILABLE" => "التحقق من العضوية غير متاح.",
        _ => "تعذر التحقق من العضوية."
    };

    private sealed class MembershipCreateRequest
    {
        public Guid CustomerId { get; set; }
        public Guid SaleContextId { get; set; }
    }

    private sealed class MembershipSaleCancelRequest
    {
        public Guid SaleContextId { get; set; }
    }

    private sealed class MembershipLinkRequest
    {
        public Guid AttemptId { get; set; }
        public Guid CustomerId { get; set; }
        public Guid VerificationId { get; set; }
        public string? Otp { get; set; }
    }
}
