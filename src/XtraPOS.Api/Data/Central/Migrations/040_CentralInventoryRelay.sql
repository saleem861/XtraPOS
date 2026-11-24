-- Central copies of branch inventory data (phase 3 of the inventory and shortages module).
-- Branches push these every minute (InventoryRelayService through PosDeviceSettingsSyncWorker):
--   * BranchStockSnapshots: each branch's stock-owner balances and daily sales rate, with the time it was received.
--   * InventorySessions / Pages / Items, InventoryAudits / Items, InventoryQuantityCorrections: finished
--     counts, audits and corrections, with the same table and column names as on the branch so the report reads
--     either side with the same SQL.
-- Central never writes branch stock from these tables. Adds tables only. Safe to run again.
-- Back up XtraPOS_Central before publishing.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.BranchStockSnapshots', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BranchStockSnapshots
    (
        BranchID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        ItemNumber nvarchar(100) NOT NULL,
        OnHandQuantity decimal(18, 4) NULL,
        IsQuantityInitialized bit NOT NULL,
        DailySalesRate decimal(18, 6) NULL,
        BalanceUpdatedAt datetime2(0) NOT NULL,
        ReceivedAt datetime2(0) NOT NULL CONSTRAINT DF_BranchStockSnapshots_ReceivedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_BranchStockSnapshots PRIMARY KEY (BranchID, ItemID)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BranchStockSnapshots_Item' AND object_id = OBJECT_ID(N'dbo.BranchStockSnapshots'))
BEGIN
    CREATE INDEX IX_BranchStockSnapshots_Item ON dbo.BranchStockSnapshots (ItemID) INCLUDE (OnHandQuantity, IsQuantityInitialized, DailySalesRate);
END;

-- One row per branch: how fresh its copy is. The shortage screens warn when LastPushAt is older than 30 minutes.
IF OBJECT_ID(N'dbo.BranchSnapshotStatus', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BranchSnapshotStatus
    (
        BranchID uniqueidentifier NOT NULL CONSTRAINT PK_BranchSnapshotStatus PRIMARY KEY,
        LastPushAt datetime2(0) NOT NULL,
        LastBalanceUpdatedAt datetime2(0) NULL,
        LastFullPushAt datetime2(0) NULL,
        SalesRateDate date NULL,
        ItemCount int NOT NULL CONSTRAINT DF_BranchSnapshotStatus_ItemCount DEFAULT (0)
    );
END;

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
        Status nvarchar(20) NOT NULL,
        StartedAt datetime2(0) NOT NULL,
        LastSavedAt datetime2(0) NOT NULL,
        CompletedAt datetime2(0) NULL,
        DiscardedAt datetime2(0) NULL,
        TotalItems int NOT NULL,
        CountedItems int NOT NULL,
        PostedItems int NOT NULL,
        RelayedAt datetime2(0) NULL,
        ReceivedAt datetime2(0) NOT NULL CONSTRAINT DF_InventorySessions_ReceivedAt DEFAULT (SYSUTCDATETIME())
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InventorySessions_Report' AND object_id = OBJECT_ID(N'dbo.InventorySessions'))
BEGIN
    CREATE INDEX IX_InventorySessions_Report ON dbo.InventorySessions (BranchID, BusinessDate, Status);
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
        IsCounted bit NOT NULL,
        CountedQuantity decimal(18, 4) NULL,
        SystemQuantityAtCount decimal(18, 4) NULL,
        SystemQuantityKnown bit NOT NULL,
        CountedAt datetime2(0) NULL,
        CountedByUserID uniqueidentifier NULL,
        CountedByName nvarchar(200) NULL,
        StockMovementID uniqueidentifier NULL,
        PostedAt datetime2(0) NULL,
        QuantityBeforePosting decimal(18, 4) NULL,
        QuantityAfterPosting decimal(18, 4) NULL,
        CONSTRAINT FK_InventorySessionItems_Sessions FOREIGN KEY (SessionID) REFERENCES dbo.InventorySessions (SessionID)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InventorySessionItems_Session' AND object_id = OBJECT_ID(N'dbo.InventorySessionItems'))
BEGIN
    CREATE INDEX IX_InventorySessionItems_Session ON dbo.InventorySessionItems (SessionID, ItemID);
END;

IF OBJECT_ID(N'dbo.InventoryAudits', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InventoryAudits
    (
        AuditID uniqueidentifier NOT NULL CONSTRAINT PK_InventoryAudits PRIMARY KEY,
        SessionID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        UserID uniqueidentifier NOT NULL,
        UserName nvarchar(200) NULL,
        AuditMode nvarchar(20) NOT NULL,
        Status nvarchar(20) NOT NULL,
        StartedAt datetime2(0) NOT NULL,
        CompletedAt datetime2(0) NULL,
        TotalItems int NOT NULL,
        CountedItems int NOT NULL,
        MatchedItems int NOT NULL,
        RelayedAt datetime2(0) NULL,
        ReceivedAt datetime2(0) NOT NULL CONSTRAINT DF_InventoryAudits_ReceivedAt DEFAULT (SYSUTCDATETIME())
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InventoryAudits_Session' AND object_id = OBJECT_ID(N'dbo.InventoryAudits'))
BEGIN
    CREATE INDEX IX_InventoryAudits_Session ON dbo.InventoryAudits (SessionID, Status);
END;

IF OBJECT_ID(N'dbo.InventoryAuditItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InventoryAuditItems
    (
        AuditItemID uniqueidentifier NOT NULL CONSTRAINT PK_InventoryAuditItems PRIMARY KEY,
        AuditID uniqueidentifier NOT NULL,
        SessionItemID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        ItemNumber nvarchar(100) NOT NULL,
        ItemName nvarchar(500) NULL,
        IsCounted bit NOT NULL,
        AuditQuantity decimal(18, 4) NULL,
        CountedAt datetime2(0) NULL,
        CONSTRAINT FK_InventoryAuditItems_Audits FOREIGN KEY (AuditID) REFERENCES dbo.InventoryAudits (AuditID)
    );
END;

IF OBJECT_ID(N'dbo.InventoryQuantityCorrections', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.InventoryQuantityCorrections
    (
        CorrectionID uniqueidentifier NOT NULL CONSTRAINT PK_InventoryQuantityCorrections PRIMARY KEY,
        BranchID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        ItemNumber nvarchar(100) NOT NULL,
        ItemName nvarchar(500) NULL,
        PreviousQuantity decimal(18, 4) NULL,
        NewQuantity decimal(18, 4) NOT NULL,
        Reason nvarchar(500) NOT NULL,
        UserID uniqueidentifier NOT NULL,
        UserName nvarchar(200) NULL,
        CorrectedAt datetime2(0) NOT NULL,
        StockMovementID uniqueidentifier NULL,
        RelayedAt datetime2(0) NULL,
        ReceivedAt datetime2(0) NOT NULL CONSTRAINT DF_InventoryQuantityCorrections_ReceivedAt DEFAULT (SYSUTCDATETIME())
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InventoryQuantityCorrections_Item' AND object_id = OBJECT_ID(N'dbo.InventoryQuantityCorrections'))
BEGIN
    CREATE INDEX IX_InventoryQuantityCorrections_Item ON dbo.InventoryQuantityCorrections (BranchID, ItemID, CorrectedAt);
END;
