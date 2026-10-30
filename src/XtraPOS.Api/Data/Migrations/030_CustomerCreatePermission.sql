IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = N'Customers.Create')
BEGIN
    INSERT INTO dbo.Permissions (PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
    VALUES (N'Customers.Create', N'إضافة زبون', N'Customers', N'إنشاء زبون جديد بعد التحقق من الهاتف', 1, SYSUTCDATETIME());
END;

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS role
INNER JOIN dbo.Permissions AS permission
    ON (
        (role.RoleCode IN (N'Admin', N'Supervisor', N'User') AND permission.PermissionCode = N'Customers.Create')
        OR (role.RoleCode = N'User' AND permission.PermissionCode = N'Customers.View'))
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.RolePermissions AS existing
    WHERE existing.RoleID = role.RoleID
      AND existing.PermissionID = permission.PermissionID);
