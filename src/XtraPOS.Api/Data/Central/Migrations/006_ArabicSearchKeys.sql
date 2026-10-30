IF COL_LENGTH(N'dbo.Items', N'ItemNameSearchStrict') IS NULL
BEGIN
    ALTER TABLE dbo.Items ADD
        ItemNameSearchStrict nvarchar(500) NULL,
        ItemNameSearchLoose nvarchar(500) NULL;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Items_ItemNameSearchStrict' AND object_id = OBJECT_ID(N'dbo.Items'))
    CREATE INDEX IX_Items_ItemNameSearchStrict ON dbo.Items (ItemNameSearchStrict);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Items_ItemNameSearchLoose' AND object_id = OBJECT_ID(N'dbo.Items'))
    CREATE INDEX IX_Items_ItemNameSearchLoose ON dbo.Items (ItemNameSearchLoose);

IF OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL AND COL_LENGTH(N'dbo.Customers', N'NameSearchStrict') IS NULL
BEGIN
    ALTER TABLE dbo.Customers ADD
        NameSearchStrict nvarchar(300) NULL,
        NameSearchLoose nvarchar(300) NULL;
END;

IF OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Customers_NameSearchStrict' AND object_id = OBJECT_ID(N'dbo.Customers'))
    CREATE INDEX IX_Customers_NameSearchStrict ON dbo.Customers (NameSearchStrict);

IF OBJECT_ID(N'dbo.Customers', N'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Customers_NameSearchLoose' AND object_id = OBJECT_ID(N'dbo.Customers'))
    CREATE INDEX IX_Customers_NameSearchLoose ON dbo.Customers (NameSearchLoose);
