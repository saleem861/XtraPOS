-- القيم الأولية للنقطة الحالية: سجل ترحيل التعديلات من الفرع إلى المركز (Outbox)،
-- وسجل ما طُبِّق من المركز على هذا الفرع (Inbox)، وصلاحيتان جديدتان.
-- الإعدادات نفسها تُحفظ داخل POSDeviceConfigurations.ConfigurationJson تحت المفتاح "settings".
-- نفس نمط BranchFinancialOutbox / BranchCollectionOutbox: الحالة، المحاولات، آخر خطأ، الحجز (Lease).
-- Adds tables, columns and permissions only. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.POSDeviceConfigurations', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.POSDeviceConfigurations', N'SettingsCentralVersion') IS NULL
BEGIN
    ALTER TABLE dbo.POSDeviceConfigurations
        ADD SettingsCentralVersion bigint NULL;
END;

IF OBJECT_ID(N'dbo.POSDeviceConfigurations', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.POSDeviceConfigurations', N'SettingsSyncedAtUtc') IS NULL
BEGIN
    ALTER TABLE dbo.POSDeviceConfigurations
        ADD SettingsSyncedAtUtc datetime2(0) NULL;
END;

-- كل حفظ من الفرع = سطر واحد. السطر لا يُحذف بعد الترحيل، فهو سجل الترحيلات.
IF OBJECT_ID(N'dbo.BranchDeviceSettingsOutbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BranchDeviceSettingsOutbox
    (
        OutboxEventID uniqueidentifier NOT NULL CONSTRAINT PK_BranchDeviceSettingsOutbox PRIMARY KEY,
        DeviceID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        ChangedKeys nvarchar(400) NOT NULL,
        Payload nvarchar(max) NOT NULL,
        CreatedByUserID uniqueidentifier NULL,
        CreatedByName nvarchar(150) NULL,
        Status nvarchar(30) NOT NULL,
        AttemptCount int NOT NULL
            CONSTRAINT DF_BranchDeviceSettingsOutbox_AttemptCount DEFAULT (0),
        LastAttemptAt datetime2(0) NULL,
        NextRetryAt datetime2(0) NOT NULL
            CONSTRAINT DF_BranchDeviceSettingsOutbox_NextRetryAt DEFAULT (SYSUTCDATETIME()),
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_BranchDeviceSettingsOutbox_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CompletedAt datetime2(0) NULL,
        CentralVersion bigint NULL,
        LastError nvarchar(400) NULL,
        DiagnosticID nvarchar(40) NULL,
        LeaseOwner nvarchar(80) NULL,
        LeaseUntil datetime2(0) NULL,
        CONSTRAINT FK_BranchDeviceSettingsOutbox_Devices FOREIGN KEY (DeviceID) REFERENCES dbo.Devices (DeviceID),
        CONSTRAINT CK_BranchDeviceSettingsOutbox_Status CHECK (Status IN (N'Pending', N'Completed', N'ReconciliationRequired')),
        CONSTRAINT CK_BranchDeviceSettingsOutbox_Attempts CHECK (AttemptCount >= 0)
    );
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_BranchDeviceSettingsOutbox_Pending'
      AND object_id = OBJECT_ID(N'dbo.BranchDeviceSettingsOutbox'))
    CREATE INDEX IX_BranchDeviceSettingsOutbox_Pending
        ON dbo.BranchDeviceSettingsOutbox (Status, NextRetryAt, CreatedAt);

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_BranchDeviceSettingsOutbox_Device'
      AND object_id = OBJECT_ID(N'dbo.BranchDeviceSettingsOutbox'))
    CREATE INDEX IX_BranchDeviceSettingsOutbox_Device
        ON dbo.BranchDeviceSettingsOutbox (DeviceID, CreatedAt);

-- كل تعديل مركزي وصل لنقطة من نقاط هذا الفرع. مستقل عن SyncInbox الخاص بمزامنة الزبائن والأصناف.
IF OBJECT_ID(N'dbo.DeviceSettingsInbox', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.DeviceSettingsInbox
    (
        EndpointID uniqueidentifier NOT NULL,
        ChangeSequence bigint NOT NULL,
        DeviceID uniqueidentifier NOT NULL,
        CentralVersion bigint NOT NULL,
        Outcome nvarchar(20) NOT NULL,
        AppliedAt datetime2(0) NOT NULL
            CONSTRAINT DF_DeviceSettingsInbox_AppliedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_DeviceSettingsInbox PRIMARY KEY (EndpointID, ChangeSequence),
        CONSTRAINT FK_DeviceSettingsInbox_Endpoint FOREIGN KEY (EndpointID) REFERENCES dbo.SyncEndpoints (EndpointID),
        CONSTRAINT CK_DeviceSettingsInbox_Outcome CHECK (Outcome IN (N'Applied', N'NoChange'))
    );
END;

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE name = N'IX_DeviceSettingsInbox_Device'
      AND object_id = OBJECT_ID(N'dbo.DeviceSettingsInbox'))
    CREATE INDEX IX_DeviceSettingsInbox_Device
        ON dbo.DeviceSettingsInbox (DeviceID, AppliedAt);

IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = N'Devices.ConfigureCurrent')
BEGIN
    INSERT INTO dbo.Permissions (PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
    VALUES (N'Devices.ConfigureCurrent', N'القيم الأولية للنقطة الحالية', N'Devices', N'ضبط إعدادات نقطة البيع التي يعمل عليها المستخدم', 1, SYSUTCDATETIME());
END;

IF NOT EXISTS (SELECT 1 FROM dbo.Permissions WHERE PermissionCode = N'Devices.ManageAll')
BEGIN
    INSERT INTO dbo.Permissions (PermissionCode, PermissionName, Module, Description, IsActive, CreatedAt)
    VALUES (N'Devices.ManageAll', N'إعدادات نقاط البيع في كل الفروع', N'Devices', N'ضبط إعدادات أي نقطة بيع في أي فرع عبر الإدارة المركزية', 1, SYSUTCDATETIME());
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
