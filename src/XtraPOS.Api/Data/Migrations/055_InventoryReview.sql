-- Inventory review (phase 2 of the inventory and shortages module).
-- Supervisor blind audit of a completed count, and single-item quantity corrections («كمية صنف»).
-- An audit never changes stock. A correction posts one InventoryReconciliation movement through
-- StockPostingService; CorrectionID is its source line, so a retried request never posts twice.
-- Adds tables only. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

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
        Status nvarchar(20) NOT NULL CONSTRAINT DF_InventoryAudits_Status DEFAULT (N'Open'),
        StartedAt datetime2(0) NOT NULL CONSTRAINT DF_InventoryAudits_StartedAt DEFAULT (SYSUTCDATETIME()),
        CompletedAt datetime2(0) NULL,
        TotalItems int NOT NULL CONSTRAINT DF_InventoryAudits_TotalItems DEFAULT (0),
        CountedItems int NOT NULL CONSTRAINT DF_InventoryAudits_CountedItems DEFAULT (0),
        MatchedItems int NOT NULL CONSTRAINT DF_InventoryAudits_MatchedItems DEFAULT (0),
        RelayedAt datetime2(0) NULL,
        CONSTRAINT FK_InventoryAudits_Sessions FOREIGN KEY (SessionID) REFERENCES dbo.InventorySessions (SessionID),
        CONSTRAINT CK_InventoryAudits_Mode CHECK (AuditMode IN (N'All', N'Differences', N'Manual')),
        CONSTRAINT CK_InventoryAudits_Status CHECK (Status IN (N'Open', N'Completed', N'Discarded'))
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
        IsCounted bit NOT NULL CONSTRAINT DF_InventoryAuditItems_IsCounted DEFAULT (0),
        AuditQuantity decimal(18, 4) NULL,
        CountedAt datetime2(0) NULL,
        CONSTRAINT FK_InventoryAuditItems_Audits FOREIGN KEY (AuditID) REFERENCES dbo.InventoryAudits (AuditID),
        CONSTRAINT FK_InventoryAuditItems_SessionItems FOREIGN KEY (SessionItemID) REFERENCES dbo.InventorySessionItems (SessionItemID),
        CONSTRAINT UQ_InventoryAuditItems_Audit_Line UNIQUE (AuditID, SessionItemID),
        CONSTRAINT CK_InventoryAuditItems_Quantity CHECK (AuditQuantity IS NULL OR AuditQuantity >= 0)
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
        CorrectedAt datetime2(0) NOT NULL CONSTRAINT DF_InventoryQuantityCorrections_CorrectedAt DEFAULT (SYSUTCDATETIME()),
        StockMovementID uniqueidentifier NULL,
        RelayedAt datetime2(0) NULL,
        CONSTRAINT FK_InventoryQuantityCorrections_Items FOREIGN KEY (ItemID) REFERENCES dbo.Items (ItemID),
        CONSTRAINT CK_InventoryQuantityCorrections_Quantity CHECK (NewQuantity >= 0)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InventoryQuantityCorrections_Item' AND object_id = OBJECT_ID(N'dbo.InventoryQuantityCorrections'))
BEGIN
    CREATE INDEX IX_InventoryQuantityCorrections_Item ON dbo.InventoryQuantityCorrections (BranchID, ItemID, CorrectedAt);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InventorySessions_Report' AND object_id = OBJECT_ID(N'dbo.InventorySessions'))
BEGIN
    CREATE INDEX IX_InventorySessions_Report ON dbo.InventorySessions (BranchID, BusinessDate, Status);
END;
