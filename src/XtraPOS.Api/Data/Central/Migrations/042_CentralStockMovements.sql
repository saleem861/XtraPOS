-- Branch stock movements received at once, and remote corrections by difference (owner decisions, 24 November 2026).
--   * BranchStockMovements: one row per branch stock movement (sale, return, purchase, count, correction...), keyed by
--     the branch StockMovementID so a resend never applies twice. BranchStockSnapshots follows them in near real time.
--   * BranchStockSnapshots.LastMovementAt: newest movement applied, so an older full push never overwrites it.
--   * InventoryCorrectionRequests: expected (branch balance the user saw), counted and difference. The branch adds the
--     difference to its current balance instead of writing the counted number.
--   * InventoryQuantityCorrections (central copy): the same three columns.
-- Tables and columns only. Safe to run again. Back up XtraPOS_Central before publishing.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.BranchStockMovements', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BranchStockMovements
    (
        StockMovementID uniqueidentifier NOT NULL CONSTRAINT PK_BranchStockMovements PRIMARY KEY NONCLUSTERED,
        BranchID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        ItemNumber nvarchar(100) NOT NULL,
        SourceType nvarchar(40) NOT NULL,
        SourceDocumentID uniqueidentifier NULL,
        SourceLineID uniqueidentifier NOT NULL,
        PostingMode nvarchar(20) NOT NULL,
        PreviousQuantity decimal(18, 4) NULL,
        QuantityDelta decimal(18, 4) NOT NULL,
        ResultingQuantity decimal(18, 4) NULL,
        OccurredAt datetime2(0) NOT NULL,
        CreatedAt datetime2(0) NOT NULL,
        ReceivedAt datetime2(0) NOT NULL CONSTRAINT DF_BranchStockMovements_ReceivedAt DEFAULT (SYSUTCDATETIME())
    );
    CREATE CLUSTERED INDEX IX_BranchStockMovements_Branch ON dbo.BranchStockMovements (BranchID, CreatedAt);
    CREATE INDEX IX_BranchStockMovements_Item ON dbo.BranchStockMovements (BranchID, ItemID, CreatedAt);
END;

IF OBJECT_ID(N'dbo.BranchStockSnapshots', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.BranchStockSnapshots', N'LastMovementAt') IS NULL
    ALTER TABLE dbo.BranchStockSnapshots ADD LastMovementAt datetime2(0) NULL;

IF OBJECT_ID(N'dbo.InventoryCorrectionRequests', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.InventoryCorrectionRequests', N'ExpectedQuantity') IS NULL
        ALTER TABLE dbo.InventoryCorrectionRequests ADD ExpectedQuantity decimal(18, 4) NULL;
    IF COL_LENGTH(N'dbo.InventoryCorrectionRequests', N'DeltaQuantity') IS NULL
        ALTER TABLE dbo.InventoryCorrectionRequests ADD DeltaQuantity decimal(18, 4) NULL;
    IF COL_LENGTH(N'dbo.InventoryCorrectionRequests', N'ResultingQuantity') IS NULL
        ALTER TABLE dbo.InventoryCorrectionRequests ADD ResultingQuantity decimal(18, 4) NULL;
END;

IF OBJECT_ID(N'dbo.InventoryQuantityCorrections', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.InventoryQuantityCorrections', N'ExpectedQuantity') IS NULL
        ALTER TABLE dbo.InventoryQuantityCorrections ADD ExpectedQuantity decimal(18, 4) NULL;
    IF COL_LENGTH(N'dbo.InventoryQuantityCorrections', N'CountedQuantity') IS NULL
        ALTER TABLE dbo.InventoryQuantityCorrections ADD CountedQuantity decimal(18, 4) NULL;
    IF COL_LENGTH(N'dbo.InventoryQuantityCorrections', N'DeltaQuantity') IS NULL
        ALTER TABLE dbo.InventoryQuantityCorrections ADD DeltaQuantity decimal(18, 4) NULL;
END;
