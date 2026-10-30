using XtraPOS.Api.Security;

namespace XtraPOS.Api.Endpoints;

public static class UserEndpoints
{
    public static void MapUserEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/users");
        group.MapGet("", ListAsync);
        group.MapGet("/catalog", CatalogAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("", CreateAsync);
        group.MapPut("/{id:guid}", UpdateAsync);
        group.MapPost("/{id:guid}/activation", ActivationAsync);
        group.MapPut("/{id:guid}/role", RoleAsync);
        group.MapPut("/{id:guid}/branches", BranchesAsync);
        group.MapPost("/{id:guid}/reset-password", ResetAsync);
    }

    private static async Task<IResult> ListAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        UserAdministrationService users,
        string? search,
        string? active,
        string? role,
        Guid? branchId,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.UsersView, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        bool? isActive = active switch
        {
            "active" => true,
            "inactive" => false,
            null or "" => null,
            _ => null,
        };
        if (active is not (null or "" or "active" or "inactive"))
        {
            return Results.BadRequest(new { message = "تصفية الحالة غير صالحة." });
        }

        try
        {
            return Results.Ok(await users.ListAsync(new UserListFilter(search, isActive, role, branchId), cancellationToken));
        }
        catch (UserAdministrationException exception)
        {
            return Fail(exception);
        }
    }

    private static async Task<IResult> CatalogAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        UserAdministrationService users,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.UsersView, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        var catalog = await users.CatalogAsync(cancellationToken);
        var canManage = await permissions.HasPermissionAsync(caller.UserId, PermissionCodes.UsersManage, cancellationToken);
        var canReset = await permissions.HasPermissionAsync(caller.UserId, PermissionCodes.UsersResetPassword, cancellationToken);
        return Results.Ok(new
        {
            catalog.Authority,
            canManage,
            canReset,
            catalog.Roles,
            catalog.Branches,
        });
    }

    private static async Task<IResult> GetAsync(
        HttpContext http,
        Guid id,
        AuthService auth,
        PermissionService permissions,
        UserAdministrationService users,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.UsersView, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        var user = await users.GetAsync(id, cancellationToken);
        return user is null ? Results.NotFound(new { message = "المستخدم غير موجود." }) : Results.Ok(user);
    }

    private static async Task<IResult> CreateAsync(
        HttpContext http,
        AuthService auth,
        PermissionService permissions,
        UserAdministrationService users,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.UsersManage, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        CreateManagedUserRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<CreateManagedUserRequest>(cancellationToken);
        }
        catch (Exception)
        {
            return Results.BadRequest(new { message = "بيانات المستخدم غير صالحة." });
        }

        try
        {
            var issued = await users.CreateAsync(caller.UserId, request, cancellationToken);
            var created = await users.GetAsync(issued.UserId, cancellationToken);
            return Results.Ok(new
            {
                user = created,
                temporaryPassword = issued.TemporaryPassword,
            });
        }
        catch (UserAdministrationException exception)
        {
            return Fail(exception);
        }
    }

    private static async Task<IResult> UpdateAsync(
        HttpContext http,
        Guid id,
        AuthService auth,
        PermissionService permissions,
        UserAdministrationService users,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.UsersManage, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        UpdateManagedUserRequest? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<UpdateManagedUserRequest>(cancellationToken);
        }
        catch (Exception)
        {
            return Results.BadRequest(new { message = "بيانات المستخدم غير صالحة." });
        }

        return await RunAsync(() => users.UpdateAsync(caller.UserId, id, request, cancellationToken), users, id, cancellationToken);
    }

    private static async Task<IResult> ActivationAsync(
        HttpContext http,
        Guid id,
        AuthService auth,
        PermissionService permissions,
        UserAdministrationService users,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.UsersManage, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        var request = await http.Request.ReadFromJsonAsync<ActivationBody>(cancellationToken);
        if (request is null)
        {
            return Results.BadRequest(new { message = "بيانات التفعيل غير صالحة." });
        }

        return await RunAsync(() => users.SetActiveAsync(caller.UserId, id, request.IsActive, cancellationToken), users, id, cancellationToken);
    }

    private static async Task<IResult> RoleAsync(
        HttpContext http,
        Guid id,
        AuthService auth,
        PermissionService permissions,
        UserAdministrationService users,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.UsersManage, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        var request = await http.Request.ReadFromJsonAsync<RoleBody>(cancellationToken);
        return await RunAsync(() => users.SetRoleAsync(caller.UserId, id, request?.RoleCode, cancellationToken), users, id, cancellationToken);
    }

    private static async Task<IResult> BranchesAsync(
        HttpContext http,
        Guid id,
        AuthService auth,
        PermissionService permissions,
        UserAdministrationService users,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.UsersManage, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        var request = await http.Request.ReadFromJsonAsync<BranchBody>(cancellationToken);
        return await RunAsync(() => users.SetBranchesAsync(caller.UserId, id, request?.BranchIds, cancellationToken), users, id, cancellationToken);
    }

    private static async Task<IResult> ResetAsync(
        HttpContext http,
        Guid id,
        AuthService auth,
        PermissionService permissions,
        UserAdministrationService users,
        CancellationToken cancellationToken)
    {
        var caller = await RequireAsync(http, auth, permissions, PermissionCodes.UsersResetPassword, cancellationToken);
        if (caller.Result is not null)
        {
            return caller.Result;
        }

        ResetBody? request;
        try
        {
            request = await http.Request.ReadFromJsonAsync<ResetBody>(cancellationToken);
        }
        catch (Exception)
        {
            return Results.BadRequest(new { message = "بيانات إعادة التعيين غير صالحة." });
        }

        try
        {
            var issued = await users.ResetPasswordAsync(caller.UserId, id, request?.PasswordMode, request?.Password, cancellationToken);
            var user = await users.GetAsync(id, cancellationToken);
            return Results.Ok(new { user, temporaryPassword = issued.TemporaryPassword });
        }
        catch (UserAdministrationException exception)
        {
            return Fail(exception);
        }
    }

    private static async Task<IResult> RunAsync(Func<Task> action, UserAdministrationService users, Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await action();
            return Results.Ok(await users.GetAsync(id, cancellationToken));
        }
        catch (UserAdministrationException exception)
        {
            return Fail(exception);
        }
    }

    private static IResult Fail(UserAdministrationException exception)
    {
        return Results.Json(new { message = exception.Message }, statusCode: exception.StatusCode);
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

    private sealed class ActivationBody
    {
        public bool IsActive { get; set; }
    }

    private sealed class RoleBody
    {
        public string? RoleCode { get; set; }
    }

    private sealed class BranchBody
    {
        public Guid[]? BranchIds { get; set; }
    }

    private sealed class ResetBody
    {
        public string? PasswordMode { get; set; }
        public string? Password { get; set; }
    }
}
