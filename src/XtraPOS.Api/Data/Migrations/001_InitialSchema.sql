-- Initial XtraPOS branch schema.
-- Text columns inherit the database collation (Arabic_CI_AS).
-- Do not add column-level COLLATE clauses.
-- Future temporary tables should use COLLATE DATABASE_DEFAULT.

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

CREATE TABLE dbo.Businesses
(
    BusinessID uniqueidentifier NOT NULL
        CONSTRAINT DF_Businesses_BusinessID DEFAULT (NEWSEQUENTIALID()),
    BusinessName nvarchar(200) NOT NULL,
    BusinessDisplayName nvarchar(200) NULL,
    Description nvarchar(500) NULL,
    Phone nvarchar(50) NULL,
    Address nvarchar(500) NULL,
    IsActive bit NOT NULL
        CONSTRAINT DF_Businesses_IsActive DEFAULT (1),
    CreatedAt datetime2(0) NOT NULL
        CONSTRAINT DF_Businesses_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt datetime2(0) NULL,
    CONSTRAINT PK_Businesses PRIMARY KEY (BusinessID)
);

CREATE TABLE dbo.Branches
(
    BranchID uniqueidentifier NOT NULL
        CONSTRAINT DF_Branches_BranchID DEFAULT (NEWSEQUENTIALID()),
    BusinessID uniqueidentifier NOT NULL,
    BranchCode nvarchar(50) NULL,
    BranchName nvarchar(200) NOT NULL,
    BranchType nvarchar(30) NOT NULL,
    IsActive bit NOT NULL
        CONSTRAINT DF_Branches_IsActive DEFAULT (1),
    CreatedAt datetime2(0) NOT NULL
        CONSTRAINT DF_Branches_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt datetime2(0) NULL,
    CONSTRAINT PK_Branches PRIMARY KEY (BranchID),
    CONSTRAINT FK_Branches_Businesses FOREIGN KEY (BusinessID) REFERENCES dbo.Businesses (BusinessID),
    CONSTRAINT CK_Branches_BranchType CHECK (BranchType IN (N'Retail', N'Warehouse', N'RetailAndWarehouse'))
);

CREATE UNIQUE INDEX UX_Branches_BusinessID_BranchCode
    ON dbo.Branches (BusinessID, BranchCode)
    WHERE BranchCode IS NOT NULL;

CREATE TABLE dbo.POSStations
(
    StationID uniqueidentifier NOT NULL
        CONSTRAINT DF_POSStations_StationID DEFAULT (NEWSEQUENTIALID()),
    BranchID uniqueidentifier NOT NULL,
    StationNumber int NOT NULL,
    StationName nvarchar(100) NOT NULL,
    IsActive bit NOT NULL
        CONSTRAINT DF_POSStations_IsActive DEFAULT (1),
    CreatedAt datetime2(0) NOT NULL
        CONSTRAINT DF_POSStations_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt datetime2(0) NULL,
    CONSTRAINT PK_POSStations PRIMARY KEY (StationID),
    CONSTRAINT FK_POSStations_Branches FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID),
    CONSTRAINT UQ_POSStations_BranchID_StationNumber UNIQUE (BranchID, StationNumber),
    CONSTRAINT CK_POSStations_StationNumber CHECK (StationNumber > 0)
);

CREATE TABLE dbo.Devices
(
    DeviceID uniqueidentifier NOT NULL
        CONSTRAINT DF_Devices_DeviceID DEFAULT (NEWSEQUENTIALID()),
    BranchID uniqueidentifier NOT NULL,
    StationID uniqueidentifier NULL,
    DeviceName nvarchar(150) NOT NULL,
    DeviceType nvarchar(30) NOT NULL,
    DeviceTokenHash varbinary(64) NULL,
    IsActive bit NOT NULL
        CONSTRAINT DF_Devices_IsActive DEFAULT (1),
    RegisteredAt datetime2(0) NOT NULL,
    LastSeenAt datetime2(0) NULL,
    RevokedAt datetime2(0) NULL,
    CreatedAt datetime2(0) NOT NULL
        CONSTRAINT DF_Devices_CreatedAt DEFAULT (SYSUTCDATETIME()),
    UpdatedAt datetime2(0) NULL,
    CONSTRAINT PK_Devices PRIMARY KEY (DeviceID),
    CONSTRAINT FK_Devices_Branches FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID),
    CONSTRAINT FK_Devices_POSStations FOREIGN KEY (StationID) REFERENCES dbo.POSStations (StationID),
    CONSTRAINT CK_Devices_DeviceType CHECK (DeviceType IN (N'POS', N'Management', N'CustomerDisplay', N'Warehouse'))
);

CREATE TABLE dbo.SystemSettings
(
    SettingKey nvarchar(100) NOT NULL,
    SettingValue nvarchar(max) NULL,
    UpdatedAt datetime2(0) NOT NULL
        CONSTRAINT DF_SystemSettings_UpdatedAt DEFAULT (SYSUTCDATETIME()),
    CONSTRAINT PK_SystemSettings PRIMARY KEY (SettingKey)
);
