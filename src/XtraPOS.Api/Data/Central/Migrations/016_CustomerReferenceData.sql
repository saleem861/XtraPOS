-- Reference rows for a new central database.
-- Inserts the product's price codes and customer types only when that code or name is absent.
-- Does not change existing rows, customers, prices, or inventory.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

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
