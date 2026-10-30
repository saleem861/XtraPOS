-- Stock movement source identity for idempotent posting.
-- Adds columns and lookup codes only. Does not update balances, costs, or existing movements.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.StockMovements', N'SourceType') IS NULL
    ALTER TABLE dbo.StockMovements ADD SourceType nvarchar(40) NULL;

IF COL_LENGTH(N'dbo.StockMovements', N'SourceDocumentID') IS NULL
    ALTER TABLE dbo.StockMovements ADD SourceDocumentID uniqueidentifier NULL;

IF COL_LENGTH(N'dbo.StockMovements', N'SourceLineID') IS NULL
    ALTER TABLE dbo.StockMovements ADD SourceLineID uniqueidentifier NULL;

IF COL_LENGTH(N'dbo.StockMovements', N'PostingMode') IS NULL
    ALTER TABLE dbo.StockMovements ADD PostingMode nvarchar(20) NULL;

IF COL_LENGTH(N'dbo.StockMovements', N'PreviousQuantity') IS NULL
    ALTER TABLE dbo.StockMovements ADD PreviousQuantity decimal(18, 4) NULL;

IF COL_LENGTH(N'dbo.StockMovements', N'ObservedQuantity') IS NULL
    ALTER TABLE dbo.StockMovements ADD ObservedQuantity decimal(18, 4) NULL;

IF COL_LENGTH(N'dbo.StockMovements', N'ResultingQuantity') IS NULL
    ALTER TABLE dbo.StockMovements ADD ResultingQuantity decimal(18, 4) NULL;

IF COL_LENGTH(N'dbo.StockMovements', N'ResultingUnitCost') IS NULL
    ALTER TABLE dbo.StockMovements ADD ResultingUnitCost decimal(19, 4) NULL;

IF NOT EXISTS (
    SELECT 1
    FROM sys.check_constraints
    WHERE name = N'CK_StockMovements_SourceType'
      AND parent_object_id = OBJECT_ID(N'dbo.StockMovements'))
    EXEC(N'
        ALTER TABLE dbo.StockMovements ADD CONSTRAINT CK_StockMovements_SourceType
            CHECK (SourceType IS NULL OR SourceType IN (
                N''Sale'',
                N''Purchase'',
                N''SaleReturn'',
                N''PurchaseReturn'',
                N''TransferOut'',
                N''TransferIn'',
                N''TransferReversal'',
                N''TransferRejectReturn'',
                N''InventoryCount'',
                N''OpeningBalance'',
                N''InventoryReconciliation'',
                N''Damage'',
                N''CostAdjustment''));
    ');

IF NOT EXISTS (
    SELECT 1
    FROM sys.check_constraints
    WHERE name = N'CK_StockMovements_PostingMode'
      AND parent_object_id = OBJECT_ID(N'dbo.StockMovements'))
    EXEC(N'
        ALTER TABLE dbo.StockMovements ADD CONSTRAINT CK_StockMovements_PostingMode
            CHECK (PostingMode IS NULL OR PostingMode IN (N''Delta'', N''Absolute''));
    ');

IF NOT EXISTS (
    SELECT 1
    FROM sys.indexes
    WHERE name = N'UX_StockMovements_Branch_SourceLine'
      AND object_id = OBJECT_ID(N'dbo.StockMovements'))
    EXEC(N'
        CREATE UNIQUE INDEX UX_StockMovements_Branch_SourceLine
            ON dbo.StockMovements (BranchID, SourceType, SourceLineID)
            WHERE SourceLineID IS NOT NULL;
    ');

INSERT INTO dbo.StockMovementTypes (MovementCode, MovementName, SortOrder)
SELECT seed.MovementCode, seed.MovementName, seed.SortOrder
FROM (VALUES
    (N'TransferReversal', N'عكس تحويل', 120),
    (N'TransferRejectReturn', N'إعادة كمية مرفوضة', 130),
    (N'InventoryCount', N'جرد فعلي', 140),
    (N'InventoryReconciliation', N'مطابقة مخزون', 150),
    (N'CostAdjustment', N'تعديل تكلفة', 160)
) AS seed (MovementCode, MovementName, SortOrder)
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.StockMovementTypes AS existing
    WHERE existing.MovementCode = seed.MovementCode);
