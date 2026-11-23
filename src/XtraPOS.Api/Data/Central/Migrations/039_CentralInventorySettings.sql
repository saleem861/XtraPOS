-- Central master for inventory counting (phase 1 of the inventory and shortages module).
-- Item classification (important / periodic page) and the periodic page schedule. Branches pull both every minute
-- (InventoryCountService.PullSettingsAsync through PosDeviceSettingsSyncWorker). The same tables and the same
-- permissions exist on every branch database (054). Count sessions stay on the branch in this phase.
-- Adds two tables, permissions and seed rows only. Safe to run again. Back up XtraPOS_Central before publishing.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.ItemInventorySettings', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ItemInventorySettings
    (
        ItemNumber nvarchar(100) NOT NULL CONSTRAINT PK_ItemInventorySettings PRIMARY KEY,
        IsImportant bit NOT NULL CONSTRAINT DF_ItemInventorySettings_IsImportant DEFAULT (0),
        InventoryPage tinyint NULL,
        SupplyDays int NULL,
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_ItemInventorySettings_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedByName nvarchar(200) NULL,
        CONSTRAINT CK_ItemInventorySettings_Page CHECK (InventoryPage IS NULL OR InventoryPage BETWEEN 0 AND 12),
        CONSTRAINT CK_ItemInventorySettings_SupplyDays CHECK (SupplyDays IS NULL OR SupplyDays BETWEEN 1 AND 365)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemInventorySettings_Page' AND object_id = OBJECT_ID(N'dbo.ItemInventorySettings'))
BEGIN
    CREATE INDEX IX_ItemInventorySettings_Page ON dbo.ItemInventorySettings (InventoryPage, IsImportant);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemInventorySettings_UpdatedAt' AND object_id = OBJECT_ID(N'dbo.ItemInventorySettings'))
BEGIN
    CREATE INDEX IX_ItemInventorySettings_UpdatedAt ON dbo.ItemInventorySettings (UpdatedAt);
END;

-- DayOfWeek follows .NET: 0 = Sunday ... 6 = Saturday. PeriodType AM = before the cutoff (16:00), PM = after.
IF OBJECT_ID(N'dbo.InventorySchedule', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InventorySchedule
    (
        DayOfWeek tinyint NOT NULL,
        PeriodType nvarchar(2) NOT NULL,
        InventoryPage tinyint NOT NULL,
        IsEnabled bit NOT NULL CONSTRAINT DF_InventorySchedule_IsEnabled DEFAULT (1),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_InventorySchedule_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_InventorySchedule PRIMARY KEY (DayOfWeek, PeriodType),
        CONSTRAINT CK_InventorySchedule_Day CHECK (DayOfWeek BETWEEN 0 AND 6),
        CONSTRAINT CK_InventorySchedule_Period CHECK (PeriodType IN (N'AM', N'PM')),
        CONSTRAINT CK_InventorySchedule_Page CHECK (InventoryPage BETWEEN 1 AND 12)
    );
END;

-- Friday 1/2, Saturday 3/4, Sunday 5/6, Monday 7/8, Tuesday 9/10, Wednesday 11/12. Thursday is excluded.
INSERT INTO dbo.InventorySchedule (DayOfWeek, PeriodType, InventoryPage)
SELECT seed.DayOfWeek, seed.PeriodType, seed.InventoryPage
FROM (VALUES
    (5, N'AM', 1), (5, N'PM', 2),
    (6, N'AM', 3), (6, N'PM', 4),
    (0, N'AM', 5), (0, N'PM', 6),
    (1, N'AM', 7), (1, N'PM', 8),
    (2, N'AM', 9), (2, N'PM', 10),
    (3, N'AM', 11), (3, N'PM', 12)
) AS seed (DayOfWeek, PeriodType, InventoryPage)
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.InventorySchedule AS existing
    WHERE existing.DayOfWeek = seed.DayOfWeek AND existing.PeriodType = seed.PeriodType);

INSERT INTO dbo.Permissions (PermissionID, PermissionCode, PermissionName, Module, IsActive, CreatedAt)
SELECT NEWID(), code.PermissionCode, code.PermissionName, N'Inventory', 1, SYSUTCDATETIME()
FROM (VALUES
    (N'Inventory.Count', N'الجرد'),
    (N'Inventory.Define', N'تحديد جرد مخصص'),
    (N'Inventory.Review', N'تقرير الجرد وتدقيق المشرف'),
    (N'Inventory.Classify', N'تغيير تصنيف الجرد'),
    (N'Inventory.CorrectQuantity', N'تصحيح كمية صنف'),
    (N'Shortages.Process', N'معالجة النواقص'),
    (N'Shortages.FollowUp', N'متابعة النواقص والتزويد'),
    (N'Shortages.Approve', N'اعتماد البديل والحذف'),
    (N'Shortages.Audit', N'تدقيق معالجة النواقص'),
    (N'Shortages.Efficiency', N'تقرير الكفاءة'),
    (N'Shortages.Report', N'تقرير معالجة النواقص')
) AS code(PermissionCode, PermissionName)
WHERE NOT EXISTS (SELECT 1 FROM dbo.Permissions AS existing WHERE existing.PermissionCode = code.PermissionCode);

-- Admin: all. Supervisor: all except approvals, audit requests and the processing report. User: count and own efficiency.
INSERT INTO dbo.RolePermissions (RoleID, PermissionID, GrantedAt)
SELECT role.RoleID, permission.PermissionID, SYSUTCDATETIME()
FROM dbo.Roles AS role
INNER JOIN dbo.Permissions AS permission
    ON (role.RoleCode = N'Admin' AND permission.PermissionCode IN (
            N'Inventory.Count', N'Inventory.Define', N'Inventory.Review', N'Inventory.Classify', N'Inventory.CorrectQuantity',
            N'Shortages.Process', N'Shortages.FollowUp', N'Shortages.Approve', N'Shortages.Audit', N'Shortages.Efficiency', N'Shortages.Report'))
    OR (role.RoleCode = N'Supervisor' AND permission.PermissionCode IN (
            N'Inventory.Count', N'Inventory.Define', N'Inventory.Review', N'Inventory.Classify', N'Inventory.CorrectQuantity',
            N'Shortages.Process', N'Shortages.FollowUp', N'Shortages.Efficiency'))
    OR (role.RoleCode = N'User' AND permission.PermissionCode IN (N'Inventory.Count', N'Shortages.Efficiency'))
WHERE NOT EXISTS (
    SELECT 1 FROM dbo.RolePermissions AS existing
    WHERE existing.RoleID = role.RoleID AND existing.PermissionID = permission.PermissionID);
