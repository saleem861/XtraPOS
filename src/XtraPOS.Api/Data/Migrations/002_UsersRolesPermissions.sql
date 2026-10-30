-- Users, roles, and permissions.
-- Text columns inherit the database collation (Arabic_CI_AS).
-- Do not add column-level COLLATE clauses.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

CREATE TABLE dbo.Users
(
    UserID uniqueidentifier NOT NULL
        CONSTRAINT DF_Users_UserID DEFAULT (NEWSEQUENTIALID()),
    Username nvarchar(100) NOT NULL,
    NormalizedUsername nvarchar(100) NOT NULL,
    DisplayName nvarchar(150) NOT NULL,
    FullName nvarchar(300) NULL,
    EmployeeNumber nvarchar(50) NULL,
    PasswordHash nvarchar(1000) NOT NULL,
    IsActive bit NOT NULL
        CONSTRAINT DF_Users_IsActive DEFAULT (1),
    MustChangePassword bit NOT NULL
        CONSTRAINT DF_Users_MustChangePassword DEFAULT (0),
    FailedLoginCount int NOT NULL
        CONSTRAINT DF_Users_FailedLoginCount DEFAULT (0),
    LockoutEndAt datetime2(0) NULL,
    LastLoginAt datetime2(0) NULL,
    CreatedAt datetime2(0) NOT NULL
        CONSTRAINT DF_Users_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt datetime2(0) NULL,
    CONSTRAINT PK_Users PRIMARY KEY (UserID),
    CONSTRAINT UQ_Users_NormalizedUsername UNIQUE (NormalizedUsername),
    CONSTRAINT CK_Users_FailedLoginCount CHECK (FailedLoginCount >= 0)
);

CREATE TABLE dbo.Roles
(
    RoleID uniqueidentifier NOT NULL
        CONSTRAINT DF_Roles_RoleID DEFAULT (NEWSEQUENTIALID()),
    RoleCode nvarchar(50) NOT NULL,
    RoleName nvarchar(100) NOT NULL,
    Description nvarchar(500) NULL,
    IsSystemRole bit NOT NULL
        CONSTRAINT DF_Roles_IsSystemRole DEFAULT (0),
    IsActive bit NOT NULL
        CONSTRAINT DF_Roles_IsActive DEFAULT (1),
    CreatedAt datetime2(0) NOT NULL
        CONSTRAINT DF_Roles_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt datetime2(0) NULL,
    CONSTRAINT PK_Roles PRIMARY KEY (RoleID),
    CONSTRAINT UQ_Roles_RoleCode UNIQUE (RoleCode)
);

CREATE TABLE dbo.Permissions
(
    PermissionID uniqueidentifier NOT NULL
        CONSTRAINT DF_Permissions_PermissionID DEFAULT (NEWSEQUENTIALID()),
    PermissionCode nvarchar(100) NOT NULL,
    PermissionName nvarchar(150) NOT NULL,
    Module nvarchar(100) NOT NULL,
    Description nvarchar(500) NULL,
    IsActive bit NOT NULL
        CONSTRAINT DF_Permissions_IsActive DEFAULT (1),
    CreatedAt datetime2(0) NOT NULL
        CONSTRAINT DF_Permissions_CreatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_Permissions PRIMARY KEY (PermissionID),
    CONSTRAINT UQ_Permissions_PermissionCode UNIQUE (PermissionCode)
);

CREATE TABLE dbo.UserRoles
(
    UserID uniqueidentifier NOT NULL,
    RoleID uniqueidentifier NOT NULL,
    AssignedAt datetime2(0) NOT NULL
        CONSTRAINT DF_UserRoles_AssignedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_UserRoles PRIMARY KEY (UserID, RoleID),
    CONSTRAINT FK_UserRoles_Users FOREIGN KEY (UserID) REFERENCES dbo.Users (UserID),
    CONSTRAINT FK_UserRoles_Roles FOREIGN KEY (RoleID) REFERENCES dbo.Roles (RoleID)
);

CREATE TABLE dbo.RolePermissions
(
    RoleID uniqueidentifier NOT NULL,
    PermissionID uniqueidentifier NOT NULL,
    GrantedAt datetime2(0) NOT NULL
        CONSTRAINT DF_RolePermissions_GrantedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_RolePermissions PRIMARY KEY (RoleID, PermissionID),
    CONSTRAINT FK_RolePermissions_Roles FOREIGN KEY (RoleID) REFERENCES dbo.Roles (RoleID),
    CONSTRAINT FK_RolePermissions_Permissions FOREIGN KEY (PermissionID) REFERENCES dbo.Permissions (PermissionID)
);

CREATE TABLE dbo.UserBranchAccess
(
    UserID uniqueidentifier NOT NULL,
    BranchID uniqueidentifier NOT NULL,
    IsActive bit NOT NULL
        CONSTRAINT DF_UserBranchAccess_IsActive DEFAULT (1),
    GrantedAt datetime2(0) NOT NULL
        CONSTRAINT DF_UserBranchAccess_GrantedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_UserBranchAccess PRIMARY KEY (UserID, BranchID),
    CONSTRAINT FK_UserBranchAccess_Users FOREIGN KEY (UserID) REFERENCES dbo.Users (UserID),
    CONSTRAINT FK_UserBranchAccess_Branches FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID)
);

CREATE TABLE dbo.UserLoginAudit
(
    LoginAuditID bigint IDENTITY(1, 1) NOT NULL,
    UserID uniqueidentifier NULL,
    DeviceID uniqueidentifier NULL,
    UsernameAttempted nvarchar(100) NOT NULL,
    Succeeded bit NOT NULL,
    FailureReason nvarchar(100) NULL,
    OccurredAt datetime2(0) NOT NULL
        CONSTRAINT DF_UserLoginAudit_OccurredAt DEFAULT (SYSUTCDATETIME()),
    ClientInfo nvarchar(300) NULL,
    CONSTRAINT PK_UserLoginAudit PRIMARY KEY (LoginAuditID),
    CONSTRAINT FK_UserLoginAudit_Users FOREIGN KEY (UserID) REFERENCES dbo.Users (UserID),
    CONSTRAINT FK_UserLoginAudit_Devices FOREIGN KEY (DeviceID) REFERENCES dbo.Devices (DeviceID)
);

CREATE INDEX IX_UserLoginAudit_OccurredAt ON dbo.UserLoginAudit (OccurredAt);
CREATE INDEX IX_UserLoginAudit_UserID ON dbo.UserLoginAudit (UserID);
CREATE INDEX IX_UserLoginAudit_DeviceID ON dbo.UserLoginAudit (DeviceID);

INSERT INTO dbo.Roles (RoleCode, RoleName, IsSystemRole, IsActive, CreatedAt)
VALUES
    (N'Admin', N'مدير النظام', 1, 1, SYSUTCDATETIME()),
    (N'Manager', N'مدير', 1, 1, SYSUTCDATETIME()),
    (N'Cashier', N'كاشير', 1, 1, SYSUTCDATETIME());

INSERT INTO dbo.Permissions (PermissionCode, PermissionName, Module, IsActive, CreatedAt)
VALUES
    (N'System.Administer', N'إدارة النظام', N'System', 1, SYSUTCDATETIME()),
    (N'Users.View', N'عرض المستخدمين', N'Users', 1, SYSUTCDATETIME()),
    (N'Users.Manage', N'إدارة المستخدمين', N'Users', 1, SYSUTCDATETIME()),
    (N'Roles.View', N'عرض الأدوار', N'Roles', 1, SYSUTCDATETIME()),
    (N'Roles.Manage', N'إدارة الأدوار', N'Roles', 1, SYSUTCDATETIME()),
    (N'Device.View', N'عرض الأجهزة', N'Device', 1, SYSUTCDATETIME()),
    (N'Setup.View', N'عرض الإعداد', N'Setup', 1, SYSUTCDATETIME());

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT r.RoleID, p.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS r
CROSS JOIN dbo.Permissions AS p
WHERE r.RoleCode = N'Admin';

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT r.RoleID, p.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS r
INNER JOIN dbo.Permissions AS p
    ON p.PermissionCode IN (N'Users.View', N'Roles.View', N'Device.View', N'Setup.View')
WHERE r.RoleCode = N'Manager';
