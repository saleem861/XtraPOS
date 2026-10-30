-- Lease columns so one worker claims an outbox row. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.BranchFinancialOutbox', N'LeaseOwner') IS NULL
    ALTER TABLE dbo.BranchFinancialOutbox ADD LeaseOwner nvarchar(80) NULL;

IF COL_LENGTH(N'dbo.BranchFinancialOutbox', N'LeaseUntil') IS NULL
    ALTER TABLE dbo.BranchFinancialOutbox ADD LeaseUntil datetime2(0) NULL;
