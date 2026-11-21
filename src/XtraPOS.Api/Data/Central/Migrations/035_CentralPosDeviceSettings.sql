-- النسخة المركزية من القيم الأولية لكل نقطة بيع.
-- الفرع يرفع تعديلاته إلى هنا عبر سجل الترحيل (BranchOperationLog، النوع DeviceSettings)،
-- وكل تعديل يُسجَّل في SyncChangeLog بنوع PosDeviceSettings، فتسحبه كل الفروع ويتطابق جدولها مع المركز.
-- Adds tables (settings, audit, receipts) and two permissions. Does not touch customers, items, stock or sales. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.CentralPosDeviceSettings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CentralPosDeviceSettings
    (
        DeviceID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        PosNumber int NULL,
        DeviceName nvarchar(200) NULL,
        SettingsJson nvarchar(max) NOT NULL,
        SettingsVersion bigint NOT NULL
            CONSTRAINT DF_CentralPosDeviceSettings_SettingsVersion DEFAULT (0),
        UpdatedAtUtc datetime2(3) NOT NULL
            CONSTRAINT DF_CentralPosDeviceSettings_UpdatedAtUtc DEFAULT (SYSUTCDATETIME()),
        UpdatedByUserID uniqueidentifier NULL,
        UpdatedSource nvarchar(20) NOT NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_CentralPosDeviceSettings PRIMARY KEY (DeviceID),
        CONSTRAINT CK_CentralPosDeviceSettings_UpdatedSource CHECK (UpdatedSource IN (N'Branch', N'Central'))
    );
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_CentralPosDeviceSettings_BranchID'
      AND object_id = OBJECT_ID(N'dbo.CentralPosDeviceSettings'))
    CREATE INDEX IX_CentralPosDeviceSettings_BranchID
        ON dbo.CentralPosDeviceSettings (BranchID);

-- سجل تدقيق: لكل إعداد تغيّرت قيمته الفعلية سطر بالقيمة قبل وبعد، ومن، ومن أين.
-- OperationID يربط السطر بعملية سجل الترحيل في الفرع.
IF OBJECT_ID(N'dbo.PosDeviceSettingsAudit', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PosDeviceSettingsAudit
    (
        AuditID uniqueidentifier NOT NULL CONSTRAINT PK_PosDeviceSettingsAudit PRIMARY KEY,
        DeviceID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        SettingKey nvarchar(100) NOT NULL,
        BeforeValue nvarchar(400) NULL,
        AfterValue nvarchar(400) NULL,
        Source nvarchar(20) NOT NULL,
        ActorUserID uniqueidentifier NULL,
        ActorName nvarchar(150) NULL,
        OperationID uniqueidentifier NULL,
        SettingsVersion bigint NOT NULL,
        OccurredAt datetime2(0) NOT NULL
            CONSTRAINT DF_PosDeviceSettingsAudit_OccurredAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT CK_PosDeviceSettingsAudit_Source CHECK (Source IN (N'Branch', N'Central'))
    );
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_PosDeviceSettingsAudit_Device'
      AND object_id = OBJECT_ID(N'dbo.PosDeviceSettingsAudit'))
    CREATE INDEX IX_PosDeviceSettingsAudit_Device
        ON dbo.PosDeviceSettingsAudit (DeviceID, OccurredAt);

-- سطر لكل عملية وصلت من سجل ترحيل فرع (حتى لو لم تغيّر قيمة فعلية)، فإعادة المحاولة لا تطبّقها مرتين.
-- DeviceID فارغ (00000000-...) عندما تكون العملية لجميع النقاط.
IF OBJECT_ID(N'dbo.PosDeviceSettingsReceipts', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PosDeviceSettingsReceipts
    (
        OperationID uniqueidentifier NOT NULL CONSTRAINT PK_PosDeviceSettingsReceipts PRIMARY KEY,
        DeviceID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        AffectedDevices int NOT NULL,
        ReceivedAt datetime2(0) NOT NULL
            CONSTRAINT DF_PosDeviceSettingsReceipts_ReceivedAt DEFAULT (SYSUTCDATETIME())
    );
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = N'Devices.ConfigureCurrent')
BEGIN
    INSERT INTO dbo.Permissions (PermissionID, PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
    VALUES (NEWID(), N'Devices.ConfigureCurrent', N'القيم الأولية للنقطة الحالية', N'Devices', N'ضبط إعدادات نقطة البيع التي يعمل عليها المستخدم', 1, SYSUTCDATETIME());
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = N'Devices.ManageAll')
BEGIN
    INSERT INTO dbo.Permissions (PermissionID, PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
    VALUES (NEWID(), N'Devices.ManageAll', N'إعدادات نقاط البيع في كل الفروع', N'Devices', N'ضبط إعدادات أي نقطة بيع في أي فرع عبر الإدارة المركزية', 1, SYSUTCDATETIME());
END;

INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS role
INNER JOIN dbo.Permissions AS permission
    ON (
        (role.RoleCode IN (N'Admin', N'Supervisor') AND permission.PermissionCode = N'Devices.ConfigureCurrent')
        OR (role.RoleCode = N'Admin' AND permission.PermissionCode = N'Devices.ManageAll'))
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.RolePermissions AS existing
    WHERE existing.RoleID = role.RoleID
      AND existing.PermissionID = permission.PermissionID);
