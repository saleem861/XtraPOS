using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace XtraPOS.Api.Credit;

public static class CreditDocumentSnapshot
{
    public static async Task<string> ReadAsync(SqlConnection branch, Guid saleId, decimal deferred, CancellationToken cancellationToken)
    {
        await using var header = new SqlCommand(
            """
            SELECT sale.SaleNumber, sale.Subtotal, sale.DiscountTotal, sale.Total, sale.CompletedAt,
                   customer.CustomerNumber, customer.Name, branch.BranchName, station.StationNumber, cashier.DisplayName
            FROM dbo.Sales AS sale
            LEFT JOIN dbo.Customers AS customer ON customer.CustomerID = sale.CustomerID
            LEFT JOIN dbo.Branches AS branch ON branch.BranchID = sale.BranchID
            LEFT JOIN dbo.POSStations AS station ON station.StationID = sale.POSStationID
            LEFT JOIN dbo.Users AS cashier ON cashier.UserID = sale.CashierUserID
            WHERE sale.SaleID = @sale;
            """,
            branch);
        header.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        await using var headerReader = await header.ExecuteReaderAsync(cancellationToken);
        if (!await headerReader.ReadAsync(cancellationToken))
        {
            return "{}";
        }

        var saleNumber = headerReader.IsDBNull(0) ? (long?)null : headerReader.GetInt64(0);
        var subtotal = headerReader.GetDecimal(1);
        var discount = headerReader.GetDecimal(2);
        var total = headerReader.GetDecimal(3);
        var when = headerReader.IsDBNull(4) ? (DateTime?)null : headerReader.GetDateTime(4);
        var customerNumber = headerReader.IsDBNull(5) ? null : headerReader.GetString(5);
        var customerName = headerReader.IsDBNull(6) ? null : headerReader.GetString(6);
        var branchName = headerReader.IsDBNull(7) ? null : headerReader.GetString(7);
        var posNumber = headerReader.IsDBNull(8) ? (int?)null : headerReader.GetInt32(8);
        var cashier = headerReader.IsDBNull(9) ? null : headerReader.GetString(9);
        await headerReader.CloseAsync();

        decimal immediate = 0m;
        await using (var payments = new SqlCommand("SELECT ISNULL(SUM(Amount), 0) FROM dbo.SalePayments WHERE SaleID = @sale;", branch))
        {
            payments.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
            immediate = Convert.ToDecimal(await payments.ExecuteScalarAsync(cancellationToken));
        }

        var lines = new List<object>();
        await using var items = new SqlCommand(
            """
            SELECT LineNumber, ItemNameSnapshot, Quantity, UnitPrice, LineTotal
            FROM dbo.SaleItems
            WHERE SaleID = @sale
            ORDER BY LineNumber;
            """,
            branch);
        items.Parameters.Add("@sale", SqlDbType.UniqueIdentifier).Value = saleId;
        await using var itemReader = await items.ExecuteReaderAsync(cancellationToken);
        while (await itemReader.ReadAsync(cancellationToken))
        {
            var quantity = itemReader.GetDecimal(2);
            var unitPrice = itemReader.GetDecimal(3);
            var lineTotal = itemReader.GetDecimal(4);
            var gross = quantity * unitPrice;
            var lineDiscount = gross > lineTotal ? gross - lineTotal : 0m;
            lines.Add(new
            {
                number = itemReader.GetInt32(0),
                name = itemReader.GetString(1),
                unit = "—",
                quantity,
                unitPrice,
                discount = lineDiscount,
                total = lineTotal,
            });
        }

        return JsonSerializer.Serialize(new
        {
            kind = "sale",
            saleNumber = saleNumber is long number ? "SO-" + number.ToString("D8") : null,
            customerNumber,
            customerName,
            branchName,
            posNumber,
            cashier,
            when,
            subtotal,
            discount,
            total,
            immediate,
            deferred,
            notes = "تأجيل دفع",
            lines,
        });
    }
}
