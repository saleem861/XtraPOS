using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;

namespace XtraPOS.Api.Sales;

public static class ItemUnitCost
{
    public static async Task<(decimal? UnitCost, string Source)> ReadAsync(
        SqlConnection connection,
        Guid branchId,
        Guid itemId,
        Guid ownerItemId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT
                (SELECT TOP (1) UnitCost FROM dbo.ItemBranchInventory WHERE BranchID = @branch AND ItemID = @item AND UnitCost IS NOT NULL),
                (SELECT TOP (1) UnitCost FROM dbo.ItemBranchInventory WHERE BranchID = @branch AND ItemID = @owner AND UnitCost IS NOT NULL),
                (SELECT TOP (1) UnitCost FROM dbo.BranchStockBalances WHERE BranchID = @branch AND ItemID = @owner AND UnitCost IS NOT NULL);
            """,
            connection);
        command.Parameters.AddWithValue("@branch", branchId);
        command.Parameters.AddWithValue("@item", itemId);
        command.Parameters.AddWithValue("@owner", ownerItemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return (null, VariableValuePricing.CostUnavailable);
        }

        if (!reader.IsDBNull(0))
        {
            return (reader.GetDecimal(0), VariableValuePricing.CostFromItemCard);
        }

        if (!reader.IsDBNull(1))
        {
            return (reader.GetDecimal(1), VariableValuePricing.CostFromItemCard);
        }

        if (!reader.IsDBNull(2))
        {
            return (reader.GetDecimal(2), VariableValuePricing.CostFromOwner);
        }

        return (null, VariableValuePricing.CostUnavailable);
    }
}
