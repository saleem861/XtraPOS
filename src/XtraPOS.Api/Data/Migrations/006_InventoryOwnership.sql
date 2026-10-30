-- Physical stock belongs to the stock owner. Branch SKU rows stay in ItemBranchInventory.
-- A missing quantity is pending initialization. Zero is a known empty balance.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.ItemBranchInventory', N'OnHandQuantity') IS NOT NULL
   AND COLUMNPROPERTY(OBJECT_ID(N'dbo.ItemBranchInventory'), N'OnHandQuantity', N'AllowsNull') = 0
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

    ALTER TABLE dbo.ItemBranchInventory ALTER COLUMN OnHandQuantity decimal(18, 4) NULL;
END;

IF OBJECT_ID(N'dbo.BranchStockBalances', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.BranchStockBalances
    (
        BranchStockBalanceID uniqueidentifier NOT NULL
            CONSTRAINT DF_BranchStockBalances_BranchStockBalanceID DEFAULT (NEWSEQUENTIALID()),
        BranchID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        OnHandQuantity decimal(18, 4) NULL,
        IsQuantityInitialized bit NOT NULL
            CONSTRAINT DF_BranchStockBalances_IsQuantityInitialized DEFAULT (0),
        UnitCost decimal(19, 4) NULL,
        LastRestockAt datetime2(0) NULL,
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_BranchStockBalances_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_BranchStockBalances_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_BranchStockBalances PRIMARY KEY (BranchStockBalanceID),
        CONSTRAINT UQ_BranchStockBalances_BranchID_ItemID UNIQUE (BranchID, ItemID),
        CONSTRAINT FK_BranchStockBalances_Branch FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID),
        CONSTRAINT FK_BranchStockBalances_Item FOREIGN KEY (ItemID) REFERENCES dbo.Items (ItemID),
        CONSTRAINT CK_BranchStockBalances_Initialization CHECK (
            (IsQuantityInitialized = 0 AND OnHandQuantity IS NULL)
            OR (IsQuantityInitialized = 1 AND OnHandQuantity IS NOT NULL))
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_BranchStockBalances_ItemID' AND object_id = OBJECT_ID(N'dbo.BranchStockBalances'))
    CREATE INDEX IX_BranchStockBalances_ItemID ON dbo.BranchStockBalances (ItemID);
