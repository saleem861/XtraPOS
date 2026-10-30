using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Sales;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Credit;

public sealed class CreditConflictException(string message) : InvalidOperationException(message);

public sealed record CreditQuote(decimal Total, decimal Deferred, Guid CashSessionId, CheckoutService.CreditPreparedSale Prepared);

public sealed class CreditSaleDocumentWriter(CheckoutService checkout)
{
    public async Task<CreditQuote> QuoteAsync(SqlConnection branch, CreditSaleRequest request, CancellationToken cancellationToken)
    {
        if (request.Lines.Count == 0)
        {
            throw new CreditConflictException("A credit sale needs priced lines.");
        }

        await using var customer = new SqlCommand(
            "SELECT IsActive FROM dbo.Customers WHERE CustomerID = @id AND BusinessID = @business;",
            branch);
        customer.Parameters.Add("@id", SqlDbType.UniqueIdentifier).Value = request.CustomerId;
        customer.Parameters.Add("@business", SqlDbType.UniqueIdentifier).Value = request.BusinessId;
        var active = await customer.ExecuteScalarAsync(cancellationToken);
        if (active is not bool isActive || !isActive)
        {
            throw new CreditConflictException("The customer is not an active registered customer.");
        }

        var actor = new PosDeviceContext(request.ActorUserId, "", request.BusinessId, request.BranchId, request.StationId, request.DeviceId);
        var lines = request.Lines.Select(line =>
        {
            var lineType = string.IsNullOrWhiteSpace(line.LineType) ? "Fixed" : line.LineType;
            if (lineType == VariableValuePricing.Mode
                && (line.ExpectedFinalSellingPrice is null || line.ExpectedReferenceBasePrice is null || line.ExpectedWeightKg is null || line.ExpectedStockOwnerQuantity is null))
            {
                throw new CreditConflictException("A variable-weight credit line must confirm the server price, weight, and stock quantity.");
            }

            return new CheckoutLine
            {
                CartLineId = Guid.NewGuid(),
                LineType = lineType,
                ItemId = line.ItemId,
                Quantity = line.Quantity,
                ExpectedUnitPrice = line.ExpectedUnitPrice,
                PriceOverrideMode = line.PriceOverrideMode,
                ManualUnitPrice = line.ManualUnitPrice,
                ManualLineTotal = line.ManualLineTotal,
                EnteredScaleValue = line.EnteredScaleValue,
                ExpectedFinalSellingPrice = line.ExpectedFinalSellingPrice,
                ExpectedReferenceBasePrice = line.ExpectedReferenceBasePrice,
                ExpectedWeightKg = line.ExpectedWeightKg,
                ExpectedStockOwnerQuantity = line.ExpectedStockOwnerQuantity,
            };
        }).ToArray();

        CheckoutService.CreditPreparedSale prepared;
        try
        {
            prepared = await checkout.PrepareCreditAsync(branch, actor, request.CustomerId, request.CashSessionId, lines, cancellationToken);
        }
        catch (CheckoutException exception)
        {
            throw new CreditConflictException(exception.Message);
        }

        if (request.InvoiceTotal != prepared.Total || request.ImmediatePayment < 0m || request.ImmediatePayment > prepared.Total)
        {
            throw new CreditConflictException("The submitted total does not match the server price.");
        }

        return new CreditQuote(prepared.Total, prepared.Total - request.ImmediatePayment, prepared.CashSessionId, prepared);
    }

    public async Task PersistAsync(
        SqlConnection branch,
        SqlTransaction transaction,
        CreditSaleRequest request,
        CreditQuote quote,
        Guid saleId,
        CancellationToken cancellationToken)
    {
        var actor = new PosDeviceContext(request.ActorUserId, "", request.BusinessId, request.BranchId, request.StationId, request.DeviceId);
        await checkout.PersistCreditAsync(branch, transaction, actor, saleId, request.CartId, request.CustomerId, quote.Prepared, request.ImmediatePayment, cancellationToken);
    }
}
