-- Inventory import, step 1 of 2 (phase 7 of the inventory and shortages module).
--
-- Run on saleemPOSDB (the XtraPOS Inventory database). READ ONLY: it changes nothing.
-- It returns one column of INSERT lines: the item classification (important / periodic page / supply days),
-- the periodic page schedule and two legacy settings.
--
-- In SSMS: Results to Grid, run, click the column header to select every row, Ctrl+C,
-- then paste into BLOCK 1 of scripts\inventory-import-2-central.sql and continue there.

SET NOCOUNT ON;

IF OBJECT_ID(N'dbo.InventorySettings', N'U') IS NULL
   OR OBJECT_ID(N'dbo.InventorySchedule', N'U') IS NULL
BEGIN
    RAISERROR(N'This is not the XtraPOS Inventory database (dbo.InventorySettings / dbo.InventorySchedule missing).', 16, 1);
    RETURN;
END;

DECLARE @periods int = 2;
DECLARE @pages int = 12;
IF OBJECT_ID(N'dbo.InventorySystemSettings', N'U') IS NOT NULL
BEGIN
    SELECT TOP (1)
        @periods = COALESCE(TRY_CONVERT(int, PeriodsPerDay), 2),
        @pages = COALESCE(TRY_CONVERT(int, PeriodicPageCount), 12)
    FROM dbo.InventorySystemSettings;
END;

WITH classification AS
(
    SELECT
        LTRIM(RTRIM(CONVERT(nvarchar(200), ItemNumber))) AS ItemNumber,
        -- The legacy app counts InventoryPage 0 as important too.
        MAX(CASE WHEN IsImportant = 1 OR InventoryPage = 0 THEN 1 ELSE 0 END) AS IsImportant,
        MAX(InventoryPage) AS InventoryPage,
        MAX(SupplyDays) AS SupplyDays
    FROM dbo.InventorySettings
    WHERE ItemNumber IS NOT NULL
      AND LTRIM(RTRIM(CONVERT(nvarchar(200), ItemNumber))) <> N''
    GROUP BY LTRIM(RTRIM(CONVERT(nvarchar(200), ItemNumber)))
),
lines AS
(
    SELECT 0 AS Part, N'' AS SortKey,
           N'INSERT INTO #legacySettings VALUES (' + CONVERT(nvarchar(12), @periods) + N', ' + CONVERT(nvarchar(12), @pages) + N');' AS Line
    UNION ALL
    SELECT 1, ItemNumber,
           N'INSERT INTO #legacyItems VALUES (N''' + REPLACE(ItemNumber, N'''', N'''''') + N''', '
           + CONVERT(nvarchar(1), IsImportant) + N', '
           + COALESCE(CONVERT(nvarchar(40), InventoryPage), N'NULL') + N', '
           + COALESCE(CONVERT(nvarchar(40), SupplyDays), N'NULL') + N');'
    FROM classification
    UNION ALL
    SELECT 2, RIGHT(N'00000000000000000000' + CONVERT(nvarchar(20), ScheduleID), 20),
           N'INSERT INTO #legacySchedule VALUES ('
           + CONVERT(nvarchar(20), ScheduleID) + N', '
           + COALESCE(CONVERT(nvarchar(12), DayOfWeek), N'NULL') + N', '
           + COALESCE(N'N''' + REPLACE(LTRIM(RTRIM(CONVERT(nvarchar(20), PeriodType))), N'''', N'''''') + N'''', N'NULL') + N', '
           + COALESCE(CONVERT(nvarchar(40), InventoryPage), N'NULL') + N', '
           + CASE WHEN IsEnabled = 1 THEN N'1' ELSE N'0' END + N');'
    FROM dbo.InventorySchedule
)
SELECT Line AS [Copy all rows into BLOCK 1]
FROM lines
ORDER BY Part, SortKey;
