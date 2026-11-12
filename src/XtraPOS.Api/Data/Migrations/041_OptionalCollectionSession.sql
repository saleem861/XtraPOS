-- Office collections may be recorded without an open cash session. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.BranchDebtCollections', N'U') IS NOT NULL
   AND EXISTS (
       SELECT 1 FROM sys.columns
       WHERE object_id = OBJECT_ID(N'dbo.BranchDebtCollections')
         AND name = N'CashSessionID'
         AND is_nullable = 0)
BEGIN
    ALTER TABLE dbo.BranchDebtCollections DROP CONSTRAINT FK_BranchDebtCollections_Session;
    ALTER TABLE dbo.BranchDebtCollections ALTER COLUMN CashSessionID uniqueidentifier NULL;
    ALTER TABLE dbo.BranchDebtCollections
        ADD CONSTRAINT FK_BranchDebtCollections_Session
        FOREIGN KEY (CashSessionID, BranchID) REFERENCES dbo.POSCashSessions (CashSessionID, BranchID);
END;
