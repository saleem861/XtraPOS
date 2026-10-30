namespace XtraPOS.Api.Security;

public static class AuthClaimTypes
{
    public const string UserId = "user_id";
    public const string Username = "username";
    public const string DeviceId = "device_id";
}

public sealed class LoginRequest
{
    public string? Username { get; set; }
    public string? Password { get; set; }
}

public sealed class ChangePasswordRequest
{
    public string? CurrentPassword { get; set; }
    public string? NewPassword { get; set; }
    public string? ConfirmPassword { get; set; }
}

public sealed class CurrentUserResponse
{
    public Guid UserId { get; init; }
    public string Username { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? FullName { get; init; }
    public IReadOnlyList<RoleInfo> Roles { get; init; } = [];
    public IReadOnlyList<string> Permissions { get; init; } = [];
    public string BusinessName { get; init; } = "";
    public string? BusinessDisplayName { get; init; }
    public string BranchName { get; init; } = "";
    public string StationName { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public bool MustChangePassword { get; init; }
}

public sealed record RoleInfo(string RoleCode, string RoleName);

public sealed record CatalogCaller(
    Guid UserId,
    Guid BranchId,
    bool CanViewItems,
    bool CanEditMaster,
    bool CanEditBranchState,
    bool CanEditBranchPrice);

public sealed record PosDeviceContext(
    Guid UserId,
    string DisplayName,
    Guid BusinessId,
    Guid BranchId,
    Guid StationId,
    Guid DeviceId);

public sealed class LoginResult
{
    public bool Succeeded { get; init; }
    public string? Message { get; init; }
    public CurrentUserResponse? User { get; init; }

    public static LoginResult Ok(CurrentUserResponse user) => new() { Succeeded = true, User = user };

    public static LoginResult Fail(string message) => new() { Succeeded = false, Message = message };
}
