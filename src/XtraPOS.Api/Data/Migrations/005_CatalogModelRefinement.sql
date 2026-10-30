-- Move operational availability from the item master to the branch inventory row.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.Items', N'IsAvailable') IS NOT NULL
BEGIN
    DECLARE @itemAvailabilityDefault sysname;
    SELECT @itemAvailabilityDefault = defaultConstraint.name
    FROM sys.default_constraints AS defaultConstraint
    INNER JOIN sys.columns AS itemColumn
        ON itemColumn.object_id = defaultConstraint.parent_object_id
       AND itemColumn.column_id = defaultConstraint.parent_column_id
    WHERE defaultConstraint.parent_object_id = OBJECT_ID(N'dbo.Items')
      AND itemColumn.name = N'IsAvailable';

    IF @itemAvailabilityDefault IS NOT NULL
    BEGIN
        DECLARE @dropAvailabilityDefault nvarchar(400) =
            N'ALTER TABLE dbo.Items DROP CONSTRAINT ' + QUOTENAME(@itemAvailabilityDefault);
        EXEC(@dropAvailabilityDefault);
    END;

    ALTER TABLE dbo.Items DROP COLUMN IsAvailable;
END;

IF COL_LENGTH(N'dbo.ItemBranchInventory', N'IsAvailable') IS NULL
BEGIN
    ALTER TABLE dbo.ItemBranchInventory
        ADD IsAvailable bit NOT NULL
            CONSTRAINT DF_ItemBranchInventory_IsAvailable DEFAULT (1);
END;
