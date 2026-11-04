using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XtraPOS.Api.Customers;
using XtraPOS.Api.Data;
using XtraPOS.Api.Diagnostics;
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
        app.MapPost("/api/membership/enrollment-qr", CreateEnrollmentQrAsync);
        app.MapGet("/api/membership/enrollment-qr/{token}", ReadEnrollmentQrAsync);
        app.MapPost("/api/membership/enrollment/prove", ProveAsync);
        app.MapPost("/api/membership/enrollment/commit", CommitAsync);

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
        staff.MapGet("/exception", ReadExceptionAsync);
        staff.MapPost("/exception", MarkExceptionAsync);
        staff.MapPost("/exception/approve", ApproveExceptionAsync);
    }

    private static async Task<IResult> StartAsync(HttpContext http, MembershipAuthService auth, string provider, CancellationToken cancellationToken)
    {
        try
        {
            var enrollment = http.Request.Query["enrollment"].ToString();
            var returnPath = SafeMembershipReturn(http.Request.Query["return"].ToString());
            var start = await auth.BeginAsync(provider, RedirectUri(http, provider), cancellationToken, string.IsNullOrWhiteSpace(enrollment) ? null : enrollment, returnPath);
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
            var back = ReturnFromState(state);
            if (!string.IsNullOrWhiteSpace(result.SessionSecret))
            {
                http.Response.Cookies.Append(MembershipService.SessionCookie, result.SessionSecret, SessionCookie(http));
                return Results.Redirect(back ?? "/membership/ready");
            }
            if (back is not null && result.Code == "MEMBERSHIP_OWNERSHIP_PROOF_REQUIRED")
            {
                return Results.Redirect(back + "?unlinked=1");
            }
            if (result.Code == "MEMBERSHIP_OWNERSHIP_PROOF_REQUIRED" && Guid.TryParse(result.ReturnPath, out var attemptId))
            {
                return Results.Redirect($"/membership/ready?pending={attemptId}");
            }
            return Results.Json(new { code = result.Code ?? "MEMBERSHIP_OWNERSHIP_PROOF_REQUIRED" }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (MembershipException exception)
        {
            return Results.Json(new { code = exception.Code }, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> ReadEnrollmentQrAsync(string token, HttpContext http, AuthService auth, MembershipService membership, CancellationToken cancellationToken)
    {
        if (await auth.GetCurrentAsync(http, cancellationToken) is null) return Results.Unauthorized();
        try
        {
            var read = await membership.ReadEnrollmentQrAsync(token, cancellationToken);
            return Results.Ok(new { status = read.Status, provider = read.Provider, display = read.Display, attemptId = read.AttemptId });
        }
        catch (MembershipException exception)
        {
            return Results.Json(new { code = exception.Code, message = Arabic(exception.Code) }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> CreateEnrollmentQrAsync(HttpContext http, AuthService auth, MembershipService membership, EnrollmentQrRequest? request, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null) return Results.Unauthorized();
        var device = await auth.GetPosDeviceContextAsync(http, cancellationToken);
        var businessId = device?.BusinessId ?? await auth.GetDeviceBusinessIdAsync(http, cancellationToken);
        if (businessId is null || businessId == Guid.Empty)
        {
            DiagnosticLog.Write(http.RequestServices.GetRequiredService<IHostEnvironment>(), "enrollment-qr", "no business id for the signed-in device");
            return Results.Json(new { code = "MEMBERSHIP_SERVER_UNAVAILABLE", message = "تعذر تحديد نشاط هذا الجهاز." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        try
        {
            var created = await membership.CreateEnrollmentQrAsync(businessId.Value, current.UserId, request?.CustomerId is Guid id && id != Guid.Empty ? id : null, cancellationToken);
            DiagnosticLog.Write(http.RequestServices.GetRequiredService<IHostEnvironment>(), "enrollment-qr", "created");
            return Results.Ok(new { token = created.Token, expiresAtUtc = created.ExpiresAtUtc });
        }
        catch (MembershipException exception)
        {
            DiagnosticLog.Write(http.RequestServices.GetRequiredService<IHostEnvironment>(), "enrollment-qr", exception.Code);
            return Results.Json(new { code = exception.Code, message = Arabic(exception.Code) }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write(http.RequestServices.GetRequiredService<IHostEnvironment>(), "enrollment-qr", exception.GetType().Name + " " + exception.Message);
            return Results.Json(new { message = "تعذر توليد رمز العضوية. التفاصيل في سجل التشخيص." }, statusCode: StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<IResult> ProveAsync(MembershipAuthService auth, MembershipProveRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || request.AttemptId == Guid.Empty || request.VerificationId == Guid.Empty) return Results.BadRequest(new { code = "MEMBERSHIP_OWNERSHIP_PROOF_REQUIRED" });
        try
        {
            await auth.ProveOwnershipAsync(request.AttemptId, request.VerificationId, request.Destination, request.Otp, cancellationToken);
            return Results.Ok(new { pending = true, linked = false });
        }
        catch (MembershipException exception)
        {
            return Results.Json(new { code = exception.Code }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (CustomerMasterException)
        {
            return Results.Json(new { code = "MEMBERSHIP_OWNERSHIP_PROOF_REQUIRED" }, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> CommitAsync(HttpContext http, AuthService authService, MembershipAuthService auth, MembershipCommitRequest? request, CancellationToken cancellationToken)
    {
        var current = await authService.GetCurrentAsync(http, cancellationToken);
        if (current is null) return Results.Unauthorized();
        if (request is null || request.AttemptId == Guid.Empty || request.CustomerId == Guid.Empty) return Results.BadRequest(new { code = "MEMBERSHIP_OWNERSHIP_PROOF_REQUIRED" });
        try
        {
            await auth.CommitNewCustomerAsync(request.AttemptId, request.CustomerId, cancellationToken);
            return Results.Ok(new { linked = true });
        }
        catch (MembershipException exception)
        {
            DiagnosticLog.Write(http.RequestServices.GetRequiredService<IHostEnvironment>(), "customer-identity", exception.Code);
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

    private static async Task<IResult> ReadExceptionAsync(Guid id, HttpContext http, AuthService auth, SqlConnectionFactory connections, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null) return Results.Unauthorized();
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand("SELECT MembershipExceptionAt, MembershipExceptionApprovedAt FROM dbo.Customers WHERE CustomerID = @id;", connection);
        command.Parameters.AddWithValue("@id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return Results.NotFound();
        var marked = !reader.IsDBNull(0);
        var approved = !reader.IsDBNull(1);
        return Results.Ok(new { pending = marked && !approved });
    }

    private static async Task<IResult> MarkExceptionAsync(Guid id, HttpContext http, AuthService auth, PermissionService permissions, SqlConnectionFactory connections, MembershipService membership, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null) return Results.Unauthorized();
        var allowed = current.Permissions.Contains(PermissionCodes.SystemAdminister, StringComparer.Ordinal) || await permissions.HasPermissionAsync(current.UserId, PermissionCodes.CustomersManage, cancellationToken);
        if (!allowed) return Results.Forbid();
        await WriteExceptionAsync(connections, membership, id, current.UserId, approve: false, cancellationToken);
        return Results.Ok(new { pending = true });
    }

    private static async Task<IResult> ApproveExceptionAsync(Guid id, HttpContext http, AuthService auth, PermissionService permissions, SqlConnectionFactory connections, MembershipService membership, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null) return Results.Unauthorized();
        if (!await permissions.HasPermissionAsync(current.UserId, PermissionCodes.SystemAdminister, cancellationToken) && !current.Permissions.Contains(PermissionCodes.SystemAdminister, StringComparer.Ordinal)) return Results.Forbid();
        await WriteExceptionAsync(connections, membership, id, current.UserId, approve: true, cancellationToken);
        return Results.Ok(new { pending = false });
    }

    private static async Task WriteExceptionAsync(SqlConnectionFactory connections, MembershipService membership, Guid customerId, Guid actorId, bool approve, CancellationToken cancellationToken)
    {
        var branchSql = approve
            ? "UPDATE dbo.Customers SET MembershipExceptionApprovedAt = SYSUTCDATETIME(), UpdatedAt = SYSUTCDATETIME() WHERE CustomerID = @id AND MembershipExceptionAt IS NOT NULL;"
            : "UPDATE dbo.Customers SET MembershipExceptionAt = SYSUTCDATETIME(), MembershipExceptionByUserID = @actor, MembershipExceptionApprovedAt = NULL, UpdatedAt = SYSUTCDATETIME() WHERE CustomerID = @id;";
        await using (var connection = new SqlConnection(connections.CreateBranchConnectionString()))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = new SqlCommand(branchSql, connection);
            command.Parameters.AddWithValue("@id", customerId);
            command.Parameters.AddWithValue("@actor", actorId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        try
        {
            await using var central = await membership.OpenCentralAsync(cancellationToken);
            await using var command = new SqlCommand(branchSql, central);
            command.Parameters.AddWithValue("@id", customerId);
            command.Parameters.AddWithValue("@actor", actorId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MembershipException)
        {
        }
    }

    private static string? SafeMembershipReturn(string? value)
    {
        const string prefix = "/membership/verify/";
        if (string.IsNullOrWhiteSpace(value) || !value.StartsWith(prefix, StringComparison.Ordinal)) return null;
        if (value.Contains('?') || value.Contains('#') || value.Contains('\\') || value.Contains("//")) return null;
        var token = value[prefix.Length..];
        if (token.Length is < 20 or > 128 || token.Any(item => !char.IsLetterOrDigit(item) && item != '-' && item != '_')) return null;
        return value;
    }

    private static string? ReturnFromState(string? state)
    {
        if (string.IsNullOrWhiteSpace(state)) return null;
        var bar = state.IndexOf('|');
        if (bar < 0 || bar == state.Length - 1) return null;
        return SafeMembershipReturn(state[(bar + 1)..]);
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
        if (device is null || request is null || request.SaleContextId == Guid.Empty)
        {
            DiagnosticLog.Write(http.RequestServices.GetRequiredService<IHostEnvironment>(), "pos-qr", "rejected");
            return Results.Unauthorized();
        }

        try
        {
            var created = await membership.CreateAsync(device.BusinessId, device.BranchId, device.StationId, device.UserId, request.CustomerId ?? Guid.Empty, request.SaleContextId, cancellationToken);
            DiagnosticLog.Write(http.RequestServices.GetRequiredService<IHostEnvironment>(), "pos-qr", "created");
            return Results.Ok(new { verificationRequestId = created.VerificationRequestId, token = created.Token, expiresAtUtc = created.ExpiresAtUtc, pollingIntervalMs = created.PollingIntervalMs });
        }
        catch (MembershipException exception)
        {
            DiagnosticLog.Write(http.RequestServices.GetRequiredService<IHostEnvironment>(), "pos-qr", exception.Code);
            return Results.Json(new { code = exception.Code, message = Arabic(exception.Code) }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write(http.RequestServices.GetRequiredService<IHostEnvironment>(), "pos-qr", exception.GetType().Name + " " + exception.Message);
            return Results.Json(new { message = "تعذر إنشاء رمز العضوية. التفاصيل في سجل التشخيص." }, statusCode: StatusCodes.Status500InternalServerError);
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
        public Guid? CustomerId { get; set; }
        public Guid SaleContextId { get; set; }
    }

    private sealed class MembershipSaleCancelRequest
    {
        public Guid SaleContextId { get; set; }
    }

    private sealed class EnrollmentQrRequest
    {
        public Guid? CustomerId { get; set; }
    }

    private sealed class MembershipProveRequest
    {
        public Guid AttemptId { get; set; }
        public Guid VerificationId { get; set; }
        public string? Destination { get; set; }
        public string? Otp { get; set; }
    }

    private sealed class MembershipCommitRequest
    {
        public Guid AttemptId { get; set; }
        public Guid CustomerId { get; set; }
    }

    private sealed class MembershipLinkRequest
    {
        public Guid AttemptId { get; set; }
        public Guid CustomerId { get; set; }
        public Guid VerificationId { get; set; }
        public string? Otp { get; set; }
    }
}
