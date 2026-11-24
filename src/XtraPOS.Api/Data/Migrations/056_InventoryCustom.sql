-- Custom inventories on the branch (phase 6 of the inventory and shortages module).
-- Local read copies of the custom inventories assigned to this branch, pulled from XtraPOS_Central
-- every minute (InventoryRelayService), and the «Custom» count type.
-- Adds tables, one column and widens one CHECK. Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.CustomInventories', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomInventories
    (
        CustomInventoryID uniqueidentifier NOT NULL CONSTRAINT PK_CustomInventories PRIMARY KEY,
        InventoryName nvarchar(200) NOT NULL,
        IsActive bit NOT NULL,
        UpdatedAt datetime2(3) NOT NULL
    );
END;

IF OBJECT_ID(N'dbo.CustomInventoryItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.CustomInventoryItems
    (
        CustomInventoryID uniqueidentifier NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        ItemNumber nvarchar(100) NOT NULL,
        ItemName nvarchar(500) NULL,
        CONSTRAINT PK_CustomInventoryItems PRIMARY KEY (CustomInventoryID, ItemID),
        CONSTRAINT FK_CustomInventoryItems_Inventories FOREIGN KEY (CustomInventoryID) REFERENCES dbo.CustomInventories (CustomInventoryID)
    );
END;

IF COL_LENGTH(N'dbo.InventorySessions', N'CustomInventoryID') IS NULL
BEGIN
    ALTER TABLE dbo.InventorySessions ADD CustomInventoryID uniqueidentifier NULL;
END;

IF EXISTS (
    SELECT 1 FROM sys.check_constraints
    WHERE name = N'CK_InventorySessions_Type'
      AND parent_object_id = OBJECT_ID(N'dbo.InventorySessions')
      AND definition NOT LIKE N'%Custom%')
BEGIN
    ALTER TABLE dbo.InventorySessions DROP CONSTRAINT CK_InventorySessions_Type;
END;

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = N'CK_InventorySessions_Type' AND parent_object_id = OBJECT_ID(N'dbo.InventorySessions'))
BEGIN
    ALTER TABLE dbo.InventorySessions WITH CHECK
        ADD CONSTRAINT CK_InventorySessions_Type CHECK (InventoryType IN (N'Important', N'Periodic', N'Manual', N'Custom'));
END;
