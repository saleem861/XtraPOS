-- Replace the single item/price-type unique rule with the branch pricing rules:
-- one default price (BranchID IS NULL) and one price per branch.
-- Existing rows are checked before the old unique constraint is removed.
-- Completed sales are not read or rewritten.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.ItemPrices', N'U') IS NULL
    RETURN;

IF EXISTS (
        SELECT 1
        FROM dbo.ItemPrices
        WHERE BranchID IS NULL
        GROUP BY ItemID, PriceTypeID
        HAVING COUNT(*) > 1)
    OR EXISTS (
        SELECT 1
        FROM dbo.ItemPrices
        WHERE BranchID IS NOT NULL
        GROUP BY ItemID, PriceTypeID, BranchID
        HAVING COUNT(*) > 1)
    THROW 51045, N'ItemPrices uniqueness conflict. The old unique index was not removed.', 1;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_ItemPrices_Default' AND object_id = OBJECT_ID(N'dbo.ItemPrices'))
    CREATE UNIQUE INDEX UX_ItemPrices_Default
        ON dbo.ItemPrices (ItemID, PriceTypeID)
        WHERE BranchID IS NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_ItemPrices_Branch' AND object_id = OBJECT_ID(N'dbo.ItemPrices'))
    CREATE UNIQUE INDEX UX_ItemPrices_Branch
        ON dbo.ItemPrices (ItemID, PriceTypeID, BranchID)
        WHERE BranchID IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemPrices_BranchID' AND object_id = OBJECT_ID(N'dbo.ItemPrices'))
    CREATE INDEX IX_ItemPrices_BranchID ON dbo.ItemPrices (BranchID);

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_ItemPrices_Default' AND object_id = OBJECT_ID(N'dbo.ItemPrices'))
   AND EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_ItemPrices_Branch' AND object_id = OBJECT_ID(N'dbo.ItemPrices'))
   AND EXISTS (SELECT 1 FROM sys.key_constraints WHERE name = N'UQ_ItemPrices_ItemID_PriceTypeID')
    ALTER TABLE dbo.ItemPrices DROP CONSTRAINT UQ_ItemPrices_ItemID_PriceTypeID;
