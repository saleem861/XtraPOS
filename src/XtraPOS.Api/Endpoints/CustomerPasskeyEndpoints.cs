using System.Collections.Concurrent;
using XtraPOS.Api.Customers;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

public static class CustomerPasskeyEndpoints
{
    private static readonly ConcurrentDictionary<string, (int Count, DateTime Window)> Rates = new();

    public static void MapCustomerPasskeyEndpoints(this WebApplication app)
    {
        var staff = app.MapGroup("/api/customers/{id:guid}/passkey");
        staff.MapGet("", StatusAsync);
        staff.MapPost("/enrollment", BeginEnrollmentAsync);
        staff.MapPost("/verification", BeginVerificationAsync);
        staff.MapPost("/confirm", ConfirmBindingAsync);
        staff.MapPost("/revoke", RevokeAsync);
        staff.MapGet("/sessions/{sessionId:guid}", PollAsync);

        var phone = app.MapGroup("/api/passkey/{token}");
        phone.MapGet("", ViewAsync);
        phone.MapPost("/phone/send", SendPhoneAsync);
        phone.MapPost("/phone/confirm", ConfirmPhoneAsync);
        phone.MapPost("/registration/options", RegistrationOptionsAsync);
        phone.MapPost("/registration", CompleteRegistrationAsync);
        phone.MapPost("/assertion/options", AssertionOptionsAsync);
        phone.MapPost("/assertion", CompleteAssertionAsync);
    }

    private static async Task<IResult> StatusAsync(HttpContext http, Guid id, AuthService auth, PermissionService permissions, CustomerPasskeyService passkeys, CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, [PermissionCodes.CustomersView, PermissionCodes.CustomersCreate, PermissionCodes.CustomersManage], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        return Results.Ok(await passkeys.StatusAsync(id, cancellationToken));
    }

    private static async Task<IResult> BeginEnrollmentAsync(HttpContext http, Guid id, AuthService auth, PermissionService permissions, CustomerPasskeyService passkeys, CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, [PermissionCodes.CustomersCreate, PermissionCodes.CustomersManage], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        var active = await passkeys.HasActiveAsync(id, cancellationToken);
        if (active && !await permissions.HasPermissionAsync(caller.UserId, PermissionCodes.CustomersManage, cancellationToken))
        {
            return Results.Json(new { code = "FORBIDDEN", message = "تعديل Passkey لزبون موجود يحتاج صلاحية الإدارة." }, statusCode: StatusCodes.Status403Forbidden);
        }

        try
        {
            var started = await passkeys.BeginAsync(caller.UserId, id, "Enroll", active, cancellationToken);
            return Results.Ok(new { started.SessionId, started.Token, started.ExpiresAt, started.UrlPath });
        }
        catch (PasskeyRejectedException exception)
        {
            return Reject(exception);
        }
    }

    private static async Task<IResult> BeginVerificationAsync(HttpContext http, Guid id, AuthService auth, PermissionService permissions, CustomerPasskeyService passkeys, CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, [PermissionCodes.CustomersView, PermissionCodes.CustomersCreate, PermissionCodes.CustomersManage], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        try
        {
            if (!await passkeys.HasActiveAsync(id, cancellationToken))
            {
                return Results.Json(new { code = "NO_PASSKEY", message = "لا يوجد Passkey مفعل." }, statusCode: StatusCodes.Status409Conflict);
            }

            var started = await passkeys.BeginAsync(caller.UserId, id, "Verify", false, cancellationToken);
            return Results.Ok(new { started.SessionId, started.Token, started.ExpiresAt, started.UrlPath });
        }
        catch (PasskeyRejectedException exception)
        {
            return Reject(exception);
        }
    }

    private static async Task<IResult> ConfirmBindingAsync(HttpContext http, Guid id, AuthService auth, PermissionService permissions, CustomerPasskeyService passkeys, CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, [PermissionCodes.CustomersManage, PermissionCodes.CustomersCreate], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        try
        {
            await passkeys.ConfirmBindingAsync(id, caller.UserId, cancellationToken);
            return Results.Ok(new { status = "Active" });
        }
        catch (PasskeyRejectedException exception)
        {
            return Reject(exception);
        }
    }

    private static async Task<IResult> RevokeAsync(HttpContext http, Guid id, AuthService auth, PermissionService permissions, CustomerPasskeyService passkeys, CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, [PermissionCodes.CustomersManage], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        await passkeys.RevokeAsync(caller.UserId, id, cancellationToken);
        return Results.Ok(new { status = "Revoked" });
    }

    private static async Task<IResult> PollAsync(HttpContext http, Guid sessionId, AuthService auth, PermissionService permissions, CustomerPasskeyService passkeys, CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, [PermissionCodes.CustomersView, PermissionCodes.CustomersCreate, PermissionCodes.CustomersManage], cancellationToken);
        if (caller.Result is not null) return caller.Result;
        try
        {
            return Results.Ok(new { status = await passkeys.PollAsync(caller.UserId, sessionId, cancellationToken) });
        }
        catch (PasskeyRejectedException exception)
        {
            return Reject(exception);
        }
    }

    private static async Task<IResult> ViewAsync(HttpContext http, string token, CustomerPasskeyService passkeys, CancellationToken cancellationToken)
    {
        if (!Allow(http)) return Results.StatusCode(StatusCodes.Status429TooManyRequests);
        try
        {
            return Results.Ok(await passkeys.ViewAsync(token, cancellationToken));
        }
        catch (PasskeyRejectedException exception)
        {
            return Reject(exception);
        }
    }

    private static async Task<IResult> SendPhoneAsync(HttpContext http, string token, CustomerPasskeyService passkeys, PhoneVerificationService phones, CancellationToken cancellationToken)
    {
        if (!Allow(http) || !OriginAllowed(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        try
        {
            var customerId = await passkeys.CustomerForTokenAsync(token, "Enroll", cancellationToken);
            var sent = await phones.SendAsync(customerId, cancellationToken);
            return Results.Ok(new { sent.VerificationId, sent.ResendAvailableAt, sent.MaskedDestination });
        }
        catch (PasskeyRejectedException exception)
        {
            return Reject(exception);
        }
        catch (CustomerMasterException exception)
        {
            return Results.Json(new { code = "PHONE", message = exception.Message }, statusCode: exception.StatusCode);
        }
    }

    private static async Task<IResult> ConfirmPhoneAsync(HttpContext http, string token, PhoneBody? body, CustomerPasskeyService passkeys, PhoneVerificationService phones, CancellationToken cancellationToken)
    {
        if (!Allow(http) || !OriginAllowed(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        try
        {
            var customerId = await passkeys.CustomerForTokenAsync(token, "Enroll", cancellationToken);
            await phones.VerifyAsync(customerId, body?.VerificationId ?? Guid.Empty, body?.Otp, cancellationToken);
            await passkeys.MarkPhoneVerifiedAsync(token, cancellationToken);
            return Results.Ok(new { status = "Verified" });
        }
        catch (PasskeyRejectedException exception)
        {
            return Reject(exception);
        }
        catch (CustomerMasterException exception)
        {
            return Results.Json(new { code = "PHONE", message = exception.Message }, statusCode: exception.StatusCode);
        }
    }

    private static async Task<IResult> RegistrationOptionsAsync(HttpContext http, string token, CustomerPasskeyService passkeys, CancellationToken cancellationToken)
    {
        if (!Allow(http) || !OriginAllowed(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        try
        {
            var (challenge, rpId, userHandle) = await passkeys.RegistrationOptionsAsync(token, RelyingParty(http), cancellationToken);
            return Results.Ok(new { challenge, rpId, userHandle, userName = "customer" });
        }
        catch (PasskeyRejectedException exception)
        {
            return Reject(exception);
        }
    }

    private static async Task<IResult> CompleteRegistrationAsync(HttpContext http, string token, CredentialBody? body, CustomerPasskeyService passkeys, CancellationToken cancellationToken)
    {
        if (!Allow(http) || !OriginAllowed(http) || body?.ClientDataJSON is null || body.AttestationObject is null) return Results.StatusCode(StatusCodes.Status400BadRequest);
        try
        {
            await passkeys.CompleteRegistrationAsync(token, Text(body.ClientDataJSON), body.AttestationObject, RelyingParty(http), Origins(http), cancellationToken);
            return Results.Ok(new { status = "Verified" });
        }
        catch (PasskeyRejectedException exception)
        {
            return Reject(exception);
        }
    }

    private static async Task<IResult> AssertionOptionsAsync(HttpContext http, string token, CustomerPasskeyService passkeys, CancellationToken cancellationToken)
    {
        if (!Allow(http) || !OriginAllowed(http)) return Results.StatusCode(StatusCodes.Status403Forbidden);
        try
        {
            var (challenge, credentials) = await passkeys.AssertionOptionsAsync(token, cancellationToken);
            return Results.Ok(new { challenge, rpId = RelyingParty(http), credentials });
        }
        catch (PasskeyRejectedException exception)
        {
            return Reject(exception);
        }
    }

    private static async Task<IResult> CompleteAssertionAsync(HttpContext http, string token, AssertionBody? body, CustomerPasskeyService passkeys, CancellationToken cancellationToken)
    {
        if (!Allow(http) || !OriginAllowed(http) || body is null) return Results.StatusCode(StatusCodes.Status400BadRequest);
        try
        {
            await passkeys.CompleteAssertionAsync(token, body.CredentialId ?? "", Text(body.ClientDataJSON), body.AuthenticatorData ?? "", body.Signature ?? "", RelyingParty(http), Origins(http), cancellationToken);
            return Results.Ok(new { status = "Verified" });
        }
        catch (PasskeyRejectedException exception)
        {
            return Reject(exception);
        }
    }

    private static string RelyingParty(HttpContext http)
    {
        var configured = http.RequestServices.GetRequiredService<IConfiguration>()["Passkey:RpId"];
        return string.IsNullOrWhiteSpace(configured) ? http.Request.Host.Host : configured;
    }

    private static IReadOnlySet<string> Origins(HttpContext http)
    {
        var configured = http.RequestServices.GetRequiredService<IConfiguration>().GetSection("Passkey:Origins").Get<string[]>();
        if (configured is { Length: > 0 }) return configured.ToHashSet(StringComparer.Ordinal);
        return new HashSet<string>(StringComparer.Ordinal) { $"{http.Request.Scheme}://{http.Request.Host}" };
    }

    private static bool OriginAllowed(HttpContext http)
    {
        var origin = http.Request.Headers.Origin.ToString();
        return origin.Length > 0 && Origins(http).Contains(origin);
    }

    private static bool Allow(HttpContext http)
    {
        var key = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var now = DateTime.UtcNow;
        var current = Rates.AddOrUpdate(key, _ => (1, now), (_, value) => now - value.Window > TimeSpan.FromMinutes(1) ? (1, now) : (value.Count + 1, value.Window));
        return current.Count <= 30;
    }

    private static string Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        if (value.TrimStart().StartsWith('{')) return value;
        return System.Text.Encoding.UTF8.GetString(PasskeyCeremony.Decode(value));
    }

    private static IResult Reject(PasskeyRejectedException exception) =>
        Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: exception.Code is "NOT_FOUND" ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict);

    private static async Task<(IResult? Result, Guid UserId)> RequireAsync(HttpContext http, AuthService auth, PermissionService permissions, IReadOnlyList<string> accepted, CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword) return (Results.Unauthorized(), Guid.Empty);
        foreach (var permission in accepted)
        {
            if (await permissions.HasPermissionAsync(current.UserId, permission, cancellationToken)) return (null, current.UserId);
        }

        return (Results.Forbid(), Guid.Empty);
    }

    private sealed class PhoneBody
    {
        public Guid? VerificationId { get; set; }
        public string? Otp { get; set; }
    }

    private sealed class CredentialBody
    {
        public string? ClientDataJSON { get; set; }
        public string? AttestationObject { get; set; }
    }

    private sealed class AssertionBody
    {
        public string? CredentialId { get; set; }
        public string? ClientDataJSON { get; set; }
        public string? AuthenticatorData { get; set; }
        public string? Signature { get; set; }
    }
}
