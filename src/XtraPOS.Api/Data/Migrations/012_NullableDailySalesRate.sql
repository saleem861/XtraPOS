-- NULL means demand has not been calculated. Zero means a calculated demand of zero.
-- Existing stored rates are preserved. This migration does not add sales tables.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.ItemBranchInventory', N'DailySalesRate') IS NOT NULL
   AND (
        COLUMNPROPERTY(OBJECT_ID(N'dbo.ItemBranchInventory'), N'DailySalesRate', N'AllowsNull') = 0
        OR EXISTS (
            SELECT 1
            FROM sys.default_constraints AS defaultConstraint
            INNER JOIN sys.columns AS rateColumn
                ON rateColumn.object_id = defaultConstraint.parent_object_id
               AND rateColumn.column_id = defaultConstraint.parent_column_id
            WHERE defaultConstraint.parent_object_id = OBJECT_ID(N'dbo.ItemBranchInventory')
              AND rateColumn.name = N'DailySalesRate'))
BEGIN
    DECLARE @rateDefault sysname;
    SELECT @rateDefault = defaultConstraint.name
    FROM sys.default_constraints AS defaultConstraint
    INNER JOIN sys.columns AS rateColumn
        ON rateColumn.object_id = defaultConstraint.parent_object_id
       AND rateColumn.column_id = defaultConstraint.parent_column_id
    WHERE defaultConstraint.parent_object_id = OBJECT_ID(N'dbo.ItemBranchInventory')
      AND rateColumn.name = N'DailySalesRate';

    IF @rateDefault IS NOT NULL
    BEGIN
        DECLARE @dropRateDefault nvarchar(400) =
            N'ALTER TABLE dbo.ItemBranchInventory DROP CONSTRAINT ' + QUOTENAME(@rateDefault);
        EXEC(@dropRateDefault);
    END;

    IF COLUMNPROPERTY(OBJECT_ID(N'dbo.ItemBranchInventory'), N'DailySalesRate', N'AllowsNull') = 0
        ALTER TABLE dbo.ItemBranchInventory ALTER COLUMN DailySalesRate decimal(18, 6) NULL;
END;
