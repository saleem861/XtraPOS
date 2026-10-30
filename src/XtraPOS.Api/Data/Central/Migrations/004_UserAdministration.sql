-- Central user administration permission and audit.
-- Does not store passwords. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = N'Users.ResetPassword')
BEGIN
    INSERT INTO dbo.Permissions (PermissionID, PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
    VALUES (NEWID(), N'Users.ResetPassword', N'إعادة تعيين كلمة المرور', N'Users', N'إصدار كلمة مرور مؤقتة لمستخدم', 1, SYSUTCDATETIME());
END;

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS role
INNER JOIN dbo.Permissions AS permission
    ON permission.PermissionCode = N'Users.ResetPassword'
WHERE role.RoleCode = N'Admin'
  AND NOT EXISTS (
      SELECT 1
      FROM dbo.RolePermissions AS existing
      WHERE existing.RoleID = role.RoleID
        AND existing.PermissionID = permission.PermissionID);

IF OBJECT_ID(N'dbo.UserAdministrationAudit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.UserAdministrationAudit
    (
        AuditID uniqueidentifier NOT NULL CONSTRAINT PK_UserAdministrationAudit PRIMARY KEY,
        ActorUserID uniqueidentifier NOT NULL,
        TargetUserID uniqueidentifier NOT NULL,
        Action nvarchar(50) NOT NULL,
        OccurredAt datetime2(0) NOT NULL CONSTRAINT DF_UserAdministrationAudit_OccurredAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_UserAdministrationAudit_Actor FOREIGN KEY (ActorUserID) REFERENCES dbo.Users (UserID),
        CONSTRAINT FK_UserAdministrationAudit_Target FOREIGN KEY (TargetUserID) REFERENCES dbo.Users (UserID)
    );
END;

IF EXISTS (
    SELECT EmployeeNumber
    FROM dbo.Users
    WHERE EmployeeNumber IS NOT NULL
    GROUP BY EmployeeNumber
    HAVING COUNT(*) > 1)
BEGIN
    THROW 51013, N'Duplicate employee numbers must be resolved before user administration.', 1;
END;

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_Users_EmployeeNumber'
      AND object_id = OBJECT_ID(N'dbo.Users'))
BEGIN
    CREATE UNIQUE INDEX UX_Users_EmployeeNumber
        ON dbo.Users (EmployeeNumber)
        WHERE EmployeeNumber IS NOT NULL;
END;
