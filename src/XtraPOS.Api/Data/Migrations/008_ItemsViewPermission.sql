-- Grant catalog lookup to every operational role.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = N'Items.View')
BEGIN
    INSERT INTO dbo.Permissions (PermissionCode, PermissionName, Module, IsActive, CreatedAt)
    VALUES (N'Items.View', N'عرض الأصناف', N'Items', 1, SYSUTCDATETIME());
END;

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS role
INNER JOIN dbo.Permissions AS permission
    ON permission.PermissionCode = N'Items.View'
WHERE role.RoleCode IN (N'Admin', N'Supervisor', N'User')
  AND NOT EXISTS (
      SELECT 1
      FROM dbo.RolePermissions AS existing
      WHERE existing.RoleID = role.RoleID
        AND existing.PermissionID = permission.PermissionID);
