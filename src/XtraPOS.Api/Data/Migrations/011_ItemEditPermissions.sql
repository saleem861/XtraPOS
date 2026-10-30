-- Item edit permissions. Items.View stays as the read permission.
-- User does not receive edit permissions.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = N'Items.EditMaster')
BEGIN
    INSERT INTO dbo.Permissions (PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
    VALUES (N'Items.EditMaster', N'تعديل البيانات الأساسية للصنف', N'Items', N'تغيير البيانات المركزية للصنف', 1, SYSUTCDATETIME());
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = N'Items.EditBranchState')
BEGIN
    INSERT INTO dbo.Permissions (PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
    VALUES (N'Items.EditBranchState', N'تعديل بيانات الفرع للصنف', N'Items', N'تغيير التكلفة والتوفر وحالة الفرع', 1, SYSUTCDATETIME());
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = N'Items.EditBranchPrice')
BEGIN
    INSERT INTO dbo.Permissions (PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
    VALUES (N'Items.EditBranchPrice', N'تعديل سعر الفرع', N'Items', N'تغيير سعر هذا الفرع فقط', 1, SYSUTCDATETIME());
END;

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS role
INNER JOIN dbo.Permissions AS permission
    ON permission.PermissionCode IN (N'Items.EditMaster', N'Items.EditBranchState', N'Items.EditBranchPrice')
WHERE role.RoleCode IN (N'Admin', N'Supervisor')
  AND NOT EXISTS (
      SELECT 1
      FROM dbo.RolePermissions AS existing
      WHERE existing.RoleID = role.RoleID
        AND existing.PermissionID = permission.PermissionID);
