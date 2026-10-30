-- Quantity-linked customer exceptional prices on the central customer table.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF COL_LENGTH(N'dbo.CustomerItemSpecialPrices', N'MinimumQuantity') IS NULL
    ALTER TABLE dbo.CustomerItemSpecialPrices ADD MinimumQuantity decimal(18, 4) NOT NULL
        CONSTRAINT DF_CustomerItemSpecialPrices_MinimumQuantity DEFAULT (1);

IF COL_LENGTH(N'dbo.CustomerItemSpecialPrices', N'BundlePrice') IS NULL
BEGIN
    ALTER TABLE dbo.CustomerItemSpecialPrices ADD BundlePrice decimal(19, 4) NULL;
    EXEC(N'UPDATE dbo.CustomerItemSpecialPrices SET BundlePrice = SpecialPrice WHERE BundlePrice IS NULL;');
    EXEC(N'ALTER TABLE dbo.CustomerItemSpecialPrices ALTER COLUMN BundlePrice decimal(19, 4) NOT NULL;');
END;
