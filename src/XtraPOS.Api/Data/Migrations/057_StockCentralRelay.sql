-- Stock movements relayed to central at once (owner decision, 24 November 2026), and remote corrections by difference.
--   * StockMovements.RelayOperationID / CentralRelayedAt: every quantity movement is logged in BranchOperationLog
--     (type StockMovement) inside the posting transaction, then sent to central. Movements already on the branch
--     before this migration are marked as sent: the daily full balance push already covers them.
--   * InventoryQuantityCorrections: expected, counted and difference columns. A correction can leave a negative
--     balance (sales after the count), so the non-negative check on NewQuantity is removed.
-- Columns and one index only. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.StockMovements', N'RelayOperationID') IS NULL
    ALTER TABLE dbo.StockMovements ADD RelayOperationID uniqueidentifier NULL;

IF COL_LENGTH(N'dbo.StockMovements', N'CentralRelayedAt') IS NULL
BEGIN
    ALTER TABLE dbo.StockMovements ADD CentralRelayedAt datetime2(0) NULL;
    EXEC (N'UPDATE dbo.StockMovements SET CentralRelayedAt = SYSUTCDATETIME() WHERE CentralRelayedAt IS NULL;');
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(N'dbo.StockMovements') AND name = N'IX_StockMovements_CentralPending')
    EXEC (N'CREATE INDEX IX_StockMovements_CentralPending ON dbo.StockMovements (CreatedAt, StockMovementID)
           INCLUDE (RelayOperationID) WHERE CentralRelayedAt IS NULL;');

IF OBJECT_ID(N'dbo.InventoryQuantityCorrections', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.InventoryQuantityCorrections', N'ExpectedQuantity') IS NULL
        ALTER TABLE dbo.InventoryQuantityCorrections ADD ExpectedQuantity decimal(18, 4) NULL;
    IF COL_LENGTH(N'dbo.InventoryQuantityCorrections', N'CountedQuantity') IS NULL
        ALTER TABLE dbo.InventoryQuantityCorrections ADD CountedQuantity decimal(18, 4) NULL;
    IF COL_LENGTH(N'dbo.InventoryQuantityCorrections', N'DeltaQuantity') IS NULL
        ALTER TABLE dbo.InventoryQuantityCorrections ADD DeltaQuantity decimal(18, 4) NULL;
    IF OBJECT_ID(N'dbo.CK_InventoryQuantityCorrections_Quantity', N'C') IS NOT NULL
        ALTER TABLE dbo.InventoryQuantityCorrections DROP CONSTRAINT CK_InventoryQuantityCorrections_Quantity;
END;
