-- Physical on-hand quantity belongs only to BranchStockBalances.
-- ItemBranchInventory keeps branch SKU state and must not store a second quantity.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.ItemBranchInventory', N'OnHandQuantity') IS NOT NULL
BEGIN
    DECLARE @onHandDefault sysname;
    SELECT @onHandDefault = defaultConstraint.name
    FROM sys.default_constraints AS defaultConstraint
    INNER JOIN sys.columns AS quantityColumn
        ON quantityColumn.object_id = defaultConstraint.parent_object_id
       AND quantityColumn.column_id = defaultConstraint.parent_column_id
    WHERE defaultConstraint.parent_object_id = OBJECT_ID(N'dbo.ItemBranchInventory')
      AND quantityColumn.name = N'OnHandQuantity';

    IF @onHandDefault IS NOT NULL
    BEGIN
        DECLARE @dropOnHandDefault nvarchar(400) =
            N'ALTER TABLE dbo.ItemBranchInventory DROP CONSTRAINT ' + QUOTENAME(@onHandDefault);
        EXEC(@dropOnHandDefault);
    END;

    ALTER TABLE dbo.ItemBranchInventory DROP COLUMN OnHandQuantity;
END;
