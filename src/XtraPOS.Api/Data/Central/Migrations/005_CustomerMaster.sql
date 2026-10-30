-- Customer Master. Central-owned classifications and customers.
-- Adds tables, permissions, and indexes only.
-- Does not import customers, seed classifications, or change items, prices, or stock.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.CustomerClassifications', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerClassifications
    (
        CustomerClassificationID uniqueidentifier NOT NULL CONSTRAINT PK_CustomerClassifications PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        Code nvarchar(50) NULL,
        Name nvarchar(200) NOT NULL,
        DefaultPriceTypeID uniqueidentifier NULL,
        DefaultVariableValueDiscountPercent decimal(7, 4) NOT NULL
            CONSTRAINT DF_CustomerClassifications_DefaultVariableValueDiscountPercent DEFAULT (0),
        IsDefault bit NOT NULL
            CONSTRAINT DF_CustomerClassifications_IsDefault DEFAULT (0),
        IsActive bit NOT NULL
            CONSTRAINT DF_CustomerClassifications_IsActive DEFAULT (1),
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_CustomerClassifications_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_CustomerClassifications_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_CustomerClassifications_Businesses FOREIGN KEY (BusinessID) REFERENCES dbo.Businesses (BusinessID),
        CONSTRAINT FK_CustomerClassifications_PriceTypes FOREIGN KEY (DefaultPriceTypeID) REFERENCES dbo.PriceTypes (PriceTypeID),
        CONSTRAINT CK_CustomerClassifications_DefaultVariableValueDiscountPercent
            CHECK (DefaultVariableValueDiscountPercent >= 0 AND DefaultVariableValueDiscountPercent <= 100)
    );
END;

IF OBJECT_ID(N'dbo.Customers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Customers
    (
        CustomerID uniqueidentifier NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        CustomerNumber nvarchar(50) NULL,
        Name nvarchar(300) NOT NULL,
        Phone nvarchar(50) NULL,
        NormalizedPhone nvarchar(50) NULL,
        SecondaryPhone nvarchar(50) NULL,
        SecondaryNormalizedPhone nvarchar(50) NULL,
        CustomerClassificationID uniqueidentifier NULL,
        PriceTypeID uniqueidentifier NULL,
        VariableValueDiscountPercent decimal(7, 4) NOT NULL
            CONSTRAINT DF_Customers_VariableValueDiscountPercent DEFAULT (0),
        IsActive bit NOT NULL
            CONSTRAINT DF_Customers_IsActive DEFAULT (1),
        Notes nvarchar(1000) NULL,
        LegacySource nvarchar(64) NULL,
        LegacyCustomerKey nvarchar(64) NULL,
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_Customers_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_Customers_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_Customers_Businesses FOREIGN KEY (BusinessID) REFERENCES dbo.Businesses (BusinessID),
        CONSTRAINT FK_Customers_CustomerClassifications FOREIGN KEY (CustomerClassificationID) REFERENCES dbo.CustomerClassifications (CustomerClassificationID),
        CONSTRAINT FK_Customers_PriceTypes FOREIGN KEY (PriceTypeID) REFERENCES dbo.PriceTypes (PriceTypeID),
        CONSTRAINT CK_Customers_VariableValueDiscountPercent
            CHECK (VariableValueDiscountPercent >= 0 AND VariableValueDiscountPercent <= 100)
    );
END;

IF OBJECT_ID(N'dbo.CustomerAdministrationAudit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomerAdministrationAudit
    (
        AuditID uniqueidentifier NOT NULL CONSTRAINT PK_CustomerAdministrationAudit PRIMARY KEY,
        ActorUserID uniqueidentifier NOT NULL,
        CustomerID uniqueidentifier NOT NULL,
        Action nvarchar(50) NOT NULL,
        OccurredAt datetime2(0) NOT NULL
            CONSTRAINT DF_CustomerAdministrationAudit_OccurredAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT FK_CustomerAdministrationAudit_Actor FOREIGN KEY (ActorUserID) REFERENCES dbo.Users (UserID),
        CONSTRAINT FK_CustomerAdministrationAudit_Customer FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID)
    );
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UX_CustomerClassifications_Default'
      AND object_id = OBJECT_ID(N'dbo.CustomerClassifications'))
    CREATE UNIQUE INDEX UX_CustomerClassifications_Default
        ON dbo.CustomerClassifications (BusinessID)
        WHERE IsDefault = 1;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_CustomerClassifications_Business_Name'
      AND object_id = OBJECT_ID(N'dbo.CustomerClassifications'))
    CREATE INDEX IX_CustomerClassifications_Business_Name
        ON dbo.CustomerClassifications (BusinessID, Name);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UX_Customers_Business_CustomerNumber'
      AND object_id = OBJECT_ID(N'dbo.Customers'))
    CREATE UNIQUE INDEX UX_Customers_Business_CustomerNumber
        ON dbo.Customers (BusinessID, CustomerNumber)
        WHERE CustomerNumber IS NOT NULL;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_Customers_Business_NormalizedPhone'
      AND object_id = OBJECT_ID(N'dbo.Customers'))
    CREATE INDEX IX_Customers_Business_NormalizedPhone
        ON dbo.Customers (BusinessID, NormalizedPhone);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_Customers_Business_Name'
      AND object_id = OBJECT_ID(N'dbo.Customers'))
    CREATE INDEX IX_Customers_Business_Name
        ON dbo.Customers (BusinessID, Name);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'UX_Customers_LegacyIdentity'
      AND object_id = OBJECT_ID(N'dbo.Customers'))
    CREATE UNIQUE INDEX UX_Customers_LegacyIdentity
        ON dbo.Customers (BusinessID, LegacySource, LegacyCustomerKey)
        WHERE LegacySource IS NOT NULL AND LegacyCustomerKey IS NOT NULL;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_CustomerAdministrationAudit_CustomerID'
      AND object_id = OBJECT_ID(N'dbo.CustomerAdministrationAudit'))
    CREATE INDEX IX_CustomerAdministrationAudit_CustomerID
        ON dbo.CustomerAdministrationAudit (CustomerID);

IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = N'Customers.View')
BEGIN
    INSERT INTO dbo.Permissions (PermissionID, PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
    VALUES (NEWID(), N'Customers.View', N'عرض الزبائن', N'Customers', N'عرض دليل الزبائن وتصنيفاتهم', 1, SYSUTCDATETIME());
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = N'Customers.Manage')
BEGIN
    INSERT INTO dbo.Permissions (PermissionID, PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
    VALUES (NEWID(), N'Customers.Manage', N'إدارة الزبائن', N'Customers', N'إنشاء وتعديل وتفعيل الزبائن', 1, SYSUTCDATETIME());
END;

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS role
INNER JOIN dbo.Permissions AS permission
    ON (
        (role.RoleCode = N'Admin' AND permission.PermissionCode IN (N'Customers.View', N'Customers.Manage'))
        OR (role.RoleCode = N'Supervisor' AND permission.PermissionCode = N'Customers.View'))
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.RolePermissions AS existing
    WHERE existing.RoleID = role.RoleID
      AND existing.PermissionID = permission.PermissionID);

IF NOT EXISTS (SELECT 1 FROM dbo.SyncEntityOwnership WHERE EntityType = N'CustomerClassifications')
    INSERT INTO dbo.SyncEntityOwnership (EntityType, OwnerRole, IsImplemented)
    VALUES (N'CustomerClassifications', N'Central', 1);

IF NOT EXISTS (SELECT 1 FROM dbo.SyncEntityOwnership WHERE EntityType = N'Customers')
    INSERT INTO dbo.SyncEntityOwnership (EntityType, OwnerRole, IsImplemented)
    VALUES (N'Customers', N'Central', 1);
