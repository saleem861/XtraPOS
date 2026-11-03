-- Reference customer types for a branch that is not reading central.
-- Inserts a type only when that business does not already have the same name.
-- Does not change existing rows, customers, prices, or inventory.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

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
