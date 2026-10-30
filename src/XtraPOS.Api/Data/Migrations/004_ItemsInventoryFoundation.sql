-- Item master, units, prices, branch inventory, and the stock ledger.
-- Text columns inherit the database collation (Arabic_CI_AS).
-- Do not add column-level COLLATE clauses.
-- Safe to run again: existing tables, indexes, and seed rows are left in place.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.Items', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Items
    (
        ItemID uniqueidentifier NOT NULL
            CONSTRAINT DF_Items_ItemID DEFAULT (NEWSEQUENTIALID()),
        ItemNumber nvarchar(100) NOT NULL,
        ItemName nvarchar(500) NOT NULL,
        ItemType nvarchar(100) NULL,
        Manufacturer nvarchar(100) NULL,
        Color nvarchar(100) NULL,
        Taste nvarchar(100) NULL,
        Size nvarchar(100) NULL,
        Packing nvarchar(100) NULL,
        Classification nvarchar(200) NULL,
        CountryOfOrigin nvarchar(100) NULL,
        ProductType nvarchar(50) NULL,
        ParentItemID uniqueidentifier NULL,
        IsAvailable bit NOT NULL
            CONSTRAINT DF_Items_IsAvailable DEFAULT (1),
        IsProhibited bit NOT NULL
            CONSTRAINT DF_Items_IsProhibited DEFAULT (0),
        IsPublishedOnline bit NOT NULL
            CONSTRAINT DF_Items_IsPublishedOnline DEFAULT (0),
        IsActive bit NOT NULL
            CONSTRAINT DF_Items_IsActive DEFAULT (1),
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_Items_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_Items_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_Items PRIMARY KEY (ItemID),
        CONSTRAINT UQ_Items_ItemNumber UNIQUE (ItemNumber),
        CONSTRAINT FK_Items_ParentItem FOREIGN KEY (ParentItemID) REFERENCES dbo.Items (ItemID)
    );
END;

IF OBJECT_ID(N'dbo.ItemUnits', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ItemUnits
    (
        ItemUnitID uniqueidentifier NOT NULL
            CONSTRAINT DF_ItemUnits_ItemUnitID DEFAULT (NEWSEQUENTIALID()),
        ItemID uniqueidentifier NOT NULL,
        UnitsPerPackage decimal(18, 4) NOT NULL,
        InternalUnitItemID uniqueidentifier NULL,
        InternalUnitItemNumber nvarchar(100) NULL,
        InternalUnitItemName nvarchar(500) NULL,
        IsBaseUnit bit NOT NULL
            CONSTRAINT DF_ItemUnits_IsBaseUnit DEFAULT (0),
        IsActive bit NOT NULL
            CONSTRAINT DF_ItemUnits_IsActive DEFAULT (1),
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_ItemUnits_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_ItemUnits_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_ItemUnits PRIMARY KEY (ItemUnitID),
        CONSTRAINT FK_ItemUnits_Item FOREIGN KEY (ItemID) REFERENCES dbo.Items (ItemID),
        CONSTRAINT FK_ItemUnits_InternalUnitItem FOREIGN KEY (InternalUnitItemID) REFERENCES dbo.Items (ItemID),
        CONSTRAINT CK_ItemUnits_UnitsPerPackage CHECK (UnitsPerPackage > 0)
    );
END;

IF OBJECT_ID(N'dbo.PriceTypes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.PriceTypes
    (
        PriceTypeID uniqueidentifier NOT NULL
            CONSTRAINT DF_PriceTypes_PriceTypeID DEFAULT (NEWSEQUENTIALID()),
        PriceCode nvarchar(50) NOT NULL,
        PriceName nvarchar(100) NOT NULL,
        SortOrder int NOT NULL,
        IsActive bit NOT NULL
            CONSTRAINT DF_PriceTypes_IsActive DEFAULT (1),
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_PriceTypes_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_PriceTypes PRIMARY KEY (PriceTypeID),
        CONSTRAINT UQ_PriceTypes_PriceCode UNIQUE (PriceCode)
    );
END;

IF OBJECT_ID(N'dbo.ItemPrices', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ItemPrices
    (
        ItemPriceID uniqueidentifier NOT NULL
            CONSTRAINT DF_ItemPrices_ItemPriceID DEFAULT (NEWSEQUENTIALID()),
        ItemID uniqueidentifier NOT NULL,
        PriceTypeID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NULL,
        Price decimal(19, 4) NOT NULL,
        IsActive bit NOT NULL
            CONSTRAINT DF_ItemPrices_IsActive DEFAULT (1),
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_ItemPrices_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_ItemPrices_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_ItemPrices PRIMARY KEY (ItemPriceID),
        CONSTRAINT FK_ItemPrices_Item FOREIGN KEY (ItemID) REFERENCES dbo.Items (ItemID),
        CONSTRAINT FK_ItemPrices_PriceType FOREIGN KEY (PriceTypeID) REFERENCES dbo.PriceTypes (PriceTypeID),
        CONSTRAINT FK_ItemPrices_Branch FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID)
    );
END;

IF OBJECT_ID(N'dbo.ItemPriceHistory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ItemPriceHistory
    (
        ItemPriceHistoryID uniqueidentifier NOT NULL
            CONSTRAINT DF_ItemPriceHistory_ItemPriceHistoryID DEFAULT (NEWSEQUENTIALID()),
        ItemID uniqueidentifier NOT NULL,
        PriceTypeID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NULL,
        OldPrice decimal(19, 4) NULL,
        NewPrice decimal(19, 4) NOT NULL,
        ChangedAt datetime2(0) NOT NULL
            CONSTRAINT DF_ItemPriceHistory_ChangedAt DEFAULT (SYSUTCDATETIME()),
        ChangedByUserID uniqueidentifier NULL,
        ChangeSource nvarchar(50) NULL,
        Reason nvarchar(500) NULL,
        CONSTRAINT PK_ItemPriceHistory PRIMARY KEY (ItemPriceHistoryID),
        CONSTRAINT FK_ItemPriceHistory_Item FOREIGN KEY (ItemID) REFERENCES dbo.Items (ItemID),
        CONSTRAINT FK_ItemPriceHistory_PriceType FOREIGN KEY (PriceTypeID) REFERENCES dbo.PriceTypes (PriceTypeID),
        CONSTRAINT FK_ItemPriceHistory_Branch FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID),
        CONSTRAINT FK_ItemPriceHistory_ChangedByUser FOREIGN KEY (ChangedByUserID) REFERENCES dbo.Users (UserID)
    );
END;

IF OBJECT_ID(N'dbo.ItemBranchInventory', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.ItemBranchInventory
    (
        ItemBranchInventoryID uniqueidentifier NOT NULL
            CONSTRAINT DF_ItemBranchInventory_ItemBranchInventoryID DEFAULT (NEWSEQUENTIALID()),
        BranchID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        OnHandQuantity decimal(18, 4) NOT NULL
            CONSTRAINT DF_ItemBranchInventory_OnHandQuantity DEFAULT (0),
        UnitCost decimal(19, 4) NULL,
        DailySalesRate decimal(18, 6) NOT NULL
            CONSTRAINT DF_ItemBranchInventory_DailySalesRate DEFAULT (0),
        AvailabilityDays decimal(18, 4) NULL,
        LastSaleAt datetime2(0) NULL,
        LastRestockAt datetime2(0) NULL,
        IsActive bit NOT NULL
            CONSTRAINT DF_ItemBranchInventory_IsActive DEFAULT (1),
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_ItemBranchInventory_CreatedAt DEFAULT (SYSUTCDATETIME()),
        UpdatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_ItemBranchInventory_UpdatedAt DEFAULT (SYSUTCDATETIME()),
        RowVersion rowversion NOT NULL,
        CONSTRAINT PK_ItemBranchInventory PRIMARY KEY (ItemBranchInventoryID),
        CONSTRAINT UQ_ItemBranchInventory_BranchID_ItemID UNIQUE (BranchID, ItemID),
        CONSTRAINT FK_ItemBranchInventory_Branch FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID),
        CONSTRAINT FK_ItemBranchInventory_Item FOREIGN KEY (ItemID) REFERENCES dbo.Items (ItemID)
    );
END;

IF OBJECT_ID(N'dbo.StockMovementTypes', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockMovementTypes
    (
        MovementTypeID uniqueidentifier NOT NULL
            CONSTRAINT DF_StockMovementTypes_MovementTypeID DEFAULT (NEWSEQUENTIALID()),
        MovementCode nvarchar(50) NOT NULL,
        MovementName nvarchar(100) NOT NULL,
        SortOrder int NOT NULL,
        IsActive bit NOT NULL
            CONSTRAINT DF_StockMovementTypes_IsActive DEFAULT (1),
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_StockMovementTypes_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CONSTRAINT PK_StockMovementTypes PRIMARY KEY (MovementTypeID),
        CONSTRAINT UQ_StockMovementTypes_MovementCode UNIQUE (MovementCode)
    );
END;

IF OBJECT_ID(N'dbo.StockMovements', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.StockMovements
    (
        StockMovementID uniqueidentifier NOT NULL
            CONSTRAINT DF_StockMovements_StockMovementID DEFAULT (NEWSEQUENTIALID()),
        BranchID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        MovementTypeID uniqueidentifier NOT NULL,
        QuantityDelta decimal(18, 4) NOT NULL,
        UnitCost decimal(19, 4) NULL,
        OccurredAt datetime2(0) NOT NULL,
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_StockMovements_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CreatedByUserID uniqueidentifier NULL,
        ReferenceType nvarchar(50) NULL,
        ReferenceID uniqueidentifier NULL,
        MovementGroupID uniqueidentifier NULL,
        Notes nvarchar(1000) NULL,
        CONSTRAINT PK_StockMovements PRIMARY KEY (StockMovementID),
        CONSTRAINT FK_StockMovements_Branch FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID),
        CONSTRAINT FK_StockMovements_Item FOREIGN KEY (ItemID) REFERENCES dbo.Items (ItemID),
        CONSTRAINT FK_StockMovements_MovementType FOREIGN KEY (MovementTypeID) REFERENCES dbo.StockMovementTypes (MovementTypeID),
        CONSTRAINT FK_StockMovements_CreatedByUser FOREIGN KEY (CreatedByUserID) REFERENCES dbo.Users (UserID)
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Items_ItemName' AND object_id = OBJECT_ID(N'dbo.Items'))
    CREATE INDEX IX_Items_ItemName ON dbo.Items (ItemName);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Items_ItemType' AND object_id = OBJECT_ID(N'dbo.Items'))
    CREATE INDEX IX_Items_ItemType ON dbo.Items (ItemType);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Items_Classification' AND object_id = OBJECT_ID(N'dbo.Items'))
    CREATE INDEX IX_Items_Classification ON dbo.Items (Classification);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemUnits_ItemID' AND object_id = OBJECT_ID(N'dbo.ItemUnits'))
    CREATE INDEX IX_ItemUnits_ItemID ON dbo.ItemUnits (ItemID);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemUnits_InternalUnitItemID' AND object_id = OBJECT_ID(N'dbo.ItemUnits'))
    CREATE INDEX IX_ItemUnits_InternalUnitItemID ON dbo.ItemUnits (InternalUnitItemID);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemUnits_InternalUnitItemNumber' AND object_id = OBJECT_ID(N'dbo.ItemUnits'))
    CREATE INDEX IX_ItemUnits_InternalUnitItemNumber ON dbo.ItemUnits (InternalUnitItemNumber);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_ItemPrices_Default' AND object_id = OBJECT_ID(N'dbo.ItemPrices'))
    CREATE UNIQUE INDEX UX_ItemPrices_Default ON dbo.ItemPrices (ItemID, PriceTypeID) WHERE BranchID IS NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_ItemPrices_Branch' AND object_id = OBJECT_ID(N'dbo.ItemPrices'))
    CREATE UNIQUE INDEX UX_ItemPrices_Branch ON dbo.ItemPrices (ItemID, PriceTypeID, BranchID) WHERE BranchID IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemPrices_BranchID' AND object_id = OBJECT_ID(N'dbo.ItemPrices'))
    CREATE INDEX IX_ItemPrices_BranchID ON dbo.ItemPrices (BranchID);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemPriceHistory_ItemID_ChangedAt' AND object_id = OBJECT_ID(N'dbo.ItemPriceHistory'))
    CREATE INDEX IX_ItemPriceHistory_ItemID_ChangedAt ON dbo.ItemPriceHistory (ItemID, ChangedAt);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemBranchInventory_BranchID_DailySalesRate' AND object_id = OBJECT_ID(N'dbo.ItemBranchInventory'))
    CREATE INDEX IX_ItemBranchInventory_BranchID_DailySalesRate ON dbo.ItemBranchInventory (BranchID, DailySalesRate);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ItemBranchInventory_BranchID_LastSaleAt' AND object_id = OBJECT_ID(N'dbo.ItemBranchInventory'))
    CREATE INDEX IX_ItemBranchInventory_BranchID_LastSaleAt ON dbo.ItemBranchInventory (BranchID, LastSaleAt);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StockMovements_BranchID_ItemID_OccurredAt' AND object_id = OBJECT_ID(N'dbo.StockMovements'))
    CREATE INDEX IX_StockMovements_BranchID_ItemID_OccurredAt ON dbo.StockMovements (BranchID, ItemID, OccurredAt);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StockMovements_ItemID_OccurredAt' AND object_id = OBJECT_ID(N'dbo.StockMovements'))
    CREATE INDEX IX_StockMovements_ItemID_OccurredAt ON dbo.StockMovements (ItemID, OccurredAt);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StockMovements_ReferenceType_ReferenceID' AND object_id = OBJECT_ID(N'dbo.StockMovements'))
    CREATE INDEX IX_StockMovements_ReferenceType_ReferenceID ON dbo.StockMovements (ReferenceType, ReferenceID);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StockMovements_MovementGroupID' AND object_id = OBJECT_ID(N'dbo.StockMovements'))
    CREATE INDEX IX_StockMovements_MovementGroupID ON dbo.StockMovements (MovementGroupID);

INSERT INTO dbo.PriceTypes (PriceCode, PriceName, SortOrder)
SELECT seed.PriceCode, seed.PriceName, seed.SortOrder
FROM (VALUES
    (N'Retail', N'السعر العادي', 10),
    (N'Dealer', N'سعر الجملة', 20),
    (N'MajorCustomer', N'سعر مستهلك كبير', 30),
    (N'SpecialCustomer', N'زبون مميز', 40),
    (N'Price0', N'Price0', 50),
    (N'Price1', N'Price1', 60),
    (N'Price2', N'Price2', 70),
    (N'Price3', N'Price3', 80)
) AS seed (PriceCode, PriceName, SortOrder)
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.PriceTypes AS existing
    WHERE existing.PriceCode = seed.PriceCode);

INSERT INTO dbo.StockMovementTypes (MovementCode, MovementName, SortOrder)
SELECT seed.MovementCode, seed.MovementName, seed.SortOrder
FROM (VALUES
    (N'OpeningBalance', N'رصيد افتتاحي', 10),
    (N'Purchase', N'شراء', 20),
    (N'PurchaseReturn', N'مرتجع شراء', 30),
    (N'Sale', N'بيع', 40),
    (N'SaleReturn', N'مرتجع بيع', 50),
    (N'TransferOut', N'تحويل صادر', 60),
    (N'TransferIn', N'تحويل وارد', 70),
    (N'InventoryAdjustment', N'تسوية جرد', 80),
    (N'ManualAdjustment', N'تعديل يدوي', 90),
    (N'Damage', N'تلف', 100),
    (N'Loss', N'فقدان', 110)
) AS seed (MovementCode, MovementName, SortOrder)
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.StockMovementTypes AS existing
    WHERE existing.MovementCode = seed.MovementCode);
