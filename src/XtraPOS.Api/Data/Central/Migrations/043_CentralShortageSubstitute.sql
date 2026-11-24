-- Substitute item on a delete request (deferred item, executed 24 November 2026, «نفذ»).
-- A delete decision can name a substitute item; it is resolved to the stock owner and kept with the run item and the
-- follow-up task, so the manager sees it when approving the delete. Columns only. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.ShortageRunItems', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.ShortageRunItems', N'SubstituteItemID') IS NULL
        ALTER TABLE dbo.ShortageRunItems ADD SubstituteItemID uniqueidentifier NULL;
    IF COL_LENGTH(N'dbo.ShortageRunItems', N'SubstituteItemNumber') IS NULL
        ALTER TABLE dbo.ShortageRunItems ADD SubstituteItemNumber nvarchar(100) NULL;
END;

IF OBJECT_ID(N'dbo.ShortageTasks', N'U') IS NOT NULL
BEGIN
    IF COL_LENGTH(N'dbo.ShortageTasks', N'SubstituteItemID') IS NULL
        ALTER TABLE dbo.ShortageTasks ADD SubstituteItemID uniqueidentifier NULL;
    IF COL_LENGTH(N'dbo.ShortageTasks', N'SubstituteItemNumber') IS NULL
        ALTER TABLE dbo.ShortageTasks ADD SubstituteItemNumber nvarchar(100) NULL;
END;
