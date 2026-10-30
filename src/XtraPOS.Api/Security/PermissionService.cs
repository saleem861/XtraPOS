using Microsoft.EntityFrameworkCore;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Security;

public sealed class PermissionService(BranchDbContext db)
{
    public async Task<bool> HasPermissionAsync(Guid userId, string permissionCode, CancellationToken cancellationToken)
    {
        var codes = await GetPermissionCodesAsync(userId, cancellationToken);
        return codes.Contains(PermissionCodes.SystemAdminister, StringComparer.Ordinal)
            || codes.Contains(permissionCode, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> GetPermissionCodesAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await db.UserRoles
            .Where(item => item.UserId == userId && item.Role.IsActive)
            .SelectMany(item => item.Role.RolePermissions)
            .Where(item => item.Permission.IsActive)
            .Select(item => item.Permission.PermissionCode)
            .Distinct()
            .ToListAsync(cancellationToken);
    }
}
