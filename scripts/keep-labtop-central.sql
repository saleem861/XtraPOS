-- Manual form of the same cleanup the branch agent runs once after this build.
-- It keeps the branch named Labtop. Sales stay. A branch that still has sales stays.
-- Point numbers that do not belong to Labtop are released.
-- One-time cleanup for XtraPOS_Central.
-- Keeps the branch named Labtop and its stations.
-- Frees point numbers by deleting registry rows that do not belong to Labtop.
-- Does not delete sales and does not renumber Labtop stations.
-- A branch or station that still has sales, or another foreign key, stays.
SET XACT_ABORT ON;

DECLARE @keep uniqueidentifier =
(
    SELECT TOP (1) BranchID
    FROM dbo.Branches
    WHERE BranchName = N'Labtop'
);

IF @keep IS NULL
    THROW 50001, N'Branch Labtop was not found. Nothing was deleted.', 1;

BEGIN TRANSACTION;

IF OBJECT_ID(N'dbo.CentralPosReplica', N'U') IS NOT NULL
    DELETE replica
    FROM dbo.CentralPosReplica AS replica
    WHERE replica.PosNumber NOT IN
    (
        SELECT registry.PosNumber
        FROM dbo.CentralPosRegistry AS registry
        WHERE registry.BranchID = @keep
    );

DELETE registry
FROM dbo.CentralPosRegistry AS registry
WHERE registry.BranchID <> @keep;

COMMIT TRANSACTION;

BEGIN TRANSACTION;

DECLARE @childSql nvarchar(max) = N'';
SELECT @childSql = @childSql
    + N'DELETE child FROM ' + QUOTENAME(SCHEMA_NAME(child.schema_id)) + N'.' + QUOTENAME(child.name)
    + N' AS child WHERE child.' + QUOTENAME(columnName.name) + N' <> @keep AND child.' + QUOTENAME(columnName.name) + N' IS NOT NULL;'
FROM sys.foreign_keys AS link
INNER JOIN sys.foreign_key_columns AS pair ON pair.constraint_object_id = link.object_id
INNER JOIN sys.tables AS child ON child.object_id = link.parent_object_id
INNER JOIN sys.columns AS columnName ON columnName.object_id = child.object_id AND columnName.column_id = pair.parent_column_id
INNER JOIN sys.tables AS parent ON parent.object_id = link.referenced_object_id
INNER JOIN sys.columns AS parentColumn ON parentColumn.object_id = parent.object_id AND parentColumn.column_id = pair.referenced_column_id
WHERE parent.name = N'Branches' AND parentColumn.name = N'BranchID'
  AND child.name NOT IN (N'Sales', N'SaleItems', N'SalePayments', N'POSCashSessions', N'POSStations', N'Devices', N'Branches');
EXEC sys.sp_executesql @childSql, N'@keep uniqueidentifier', @keep = @keep;

IF OBJECT_ID(N'dbo.POSCashSessions', N'U') IS NOT NULL
    DELETE session
    FROM dbo.POSCashSessions AS session
    WHERE session.BranchID <> @keep
      AND NOT EXISTS (SELECT 1 FROM dbo.Sales AS sale WHERE sale.CashSessionID = session.CashSessionID)
      AND NOT EXISTS (SELECT 1 FROM dbo.SalePayments AS payment WHERE payment.CashSessionID = session.CashSessionID);

IF OBJECT_ID(N'dbo.Devices', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Devices', N'BranchID') IS NOT NULL
    DELETE device
    FROM dbo.Devices AS device
    WHERE device.BranchID <> @keep
      AND NOT EXISTS (SELECT 1 FROM dbo.Sales AS sale WHERE sale.DeviceID = device.DeviceID)
      AND NOT EXISTS (SELECT 1 FROM dbo.POSCashSessions AS session WHERE session.DeviceID = device.DeviceID);

DELETE station
FROM dbo.POSStations AS station
WHERE station.BranchID <> @keep
  AND NOT EXISTS (SELECT 1 FROM dbo.Sales AS sale WHERE sale.POSStationID = station.StationID)
  AND NOT EXISTS (SELECT 1 FROM dbo.POSCashSessions AS session WHERE session.POSStationID = station.StationID);

DELETE branch
FROM dbo.Branches AS branch
WHERE branch.BranchID <> @keep
  AND NOT EXISTS (SELECT 1 FROM dbo.Sales AS sale WHERE sale.BranchID = branch.BranchID)
  AND NOT EXISTS (SELECT 1 FROM dbo.POSStations AS station WHERE station.BranchID = branch.BranchID)
  AND NOT EXISTS (SELECT 1 FROM dbo.POSCashSessions AS session WHERE session.BranchID = branch.BranchID)
  AND NOT EXISTS (SELECT 1 FROM dbo.CentralPosRegistry AS registry WHERE registry.BranchID = branch.BranchID);

COMMIT TRANSACTION;
