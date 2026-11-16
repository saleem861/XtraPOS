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

DELETE station
FROM dbo.POSStations AS station
WHERE station.BranchID <> @keep
  AND NOT EXISTS
  (
      SELECT 1
      FROM dbo.Sales AS sale
      WHERE sale.POSStationID = station.StationID
  );

DELETE branch
FROM dbo.Branches AS branch
WHERE branch.BranchID <> @keep
  AND NOT EXISTS (SELECT 1 FROM dbo.Sales AS sale WHERE sale.BranchID = branch.BranchID)
  AND NOT EXISTS (SELECT 1 FROM dbo.POSStations AS station WHERE station.BranchID = branch.BranchID)
  AND NOT EXISTS (SELECT 1 FROM dbo.CentralPosRegistry AS registry WHERE registry.BranchID = branch.BranchID);

COMMIT TRANSACTION;
