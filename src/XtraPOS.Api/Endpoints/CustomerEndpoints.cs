using XtraPOS.Api.Credit;
using XtraPOS.Api.Customers;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

public static class CustomerEndpoints
{
    public static void MapCustomerEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/customers");
        group.MapGet("/classifications", ClassificationsAsync);
        group.MapGet("/price-types", PriceTypesAsync);
        group.MapGet("/calling-codes", CallingCodesAsync);
        group.MapGet("", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("", CreateAsync);
        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapPost("/{id:guid}/activation", ActivationAsync);
        group.MapPost("/phone-verification/send", SendRegistrationAsync);
        group.MapPost("/phone-verification/verify", VerifyRegistrationAsync);
        group.MapPost("/{id:guid}/phone-verification/send", SendVerificationAsync);
        group.MapPost("/{id:guid}/phone-verification/verify", VerifyPhoneAsync);
        group.MapGet("/{id:guid}/special-prices", ListSpecialPricesAsync);
        group.MapPost("/{id:guid}/special-prices", SaveSpecialPriceAsync);
        group.MapPut("/{id:guid}/special-prices/{priceId:guid}", UpdateSpecialPriceAsync);
        group.MapGet("/{id:guid}/credit-policy", ReadCreditPolicyAsync);
        group.MapPut("/{id:guid}/credit-policy", SaveCreditPolicyAsync);
    }

    private static async Task<IResult> ClassificationsAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CustomerMasterService customers,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.CustomersView, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        return Results.Ok(await customers.ListClassificationsAsync(cancellationToken));
    }

    private static async Task<IResult> PriceTypesAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CustomerMasterService customers,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.CustomersView, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        return Results.Ok(await customers.ListPriceTypesAsync(cancellationToken));
    }

    private static async Task<IResult> CallingCodesAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.CustomersView, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        return Results.Ok(CallingCodes.Distinct());
    }

    private static async Task<IResult> ListAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CustomerMasterService customers,
        string? search,
        string? active,
        Guid? classificationId,
        Guid? priceTypeId,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.CustomersView, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        if (active is not (null or "" or "active" or "inactive"))
        {
            return Results.BadRequest(new { message = "تصفية الحالة غير صالحة." });
        }

        bool? isActive = active switch
        {
            "active" => true,
            "inactive" => false,
            _ => null,
        };
        try
        {
            return Results.Ok(await customers.ListAsync(
                new CustomerListFilter(search, isActive, classificationId, priceTypeId, page ?? 1, pageSize ?? 50),
                cancellationToken));
        }
        catch (CustomerMasterException exception)
        {
            return Fail(exception);
        }
    }

    private static async Task<IResult> GetAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CustomerMasterService customers,
        Guid id,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.CustomersView, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        var customer = await customers.GetAsync(id, cancellationToken);
        return customer is null
            ? Results.NotFound(new { message = "الزبون غير موجود." })
            : Results.Ok(customer);
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CustomerMasterService customers,
        CustomerWriteRequest? request,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAnyAsync(http, auth, permissions, [PermissionCodes.CustomersCreate, PermissionCodes.CustomersManage], cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        try
        {
            return Results.Ok(await customers.CreateAsync(caller.UserId, request, cancellationToken));
        }
        catch (CustomerMasterException exception)
        {
            return Fail(exception);
        }
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CustomerMasterService customers,
        Guid id,
        CustomerWriteRequest? request,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.CustomersManage, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        try
        {
            return Results.Ok(await customers.UpdateAsync(caller.UserId, id, request, cancellationToken));
        }
        catch (CustomerMasterException exception)
        {
            return Fail(exception);
        }
    }

    private static async Task<IResult> ActivationAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CustomerMasterService customers,
        Guid id,
        ActivationBody? request,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.CustomersManage, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        if (request is null)
        {
            return Results.BadRequest(new { message = "بيانات التفعيل غير صالحة." });
        }

        try
        {
            return Results.Ok(await customers.SetActiveAsync(caller.UserId, id, request.IsActive, request.RowVersion, cancellationToken));
        }
        catch (CustomerMasterException exception)
        {
            return Fail(exception);
        }
    }

    private static async Task<IResult> SendRegistrationAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        PhoneVerificationService verification,
        RegistrationSendBody? request,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAnyAsync(http, auth, permissions, [PermissionCodes.CustomersCreate, PermissionCodes.CustomersManage], cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        try
        {
            return Results.Ok(await verification.SendRegistrationAsync(request?.CustomerNumber, request?.DestinationE164, cancellationToken));
        }
        catch (CustomerMasterException exception)
        {
            return Fail(exception);
        }
    }

    private static async Task<IResult> VerifyRegistrationAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        PhoneVerificationService verification,
        RegistrationVerifyBody? request,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAnyAsync(http, auth, permissions, [PermissionCodes.CustomersCreate, PermissionCodes.CustomersManage], cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        if (request?.VerificationId is not Guid verificationId)
        {
            return Results.BadRequest(new { message = "طلب التحقق غير موجود." });
        }

        try
        {
            await verification.VerifyRegistrationAsync(verificationId, request.DestinationE164, request.Otp, cancellationToken);
            return Results.Ok(new { status = "Verified", verificationId });
        }
        catch (CustomerMasterException exception)
        {
            return Fail(exception);
        }
    }

    private static async Task<IResult> SendVerificationAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        PhoneVerificationService verification,
        Guid id,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.CustomersManage, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        try
        {
            return Results.Ok(await verification.SendAsync(id, cancellationToken));
        }
        catch (CustomerMasterException exception)
        {
            return Fail(exception);
        }
    }

    private static async Task<IResult> VerifyPhoneAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        PhoneVerificationService verification,
        Guid id,
        PhoneVerifyBody? request,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.CustomersManage, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        try
        {
            await verification.VerifyAsync(id, request?.VerificationId ?? Guid.Empty, request?.Otp, cancellationToken);
            return Results.Ok(new { status = "Verified" });
        }
        catch (CustomerMasterException exception)
        {
            return Fail(exception);
        }
    }

    private static async Task<IResult> ListSpecialPricesAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CustomerItemSpecialPriceService prices,
        Guid id,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.CustomersView, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        try
        {
            return Results.Ok(await prices.ListAsync(id, cancellationToken));
        }
        catch (CustomerMasterException exception)
        {
            return Fail(exception);
        }
    }

    private static Task<IResult> SaveSpecialPriceAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CustomerItemSpecialPriceService prices,
        Guid id,
        CustomerItemSpecialPriceRequest? request,
        CancellationToken cancellationToken)
    {
        return WriteSpecialPriceAsync(http, auth, permissions, prices, id, null, request, cancellationToken);
    }

    private static Task<IResult> UpdateSpecialPriceAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CustomerItemSpecialPriceService prices,
        Guid id,
        Guid priceId,
        CustomerItemSpecialPriceRequest? request,
        CancellationToken cancellationToken)
    {
        return WriteSpecialPriceAsync(http, auth, permissions, prices, id, priceId, request, cancellationToken);
    }

    private static async Task<IResult> WriteSpecialPriceAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        CustomerItemSpecialPriceService prices,
        Guid id,
        Guid? priceId,
        CustomerItemSpecialPriceRequest? request,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.CustomersManage, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        try
        {
            return Results.Ok(await prices.SaveAsync(caller.UserId, id, priceId, request, cancellationToken));
        }
        catch (CustomerMasterException exception)
        {
            return Fail(exception);
        }
    }

    private static IResult Fail(CustomerMasterException exception)
    {
        return Results.Json(new { message = exception.Message }, statusCode: exception.StatusCode);
    }

    private static async Task<IResult> ReadCreditPolicyAsync(HttpContext http, AuthService auth, PermissionService permissions, CreditPolicyService policies, Guid id, CancellationToken cancellationToken)
    {
        var caller = await RequireAnyAsync(http, auth, permissions, CreditPolicyPermissions, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        try
        {
            var policy = await policies.ReadAsync(id, cancellationToken);
            return Results.Ok(policy);
        }
        catch (CreditPolicyException exception) when (exception.Code == "CENTRAL_UNAVAILABLE")
        {
            return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task<IResult> SaveCreditPolicyAsync(HttpContext http, AuthService auth, PermissionService permissions, CreditPolicyService policies, Guid id, CreditPolicyBody? request, CancellationToken cancellationToken)
    {
        var caller = await RequireAnyAsync(http, auth, permissions, CreditPolicyPermissions, cancellationToken);
        if (caller.Result is not null || request is null)
        {
            return caller.Result ?? Results.BadRequest(new { message = "بيانات الائتمان غير مكتملة." });
        }

        CreditPolicyView current;
        try
        {
            current = await policies.ReadAsync(id, cancellationToken);
        }
        catch (CreditPolicyException exception) when (exception.Code == "CENTRAL_UNAVAILABLE")
        {
            return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        if (!current.CentralReachable)
        {
            return Results.Json(new { code = "CENTRAL_UNAVAILABLE", message = "جهة الائتمان المركزية غير متاحة." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var manage = await permissions.HasPermissionAsync(caller.UserId, PermissionCodes.CreditManageLimit, cancellationToken);
        var suspend = await permissions.HasPermissionAsync(caller.UserId, PermissionCodes.CreditSuspend, cancellationToken);
        var administer = await permissions.HasPermissionAsync(caller.UserId, PermissionCodes.SystemAdminister, cancellationToken);
        var write = new CreditPolicyWrite(request.CreditEnabled, request.CreditLimit, request.PaymentTermDays, request.CreditStatus ?? "Inactive", request.RowVersion, request.Reason);
        var denial = CreditPolicyService.Denial(manage, suspend, administer, current, write);
        if (denial is not null)
        {
            return Results.Json(new { message = denial }, statusCode: StatusCodes.Status403Forbidden);
        }

        try
        {
            var actor = await auth.GetPosDeviceContextAsync(http, cancellationToken);
            return Results.Ok(await policies.SaveAsync(id, caller.UserId, actor?.BranchId, write, cancellationToken));
        }
        catch (CreditPolicyException exception)
        {
            var status = exception.Code == "CREDIT_CONFLICT" ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
            return Results.Json(new { code = exception.Code, message = exception.Message }, statusCode: status);
        }
    }

    private static readonly string[] CreditPolicyPermissions = [PermissionCodes.CreditView, PermissionCodes.CreditManageLimit, PermissionCodes.CreditSuspend];

    private sealed class CreditPolicyBody
    {
        public bool CreditEnabled { get; init; }
        public decimal CreditLimit { get; init; }
        public int PaymentTermDays { get; init; }
        public string? CreditStatus { get; init; }
        public string? RowVersion { get; init; }
        public string? Reason { get; init; }
    }

    private static async Task<(IResult? Result, Guid UserId)> RequireAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        string permission,
        CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword)
        {
            return (Results.Unauthorized(), Guid.Empty);
        }

        if (!await permissions.HasPermissionAsync(current.UserId, permission, cancellationToken))
        {
            return (Results.Forbid(), Guid.Empty);
        }

        return (null, current.UserId);
    }

    private static async Task<(IResult? Result, Guid UserId)> RequireAnyAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        IReadOnlyList<string> accepted,
        CancellationToken cancellationToken)
    {
        var current = await auth.GetCurrentAsync(http, cancellationToken);
        if (current is null || current.MustChangePassword)
        {
            return (Results.Unauthorized(), Guid.Empty);
        }

        foreach (var permission in accepted)
        {
            if (await permissions.HasPermissionAsync(current.UserId, permission, cancellationToken))
            {
                return (null, current.UserId);
            }
        }

        return (Results.Forbid(), Guid.Empty);
    }

    private sealed class RegistrationSendBody
    {
        public string? CustomerNumber { get; set; }
        public string? DestinationE164 { get; set; }
    }

    private sealed class RegistrationVerifyBody
    {
        public Guid? VerificationId { get; set; }
        public string? DestinationE164 { get; set; }
        public string? Otp { get; set; }
    }

    private sealed class ActivationBody
    {
        public bool IsActive { get; set; }
        public string? RowVersion { get; set; }
    }

    private sealed class PhoneVerifyBody
    {
        public Guid VerificationId { get; set; }
        public string? Otp { get; set; }
    }
}
