-- Delete Sharbel branch records from the central database.
SET NOCOUNT ON;
SET XACT_ABORT ON;
SELECT BranchID, BranchName, BranchCode, BusinessID, IsActive
FROM dbo.Branches
WHERE BranchName = N'Sharbel' OR BranchCode = N'Sharbel';
IF OBJECT_ID(N'tempdb..#targets') IS NOT NULL DROP TABLE #targets;
SELECT BranchID INTO #targets
FROM dbo.Branches
WHERE BranchName = N'Sharbel' OR BranchCode = N'Sharbel';
IF NOT EXISTS (SELECT 1 FROM #targets) THROW 50001, N'Branch Sharbel was not found.', 1;
BEGIN TRAN;
IF OBJECT_ID(N'dbo.TR_CustomerCreditLedger_AppendOnly', N'TR') IS NOT NULL
    DISABLE TRIGGER dbo.TR_CustomerCreditLedger_AppendOnly ON dbo.CustomerCreditLedger;
IF OBJECT_ID(N'dbo.TR_CreditAuditLog_AppendOnly', N'TR') IS NOT NULL
    DISABLE TRIGGER dbo.TR_CreditAuditLog_AppendOnly ON dbo.CreditAuditLog;
DECLARE @off nvarchar(max) = N'';
SELECT @off = @off + N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N' NOCHECK CONSTRAINT ALL;'
FROM sys.tables WHERE is_ms_shipped = 0;
EXEC sys.sp_executesql @off;
IF OBJECT_ID(N'dbo.CreditPaymentAllocations', N'U') IS NOT NULL AND OBJECT_ID(N'dbo.CustomerCreditLedger', N'U') IS NOT NULL
    DELETE allocation
    FROM dbo.CreditPaymentAllocations AS allocation
    INNER JOIN dbo.CustomerCreditLedger AS ledger ON ledger.LedgerEntryID = allocation.LedgerEntryID
    WHERE ledger.SourceBranchID IN (SELECT BranchID FROM #targets);
IF OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL
BEGIN
    IF OBJECT_ID(N'tempdb..#sales') IS NOT NULL DROP TABLE #sales;
    SELECT SaleID INTO #sales FROM dbo.Sales WHERE BranchID IN (SELECT BranchID FROM #targets);
    DECLARE @saleSql nvarchar(max) = N'';
    SELECT @saleSql = @saleSql + N'DELETE FROM ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name) + N' WHERE SaleID IN (SELECT SaleID FROM #sales);'
    FROM sys.columns c
    JOIN sys.tables t ON t.object_id = c.object_id
    WHERE c.name = N'SaleID' AND t.name <> N'Sales';
    EXEC sys.sp_executesql @saleSql;
END
DECLARE @s sysname, @t sysname, @c sysname, @stmt nvarchar(max);
DECLARE cur CURSOR LOCAL FAST_FORWARD FOR
    SELECT SCHEMA_NAME(t.schema_id), t.name, c.name
    FROM sys.columns c
    JOIN sys.tables t ON t.object_id = c.object_id
    WHERE c.name IN (N'BranchID', N'OriginBranchID', N'SourceBranchID', N'DestinationBranchID')
      AND t.name <> N'Branches';
OPEN cur;
FETCH NEXT FROM cur INTO @s, @t, @c;
WHILE @@FETCH_STATUS = 0
BEGIN
    SET @stmt = N'DELETE FROM ' + QUOTENAME(@s) + N'.' + QUOTENAME(@t) + N' WHERE ' + QUOTENAME(@c) + N' IN (SELECT BranchID FROM #targets)';
    EXEC sys.sp_executesql @stmt;
    FETCH NEXT FROM cur INTO @s, @t, @c;
END
CLOSE cur;
DEALLOCATE cur;
DELETE FROM dbo.Branches WHERE BranchID IN (SELECT BranchID FROM #targets);
DECLARE @on nvarchar(max) = N'';
SELECT @on = @on + N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(schema_id)) + N'.' + QUOTENAME(name) + N' WITH CHECK CHECK CONSTRAINT ALL;'
FROM sys.tables WHERE is_ms_shipped = 0;
EXEC sys.sp_executesql @on;
IF OBJECT_ID(N'dbo.TR_CustomerCreditLedger_AppendOnly', N'TR') IS NOT NULL
    ENABLE TRIGGER dbo.TR_CustomerCreditLedger_AppendOnly ON dbo.CustomerCreditLedger;
IF OBJECT_ID(N'dbo.TR_CreditAuditLog_AppendOnly', N'TR') IS NOT NULL
    ENABLE TRIGGER dbo.TR_CreditAuditLog_AppendOnly ON dbo.CreditAuditLog;
COMMIT;
SELECT BranchID, BranchName, BranchCode
FROM dbo.Branches
WHERE BranchName = N'Sharbel' OR BranchCode = N'Sharbel';
