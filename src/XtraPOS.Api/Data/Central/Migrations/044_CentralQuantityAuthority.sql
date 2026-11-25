-- Central is the quantity authority in a centralized system (owner decision, 25 November 2026).
--   * BranchStockMovements.Origin / AppliedToBalance: where a movement came from and whether it changed the central
--     balance. Branch = a branch movement (sale, return, purchase...), added to the balance by its difference.
--     Central = a count or correction posted on central first. Echo = the branch applying a central correction
--     (already counted on central, so not added again). History = past sales sent once for the sales rate only.
--   * InventoryCorrectionRequests.CentralAppliedAt / CentralPreviousQuantity / CentralResultingQuantity / SourceType:
--     the correction was posted on central first; the branch takes the difference at its next sync.
--   * BranchStockSnapshots.BranchReportedQuantity / BranchReportedAt: the branch's own balance from the daily push,
--     kept as a difference report only. It never overwrites the central balance once central knows the item.
--   * BranchSnapshotStatus.FirstSaleAt / CentralRateDate: first sale seen on central and the day the sales rate was
--     last recalculated on central for the whole branch.
-- Columns only. Safe to run again. Back up XtraPOS_Central before publishing.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.BranchStockMovements', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.BranchStockMovements', N'Origin') IS NULL
        ALTER TABLE dbo.BranchStockMovements ADD Origin nvarchar(10) NOT NULL
            CONSTRAINT DF_BranchStockMovements_Origin DEFAULT (N'Branch');
    IF COL_LENGTH(N'dbo.BranchStockMovements', N'AppliedToBalance') IS NULL
        ALTER TABLE dbo.BranchStockMovements ADD AppliedToBalance bit NOT NULL
            CONSTRAINT DF_BranchStockMovements_AppliedToBalance DEFAULT (1);
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.BranchStockMovements') AND name = N'IX_BranchStockMovements_Sales')
        EXEC (N'CREATE INDEX IX_BranchStockMovements_Sales ON dbo.BranchStockMovements (BranchID, OccurredAt)
               INCLUDE (ItemID, QuantityDelta) WHERE SourceType IN (N''Sale'', N''SaleReturn'');');
END;

IF OBJECT_ID(N'dbo.InventoryCorrectionRequests', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.InventoryCorrectionRequests', N'CentralAppliedAt') IS NULL
        ALTER TABLE dbo.InventoryCorrectionRequests ADD CentralAppliedAt datetime2(0) NULL;
    IF COL_LENGTH(N'dbo.InventoryCorrectionRequests', N'CentralPreviousQuantity') IS NULL
        ALTER TABLE dbo.InventoryCorrectionRequests ADD CentralPreviousQuantity decimal(18, 4) NULL;
    IF COL_LENGTH(N'dbo.InventoryCorrectionRequests', N'CentralResultingQuantity') IS NULL
        ALTER TABLE dbo.InventoryCorrectionRequests ADD CentralResultingQuantity decimal(18, 4) NULL;
    IF COL_LENGTH(N'dbo.InventoryCorrectionRequests', N'SourceType') IS NULL
        ALTER TABLE dbo.InventoryCorrectionRequests ADD SourceType nvarchar(40) NULL;
END;

IF OBJECT_ID(N'dbo.BranchStockSnapshots', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.BranchStockSnapshots', N'BranchReportedQuantity') IS NULL
        ALTER TABLE dbo.BranchStockSnapshots ADD BranchReportedQuantity decimal(18, 4) NULL;
    IF COL_LENGTH(N'dbo.BranchStockSnapshots', N'BranchReportedAt') IS NULL
        ALTER TABLE dbo.BranchStockSnapshots ADD BranchReportedAt datetime2(0) NULL;
END;

IF OBJECT_ID(N'dbo.BranchSnapshotStatus', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.BranchSnapshotStatus', N'FirstSaleAt') IS NULL
        ALTER TABLE dbo.BranchSnapshotStatus ADD FirstSaleAt datetime2(0) NULL;
    IF COL_LENGTH(N'dbo.BranchSnapshotStatus', N'CentralRateDate') IS NULL
        ALTER TABLE dbo.BranchSnapshotStatus ADD CentralRateDate date NULL;
END;
