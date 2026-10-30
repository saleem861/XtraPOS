-- Normalize system roles in place.
-- RoleID values stay the same so UserRoles and RolePermissions remain valid.
-- Safe to run again: obsolete codes are renamed once, then the statements match nothing.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

UPDATE dbo.Roles
SET
    RoleCode = N'Supervisor',
    RoleName = N'مشرف',
    UpdatedAt = SYSUTCDATETIME()
WHERE IsSystemRole = 1
  AND RoleCode = N'Manager'
  AND NOT EXISTS (
      SELECT 1
      FROM dbo.Roles AS existing
      WHERE existing.RoleCode = N'Supervisor');

UPDATE dbo.Roles
SET
    RoleCode = N'User',
    RoleName = N'مستخدم',
    UpdatedAt = SYSUTCDATETIME()
WHERE IsSystemRole = 1
  AND RoleCode = N'Cashier'
  AND NOT EXISTS (
      SELECT 1
      FROM dbo.Roles AS existing
      WHERE existing.RoleCode = N'User');

INSERT INTO dbo.UserRoles (UserID, RoleID, AssignedAt)
SELECT sourceRoles.UserID, target.RoleID, sourceRoles.AssignedAt
FROM dbo.UserRoles AS sourceRoles
INNER JOIN dbo.Roles AS source
    ON source.RoleID = sourceRoles.RoleID
   AND source.IsSystemRole = 1
   AND source.RoleCode = N'Manager'
INNER JOIN dbo.Roles AS target
    ON target.RoleCode = N'Supervisor'
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.UserRoles AS existing
    WHERE existing.UserID = sourceRoles.UserID
      AND existing.RoleID = target.RoleID);

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT target.RoleID, granted.PermissionID, granted.GrantedAt
FROM dbo.RolePermissions AS granted
INNER JOIN dbo.Roles AS source
    ON source.RoleID = granted.RoleID
   AND source.IsSystemRole = 1
   AND source.RoleCode = N'Manager'
INNER JOIN dbo.Roles AS target
    ON target.RoleCode = N'Supervisor'
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.RolePermissions AS existing
    WHERE existing.RoleID = target.RoleID
      AND existing.PermissionID = granted.PermissionID);

DELETE assigned
FROM dbo.UserRoles AS assigned
INNER JOIN dbo.Roles AS source
    ON source.RoleID = assigned.RoleID
   AND source.IsSystemRole = 1
   AND source.RoleCode = N'Manager'
WHERE EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleCode = N'Supervisor');

DELETE granted
FROM dbo.RolePermissions AS granted
INNER JOIN dbo.Roles AS source
    ON source.RoleID = granted.RoleID
   AND source.IsSystemRole = 1
   AND source.RoleCode = N'Manager'
WHERE EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleCode = N'Supervisor');

DELETE FROM dbo.Roles
WHERE IsSystemRole = 1
  AND RoleCode = N'Manager'
  AND EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleCode = N'Supervisor');

INSERT INTO dbo.UserRoles (UserID, RoleID, AssignedAt)
SELECT sourceRoles.UserID, target.RoleID, sourceRoles.AssignedAt
FROM dbo.UserRoles AS sourceRoles
INNER JOIN dbo.Roles AS source
    ON source.RoleID = sourceRoles.RoleID
   AND source.IsSystemRole = 1
   AND source.RoleCode = N'Cashier'
INNER JOIN dbo.Roles AS target
    ON target.RoleCode = N'User'
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.UserRoles AS existing
    WHERE existing.UserID = sourceRoles.UserID
      AND existing.RoleID = target.RoleID);

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT target.RoleID, granted.PermissionID, granted.GrantedAt
FROM dbo.RolePermissions AS granted
INNER JOIN dbo.Roles AS source
    ON source.RoleID = granted.RoleID
   AND source.IsSystemRole = 1
   AND source.RoleCode = N'Cashier'
INNER JOIN dbo.Roles AS target
    ON target.RoleCode = N'User'
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.RolePermissions AS existing
    WHERE existing.RoleID = target.RoleID
      AND existing.PermissionID = granted.PermissionID);

DELETE assigned
FROM dbo.UserRoles AS assigned
INNER JOIN dbo.Roles AS source
    ON source.RoleID = assigned.RoleID
   AND source.IsSystemRole = 1
   AND source.RoleCode = N'Cashier'
WHERE EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleCode = N'User');

DELETE granted
FROM dbo.RolePermissions AS granted
INNER JOIN dbo.Roles AS source
    ON source.RoleID = granted.RoleID
   AND source.IsSystemRole = 1
   AND source.RoleCode = N'Cashier'
WHERE EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleCode = N'User');

DELETE FROM dbo.Roles
WHERE IsSystemRole = 1
  AND RoleCode = N'Cashier'
  AND EXISTS (SELECT 1 FROM dbo.Roles WHERE RoleCode = N'User');

UPDATE dbo.Roles
SET RoleName = N'مدير النظام', UpdatedAt = SYSUTCDATETIME()
WHERE IsSystemRole = 1
  AND RoleCode = N'Admin'
  AND RoleName <> N'مدير النظام';

UPDATE dbo.Roles
SET RoleName = N'مشرف', UpdatedAt = SYSUTCDATETIME()
WHERE IsSystemRole = 1
  AND RoleCode = N'Supervisor'
  AND RoleName <> N'مشرف';

UPDATE dbo.Roles
SET RoleName = N'مستخدم', UpdatedAt = SYSUTCDATETIME()
WHERE IsSystemRole = 1
  AND RoleCode = N'User'
  AND RoleName <> N'مستخدم';

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_Users_EmployeeNumber'
      AND object_id = OBJECT_ID(N'dbo.Users'))
BEGIN
    CREATE UNIQUE INDEX UX_Users_EmployeeNumber
        ON dbo.Users (EmployeeNumber)
        WHERE EmployeeNumber IS NOT NULL;
END
