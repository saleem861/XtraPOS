-- Store unit profit and quantity profit beside the unit and quantity cost on each sale line.
-- Existing lines take a missing cost from the stock owner balance, then the item card, then the projected portion cost.
-- Safe to run again.

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;

IF OBJECT_ID(N'dbo.SaleItems', N'U') IS NULL
    RETURN;

IF COL_LENGTH(N'dbo.SaleItems', N'UnitProfitSnapshot') IS NULL
    ALTER TABLE dbo.SaleItems ADD UnitProfitSnapshot decimal(19, 4) NULL;

IF COL_LENGTH(N'dbo.SaleItems', N'LineProfitSnapshot') IS NULL
    ALTER TABLE dbo.SaleItems ADD LineProfitSnapshot decimal(19, 4) NULL;

BEGIN TRY
IF OBJECT_ID(N'dbo.Sales', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.BranchStockBalances', N'U') IS NOT NULL
   AND OBJECT_ID(N'dbo.ItemBranchInventory', N'U') IS NOT NULL
    UPDATE line
    SET UnitCostSnapshot = COALESCE(stock.UnitCost, ownerCard.UnitCost, itemCard.UnitCost)
    FROM dbo.SaleItems AS line
    INNER JOIN dbo.Sales AS sale ON sale.SaleID = line.SaleID
    LEFT JOIN dbo.BranchStockBalances AS stock
        ON stock.BranchID = sale.BranchID AND stock.ItemID = line.StockOwnerItemID AND stock.UnitCost IS NOT NULL
    LEFT JOIN dbo.ItemBranchInventory AS ownerCard
        ON ownerCard.BranchID = sale.BranchID AND ownerCard.ItemID = line.StockOwnerItemID AND ownerCard.UnitCost IS NOT NULL
    LEFT JOIN dbo.ItemBranchInventory AS itemCard
        ON itemCard.BranchID = sale.BranchID AND itemCard.ItemID = line.ItemID AND itemCard.UnitCost IS NOT NULL
    WHERE line.UnitCostSnapshot IS NULL
      AND line.LineCostSnapshot IS NULL
      AND line.ProjectedCostAtPricing IS NULL
      AND COALESCE(stock.UnitCost, ownerCard.UnitCost, itemCard.UnitCost) IS NOT NULL;

    IF COL_LENGTH(N'dbo.SaleItems', N'ProjectedCostAtPricing') IS NOT NULL
        UPDATE dbo.SaleItems
        SET LineCostSnapshot = TRY_CONVERT(decimal(19, 4), ROUND(CONVERT(float, ProjectedCostAtPricing) * CONVERT(float, Quantity), 4))
        WHERE LineCostSnapshot IS NULL
          AND ProjectedCostAtPricing IS NOT NULL
          AND TRY_CONVERT(decimal(19, 4), ROUND(CONVERT(float, ProjectedCostAtPricing) * CONVERT(float, Quantity), 4)) IS NOT NULL;

    IF COL_LENGTH(N'dbo.SaleItems', N'StockOwnerQuantity') IS NOT NULL
        UPDATE dbo.SaleItems
        SET LineCostSnapshot = TRY_CONVERT(decimal(19, 4), ROUND(CONVERT(float, UnitCostSnapshot) * CONVERT(float, COALESCE(StockOwnerQuantity, Quantity)), 4))
        WHERE LineCostSnapshot IS NULL
          AND UnitCostSnapshot IS NOT NULL
          AND TRY_CONVERT(decimal(19, 4), ROUND(CONVERT(float, UnitCostSnapshot) * CONVERT(float, COALESCE(StockOwnerQuantity, Quantity)), 4)) IS NOT NULL;

    UPDATE dbo.SaleItems
    SET
        UnitCostSnapshot = TRY_CONVERT(decimal(19, 4), ROUND(CONVERT(float, LineCostSnapshot) / CONVERT(float, Quantity), 4)),
        UnitProfitSnapshot = TRY_CONVERT(decimal(19, 4), ROUND(CONVERT(float, UnitPrice) - (CONVERT(float, LineCostSnapshot) / CONVERT(float, Quantity)), 4)),
        LineProfitSnapshot = TRY_CONVERT(decimal(19, 4), ROUND(CONVERT(float, LineTotal) - CONVERT(float, LineCostSnapshot), 4))
    WHERE LineCostSnapshot IS NOT NULL
      AND Quantity <> 0
      AND LineProfitSnapshot IS NULL;
END TRY
BEGIN CATCH
END CATCH
