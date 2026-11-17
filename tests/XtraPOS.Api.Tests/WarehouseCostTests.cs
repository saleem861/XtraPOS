using XtraPOS.Api.Catalog;
using Xunit;

namespace XtraPOS.Api.Tests;

public class WarehouseCostTests
{
    [Fact]
    public void Weighted_average_uses_warehouse_quantity_and_invoice_cost()
    {
        var next = WarehouseCostService.WeightedAverage(10m, 4m, 10m, 6m);
        Assert.Equal(5m, next);
    }

    [Fact]
    public void Empty_warehouse_takes_the_invoice_cost()
    {
        var next = WarehouseCostService.WeightedAverage(0m, null, 4m, 7.5m);
        Assert.Equal(7.5m, next);
    }
}
