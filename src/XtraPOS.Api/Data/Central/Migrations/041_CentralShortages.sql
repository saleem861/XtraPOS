-- Shortages, follow-up, suppliers, custom inventories and receipt audits
-- (phases 4, 5 and 6 of the inventory and shortages module). All on XtraPOS_Central.
-- Reads branch stock from BranchStockSnapshots (040). Central never writes branch stock directly:
-- a quantity correction for another branch is a request the branch applies itself.
-- Adds tables and one column only. Safe to run again. Back up XtraPOS_Central before publishing.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

-- ------------------------------------------------------------------
-- Phase 4: shortage processing
-- ------------------------------------------------------------------

IF OBJECT_ID(N'dbo.ShortageRuns', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ShortageRuns
    (
        RunID uniqueidentifier NOT NULL CONSTRAINT PK_ShortageRuns PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        ShortageType nvarchar(20) NOT NULL,
        SupplyDays int NOT NULL,
        Pages nvarchar(60) NULL,
        CustomInventoryID uniqueidentifier NULL,
        ManualFrom date NULL,
        ManualTo date NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_ShortageRuns_Status DEFAULT (N'Draft'),
        CreatedByUserID uniqueidentifier NOT NULL,
        CreatedByName nvarchar(200) NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_ShortageRuns_CreatedAt DEFAULT (SYSUTCDATETIME()),
        LastSavedAt datetime2(0) NOT NULL CONSTRAINT DF_ShortageRuns_LastSavedAt DEFAULT (SYSUTCDATETIME()),
        CompletedAt datetime2(0) NULL,
        ItemCount int NOT NULL CONSTRAINT DF_ShortageRuns_ItemCount DEFAULT (0),
        CONSTRAINT CK_ShortageRuns_Type CHECK (ShortageType IN (N'Important', N'Periodic', N'Manual', N'Custom')),
        CONSTRAINT CK_ShortageRuns_Status CHECK (Status IN (N'Draft', N'Completed', N'Discarded')),
        CONSTRAINT CK_ShortageRuns_SupplyDays CHECK (SupplyDays BETWEEN 1 AND 365)
    );
    CREATE INDEX IX_ShortageRuns_User ON dbo.ShortageRuns (CreatedByUserID, Status, CreatedAt);
END;

IF OBJECT_ID(N'dbo.ShortageRunBranches', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ShortageRunBranches
    (
        RunID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        BranchName nvarchar(200) NULL,
        SnapshotAt datetime2(0) NULL,
        CONSTRAINT PK_ShortageRunBranches PRIMARY KEY (RunID, BranchID),
        CONSTRAINT FK_ShortageRunBranches_Runs FOREIGN KEY (RunID) REFERENCES dbo.ShortageRuns (RunID)
    );
END;

IF OBJECT_ID(N'dbo.ShortageRunUsers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ShortageRunUsers
    (
        RunID uniqueidentifier NOT NULL,
        UserID uniqueidentifier NOT NULL,
        CONSTRAINT PK_ShortageRunUsers PRIMARY KEY (RunID, UserID),
        CONSTRAINT FK_ShortageRunUsers_Runs FOREIGN KEY (RunID) REFERENCES dbo.ShortageRuns (RunID)
    );
END;

IF OBJECT_ID(N'dbo.ShortageRunItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ShortageRunItems
    (
        RunItemID uniqueidentifier NOT NULL CONSTRAINT PK_ShortageRunItems PRIMARY KEY,
        RunID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        ItemNumber nvarchar(100) NOT NULL,
        ItemName nvarchar(500) NULL,
        TotalNeed decimal(18, 4) NOT NULL,
        TotalSurplus decimal(18, 4) NOT NULL,
        RequiredQuantity decimal(18, 4) NOT NULL,
        BalanceQuantity decimal(18, 4) NOT NULL,
        MissingRate bit NOT NULL,
        MissingQuantity bit NOT NULL,
        IsProcessed bit NOT NULL CONSTRAINT DF_ShortageRunItems_IsProcessed DEFAULT (0),
        PurchaseQuantity decimal(18, 4) NULL,
        WarehouseQuantity decimal(18, 4) NULL,
        BalanceDecision decimal(18, 4) NULL,
        DeleteRequested bit NOT NULL CONSTRAINT DF_ShortageRunItems_Delete DEFAULT (0),
        AlreadySupplied bit NOT NULL CONSTRAINT DF_ShortageRunItems_Supplied DEFAULT (0),
        Note nvarchar(500) NULL,
        ProcessedAt datetime2(0) NULL,
        ProcessedByName nvarchar(200) NULL,
        CONSTRAINT FK_ShortageRunItems_Runs FOREIGN KEY (RunID) REFERENCES dbo.ShortageRuns (RunID),
        CONSTRAINT UQ_ShortageRunItems_Run_Item UNIQUE (RunID, ItemID)
    );
END;

IF OBJECT_ID(N'dbo.ShortageRunItemBranches', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ShortageRunItemBranches
    (
        RunID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        CurrentQuantity decimal(18, 4) NULL,
        DailySalesRate decimal(18, 6) NULL,
        TargetQuantity decimal(18, 4) NOT NULL,
        NeedQuantity decimal(18, 4) NOT NULL,
        SurplusQuantity decimal(18, 4) NOT NULL,
        CONSTRAINT PK_ShortageRunItemBranches PRIMARY KEY (RunID, ItemID, BranchID),
        CONSTRAINT FK_ShortageRunItemBranches_Runs FOREIGN KEY (RunID) REFERENCES dbo.ShortageRuns (RunID)
    );
END;

-- A quantity correction for a branch other than the device's branch. The branch applies it.
IF OBJECT_ID(N'dbo.InventoryCorrectionRequests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InventoryCorrectionRequests
    (
        RequestID uniqueidentifier NOT NULL CONSTRAINT PK_InventoryCorrectionRequests PRIMARY KEY,
        BranchID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        ItemNumber nvarchar(100) NOT NULL,
        ItemName nvarchar(500) NULL,
        NewQuantity decimal(18, 4) NOT NULL,
        Reason nvarchar(500) NOT NULL,
        RequestedByUserID uniqueidentifier NOT NULL,
        RequestedByName nvarchar(200) NULL,
        RequestedAt datetime2(0) NOT NULL CONSTRAINT DF_InventoryCorrectionRequests_RequestedAt DEFAULT (SYSUTCDATETIME()),
        Status nvarchar(20) NOT NULL CONSTRAINT DF_InventoryCorrectionRequests_Status DEFAULT (N'Pending'),
        AppliedAt datetime2(0) NULL,
        PreviousQuantity decimal(18, 4) NULL,
        Message nvarchar(500) NULL,
        CONSTRAINT CK_InventoryCorrectionRequests_Status CHECK (Status IN (N'Pending', N'Applied', N'Failed')),
        CONSTRAINT CK_InventoryCorrectionRequests_Quantity CHECK (NewQuantity >= 0)
    );
    CREATE INDEX IX_InventoryCorrectionRequests_Branch ON dbo.InventoryCorrectionRequests (BranchID, Status, RequestedAt);
END;

-- ------------------------------------------------------------------
-- Phase 5: follow-up, suppliers and supply requests
-- ------------------------------------------------------------------

IF OBJECT_ID(N'dbo.Suppliers', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Suppliers
    (
        SupplierID uniqueidentifier NOT NULL CONSTRAINT PK_Suppliers PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        SupplierName nvarchar(200) NOT NULL,
        Phone nvarchar(50) NULL,
        Notes nvarchar(500) NULL,
        IsActive bit NOT NULL CONSTRAINT DF_Suppliers_IsActive DEFAULT (1),
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Suppliers_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL CONSTRAINT DF_Suppliers_UpdatedAt DEFAULT (SYSUTCDATETIME())
    );
    CREATE INDEX IX_Suppliers_Business ON dbo.Suppliers (BusinessID, IsActive, SupplierName);
END;

IF OBJECT_ID(N'dbo.SupplyRequests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SupplyRequests
    (
        RequestID uniqueidentifier NOT NULL CONSTRAINT PK_SupplyRequests PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        RequestNumber int IDENTITY(1, 1) NOT NULL,
        SupplierID uniqueidentifier NOT NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_SupplyRequests_Status DEFAULT (N'Open'),
        CreatedByName nvarchar(200) NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_SupplyRequests_CreatedAt DEFAULT (SYSUTCDATETIME()),
        ClosedByName nvarchar(200) NULL,
        ClosedAt datetime2(0) NULL,
        CONSTRAINT FK_SupplyRequests_Suppliers FOREIGN KEY (SupplierID) REFERENCES dbo.Suppliers (SupplierID),
        CONSTRAINT CK_SupplyRequests_Status CHECK (Status IN (N'Open', N'Purchased', N'Reverted'))
    );
    CREATE INDEX IX_SupplyRequests_Status ON dbo.SupplyRequests (BusinessID, Status, CreatedAt);
END;

-- One row per follow-up task created when a shortage run is finalized.
IF OBJECT_ID(N'dbo.ShortageTasks', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ShortageTasks
    (
        TaskID uniqueidentifier NOT NULL CONSTRAINT PK_ShortageTasks PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        RunID uniqueidentifier NULL,
        ItemID uniqueidentifier NOT NULL,
        ItemNumber nvarchar(100) NOT NULL,
        ItemName nvarchar(500) NULL,
        TaskType nvarchar(20) NOT NULL,
        Quantity decimal(18, 4) NULL,
        Note nvarchar(500) NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_ShortageTasks_Status DEFAULT (N'Open'),
        AssignedAt datetime2(0) NOT NULL CONSTRAINT DF_ShortageTasks_AssignedAt DEFAULT (SYSUTCDATETIME()),
        AssignedByName nvarchar(200) NULL,
        SupplyRequestID uniqueidentifier NULL,
        ApprovalStatus nvarchar(20) NULL,
        DecidedByName nvarchar(200) NULL,
        DecidedAt datetime2(0) NULL,
        CompletedAt datetime2(0) NULL,
        CompletedByName nvarchar(200) NULL,
        CONSTRAINT FK_ShortageTasks_SupplyRequests FOREIGN KEY (SupplyRequestID) REFERENCES dbo.SupplyRequests (RequestID),
        CONSTRAINT CK_ShortageTasks_Type CHECK (TaskType IN (N'Purchase', N'Warehouse', N'Balance', N'Delete')),
        CONSTRAINT CK_ShortageTasks_Status CHECK (Status IN (N'Open', N'Done', N'Cancelled')),
        CONSTRAINT CK_ShortageTasks_Approval CHECK (ApprovalStatus IS NULL OR ApprovalStatus IN (N'Pending', N'Approved', N'Rejected'))
    );
    CREATE INDEX IX_ShortageTasks_Open ON dbo.ShortageTasks (BusinessID, TaskType, Status, AssignedAt);
    CREATE INDEX IX_ShortageTasks_Item ON dbo.ShortageTasks (ItemID, Status);
END;

-- ------------------------------------------------------------------
-- Phase 6: custom inventories and receipt audits
-- ------------------------------------------------------------------

IF OBJECT_ID(N'dbo.CustomInventories', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomInventories
    (
        CustomInventoryID uniqueidentifier NOT NULL CONSTRAINT PK_CustomInventories PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        InventoryName nvarchar(200) NOT NULL,
        IsActive bit NOT NULL CONSTRAINT DF_CustomInventories_IsActive DEFAULT (1),
        CreatedByName nvarchar(200) NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_CustomInventories_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(3) NOT NULL CONSTRAINT DF_CustomInventories_UpdatedAt DEFAULT (SYSUTCDATETIME())
    );
END;

IF OBJECT_ID(N'dbo.CustomInventoryBranches', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomInventoryBranches
    (
        CustomInventoryID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        CONSTRAINT PK_CustomInventoryBranches PRIMARY KEY (CustomInventoryID, BranchID),
        CONSTRAINT FK_CustomInventoryBranches_Inventories FOREIGN KEY (CustomInventoryID) REFERENCES dbo.CustomInventories (CustomInventoryID)
    );
END;

IF OBJECT_ID(N'dbo.CustomInventoryItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomInventoryItems
    (
        CustomInventoryID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        ItemNumber nvarchar(100) NOT NULL,
        ItemName nvarchar(500) NULL,
        CONSTRAINT PK_CustomInventoryItems PRIMARY KEY (CustomInventoryID, ItemID),
        CONSTRAINT FK_CustomInventoryItems_Inventories FOREIGN KEY (CustomInventoryID) REFERENCES dbo.CustomInventories (CustomInventoryID)
    );
END;

IF COL_LENGTH(N'dbo.InventorySessions', N'CustomInventoryID') IS NULL
BEGIN
    ALTER TABLE dbo.InventorySessions ADD CustomInventoryID uniqueidentifier NULL;
END;

IF OBJECT_ID(N'dbo.ShortageAuditRequests', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ShortageAuditRequests
    (
        RequestID uniqueidentifier NOT NULL CONSTRAINT PK_ShortageAuditRequests PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        BranchName nvarchar(200) NULL,
        RecipientUserID uniqueidentifier NOT NULL,
        RecipientName nvarchar(200) NULL,
        Note nvarchar(500) NULL,
        Status nvarchar(20) NOT NULL CONSTRAINT DF_ShortageAuditRequests_Status DEFAULT (N'Pending'),
        CreatedByName nvarchar(200) NULL,
        CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_ShortageAuditRequests_CreatedAt DEFAULT (SYSUTCDATETIME()),
        ExpiresAt datetime2(0) NOT NULL,
        AnsweredAt datetime2(0) NULL,
        Score decimal(4, 1) NULL,
        CONSTRAINT CK_ShortageAuditRequests_Status CHECK (Status IN (N'Pending', N'Answered', N'Cancelled'))
    );
    CREATE INDEX IX_ShortageAuditRequests_Recipient ON dbo.ShortageAuditRequests (RecipientUserID, Status, CreatedAt);
END;

IF OBJECT_ID(N'dbo.ShortageAuditRequestItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ShortageAuditRequestItems
    (
        RequestItemID uniqueidentifier NOT NULL CONSTRAINT PK_ShortageAuditRequestItems PRIMARY KEY,
        RequestID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        ItemNumber nvarchar(100) NOT NULL,
        ItemName nvarchar(500) NULL,
        ExpectedQuantity decimal(18, 4) NOT NULL,
        ReceivedQuantity decimal(18, 4) NULL,
        CONSTRAINT FK_ShortageAuditRequestItems_Requests FOREIGN KEY (RequestID) REFERENCES dbo.ShortageAuditRequests (RequestID)
    );
END;
