-- A saved first count stays in the log. A later count on the same shift must still be allowed.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_CashDrawerCounts_OpenAttempt' AND object_id = OBJECT_ID(N'dbo.CashDrawerCounts'))
    DROP INDEX UX_CashDrawerCounts_OpenAttempt ON dbo.CashDrawerCounts;
