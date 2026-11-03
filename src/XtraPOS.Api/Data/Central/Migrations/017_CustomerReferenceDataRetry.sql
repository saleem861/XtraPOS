-- Fills customer types that 016 skipped because no business existed yet.
-- The fictional business is inserted only in the empty staging central database.
-- Existing businesses, customers, prices, and inventory are left unchanged.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF DB_NAME() = N'XtraPOS_Central_Test_Staging'
   AND NOT EXISTS (SELECT 1 FROM dbo.Businesses)
BEGIN
    DECLARE @BusinessID uniqueidentifier = '11111111-1111-4111-8111-111111111111';
    INSERT INTO dbo.Businesses
        (BusinessID, BusinessName, BusinessDisplayName, Description, Phone, Address, IsActive, CreatedAt)
    VALUES
        (@BusinessID, N'Staging Test Business', N'Staging Test Business', N'Fictional staging business', NULL, NULL, 1, SYSUTCDATETIME());

    IF NOT EXISTS (SELECT 1 FROM dbo.Branches)
    BEGIN
        INSERT INTO dbo.Branches
            (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
        VALUES
            ('22222222-2222-4222-8222-222222222222', @BusinessID, N'ST', N'Staging Test Branch', N'Retail', 1, SYSUTCDATETIME());
    END;
END;

INSERT INTO dbo.PriceTypes (PriceTypeID, PriceCode, PriceName, SortOrder, IsActive, CreatedAt)
SELECT NEWID(), seed.PriceCode, seed.PriceName, seed.SortOrder, 1, SYSUTCDATETIME()
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
    SELECT 1 FROM dbo.PriceTypes AS existing WHERE existing.PriceCode = seed.PriceCode);

INSERT INTO dbo.CustomerClassifications (
    CustomerClassificationID, BusinessID, Code, Name, DefaultPriceTypeID,
    DefaultVariableValueDiscountPercent, IsDefault, IsActive, CreatedAt, UpdatedAt)
SELECT
    NEWID(),
    business.BusinessID,
    seed.Code,
    seed.Name,
    price.PriceTypeID,
    seed.Discount,
    CASE
        WHEN seed.IsDefault = 1 AND NOT EXISTS (
            SELECT 1
            FROM dbo.CustomerClassifications AS currentDefault
            WHERE currentDefault.BusinessID = business.BusinessID AND currentDefault.IsDefault = 1)
        THEN 1
        ELSE 0
    END,
    1,
    SYSUTCDATETIME(),
    SYSUTCDATETIME()
FROM dbo.Businesses AS business
CROSS JOIN (VALUES
    (N'Regular', N'عادي', N'Price0', CAST(0 AS decimal(7, 4)), 1),
    (N'Bronze', N'برونزي', N'Price3', CAST(12 AS decimal(7, 4)), 0),
    (N'Silver', N'فضي', N'Price2', CAST(18 AS decimal(7, 4)), 0),
    (N'Gold', N'ذهبي', N'Price1', CAST(22 AS decimal(7, 4)), 0),
    (N'Shop', N'محل تجاري', N'Dealer', CAST(31.8182 AS decimal(7, 4)), 0),
    (N'Special', N'زبون مميز', N'SpecialCustomer', CAST(10 AS decimal(7, 4)), 0),
    (N'Major', N'مستهلك كبير', N'MajorCustomer', CAST(26.2626 AS decimal(7, 4)), 0)
) AS seed (Code, Name, PriceCode, Discount, IsDefault)
INNER JOIN dbo.PriceTypes AS price ON price.PriceCode = seed.PriceCode
WHERE NOT EXISTS (
    SELECT 1
    FROM dbo.CustomerClassifications AS existing
    WHERE existing.BusinessID = business.BusinessID AND existing.Name = seed.Name);
