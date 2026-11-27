-- Owner decision, 27 November 2026: a speed copied from Shop1/Shop2 is kept on the central snapshot.
-- The daily central recalculation skips SalesRateLocked = 1. Column only. Back up XtraPOS_Central before publishing.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.BranchStockSnapshots', N'U') IS NOT NULL
   AND COL_LENGTH(N'dbo.BranchStockSnapshots', N'SalesRateLocked') IS NULL
BEGIN
    ALTER TABLE dbo.BranchStockSnapshots ADD SalesRateLocked bit NOT NULL
        CONSTRAINT DF_BranchStockSnapshots_SalesRateLocked DEFAULT (0);
END;
