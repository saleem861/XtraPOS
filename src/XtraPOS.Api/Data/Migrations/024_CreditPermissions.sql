-- Branch permission codes for central credit. Does not store debt balances.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

INSERT INTO dbo.Permissions (PermissionCode, PermissionName, Module, IsActive, CreatedAt)
SELECT code.PermissionCode, code.PermissionName, N'Credit', 1, SYSUTCDATETIME()
FROM (VALUES
    (N'Credit.View', N'عرض حساب الدين'),
    (N'Credit.Create', N'بيع بالدين'),
    (N'Credit.Collect', N'تسديد دين'),
    (N'Credit.ManageLimit', N'تعديل حد الائتمان'),
    (N'Credit.Suspend', N'إيقاف الحساب الائتماني'),
    (N'Credit.Reverse', N'عكس حركة دين'),
    (N'Credit.Audit', N'عرض تدقيق الديون')
) AS code(PermissionCode, PermissionName)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.Permissions AS existing WHERE existing.PermissionCode = code.PermissionCode);

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS role
INNER JOIN dbo.Permissions AS permission
    ON permission.PermissionCode IN (
        N'Credit.View', N'Credit.Create', N'Credit.Collect',
        N'Credit.ManageLimit', N'Credit.Suspend', N'Credit.Reverse', N'Credit.Audit')
WHERE role.RoleCode = N'Admin'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.RolePermissions AS existing
      WHERE existing.RoleID = role.RoleID AND existing.PermissionID = permission.PermissionID);

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS role
INNER JOIN dbo.Permissions AS permission
    ON permission.PermissionCode IN (N'Credit.View', N'Credit.Create', N'Credit.Collect', N'Credit.Audit')
WHERE role.RoleCode = N'Supervisor'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.RolePermissions AS existing
      WHERE existing.RoleID = role.RoleID AND existing.PermissionID = permission.PermissionID);

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS role
INNER JOIN dbo.Permissions AS permission
    ON permission.PermissionCode IN (N'Credit.View', N'Credit.Create', N'Credit.Collect')
WHERE role.RoleCode = N'User'
  AND NOT EXISTS (
      SELECT 1 FROM dbo.RolePermissions AS existing
      WHERE existing.RoleID = role.RoleID AND existing.PermissionID = permission.PermissionID);
