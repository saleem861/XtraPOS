namespace XtraPOS.Api.Data;

public sealed class Business
{
    public Guid BusinessId { get; set; }
    public string BusinessName { get; set; } = "";
    public string? BusinessDisplayName { get; set; }
    public string? Description { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public ICollection<Branch> Branches { get; set; } = new List<Branch>();
}

public sealed class Branch
{
    public Guid BranchId { get; set; }
    public Guid BusinessId { get; set; }
    public string? BranchCode { get; set; }
    public string BranchName { get; set; } = "";
    public string BranchType { get; set; } = "";
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Business Business { get; set; } = null!;
    public ICollection<PosStation> Stations { get; set; } = new List<PosStation>();
    public ICollection<Device> Devices { get; set; } = new List<Device>();
}

public sealed class PosStation
{
    public Guid StationId { get; set; }
    public Guid BranchId { get; set; }
    public int StationNumber { get; set; }
    public string StationName { get; set; } = "";
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Branch Branch { get; set; } = null!;
    public ICollection<Device> Devices { get; set; } = new List<Device>();
}

public sealed class Device
{
    public Guid DeviceId { get; set; }
    public Guid BranchId { get; set; }
    public Guid? StationId { get; set; }
    public string DeviceName { get; set; } = "";
    public string DeviceType { get; set; } = "";
    public byte[]? DeviceTokenHash { get; set; }
    public bool IsActive { get; set; }
    public DateTime RegisteredAt { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Branch Branch { get; set; } = null!;
    public PosStation? Station { get; set; }
}

public sealed class SchemaMigrationRecord
{
    public string MigrationId { get; set; } = "";
    public string MigrationName { get; set; } = "";
    public string? ApplicationVersion { get; set; }
    public DateTime AppliedAt { get; set; }
}

public sealed class SystemSetting
{
    public string SettingKey { get; set; } = "";
    public string? SettingValue { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class AppUser
{
    public Guid UserId { get; set; }
    public string Username { get; set; } = "";
    public string NormalizedUsername { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? FullName { get; set; }
    public string? EmployeeNumber { get; set; }
    public string PasswordHash { get; set; } = "";
    public bool IsActive { get; set; }
    public bool MustChangePassword { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTime? LockoutEndAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public ICollection<UserBranchAccess> BranchAccess { get; set; } = new List<UserBranchAccess>();
}

public sealed class Role
{
    public Guid RoleId { get; set; }
    public string RoleCode { get; set; } = "";
    public string RoleName { get; set; } = "";
    public string? Description { get; set; }
    public bool IsSystemRole { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}

public sealed class Permission
{
    public Guid PermissionId { get; set; }
    public string PermissionCode { get; set; } = "";
    public string PermissionName { get; set; } = "";
    public string Module { get; set; } = "";
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}

public sealed class UserRole
{
    public Guid UserId { get; set; }
    public Guid RoleId { get; set; }
    public DateTime AssignedAt { get; set; }
    public AppUser User { get; set; } = null!;
    public Role Role { get; set; } = null!;
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public Guid PermissionId { get; set; }
    public DateTime GrantedAt { get; set; }
    public Role Role { get; set; } = null!;
    public Permission Permission { get; set; } = null!;
}

public sealed class UserBranchAccess
{
    public Guid UserId { get; set; }
    public Guid BranchId { get; set; }
    public bool IsActive { get; set; }
    public DateTime GrantedAt { get; set; }
    public AppUser User { get; set; } = null!;
    public Branch Branch { get; set; } = null!;
}

public sealed class UserLoginAudit
{
    public long LoginAuditId { get; set; }
    public Guid? UserId { get; set; }
    public Guid? DeviceId { get; set; }
    public string UsernameAttempted { get; set; } = "";
    public bool Succeeded { get; set; }
    public string? FailureReason { get; set; }
    public DateTime OccurredAt { get; set; }
    public string? ClientInfo { get; set; }
}
