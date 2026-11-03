-- The staging branch already belongs to Smoker World. Migration 017 inserted a
-- placeholder business because central had no businesses, so customer types
-- were not attached to the branch the POS is using.
-- This runs only on the staging central database. It does not change customers,
-- prices, or inventory.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF DB_NAME() = N'XtraPOS_Central_Test_Staging'
BEGIN
    DECLARE @BusinessID uniqueidentifier = '422cda3a-498b-4eae-9eda-1db23e399ae1';
    DECLARE @PlaceholderID uniqueidentifier = '11111111-1111-4111-8111-111111111111';

    IF NOT EXISTS (SELECT 1 FROM dbo.Businesses WHERE BusinessID = @BusinessID)
    BEGIN
        INSERT INTO dbo.Businesses
            (BusinessID, BusinessName, BusinessDisplayName, Description, Phone, Address, IsActive, CreatedAt)
        VALUES
            (@BusinessID, N'Smoker World', N'SW', NULL, NULL, NULL, 1, SYSUTCDATETIME());
    END;

    IF NOT EXISTS (SELECT 1 FROM dbo.Branches WHERE BranchID = 'b9c3423e-f36b-1410-84f4-003de19a782d')
    BEGIN
        INSERT INTO dbo.Branches
            (BranchID, BusinessID, BranchCode, BranchName, BranchType, IsActive, CreatedAt)
        VALUES
            ('b9c3423e-f36b-1410-84f4-003de19a782d', @BusinessID, N'03', N'المقاطعة', N'Retail', 1, SYSUTCDATETIME());
    END;

    DELETE FROM dbo.CustomerClassifications WHERE BusinessID = @PlaceholderID;
    DELETE FROM dbo.Branches WHERE BranchID = '22222222-2222-4222-8222-222222222222';
    DELETE FROM dbo.Businesses WHERE BusinessID = @PlaceholderID;

    IF NOT EXISTS (SELECT 1 FROM dbo.SystemSettings WHERE SettingKey = N'BusinessID')
    BEGIN
        INSERT INTO dbo.SystemSettings (SettingKey, SettingValue)
        VALUES (N'BusinessID', CONVERT(nvarchar(36), @BusinessID));
    END;
END;

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
