-- Central foundation. Shared master data only.
-- Customers is a future centrally-owned entity and is intentionally not created here.
-- No branch inventory, stock, sales, devices, or POS sessions.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

CREATE TABLE dbo.DatabaseSchemaHistory
(
    MigrationID nvarchar(100) NOT NULL,
    MigrationName nvarchar(200) NOT NULL,
    ApplicationVersion nvarchar(50) NULL,
    AppliedAt datetime2(0) NOT NULL
        CONSTRAINT DF_DatabaseSchemaHistory_AppliedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_DatabaseSchemaHistory PRIMARY KEY (MigrationID)
);

CREATE TABLE dbo.SystemSettings
(
    SettingKey nvarchar(100) NOT NULL,
    SettingValue nvarchar(max) NULL,
    UpdatedAt datetime2(0) NOT NULL
        CONSTRAINT DF_SystemSettings_UpdatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_SystemSettings PRIMARY KEY (SettingKey)
);

INSERT INTO dbo.SystemSettings (SettingKey, SettingValue)
VALUES (N'DatabaseRole', N'Central');

CREATE TABLE dbo.DataImportHistory
(
    ImportID uniqueidentifier NOT NULL CONSTRAINT PK_DataImportHistory PRIMARY KEY,
    ImportType nvarchar(64) NOT NULL,
    Source nvarchar(128) NOT NULL,
    StartedAt datetime2(3) NOT NULL,
    CompletedAt datetime2(3) NULL,
    Status nvarchar(32) NOT NULL,
    RowsProcessed int NOT NULL,
    Notes nvarchar(400) NULL
);

CREATE TABLE dbo.SyncEntityOwnership
(
    EntityType nvarchar(64) NOT NULL CONSTRAINT PK_SyncEntityOwnership PRIMARY KEY,
    OwnerRole nvarchar(20) NOT NULL,
    IsImplemented bit NOT NULL
);

INSERT INTO dbo.SyncEntityOwnership (EntityType, OwnerRole, IsImplemented)
VALUES
    (N'Businesses', N'Central', 1),
    (N'Branches', N'Central', 1),
    (N'Items', N'Central', 1),
    (N'ItemUnits', N'Central', 1),
    (N'PriceTypes', N'Central', 1),
    (N'ItemPrices', N'Central', 1),
    (N'Users', N'Central', 1),
    (N'Roles', N'Central', 1),
    (N'Permissions', N'Central', 1),
    (N'RolePermissions', N'Central', 1),
    (N'UserRoles', N'Central', 1),
    (N'UserBranchAccess', N'Central', 1);

CREATE TABLE dbo.SyncChangeLog
(
    ChangeSequence bigint IDENTITY(1, 1) NOT NULL CONSTRAINT PK_SyncChangeLog PRIMARY KEY,
    EntityType nvarchar(64) NOT NULL,
    EntityId uniqueidentifier NOT NULL,
    Operation nvarchar(16) NOT NULL,
    Payload nvarchar(max) NULL,
    CreatedAt datetime2(3) NOT NULL
        CONSTRAINT DF_SyncChangeLog_CreatedAt DEFAULT (SYSUTCDATETIME())
);

CREATE TABLE dbo.Businesses
(
    BusinessID uniqueidentifier NOT NULL CONSTRAINT PK_Businesses PRIMARY KEY,
    BusinessName nvarchar(200) NOT NULL,
    BusinessDisplayName nvarchar(200) NULL,
    Description nvarchar(500) NULL,
    Phone nvarchar(50) NULL,
    Address nvarchar(500) NULL,
    IsActive bit NOT NULL,
    CreatedAt datetime2(0) NOT NULL,
    UpdatedAt datetime2(0) NULL
);

CREATE TABLE dbo.Branches
(
    BranchID uniqueidentifier NOT NULL CONSTRAINT PK_Branches PRIMARY KEY,
    BusinessID uniqueidentifier NOT NULL,
    BranchCode nvarchar(50) NULL,
    BranchName nvarchar(200) NOT NULL,
    BranchType nvarchar(30) NOT NULL,
    IsActive bit NOT NULL,
    CreatedAt datetime2(0) NOT NULL,
    UpdatedAt datetime2(0) NULL,
    CONSTRAINT FK_Branches_Businesses FOREIGN KEY (BusinessID) REFERENCES dbo.Businesses (BusinessID)
);

CREATE TABLE dbo.Items
(
    ItemID uniqueidentifier NOT NULL CONSTRAINT PK_Items PRIMARY KEY,
    ItemNumber nvarchar(100) NOT NULL,
    ItemName nvarchar(500) NOT NULL,
    ItemType nvarchar(100) NULL,
    Manufacturer nvarchar(100) NULL,
    Color nvarchar(100) NULL,
    Taste nvarchar(100) NULL,
    Size nvarchar(100) NULL,
    Packing nvarchar(100) NULL,
    Classification nvarchar(200) NULL,
    CountryOfOrigin nvarchar(100) NULL,
    ProductType nvarchar(50) NULL,
    ParentItemID uniqueidentifier NULL,
    IsProhibited bit NOT NULL,
    IsPublishedOnline bit NOT NULL,
    IsActive bit NOT NULL,
    CreatedAt datetime2(0) NOT NULL,
    UpdatedAt datetime2(0) NOT NULL,
    CONSTRAINT UQ_Items_ItemNumber UNIQUE (ItemNumber),
    CONSTRAINT FK_Items_Parent FOREIGN KEY (ParentItemID) REFERENCES dbo.Items (ItemID)
);

CREATE TABLE dbo.ItemUnits
(
    ItemUnitID uniqueidentifier NOT NULL CONSTRAINT PK_ItemUnits PRIMARY KEY,
    ItemID uniqueidentifier NOT NULL,
    UnitsPerPackage decimal(18, 4) NOT NULL,
    InternalUnitItemID uniqueidentifier NULL,
    InternalUnitItemNumber nvarchar(100) NULL,
    InternalUnitItemName nvarchar(500) NULL,
    IsBaseUnit bit NOT NULL,
    IsActive bit NOT NULL,
    CreatedAt datetime2(0) NOT NULL,
    UpdatedAt datetime2(0) NOT NULL,
    CONSTRAINT FK_ItemUnits_Items FOREIGN KEY (ItemID) REFERENCES dbo.Items (ItemID)
);

CREATE TABLE dbo.PriceTypes
(
    PriceTypeID uniqueidentifier NOT NULL CONSTRAINT PK_PriceTypes PRIMARY KEY,
    PriceCode nvarchar(50) NOT NULL,
    PriceName nvarchar(100) NOT NULL,
    SortOrder int NOT NULL,
    IsActive bit NOT NULL,
    CreatedAt datetime2(0) NOT NULL,
    CONSTRAINT UQ_PriceTypes_PriceCode UNIQUE (PriceCode)
);

CREATE TABLE dbo.ItemPrices
(
    ItemPriceID uniqueidentifier NOT NULL CONSTRAINT PK_ItemPrices PRIMARY KEY,
    ItemID uniqueidentifier NOT NULL,
    PriceTypeID uniqueidentifier NOT NULL,
    Price decimal(19, 4) NOT NULL,
    IsActive bit NOT NULL,
    CreatedAt datetime2(0) NOT NULL,
    UpdatedAt datetime2(0) NOT NULL,
    CONSTRAINT FK_ItemPrices_Items FOREIGN KEY (ItemID) REFERENCES dbo.Items (ItemID),
    CONSTRAINT FK_ItemPrices_PriceTypes FOREIGN KEY (PriceTypeID) REFERENCES dbo.PriceTypes (PriceTypeID),
    CONSTRAINT UQ_ItemPrices_ItemID_PriceTypeID UNIQUE (ItemID, PriceTypeID)
);

CREATE TABLE dbo.Users
(
    UserID uniqueidentifier NOT NULL CONSTRAINT PK_Users PRIMARY KEY,
    Username nvarchar(100) NOT NULL,
    NormalizedUsername nvarchar(100) NOT NULL,
    DisplayName nvarchar(150) NOT NULL,
    FullName nvarchar(300) NULL,
    EmployeeNumber nvarchar(50) NULL,
    PasswordHash nvarchar(1000) NOT NULL,
    IsActive bit NOT NULL,
    MustChangePassword bit NOT NULL,
    CreatedAt datetime2(0) NOT NULL,
    UpdatedAt datetime2(0) NULL,
    CONSTRAINT UQ_Users_NormalizedUsername UNIQUE (NormalizedUsername)
);

CREATE TABLE dbo.Roles
(
    RoleID uniqueidentifier NOT NULL CONSTRAINT PK_Roles PRIMARY KEY,
    RoleCode nvarchar(50) NOT NULL,
    RoleName nvarchar(100) NOT NULL,
    Description nvarchar(500) NULL,
    IsSystemRole bit NOT NULL,
    IsActive bit NOT NULL,
    CreatedAt datetime2(0) NOT NULL,
    UpdatedAt datetime2(0) NULL,
    CONSTRAINT UQ_Roles_RoleCode UNIQUE (RoleCode)
);

CREATE TABLE dbo.Permissions
(
    PermissionID uniqueidentifier NOT NULL CONSTRAINT PK_Permissions PRIMARY KEY,
    PermissionCode nvarchar(100) NOT NULL,
    PermissionName nvarchar(150) NOT NULL,
    Module nvarchar(100) NOT NULL,
    Description nvarchar(500) NULL,
    IsActive bit NOT NULL,
    CreatedAt datetime2(0) NOT NULL,
    CONSTRAINT UQ_Permissions_PermissionCode UNIQUE (PermissionCode)
);

CREATE TABLE dbo.RolePermissions
(
    RoleID uniqueidentifier NOT NULL,
    PermissionID uniqueidentifier NOT NULL,
    GrantedAt datetime2(0) NOT NULL,
    CONSTRAINT PK_RolePermissions PRIMARY KEY (RoleID, PermissionID),
    CONSTRAINT FK_RolePermissions_Roles FOREIGN KEY (RoleID) REFERENCES dbo.Roles (RoleID),
    CONSTRAINT FK_RolePermissions_Permissions FOREIGN KEY (PermissionID) REFERENCES dbo.Permissions (PermissionID)
);

CREATE TABLE dbo.UserRoles
(
    UserID uniqueidentifier NOT NULL,
    RoleID uniqueidentifier NOT NULL,
    AssignedAt datetime2(0) NOT NULL,
    CONSTRAINT PK_UserRoles PRIMARY KEY (UserID, RoleID),
    CONSTRAINT FK_UserRoles_Users FOREIGN KEY (UserID) REFERENCES dbo.Users (UserID),
    CONSTRAINT FK_UserRoles_Roles FOREIGN KEY (RoleID) REFERENCES dbo.Roles (RoleID)
);

CREATE TABLE dbo.UserBranchAccess
(
    UserID uniqueidentifier NOT NULL,
    BranchID uniqueidentifier NOT NULL,
    IsActive bit NOT NULL,
    GrantedAt datetime2(0) NOT NULL,
    CONSTRAINT PK_UserBranchAccess PRIMARY KEY (UserID, BranchID),
    CONSTRAINT FK_UserBranchAccess_Users FOREIGN KEY (UserID) REFERENCES dbo.Users (UserID),
    CONSTRAINT FK_UserBranchAccess_Branches FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID)
);
