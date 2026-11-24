-- Inventory import, step 2 of 2 (phase 7 of the inventory and shortages module).
--
-- Run on XtraPOS_Central AFTER a full backup. Branches copy the result within a minute
-- (InventoryCountService.PullSettingsAsync), so nothing is run on the branch databases.
--
--   1. Paste every row from step 1 (scripts\inventory-import-1-export.sql, run on saleemPOSDB) under BLOCK 1.
--   2. Run with @Apply = 0. Read the preview. Nothing is written.
--   3. Set @Apply = 1 and run again. One transaction: everything or nothing.
--
-- Safe to run again: a row is written only when its values change, so a second run writes nothing.
-- Classification of items that are not in the legacy export is left as it is.
-- The schedule becomes the legacy schedule: a day/period missing there is disabled here.

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @Apply bit = 0;   -- 0 = preview only, 1 = write

IF DB_NAME() NOT LIKE N'XtraPOS[_]Central%'
BEGIN
    RAISERROR(N'Run this on XtraPOS_Central. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END;

IF OBJECT_ID(N'dbo.ItemInventorySettings', N'U') IS NULL OR OBJECT_ID(N'dbo.InventorySchedule', N'U') IS NULL
BEGIN
    RAISERROR(N'Central migration 039_CentralInventorySettings is not applied yet. Update the API first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END;

IF OBJECT_ID(N'tempdb..#legacySettings') IS NOT NULL DROP TABLE #legacySettings;
IF OBJECT_ID(N'tempdb..#legacyItems') IS NOT NULL DROP TABLE #legacyItems;
IF OBJECT_ID(N'tempdb..#legacySchedule') IS NOT NULL DROP TABLE #legacySchedule;
CREATE TABLE #legacySettings (PeriodsPerDay int NULL, PeriodicPageCount int NULL);
CREATE TABLE #legacyItems (ItemNumber nvarchar(200) COLLATE DATABASE_DEFAULT NOT NULL, IsImportant int NULL, InventoryPage decimal(18, 4) NULL, SupplyDays decimal(18, 4) NULL);
CREATE TABLE #legacySchedule (ScheduleID bigint NULL, DayOfWeek int NULL, PeriodType nvarchar(20) COLLATE DATABASE_DEFAULT NULL, InventoryPage decimal(18, 4) NULL, IsEnabled int NULL);

-- ===================== BLOCK 1: paste all rows from step 1 below this line =====================



-- ===================== Do not edit below this line =====================

IF NOT EXISTS (SELECT 1 FROM #legacyItems)
BEGIN
    RAISERROR(N'BLOCK 1 is empty. Paste the rows from step 1 first. Nothing was changed.', 16, 1);
    SET NOEXEC ON;
END;

DECLARE @periods int = COALESCE((SELECT TOP (1) PeriodsPerDay FROM #legacySettings), 2);
DECLARE @pageCount int = COALESCE((SELECT TOP (1) PeriodicPageCount FROM #legacySettings), 12);
DECLARE @by nvarchar(200) = N'XtraPOS Inventory import';
DECLARE @now datetime2(3) = SYSUTCDATETIME();

-- Classification, cleaned to what XtraPOS accepts.
SELECT
    LTRIM(RTRIM(ItemNumber)) AS ItemNumber,
    CAST(MAX(CASE WHEN IsImportant = 1 OR InventoryPage = 0 THEN 1 ELSE 0 END) AS bit) AS IsImportant,
    CAST(MAX(CASE WHEN InventoryPage BETWEEN 0 AND 12 AND InventoryPage = ROUND(InventoryPage, 0) THEN InventoryPage END) AS tinyint) AS InventoryPage,
    CAST(MAX(CASE WHEN ROUND(SupplyDays, 0) BETWEEN 1 AND 365 THEN ROUND(SupplyDays, 0) END) AS int) AS SupplyDays,
    MAX(CASE WHEN InventoryPage IS NOT NULL AND NOT (InventoryPage BETWEEN 0 AND 12 AND InventoryPage = ROUND(InventoryPage, 0)) THEN 1 ELSE 0 END) AS BadPage,
    MAX(CASE WHEN SupplyDays IS NOT NULL AND NOT (ROUND(SupplyDays, 0) BETWEEN 1 AND 365) THEN 1 ELSE 0 END) AS BadDays
INTO #items
FROM #legacyItems
WHERE LEN(LTRIM(RTRIM(ItemNumber))) BETWEEN 1 AND 100
GROUP BY LTRIM(RTRIM(ItemNumber));

-- Schedule. With one period a day the legacy app used the day's row all day, so it fills AM and PM.
WITH legacy AS
(
    SELECT ScheduleID, DayOfWeek, UPPER(LTRIM(RTRIM(COALESCE(PeriodType, N'')))) AS PeriodType,
           CAST(InventoryPage AS int) AS InventoryPage, CASE WHEN IsEnabled = 1 THEN 1 ELSE 0 END AS IsEnabled
    FROM #legacySchedule
    WHERE DayOfWeek BETWEEN 0 AND 6 AND InventoryPage BETWEEN 1 AND 12 AND InventoryPage = ROUND(InventoryPage, 0)
),
expanded AS
(
    SELECT legacy.*, slot.Period,
           CASE WHEN legacy.PeriodType = slot.Period THEN 0 ELSE 1 END AS Distance
    FROM legacy
    CROSS JOIN (VALUES (N'AM'), (N'PM')) AS slot (Period)
    WHERE @periods <= 1 OR legacy.PeriodType = slot.Period OR legacy.PeriodType NOT IN (N'AM', N'PM')
),
ranked AS
(
    SELECT expanded.*,
           ROW_NUMBER() OVER (PARTITION BY DayOfWeek, Period ORDER BY IsEnabled DESC, Distance, ScheduleID) AS Pick
    FROM expanded
)
SELECT CAST(DayOfWeek AS tinyint) AS DayOfWeek, Period AS PeriodType, CAST(InventoryPage AS tinyint) AS InventoryPage, CAST(IsEnabled AS bit) AS IsEnabled
INTO #schedule
FROM ranked
WHERE Pick = 1;

-- ===================== Preview =====================

SELECT
    (SELECT COUNT(*) FROM #legacyItems) AS LegacyRows,
    (SELECT COUNT(*) FROM #items) AS Items,
    (SELECT COUNT(*) FROM #items WHERE IsImportant = 1) AS Important,
    (SELECT COUNT(*) FROM #items WHERE InventoryPage BETWEEN 1 AND 12) AS OnAPage,
    (SELECT COUNT(*) FROM #items WHERE SupplyDays IS NOT NULL) AS WithSupplyDays,
    (SELECT COUNT(*) FROM #items AS i WHERE NOT EXISTS (SELECT 1 FROM dbo.Items AS item WHERE item.ItemNumber = i.ItemNumber)) AS NotInXtraPOSItems,
    (SELECT COUNT(*) FROM #items WHERE BadPage = 1) AS PageIgnored,
    (SELECT COUNT(*) FROM #items WHERE BadDays = 1) AS SupplyDaysIgnored,
    (SELECT COUNT(*) FROM #legacyItems WHERE LEN(LTRIM(RTRIM(ItemNumber))) NOT BETWEEN 1 AND 100) AS ItemNumberTooLong,
    (SELECT COUNT(*) FROM #items AS i
       LEFT JOIN dbo.ItemInventorySettings AS s ON s.ItemNumber = i.ItemNumber
       WHERE s.ItemNumber IS NULL
          OR s.IsImportant <> i.IsImportant
          OR ISNULL(CAST(s.InventoryPage AS int), -1) <> ISNULL(CAST(i.InventoryPage AS int), -1)
          OR ISNULL(s.SupplyDays, -1) <> ISNULL(i.SupplyDays, -1)) AS ItemsToWrite,
    @periods AS LegacyPeriodsPerDay,
    @pageCount AS LegacyPageCount,
    CASE WHEN @pageCount > 12 THEN N'Legacy uses more than 12 pages: pages above 12 are ignored.' ELSE N'' END AS Warning;

SELECT
    CASE s.DayOfWeek WHEN 0 THEN N'Sunday' WHEN 1 THEN N'Monday' WHEN 2 THEN N'Tuesday' WHEN 3 THEN N'Wednesday'
                     WHEN 4 THEN N'Thursday' WHEN 5 THEN N'Friday' ELSE N'Saturday' END AS Day,
    s.DayOfWeek, s.PeriodType,
    current_.InventoryPage AS CurrentPage, current_.IsEnabled AS CurrentEnabled,
    imported.InventoryPage AS NewPage, COALESCE(imported.IsEnabled, CAST(0 AS bit)) AS NewEnabled
FROM (SELECT d.DayOfWeek, p.PeriodType
      FROM (VALUES (0), (1), (2), (3), (4), (5), (6)) AS d (DayOfWeek)
      CROSS JOIN (VALUES (N'AM'), (N'PM')) AS p (PeriodType)) AS s
LEFT JOIN dbo.InventorySchedule AS current_ ON current_.DayOfWeek = s.DayOfWeek AND current_.PeriodType = s.PeriodType
LEFT JOIN #schedule AS imported ON imported.DayOfWeek = s.DayOfWeek AND imported.PeriodType = s.PeriodType
ORDER BY CASE s.DayOfWeek WHEN 5 THEN 0 WHEN 6 THEN 1 ELSE s.DayOfWeek + 2 END, s.PeriodType;

-- ===================== Apply =====================

IF @Apply = 1
BEGIN
    BEGIN TRANSACTION;

    UPDATE s
    SET IsImportant = i.IsImportant, InventoryPage = i.InventoryPage, SupplyDays = i.SupplyDays, UpdatedAt = @now, UpdatedByName = @by
    FROM dbo.ItemInventorySettings AS s
    JOIN #items AS i ON i.ItemNumber = s.ItemNumber
    WHERE s.IsImportant <> i.IsImportant
       OR ISNULL(CAST(s.InventoryPage AS int), -1) <> ISNULL(CAST(i.InventoryPage AS int), -1)
       OR ISNULL(s.SupplyDays, -1) <> ISNULL(i.SupplyDays, -1);
    DECLARE @updated int = @@ROWCOUNT;

    INSERT INTO dbo.ItemInventorySettings (ItemNumber, IsImportant, InventoryPage, SupplyDays, UpdatedAt, UpdatedByName)
    SELECT i.ItemNumber, i.IsImportant, i.InventoryPage, i.SupplyDays, @now, @by
    FROM #items AS i
    WHERE NOT EXISTS (SELECT 1 FROM dbo.ItemInventorySettings AS s WHERE s.ItemNumber = i.ItemNumber);
    DECLARE @inserted int = @@ROWCOUNT;

    DECLARE @scheduleChanged int = 0;
    IF EXISTS (SELECT 1 FROM #schedule)
    BEGIN
        UPDATE current_
        SET InventoryPage = imported.InventoryPage, IsEnabled = imported.IsEnabled, UpdatedAt = @now
        FROM dbo.InventorySchedule AS current_
        JOIN #schedule AS imported ON imported.DayOfWeek = current_.DayOfWeek AND imported.PeriodType = current_.PeriodType
        WHERE current_.InventoryPage <> imported.InventoryPage OR current_.IsEnabled <> imported.IsEnabled;
        SET @scheduleChanged += @@ROWCOUNT;

        INSERT INTO dbo.InventorySchedule (DayOfWeek, PeriodType, InventoryPage, IsEnabled, UpdatedAt)
        SELECT imported.DayOfWeek, imported.PeriodType, imported.InventoryPage, imported.IsEnabled, @now
        FROM #schedule AS imported
        WHERE NOT EXISTS (SELECT 1 FROM dbo.InventorySchedule AS current_
                          WHERE current_.DayOfWeek = imported.DayOfWeek AND current_.PeriodType = imported.PeriodType);
        SET @scheduleChanged += @@ROWCOUNT;

        UPDATE current_
        SET IsEnabled = 0, UpdatedAt = @now
        FROM dbo.InventorySchedule AS current_
        WHERE current_.IsEnabled = 1
          AND NOT EXISTS (SELECT 1 FROM #schedule AS imported
                          WHERE imported.DayOfWeek = current_.DayOfWeek AND imported.PeriodType = current_.PeriodType);
        SET @scheduleChanged += @@ROWCOUNT;
    END;

    COMMIT TRANSACTION;

    SELECT N'APPLIED' AS Result, @inserted AS ItemsInserted, @updated AS ItemsUpdated, @scheduleChanged AS ScheduleRowsChanged;
END
ELSE
BEGIN
    SELECT N'PREVIEW ONLY: nothing was written. Set @Apply = 1 to import.' AS Result;
END;

SET NOEXEC OFF;
