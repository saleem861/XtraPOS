-- Optimistic concurrency and audit identity for central master edits.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.Items', N'RowVersion') IS NULL
    ALTER TABLE dbo.Items ADD RowVersion rowversion NOT NULL;

IF COL_LENGTH(N'dbo.ItemUnits', N'RowVersion') IS NULL
    ALTER TABLE dbo.ItemUnits ADD RowVersion rowversion NOT NULL;

IF COL_LENGTH(N'dbo.ItemPrices', N'RowVersion') IS NULL
    ALTER TABLE dbo.ItemPrices ADD RowVersion rowversion NOT NULL;

IF COL_LENGTH(N'dbo.SyncChangeLog', N'ChangedByUserID') IS NULL
    ALTER TABLE dbo.SyncChangeLog ADD ChangedByUserID uniqueidentifier NULL;
