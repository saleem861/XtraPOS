-- Branch-owned sales documents. Adds Sales, SaleItems, and a local sale-number sequence.
-- Does not import sales, post stock, or create payments.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF NOT EXISTS
(
    SELECT 1
    FROM sys.sequences
    WHERE name = N'SaleNumberSequence'
      AND schema_id = SCHEMA_ID(N'dbo')
)
BEGIN
    CREATE SEQUENCE dbo.SaleNumberSequence
        AS bigint
        START WITH 1
        INCREMENT BY 1
        NO CACHE;
END;

IF OBJECT_ID(N'dbo.Sales', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.Sales
    (
        SaleID uniqueidentifier NOT NULL CONSTRAINT PK_Sales PRIMARY KEY,
        BusinessID uniqueidentifier NOT NULL,
        BranchID uniqueidentifier NOT NULL,
        POSStationID uniqueidentifier NOT NULL,
        DeviceID uniqueidentifier NOT NULL,
        CartID uniqueidentifier NOT NULL,
        CustomerID uniqueidentifier NULL,
        CashierUserID uniqueidentifier NOT NULL,
        SaleNumber bigint NOT NULL,
        Status nvarchar(20) NOT NULL,
        Subtotal decimal(19, 4) NOT NULL,
        DiscountTotal decimal(19, 4) NOT NULL
            CONSTRAINT DF_Sales_DiscountTotal DEFAULT (0),
        Total decimal(19, 4) NOT NULL,
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_Sales_CreatedAt DEFAULT (SYSUTCDATETIME()),
        CompletedAt datetime2(0) NULL,
        LegacySource nvarchar(64) NULL,
        LegacySaleKey nvarchar(64) NULL,
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_Sales_Businesses FOREIGN KEY (BusinessID) REFERENCES dbo.Businesses (BusinessID),
        CONSTRAINT FK_Sales_Branches FOREIGN KEY (BranchID) REFERENCES dbo.Branches (BranchID),
        CONSTRAINT FK_Sales_POSStations FOREIGN KEY (POSStationID) REFERENCES dbo.POSStations (StationID),
        CONSTRAINT FK_Sales_Devices FOREIGN KEY (DeviceID) REFERENCES dbo.Devices (DeviceID),
        CONSTRAINT FK_Sales_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
        CONSTRAINT FK_Sales_Users FOREIGN KEY (CashierUserID) REFERENCES dbo.Users (UserID),
        CONSTRAINT CK_Sales_Status CHECK (Status IN (N'Draft', N'Completed', N'Cancelled')),
        CONSTRAINT CK_Sales_Subtotal CHECK (Subtotal >= 0),
        CONSTRAINT CK_Sales_DiscountTotal CHECK (DiscountTotal >= 0),
        CONSTRAINT CK_Sales_Total CHECK (Total >= 0),
        CONSTRAINT CK_Sales_LegacyPair CHECK
        (
            (LegacySource IS NULL AND LegacySaleKey IS NULL)
            OR (LegacySource IS NOT NULL AND LegacySaleKey IS NOT NULL)
        )
    );
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Sales_BranchID_CartID' AND object_id = OBJECT_ID(N'dbo.Sales'))
    CREATE UNIQUE INDEX UX_Sales_BranchID_CartID ON dbo.Sales (BranchID, CartID);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Sales_BranchID_SaleNumber' AND object_id = OBJECT_ID(N'dbo.Sales'))
    CREATE UNIQUE INDEX UX_Sales_BranchID_SaleNumber ON dbo.Sales (BranchID, SaleNumber);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Sales_BranchID_CreatedAt' AND object_id = OBJECT_ID(N'dbo.Sales'))
    CREATE INDEX IX_Sales_BranchID_CreatedAt ON dbo.Sales (BranchID, CreatedAt);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Sales_CustomerID_CreatedAt' AND object_id = OBJECT_ID(N'dbo.Sales'))
    CREATE INDEX IX_Sales_CustomerID_CreatedAt ON dbo.Sales (CustomerID, CreatedAt) WHERE CustomerID IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Sales_LegacyIdentity' AND object_id = OBJECT_ID(N'dbo.Sales'))
    CREATE UNIQUE INDEX UX_Sales_LegacyIdentity
        ON dbo.Sales (BusinessID, LegacySource, LegacySaleKey)
        WHERE LegacySaleKey IS NOT NULL;

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NULL
BEGIN
    CREATE TABLE dbo.SaleItems
    (
        SaleItemID uniqueidentifier NOT NULL CONSTRAINT PK_SaleItems PRIMARY KEY,
        SaleID uniqueidentifier NOT NULL,
        LineNumber int NOT NULL,
        PricingMode nvarchar(32) NOT NULL,
        ItemID uniqueidentifier NOT NULL,
        ItemNumberSnapshot nvarchar(100) NOT NULL,
        ItemNameSnapshot nvarchar(500) NOT NULL,
        Quantity decimal(18, 4) NOT NULL,
        UnitPrice decimal(19, 4) NOT NULL,
        LineTotal decimal(19, 4) NOT NULL,
        CustomerID uniqueidentifier NULL,
        PriceTypeID uniqueidentifier NULL,
        PriceTypeCodeSnapshot nvarchar(50) NULL,
        PriceSource nvarchar(50) NOT NULL,
        PricingResolvedAt datetime2(0) NOT NULL,
        StockOwnerItemID uniqueidentifier NOT NULL,
        StockOwnerItemNumberSnapshot nvarchar(100) NOT NULL,
        StockOwnerConversionFactor decimal(38, 28) NOT NULL,
        StockOwnerQuantity decimal(38, 28) NOT NULL,
        ReferenceItemID uniqueidentifier NULL,
        ReferenceItemNumberSnapshot nvarchar(100) NULL,
        ReferenceItemNameSnapshot nvarchar(500) NULL,
        EnteredScaleValue decimal(19, 4) NULL,
        ReferenceBasePrice decimal(19, 4) NULL,
        ReferencePriceSource nvarchar(50) NULL,
        WeightKg decimal(38, 28) NULL,
        WeightGrams decimal(38, 28) NULL,
        VariableValueDiscountPercent decimal(7, 4) NULL,
        RawDiscountedValue decimal(19, 4) NULL,
        FinalSellingPrice decimal(19, 4) NULL,
        RoundingPolicy nvarchar(50) NULL,
        ProjectedCostAtPricing decimal(19, 4) NULL,
        UnitCostSnapshot decimal(19, 4) NULL,
        LineCostSnapshot decimal(19, 4) NULL,
        CostSource nvarchar(50) NULL,
        CreatedAt datetime2(0) NOT NULL
            CONSTRAINT DF_SaleItems_CreatedAt DEFAULT (SYSUTCDATETIME()),
        RowVersion rowversion NOT NULL,
        CONSTRAINT FK_SaleItems_Sales FOREIGN KEY (SaleID) REFERENCES dbo.Sales (SaleID),
        CONSTRAINT FK_SaleItems_Items FOREIGN KEY (ItemID) REFERENCES dbo.Items (ItemID),
        CONSTRAINT FK_SaleItems_StockOwner FOREIGN KEY (StockOwnerItemID) REFERENCES dbo.Items (ItemID),
        CONSTRAINT FK_SaleItems_ReferenceItem FOREIGN KEY (ReferenceItemID) REFERENCES dbo.Items (ItemID),
        CONSTRAINT FK_SaleItems_PriceTypes FOREIGN KEY (PriceTypeID) REFERENCES dbo.PriceTypes (PriceTypeID),
        CONSTRAINT FK_SaleItems_Customers FOREIGN KEY (CustomerID) REFERENCES dbo.Customers (CustomerID),
        CONSTRAINT UQ_SaleItems_SaleID_LineNumber UNIQUE (SaleID, LineNumber),
        CONSTRAINT CK_SaleItems_LineNumber CHECK (LineNumber > 0),
        CONSTRAINT CK_SaleItems_Quantity CHECK (Quantity > 0),
        CONSTRAINT CK_SaleItems_StockOwnerQuantity CHECK (StockOwnerQuantity > 0),
        CONSTRAINT CK_SaleItems_StockOwnerFactor CHECK (StockOwnerConversionFactor > 0),
        CONSTRAINT CK_SaleItems_PricingMode CHECK (PricingMode IN (N'Fixed', N'VariableValueByWeight')),
        CONSTRAINT CK_SaleItems_UnitPrice CHECK (UnitPrice >= 0),
        CONSTRAINT CK_SaleItems_LineTotal CHECK (LineTotal >= 0)
    );
END;
