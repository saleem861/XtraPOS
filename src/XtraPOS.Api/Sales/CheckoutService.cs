using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Catalog;
using XtraPOS.Api.Data;
using XtraPOS.Api.Security;

namespace XtraPOS.Api.Sales;

public enum CheckoutFault
{
    None,
    AfterSale,
    AfterItems,
    DuringPayment,
    DuringStock,
}

public sealed class CheckoutException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public static class CheckoutCodes
{
    public const string CashSessionRequired = "CASH_SESSION_REQUIRED";
    public const string PaymentTotalMismatch = "PAYMENT_TOTAL_MISMATCH";
    public const string PriceChanged = "PRICE_CHANGED";
    public const string CartAlreadyUsed = "CART_ALREADY_USED";
}

public sealed record CheckoutPriceChange(Guid CartLineId, string Field, decimal Preview, decimal Authoritative);

public sealed record CheckoutPaymentResult(string Method, decimal Amount, decimal? TenderedAmount, decimal ChangeAmount);

public sealed record CheckoutCompleted(
    Guid SaleId,
    long SaleNumber,
    decimal Total,
    IReadOnlyList<CheckoutPaymentResult> Payments,
    bool IdempotentReplay);

public abstract record CheckoutResult;

public sealed record CheckoutSucceeded(CheckoutCompleted Sale) : CheckoutResult;

public sealed record CheckoutPriceRejected(IReadOnlyList<CheckoutPriceChange> Changes) : CheckoutResult;

public sealed class CheckoutLine
{
    public Guid CartLineId { get; init; }
    public string LineType { get; init; } = "";
    public Guid? ItemId { get; init; }
    public decimal Quantity { get; init; }
    public decimal? ExpectedUnitPrice { get; init; }
    public decimal? ExpectedStockOwnerQuantity { get; init; }
    public decimal? EnteredScaleValue { get; init; }
    public decimal? ExpectedFinalSellingPrice { get; init; }
    public decimal? ExpectedReferenceBasePrice { get; init; }
    public decimal? ExpectedWeightKg { get; init; }
    public string? PriceOverrideMode { get; init; }
    public decimal? ManualUnitPrice { get; init; }
    public decimal? ManualLineTotal { get; init; }
}

public sealed class CheckoutService(
    SqlConnectionFactory connections,
    PricingResolver fixedPricing,
    VariableValuePricingResolver variablePricing,
    StockPostingService stock,
    SalePaymentService payments,
    PermissionService permissions)
{
    public Task<CheckoutResult> CheckoutAsync(
        PosDeviceContext actor,
        Guid cartId,
        Guid? customerId,
        IReadOnlyList<CheckoutLine> lines,
        IReadOnlyList<CheckoutPaymentInput> payments,
        CancellationToken cancellationToken) =>
        CheckoutAsync(actor, cartId, customerId, lines, payments, CheckoutFault.None, cancellationToken);

    public async Task<CheckoutResult> CheckoutAsync(
        PosDeviceContext actor,
        Guid cartId,
        Guid? customerId,
        IReadOnlyList<CheckoutLine> lines,
        IReadOnlyList<CheckoutPaymentInput> payments,
        CheckoutFault fault,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);
        var existing = await FindSaleAsync(connection, actor.BranchId, cartId, cancellationToken);
        if (existing is not null)
        {
            if (existing.Value.Status != SaleStatuses.Completed)
            {
                throw new CheckoutException(CheckoutCodes.CartAlreadyUsed, "هذه السلة مرتبطة ببيع غير مكتمل.", StatusCodes.Status409Conflict);
            }

            return new CheckoutSucceeded(await ReadCompletedAsync(connection, existing.Value.SaleId, replay: true, cancellationToken));
        }

        if (lines.Count == 0)
        {
            throw new CheckoutException(SaleCodes.EmptyCart, "السلة فارغة.", StatusCodes.Status400BadRequest);
        }

        if (lines.Any(line => PriceOverrideModes.IsManual(line.PriceOverrideMode))
            && !await permissions.HasPermissionAsync(actor.UserId, PermissionCodes.PosOverrideLinePrice, cancellationToken))
        {
            throw new CheckoutException(PriceOverrideModes.Forbidden, "لا توجد صلاحية لتعديل سعر سطر البيع.", StatusCodes.Status403Forbidden);
        }

        if (await FindOpenSessionIdAsync(connection, null, actor, cancellationToken) is null)
        {
            throw new CheckoutException(CheckoutCodes.CashSessionRequired, "يجب فتح الصندوق أولاً.", StatusCodes.Status409Conflict);
        }

        var prepared = new List<PreparedLine>();
        var changes = new List<CheckoutPriceChange>();
        foreach (var line in lines)
        {
            prepared.Add(await PrepareLineAsync(connection, actor, customerId, line, changes, cancellationToken));
        }

        if (changes.Count > 0)
        {
            return new CheckoutPriceRejected(changes);
        }

        var total = decimal.Round(prepared.Sum(line => line.LineTotal), 4, MidpointRounding.AwayFromZero);
        var normalizedPayments = NormalizePayments(payments, total);

        try
        {
            return await stock.InTransactionAsync(async (session, token) =>
            {
                var sessionId = await FindOpenSessionIdAsync(session.Connection, session.Transaction, actor, token)
                    ?? throw new CheckoutException(CheckoutCodes.CashSessionRequired, "يجب فتح الصندوق أولاً.", StatusCodes.Status409Conflict);
                var saleId = Guid.NewGuid();
                var saleNumber = await InsertSaleAsync(session, actor, cartId, customerId, sessionId, saleId, total, token);
                await FailAsync(fault, CheckoutFault.AfterSale);
                var lineNumber = 1;
                foreach (var line in prepared)
                {
                    await InsertItemAsync(session, saleId, lineNumber, line, token);
                    lineNumber++;
                }

                await FailAsync(fault, CheckoutFault.AfterItems);
                await FailAsync(fault, CheckoutFault.DuringPayment);
                foreach (var payment in normalizedPayments)
                {
                    await InsertPaymentAsync(session, saleId, sessionId, actor, payment, token);
                }

                await FailAsync(fault, CheckoutFault.DuringStock);
                foreach (var line in prepared.OrderBy(item => item.StockOwnerItemId))
                {
                    await stock.PostDeltaAsync(new StockDeltaRequest(
                        actor.BranchId,
                        line.StockOwnerItemId,
                        StockSourceTypes.Sale,
                        saleId,
                        line.SaleItemId,
                        -line.StockOwnerQuantity,
                        line.UnitCost,
                        DateTime.UtcNow,
                        actor.UserId), session, token);
                }

                await using var complete = new SqlCommand(
                    """
                    UPDATE dbo.Sales
                    SET Status = N'Completed', CompletedAt = SYSUTCDATETIME()
                    WHERE SaleID = @id AND Status = N'Draft';
                    """,
                    session.Connection,
                    session.Transaction);
                complete.Parameters.AddWithValue("@id", saleId);
                if (await complete.ExecuteNonQueryAsync(token) != 1)
                {
                    throw new InvalidOperationException("Could not complete the sale.");
                }

                return (CheckoutResult)new CheckoutSucceeded(new CheckoutCompleted(
                    saleId,
                    saleNumber,
                    total,
                    normalizedPayments.Select(payment => new CheckoutPaymentResult(payment.Method, payment.Amount, payment.TenderedAmount, payment.ChangeAmount)).ToArray(),
                    false));
            }, cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            var replay = await FindSaleAsync(connection, actor.BranchId, cartId, cancellationToken);
            if (replay is not null && replay.Value.Status == SaleStatuses.Completed)
            {
                return new CheckoutSucceeded(await ReadCompletedAsync(connection, replay.Value.SaleId, replay: true, cancellationToken));
            }

            throw;
        }
    }

    private async Task<PreparedLine> PrepareLineAsync(
        SqlConnection connection,
        PosDeviceContext actor,
        Guid? customerId,
        CheckoutLine line,
        List<CheckoutPriceChange> changes,
        CancellationToken cancellationToken)
    {
        if (line.CartLineId == Guid.Empty)
        {
            throw new CheckoutException(SaleCodes.InvalidQuantity, "سطر السلة غير صالح.", StatusCodes.Status400BadRequest);
        }

        if (line.LineType == PricingResolver.FixedMode)
        {
            if (line.ItemId is not Guid itemId || line.Quantity <= 0m)
            {
                throw new CheckoutException(SaleCodes.InvalidQuantity, "كمية البيع يجب أن تكون أكبر من صفر.", StatusCodes.Status400BadRequest);
            }

            await RejectAssortmentParentAsync(connection, itemId, cancellationToken);
            var price = await fixedPricing.ResolveFixedPriceAsync(itemId, customerId, cancellationToken, line.Quantity);
            var item = await ReadItemAsync(connection, itemId, cancellationToken);
            var owner = StockOwnership.Resolve(itemId, await ReadLinksAsync(connection, cancellationToken));
            var ownerQuantity = line.Quantity * owner.BaseQuantityFactor;
            if (!PriceOverrideModes.IsManual(line.PriceOverrideMode))
            {
                Compare(changes, line.CartLineId, "UnitPrice", line.ExpectedUnitPrice, price.ResolvedPrice);
            }
            Compare(changes, line.CartLineId, "StockOwnerQuantity", line.ExpectedStockOwnerQuantity, ownerQuantity);
            var unitCost = await ReadUnitCostAsync(connection, actor.BranchId, owner.StockOwnerItemId, cancellationToken);
            var charged = Charge(line, price.ResolvedPrice, price.PriceSource, line.Quantity);
            return new PreparedLine(
                Guid.NewGuid(),
                line.CartLineId,
                PricingResolver.FixedMode,
                itemId,
                item.Number,
                item.Name,
                line.Quantity,
                charged.UnitPrice,
                charged.LineTotal,
                customerId,
                price.PriceTypeId,
                price.PriceTypeCode,
                charged.PriceSource,
                owner.StockOwnerItemId,
                await ReadItemNumberAsync(connection, owner.StockOwnerItemId, cancellationToken),
                owner.BaseQuantityFactor,
                ownerQuantity,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                unitCost,
                unitCost is null ? null : decimal.Round(unitCost.Value * ownerQuantity, 4, MidpointRounding.AwayFromZero),
                unitCost is null ? VariableValuePricing.CostUnavailable : VariableValuePricing.CostFromOwner,
                price.ResolvedPrice,
                price.PriceSource,
                charged.Mode,
                price.AppliedSpecialPriceId,
                price.AppliedMinimumQuantity,
                price.AppliedBundlePrice,
                price.AppliedTargetUnitPrice);
        }

        if (line.LineType != VariableValuePricing.Mode)
        {
            throw new CheckoutException(SaleCodes.InvalidQuantity, "سطر السلة غير معروف.", StatusCodes.Status400BadRequest);
        }

        if (line.EnteredScaleValue is not decimal entered || line.Quantity < 1m || line.Quantity != decimal.Truncate(line.Quantity))
        {
            throw new CheckoutException(SaleCodes.InvalidQuantity, "عدد الحصص يجب أن يكون عدداً صحيحاً أكبر من صفر.", StatusCodes.Status400BadRequest);
        }

        var resolved = await variablePricing.ResolveAsync(entered, customerId, cancellationToken);
        await RejectAssortmentParentAsync(connection, resolved.ReferenceItemId, cancellationToken);
        if (!PriceOverrideModes.IsManual(line.PriceOverrideMode))
        {
            Compare(changes, line.CartLineId, "FinalSellingPrice", line.ExpectedFinalSellingPrice, resolved.FinalSellingPrice);
        }
        Compare(changes, line.CartLineId, "ReferenceBasePrice", line.ExpectedReferenceBasePrice, resolved.ReferenceBasePrice);
        Compare(changes, line.CartLineId, "WeightKg", line.ExpectedWeightKg, resolved.WeightKg);
        Compare(changes, line.CartLineId, "StockOwnerQuantity", line.ExpectedStockOwnerQuantity, resolved.ProjectedStockOwnerQuantity);
        var portions = line.Quantity;
        var referenceName = await ReadItemNameAsync(connection, resolved.ReferenceItemId, cancellationToken);
        var lineCost = resolved.ProjectedCost is null ? (decimal?)null : decimal.Round(resolved.ProjectedCost.Value * portions, 4, MidpointRounding.AwayFromZero);
        var weighedCharge = Charge(line, resolved.FinalSellingPrice, resolved.ReferencePriceSource, portions);
        return new PreparedLine(
            Guid.NewGuid(),
            line.CartLineId,
            VariableValuePricing.Mode,
            resolved.ReferenceItemId,
            resolved.ReferenceItemNumber,
            referenceName,
            portions,
            weighedCharge.UnitPrice,
            weighedCharge.LineTotal,
            customerId,
            await ReadPriceTypeIdAsync(connection, resolved.ReferencePriceTypeCode, cancellationToken),
            resolved.ReferencePriceTypeCode,
            weighedCharge.PriceSource,
            resolved.StockOwnerItemId,
            resolved.StockOwnerItemNumber,
            resolved.StockOwnerConversionFactor,
            resolved.ProjectedStockOwnerQuantity * portions,
            resolved.ReferenceItemId,
            resolved.ReferenceItemNumber,
            referenceName,
            resolved.EnteredScaleValue,
            resolved.ReferenceBasePrice,
            resolved.ReferencePriceSource,
            resolved.WeightKg,
            resolved.WeightGrams,
            resolved.VariableValueDiscountPercent,
            resolved.RawDiscountedValue,
            resolved.FinalSellingPrice,
            resolved.RoundingPolicy,
            resolved.ProjectedCost,
            resolved.ProjectedCost is null ? null : await ReadUnitCostAsync(connection, actor.BranchId, resolved.StockOwnerItemId, cancellationToken),
            lineCost,
            resolved.CostSource,
            resolved.FinalSellingPrice,
            resolved.ReferencePriceSource,
            weighedCharge.Mode);
    }

    private static ChargedPrice Charge(CheckoutLine line, decimal resolvedUnit, string resolvedSource, decimal quantity)
    {
        var mode = string.IsNullOrWhiteSpace(line.PriceOverrideMode) ? PriceOverrideModes.None : line.PriceOverrideMode;
        if (mode == PriceOverrideModes.None)
        {
            return new ChargedPrice(resolvedUnit, decimal.Round(resolvedUnit * quantity, 4, MidpointRounding.AwayFromZero), resolvedSource, PriceOverrideModes.None);
        }

        if (mode == PriceOverrideModes.UnitPrice)
        {
            if (line.ManualUnitPrice is not decimal manual || manual < 0m || !MoneyAmount.Fits(manual))
            {
                throw new CheckoutException(PriceOverrideModes.Invalid, "سعر الوحدة اليدوي غير صالح.", StatusCodes.Status400BadRequest);
            }

            return new ChargedPrice(manual, decimal.Round(manual * quantity, 4, MidpointRounding.AwayFromZero), PriceOverrideModes.Source, PriceOverrideModes.UnitPrice);
        }

        if (mode == PriceOverrideModes.LineTotal)
        {
            if (line.ManualLineTotal is not decimal total || total < 0m || !MoneyAmount.Fits(total) || quantity <= 0m)
            {
                throw new CheckoutException(PriceOverrideModes.Invalid, "الإجمالي اليدوي غير صالح.", StatusCodes.Status400BadRequest);
            }

            var unit = decimal.Round(total / quantity, 4, MidpointRounding.AwayFromZero);
            return new ChargedPrice(unit, total, PriceOverrideModes.Source, PriceOverrideModes.LineTotal);
        }

        throw new CheckoutException(PriceOverrideModes.Invalid, "نوع تعديل السعر غير معروف.", StatusCodes.Status400BadRequest);
    }

    private List<SalePaymentDraft> NormalizePayments(IReadOnlyList<CheckoutPaymentInput> requested, decimal total)
    {
        if (requested.Count == 0)
        {
            throw new CheckoutException(CheckoutCodes.PaymentTotalMismatch, "المدفوعات لا تطابق إجمالي البيع.", StatusCodes.Status400BadRequest);
        }

        var drafts = requested.Select(payment => payments.Normalize(payment)).ToList();
        var applied = decimal.Round(drafts.Sum(payment => payment.Amount), 4, MidpointRounding.AwayFromZero);
        if (applied != total)
        {
            throw new CheckoutException(CheckoutCodes.PaymentTotalMismatch, "المدفوعات لا تطابق إجمالي البيع.", StatusCodes.Status400BadRequest);
        }

        return drafts;
    }

    private static async Task RejectAssortmentParentAsync(SqlConnection connection, Guid itemId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT ItemNumber, ItemName FROM dbo.Items WHERE ItemID = @id;",
            connection);
        command.Parameters.AddWithValue("@id", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new PricingException(PricingCodes.ItemNotFound, "الصنف غير موجود.");
        }

        var number = reader.GetString(0);
        var name = reader.GetString(1);
        await reader.CloseAsync();
        await using var children = new SqlCommand(
            "SELECT COUNT(*) FROM dbo.Items WHERE ParentItemID = @id AND IsActive = 1;",
            connection);
        children.Parameters.AddWithValue("@id", itemId);
        if (Convert.ToInt32(await children.ExecuteScalarAsync(cancellationToken)) > 0)
        {
            throw new AssortmentRuleException(itemId, number, name);
        }
    }

    private static void Compare(List<CheckoutPriceChange> changes, Guid cartLineId, string field, decimal? preview, decimal authoritative)
    {
        if (preview is decimal expected && expected != authoritative)
        {
            changes.Add(new CheckoutPriceChange(cartLineId, field, expected, authoritative));
        }
    }

    private static async Task FailAsync(CheckoutFault actual, CheckoutFault point)
    {
        if (actual == point)
        {
            throw new InvalidOperationException($"Checkout fault {point}.");
        }

        await Task.CompletedTask;
    }

    private static async Task<Guid?> FindOpenSessionIdAsync(
        SqlConnection connection,
        SqlTransaction? transaction,
        PosDeviceContext actor,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT CashSessionID
            FROM dbo.POSCashSessions WITH (UPDLOCK, HOLDLOCK)
            WHERE BranchID = @branch AND DeviceID = @device AND Status = N'Open';
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@branch", actor.BranchId);
        command.Parameters.AddWithValue("@device", actor.DeviceId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }

    private static async Task<long> InsertSaleAsync(
        StockPostingSession session,
        PosDeviceContext actor,
        Guid cartId,
        Guid? customerId,
        Guid cashSessionId,
        Guid saleId,
        decimal total,
        CancellationToken cancellationToken,
        string status = "Draft")
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.Sales
            (
                SaleID, BusinessID, BranchID, POSStationID, DeviceID, CartID, CustomerID, CashierUserID,
                CashSessionID, SaleNumber, Status, Subtotal, DiscountTotal, Total
            )
            OUTPUT INSERTED.SaleNumber
            VALUES
            (
                @sale, @business, @branch, @station, @device, @cart, @customer, @cashier,
                @session, NEXT VALUE FOR dbo.SaleNumberSequence, @status, @total, 0, @total
            );
            """,
            session.Connection,
            session.Transaction);
        command.Parameters.AddWithValue("@sale", saleId);
        command.Parameters.AddWithValue("@business", actor.BusinessId);
        command.Parameters.AddWithValue("@branch", actor.BranchId);
        command.Parameters.AddWithValue("@station", actor.StationId);
        command.Parameters.AddWithValue("@device", actor.DeviceId);
        command.Parameters.AddWithValue("@cart", cartId);
        command.Parameters.AddWithValue("@customer", (object?)customerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@cashier", actor.UserId);
        command.Parameters.AddWithValue("@session", cashSessionId);
        AddMoney(command, "@total", total);
        command.Parameters.AddWithValue("@status", status);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<CreditPreparedSale> PrepareCreditAsync(
        SqlConnection connection,
        PosDeviceContext actor,
        Guid customerId,
        Guid claimedSessionId,
        IReadOnlyList<CheckoutLine> lines,
        CancellationToken cancellationToken)
    {
        if (lines.Count == 0)
        {
            throw new CheckoutException(SaleCodes.EmptyCart, "السلة فارغة.", StatusCodes.Status400BadRequest);
        }

        if (lines.Any(line => PriceOverrideModes.IsManual(line.PriceOverrideMode))
            && !await permissions.HasPermissionAsync(actor.UserId, PermissionCodes.PosOverrideLinePrice, cancellationToken))
        {
            throw new CheckoutException(PriceOverrideModes.Forbidden, "لا توجد صلاحية لتعديل سعر سطر البيع.", StatusCodes.Status403Forbidden);
        }

        var sessionId = await FindOpenSessionIdAsync(connection, null, actor, cancellationToken)
            ?? throw new CheckoutException(CheckoutCodes.CashSessionRequired, "يجب فتح الصندوق أولاً.", StatusCodes.Status409Conflict);
        if (claimedSessionId != Guid.Empty && claimedSessionId != sessionId)
        {
            throw new CheckoutException(CheckoutCodes.CashSessionRequired, "جلسة الصندوق لا تخص هذا الجهاز.", StatusCodes.Status409Conflict);
        }

        var prepared = new List<PreparedLine>();
        var changes = new List<CheckoutPriceChange>();
        foreach (var line in lines)
        {
            prepared.Add(await PrepareLineAsync(connection, actor, customerId, line, changes, cancellationToken));
        }

        if (changes.Count > 0)
        {
            throw new CheckoutException(CheckoutCodes.PriceChanged, "السعر المرسل لا يطابق سعر الخادم.", StatusCodes.Status409Conflict);
        }

        var total = decimal.Round(prepared.Sum(line => line.LineTotal), 4, MidpointRounding.AwayFromZero);
        return new CreditPreparedSale(sessionId, total, prepared);
    }

    public async Task PersistCreditAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        PosDeviceContext actor,
        Guid saleId,
        Guid cartId,
        Guid customerId,
        CreditPreparedSale prepared,
        decimal immediatePayment,
        CancellationToken cancellationToken)
    {
        var session = new StockPostingSession(connection, transaction);
        await InsertSaleAsync(session, actor, cartId, customerId, prepared.CashSessionId, saleId, prepared.Total, cancellationToken, SaleStatuses.CreditPending);
        var lineNumber = 1;
        foreach (var line in prepared.Lines)
        {
            await InsertItemAsync(session, saleId, lineNumber, line, cancellationToken);
            lineNumber++;
            await stock.PostDeltaAsync(new StockDeltaRequest(
                actor.BranchId,
                line.StockOwnerItemId,
                StockSourceTypes.Sale,
                saleId,
                line.SaleItemId,
                -line.StockOwnerQuantity,
                line.UnitCost,
                DateTime.UtcNow,
                actor.UserId), session, cancellationToken);
        }

        if (immediatePayment > 0m)
        {
            await InsertPaymentAsync(session, saleId, prepared.CashSessionId, actor, new SalePaymentDraft(
                Guid.NewGuid(),
                "Cash",
                immediatePayment,
                immediatePayment,
                0m,
                null), cancellationToken);
        }
    }

    private static async Task InsertPaymentAsync(
        StockPostingSession session,
        Guid saleId,
        Guid cashSessionId,
        PosDeviceContext actor,
        SalePaymentDraft payment,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.SalePayments
            (
                SalePaymentID, SaleID, CashSessionID, BranchID, ClientPaymentKey,
                PaymentMethodCode, Amount, TenderedAmount, ChangeAmount, ExternalReference, CreatedByUserID
            )
            VALUES
            (
                @id, @sale, @session, @branch, @key,
                @method, @amount, @tendered, @change, @reference, @user
            );
            """,
            session.Connection,
            session.Transaction);
        command.Parameters.AddWithValue("@id", Guid.NewGuid());
        command.Parameters.AddWithValue("@sale", saleId);
        command.Parameters.AddWithValue("@session", cashSessionId);
        command.Parameters.AddWithValue("@branch", actor.BranchId);
        command.Parameters.AddWithValue("@key", payment.ClientPaymentKey);
        command.Parameters.AddWithValue("@method", payment.Method);
        AddMoney(command, "@amount", payment.Amount);
        AddNullableMoney(command, "@tendered", payment.TenderedAmount);
        AddMoney(command, "@change", payment.ChangeAmount);
        command.Parameters.AddWithValue("@reference", (object?)payment.ExternalReference ?? DBNull.Value);
        command.Parameters.AddWithValue("@user", actor.UserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task InsertItemAsync(
        StockPostingSession session,
        Guid saleId,
        int lineNumber,
        PreparedLine line,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            INSERT INTO dbo.SaleItems
            (
                SaleItemID, SaleID, LineNumber, PricingMode, ItemID, ItemNumberSnapshot, ItemNameSnapshot,
                Quantity, UnitPrice, LineTotal, CustomerID, PriceTypeID, PriceTypeCodeSnapshot, PriceSource,
                PricingResolvedAt, StockOwnerItemID, StockOwnerItemNumberSnapshot, StockOwnerConversionFactor,
                StockOwnerQuantity, ReferenceItemID, ReferenceItemNumberSnapshot, ReferenceItemNameSnapshot,
                EnteredScaleValue, ReferenceBasePrice, ReferencePriceSource, WeightKg, WeightGrams,
                VariableValueDiscountPercent, RawDiscountedValue, FinalSellingPrice, RoundingPolicy,
                ProjectedCostAtPricing, UnitCostSnapshot, LineCostSnapshot, CostSource,
                ResolvedUnitPrice, ResolvedPriceSource, PriceOverrideMode,
                AppliedSpecialPriceID, AppliedMinimumQuantity, AppliedBundlePrice, AppliedTargetUnitPrice
            )
            VALUES
            (
                @id, @sale, @lineNumber, @mode, @item, @itemNumber, @itemName,
                @quantity, @unitPrice, @lineTotal, @customer, @priceType, @priceCode, @priceSource,
                SYSUTCDATETIME(), @owner, @ownerNumber, CONVERT(decimal(38, 28), @factor),
                CONVERT(decimal(38, 28), @ownerQuantity), @reference, @referenceNumber, @referenceName,
                @entered, @basePrice, @baseSource, CONVERT(decimal(38, 28), @weightKg), CONVERT(decimal(38, 28), @weightGrams),
                @discount, @raw, @final, @rounding,
                @projected, @unitCost, @lineCost, @costSource,
                @resolvedUnit, @resolvedSource, @overrideMode,
                @specialId, @specialMinimum, @specialBundle, @specialTarget
            );
            """,
            session.Connection,
            session.Transaction);
        command.Parameters.AddWithValue("@id", line.SaleItemId);
        command.Parameters.AddWithValue("@sale", saleId);
        command.Parameters.AddWithValue("@lineNumber", lineNumber);
        command.Parameters.AddWithValue("@mode", line.PricingMode);
        command.Parameters.AddWithValue("@item", line.ItemId);
        command.Parameters.AddWithValue("@itemNumber", line.ItemNumber);
        command.Parameters.AddWithValue("@itemName", line.ItemName);
        AddDecimal(command, "@quantity", line.Quantity, 18, 4);
        AddMoney(command, "@unitPrice", line.UnitPrice);
        AddMoney(command, "@lineTotal", line.LineTotal);
        command.Parameters.AddWithValue("@customer", (object?)line.CustomerId ?? DBNull.Value);
        command.Parameters.AddWithValue("@priceType", (object?)line.PriceTypeId ?? DBNull.Value);
        command.Parameters.AddWithValue("@priceCode", (object?)line.PriceTypeCode ?? DBNull.Value);
        command.Parameters.AddWithValue("@priceSource", line.PriceSource);
        command.Parameters.AddWithValue("@owner", line.StockOwnerItemId);
        command.Parameters.AddWithValue("@ownerNumber", line.StockOwnerNumber);
        AddWide(command, "@factor", line.Factor);
        AddWide(command, "@ownerQuantity", line.StockOwnerQuantity);
        command.Parameters.AddWithValue("@reference", (object?)line.ReferenceItemId ?? DBNull.Value);
        command.Parameters.AddWithValue("@referenceNumber", (object?)line.ReferenceNumber ?? DBNull.Value);
        command.Parameters.AddWithValue("@referenceName", (object?)line.ReferenceName ?? DBNull.Value);
        AddNullableMoney(command, "@entered", line.EnteredScaleValue);
        AddNullableMoney(command, "@basePrice", line.ReferenceBasePrice);
        command.Parameters.AddWithValue("@baseSource", (object?)line.ReferencePriceSource ?? DBNull.Value);
        AddNullableWide(command, "@weightKg", line.WeightKg);
        AddNullableWide(command, "@weightGrams", line.WeightGrams);
        AddNullableDecimal(command, "@discount", line.DiscountPercent, 7, 4);
        AddNullableMoney(command, "@raw", line.RawDiscountedValue);
        AddNullableMoney(command, "@final", line.FinalSellingPrice);
        command.Parameters.AddWithValue("@rounding", (object?)line.RoundingPolicy ?? DBNull.Value);
        AddNullableMoney(command, "@projected", line.ProjectedCost);
        AddNullableMoney(command, "@unitCost", line.UnitCost);
        AddNullableMoney(command, "@lineCost", line.LineCost);
        command.Parameters.AddWithValue("@costSource", (object?)line.CostSource ?? DBNull.Value);
        AddMoney(command, "@resolvedUnit", line.ResolvedUnitPrice);
        command.Parameters.AddWithValue("@resolvedSource", line.ResolvedPriceSource);
        command.Parameters.AddWithValue("@overrideMode", line.PriceOverrideMode);
        command.Parameters.AddWithValue("@specialId", (object?)line.AppliedSpecialPriceId ?? DBNull.Value);
        AddNullableDecimal(command, "@specialMinimum", line.AppliedMinimumQuantity, 18, 4);
        AddNullableMoney(command, "@specialBundle", line.AppliedBundlePrice);
        AddNullableMoney(command, "@specialTarget", line.AppliedTargetUnitPrice);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(Guid SaleId, long SaleNumber, string Status)?> FindSaleAsync(
        SqlConnection connection,
        Guid branchId,
        Guid cartId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT SaleID, SaleNumber, Status FROM dbo.Sales WHERE BranchID = @branch AND CartID = @cart;",
            connection);
        command.Parameters.AddWithValue("@branch", branchId);
        command.Parameters.AddWithValue("@cart", cartId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetGuid(0), reader.GetInt64(1), reader.GetString(2));
    }

    private static async Task<CheckoutCompleted> ReadCompletedAsync(
        SqlConnection connection,
        Guid saleId,
        bool replay,
        CancellationToken cancellationToken)
    {
        await using var sale = new SqlCommand("SELECT SaleNumber, Total FROM dbo.Sales WHERE SaleID = @id;", connection);
        sale.Parameters.AddWithValue("@id", saleId);
        await using var header = await sale.ExecuteReaderAsync(cancellationToken);
        await header.ReadAsync(cancellationToken);
        var number = header.GetInt64(0);
        var total = header.GetDecimal(1);
        await header.CloseAsync();
        var payments = new List<CheckoutPaymentResult>();
        await using var rows = new SqlCommand(
            "SELECT PaymentMethodCode, Amount, TenderedAmount, ChangeAmount FROM dbo.SalePayments WHERE SaleID = @id ORDER BY CreatedAt;",
            connection);
        rows.Parameters.AddWithValue("@id", saleId);
        await using var reader = await rows.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            payments.Add(new CheckoutPaymentResult(
                reader.GetString(0),
                reader.GetDecimal(1),
                reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                reader.GetDecimal(3)));
        }

        return new CheckoutCompleted(saleId, number, total, payments, replay);
    }

    private static async Task<decimal?> ReadUnitCostAsync(SqlConnection connection, Guid branchId, Guid itemId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT UnitCost FROM dbo.BranchStockBalances WHERE BranchID = @branch AND ItemID = @item AND IsQuantityInitialized = 1;",
            connection);
        command.Parameters.AddWithValue("@branch", branchId);
        command.Parameters.AddWithValue("@item", itemId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is decimal cost ? cost : null;
    }

    private static async Task<(string Number, string Name)> ReadItemAsync(SqlConnection connection, Guid itemId, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT ItemNumber, ItemName FROM dbo.Items WHERE ItemID = @id;", connection);
        command.Parameters.AddWithValue("@id", itemId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new PricingException(PricingCodes.ItemNotFound, "الصنف غير موجود.");
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    private static async Task<string> ReadItemNumberAsync(SqlConnection connection, Guid itemId, CancellationToken cancellationToken) =>
        (await ReadItemAsync(connection, itemId, cancellationToken)).Number;

    private static async Task<string> ReadItemNameAsync(SqlConnection connection, Guid itemId, CancellationToken cancellationToken) =>
        (await ReadItemAsync(connection, itemId, cancellationToken)).Name;

    private static async Task<Guid?> ReadPriceTypeIdAsync(SqlConnection connection, string code, CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand("SELECT PriceTypeID FROM dbo.PriceTypes WHERE PriceCode = @code;", connection);
        command.Parameters.AddWithValue("@code", code);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is Guid id ? id : null;
    }

    private static async Task<List<StockUnitLink>> ReadLinksAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        var links = new List<StockUnitLink>();
        await using var command = new SqlCommand(
            "SELECT ItemID, InternalUnitItemID, UnitsPerPackage FROM dbo.ItemUnits WHERE IsActive = 1 AND InternalUnitItemID IS NOT NULL AND UnitsPerPackage > 0;",
            connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            links.Add(new StockUnitLink(reader.GetGuid(0), reader.GetGuid(1), reader.GetDecimal(2)));
        }

        return links;
    }

    private static void AddMoney(SqlCommand command, string name, decimal value) => AddDecimal(command, name, value, 19, 4);

    private static void AddNullableMoney(SqlCommand command, string name, decimal? value) => AddNullableDecimal(command, name, value, 19, 4);

    private static void AddDecimal(SqlCommand command, string name, decimal value, byte precision, byte scale)
    {
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Decimal) { Precision = precision, Scale = scale, Value = value });
    }

    private static void AddNullableDecimal(SqlCommand command, string name, decimal? value, byte precision, byte scale)
    {
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Decimal) { Precision = precision, Scale = scale, Value = (object?)value ?? DBNull.Value });
    }

    private static void AddWide(SqlCommand command, string name, decimal value) =>
        command.Parameters.AddWithValue(name, value.ToString("G29", CultureInfo.InvariantCulture));

    private static void AddNullableWide(SqlCommand command, string name, decimal? value) =>
        command.Parameters.AddWithValue(name, (object?)value?.ToString("G29", CultureInfo.InvariantCulture) ?? DBNull.Value);

    public sealed record CreditPreparedSale(Guid CashSessionId, decimal Total, IReadOnlyList<PreparedLine> Lines);

    public sealed record PreparedLine(
        Guid SaleItemId,
        Guid CartLineId,
        string PricingMode,
        Guid ItemId,
        string ItemNumber,
        string ItemName,
        decimal Quantity,
        decimal UnitPrice,
        decimal LineTotal,
        Guid? CustomerId,
        Guid? PriceTypeId,
        string? PriceTypeCode,
        string PriceSource,
        Guid StockOwnerItemId,
        string StockOwnerNumber,
        decimal Factor,
        decimal StockOwnerQuantity,
        Guid? ReferenceItemId,
        string? ReferenceNumber,
        string? ReferenceName,
        decimal? EnteredScaleValue,
        decimal? ReferenceBasePrice,
        string? ReferencePriceSource,
        decimal? WeightKg,
        decimal? WeightGrams,
        decimal? DiscountPercent,
        decimal? RawDiscountedValue,
        decimal? FinalSellingPrice,
        string? RoundingPolicy,
        decimal? ProjectedCost,
        decimal? UnitCost,
        decimal? LineCost,
        string? CostSource,
        decimal ResolvedUnitPrice,
        string ResolvedPriceSource,
        string PriceOverrideMode,
        Guid? AppliedSpecialPriceId = null,
        decimal? AppliedMinimumQuantity = null,
        decimal? AppliedBundlePrice = null,
        decimal? AppliedTargetUnitPrice = null);
}

public static class PriceOverrideModes
{
    public const string None = "None";
    public const string UnitPrice = "UnitPrice";
    public const string LineTotal = "LineTotal";
    public const string Source = "ManualOverride";
    public const string Forbidden = "PRICE_OVERRIDE_FORBIDDEN";
    public const string Invalid = "INVALID_PRICE_OVERRIDE";

    public static bool IsManual(string? mode) => mode is UnitPrice or LineTotal;
}

public sealed record ChargedPrice(decimal UnitPrice, decimal LineTotal, string PriceSource, string Mode);
