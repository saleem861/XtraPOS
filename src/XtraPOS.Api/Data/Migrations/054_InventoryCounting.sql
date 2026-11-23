-- Inventory counting (phase 1 of the inventory and shortages module).
-- Item classification (important / periodic page), the periodic page schedule, count sessions and their lines.
-- A finalized count posts through StockPostingService with SourceType InventoryCount; this migration does not
-- change balances or movements. The settings and schedule tables also exist on XtraPOS_Central (039) and are
-- pulled to the branch. Adds tables, permissions and seed rows only. Safe to run again.

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

IF OBJECT_ID(N'dbo.InventorySessions', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InventorySessions
    (
        SessionID uniqueidentifier NOT NULL CONSTRAINT PK_InventorySessions PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        StationID uniqueidentifier NULL,
        DeviceID uniqueidentifier NULL,
        UserID uniqueidentifier NOT NULL,
        UserName nvarchar(200) NULL,
        InventoryType nvarchar(20) NOT NULL,
        BusinessDate date NOT NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_InventorySessions_Status DEFAULT (N'Open'),
        StartedAt datetime2(0) NOT NULL CONSTRAINT DF_InventorySessions_StartedAt DEFAULT (SYSUTCDATETIME()),
        LastSavedAt datetime2(0) NOT NULL CONSTRAINT DF_InventorySessions_LastSavedAt DEFAULT (SYSUTCDATETIME()),
        CompletedAt datetime2(0) NULL,
        DiscardedAt datetime2(0) NULL,
        TotalItems int NOT NULL CONSTRAINT DF_InventorySessions_TotalItems DEFAULT (0),
        CountedItems int NOT NULL CONSTRAINT DF_InventorySessions_CountedItems DEFAULT (0),
        PostedItems int NOT NULL CONSTRAINT DF_InventorySessions_PostedItems DEFAULT (0),
        RelayedAt datetime2(0) NULL,
        CONSTRAINT CK_InventorySessions_Type CHECK (InventoryType IN (N'Important', N'Periodic', N'Manual')),
        CONSTRAINT CK_InventorySessions_Status CHECK (Status IN (N'Open', N'Completed', N'Discarded'))
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InventorySessions_Open' AND object_id = OBJECT_ID(N'dbo.InventorySessions'))
BEGIN
    CREATE INDEX IX_InventorySessions_Open ON dbo.InventorySessions (BranchID, UserID, InventoryType, Status, BusinessDate);
END;

IF OBJECT_ID(N'dbo.InventorySessionPages', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InventorySessionPages
    (
        SessionID uniqueidentifier NOT NULL,
        InventoryPage tinyint NOT NULL,
        CONSTRAINT PK_InventorySessionPages PRIMARY KEY (SessionID, InventoryPage),
        CONSTRAINT FK_InventorySessionPages_Sessions FOREIGN KEY (SessionID) REFERENCES dbo.InventorySessions (SessionID)
    );
END;

-- One line per stock-owner item. SessionItemID is the stock movement source line, so posting twice never doubles.
-- SystemQuantityAtCount is read when the count is saved (and again when it is edited), not when the session starts.
IF OBJECT_ID(N'dbo.InventorySessionItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InventorySessionItems
    (
        SessionItemID uniqueidentifier NOT NULL CONSTRAINT PK_InventorySessionItems PRIMARY KEY,
        SessionID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        ItemNumber nvarchar(100) NOT NULL,
        ItemName nvarchar(500) NULL,
        DailySalesRate decimal(18, 6) NULL,
        SupplyDays int NULL,
        IsCounted bit NOT NULL CONSTRAINT DF_InventorySessionItems_IsCounted DEFAULT (0),
        CountedQuantity decimal(18, 4) NULL,
        SystemQuantityAtCount decimal(18, 4) NULL,
        SystemQuantityKnown bit NOT NULL CONSTRAINT DF_InventorySessionItems_SystemQuantityKnown DEFAULT (0),
        CountedAt datetime2(0) NULL,
        CountedByUserID uniqueidentifier NULL,
        CountedByName nvarchar(200) NULL,
        StockMovementID uniqueidentifier NULL,
        PostedAt datetime2(0) NULL,
        QuantityBeforePosting decimal(18, 4) NULL,
        QuantityAfterPosting decimal(18, 4) NULL,
        CONSTRAINT FK_InventorySessionItems_Sessions FOREIGN KEY (SessionID) REFERENCES dbo.InventorySessions (SessionID),
        CONSTRAINT FK_InventorySessionItems_Items FOREIGN KEY (ItemID) REFERENCES dbo.Items (ItemID),
        CONSTRAINT UQ_InventorySessionItems_Session_Item UNIQUE (SessionID, ItemID),
        CONSTRAINT CK_InventorySessionItems_Counted CHECK (CountedQuantity IS NULL OR CountedQuantity >= 0)
    );
END;

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
