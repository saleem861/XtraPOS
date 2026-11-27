-- Owner decision, 27 November 2026: speeds copied from Shop1 (المقاطعة) and Shop2 (Labtop)
-- stay on the item. SalesRateLocked = 1 is not replaced by the daily sales-rate job.
-- Column only. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.ItemBranchInventory', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.ItemBranchInventory', N'SalesRateLocked') IS NULL
BEGIN
    ALTER TABLE dbo.ItemBranchInventory ADD SalesRateLocked bit NOT NULL
        CONSTRAINT DF_ItemBranchInventory_SalesRateLocked DEFAULT (0);
END;
