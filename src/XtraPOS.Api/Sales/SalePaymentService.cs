using System.Data;
using Microsoft.Data.SqlClient;
using XtraPOS.Api.Data;

namespace XtraPOS.Api.Sales;

public sealed class SalePaymentService(SqlConnectionFactory connections)
{
    public const int ExternalReferenceMaxLength = 100;

    public SalePaymentDraft Normalize(CheckoutPaymentInput? input)
    {
        if (input?.ClientPaymentKey is not Guid key || key == Guid.Empty)
        {
            throw new SalePaymentException(SalePaymentCodes.InvalidKey, "مفتاح الدفعة غير صالح.", StatusCodes.Status400BadRequest);
        }

        var method = input.Method?.Trim() ?? "";
        if (method.Equals(PaymentMethodCodes.Cash, StringComparison.OrdinalIgnoreCase))
        {
            method = PaymentMethodCodes.Cash;
        }
        else if (method.Equals(PaymentMethodCodes.Card, StringComparison.OrdinalIgnoreCase))
        {
            method = PaymentMethodCodes.Card;
        }
        else
        {
            throw new SalePaymentException(SalePaymentCodes.InvalidMethod, "طريقة الدفع غير مدعومة.", StatusCodes.Status400BadRequest);
        }

        if (input.Amount is not decimal amount || amount <= 0m || !MoneyAmount.Fits(amount))
        {
            throw new SalePaymentException(SalePaymentCodes.InvalidAmount, "مبلغ الدفعة يجب أن يكون أكبر من صفر.", StatusCodes.Status400BadRequest);
        }

        decimal? tendered = null;
        decimal change;
        if (method == PaymentMethodCodes.Cash)
        {
            if (input.TenderedAmount is not decimal given || !MoneyAmount.Fits(given) || given < amount)
            {
                throw new SalePaymentException(
                    SalePaymentCodes.InvalidTenderedAmount,
                    "المبلغ المستلم يجب أن يغطي مبلغ الدفعة.",
                    StatusCodes.Status400BadRequest);
            }

            tendered = given;
            change = given - amount;
        }
        else
        {
            if (input.TenderedAmount is not null)
            {
                throw new SalePaymentException(
                    SalePaymentCodes.InvalidTenderedAmount,
                    "الدفع بالبطاقة لا يستخدم مبلغاً مستلماً.",
                    StatusCodes.Status400BadRequest);
            }

            change = 0m;
        }

        if (change < 0m)
        {
            throw new SalePaymentException(SalePaymentCodes.InvalidTenderedAmount, "الباقي لا يمكن أن يكون سالباً.", StatusCodes.Status400BadRequest);
        }

        var reference = string.IsNullOrWhiteSpace(input.ExternalReference) ? null : input.ExternalReference.Trim();
        if (reference is not null && reference.Length > ExternalReferenceMaxLength)
        {
            throw new SalePaymentException(SalePaymentCodes.InvalidReference, "المرجع الخارجي أطول من الحد المسموح.", StatusCodes.Status400BadRequest);
        }

        return new SalePaymentDraft(key, method, amount, tendered, change, reference);
    }

    public async Task<SalePaymentRecord> RecordAsync(
        Guid saleId,
        Guid cashSessionId,
        Guid createdByUserId,
        CheckoutPaymentInput input,
        CancellationToken cancellationToken)
    {
        var draft = Normalize(input);
        await using var connection = new SqlConnection(connections.CreateBranchConnectionString());
        await connection.OpenAsync(cancellationToken);

        var sale = await ReadSaleAsync(connection, saleId, cancellationToken);
        if (sale is null)
        {
            throw new SalePaymentException(SalePaymentCodes.SaleNotFound, "فاتورة البيع غير موجودة.", StatusCodes.Status404NotFound);
        }

        var session = await ReadSessionAsync(connection, cashSessionId, cancellationToken);
        if (session is null
            || session.Value.Status != CashSessionStatus.Open
            || session.Value.BranchId != sale.Value.BranchId
            || session.Value.BusinessId != sale.Value.BusinessId)
        {
            throw new SalePaymentException(
                SalePaymentCodes.CashSessionInvalid,
                "صندوق هذه الدفعة غير صالح لهذا الفرع.",
                StatusCodes.Status409Conflict);
        }

        if (!await UserIsActiveAsync(connection, createdByUserId, cancellationToken))
        {
            throw new SalePaymentException(SalePaymentCodes.UserNotFound, "مستخدم الدفعة غير معروف.", StatusCodes.Status400BadRequest);
        }

        var existing = await FindByKeyAsync(connection, saleId, draft.ClientPaymentKey, cancellationToken);
        if (existing is not null)
        {
            return Match(existing, draft);
        }

        var paymentId = Guid.NewGuid();
        try
        {
            await InsertAsync(connection, paymentId, saleId, cashSessionId, sale.Value.BranchId, createdByUserId, draft, cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            var raced = await FindByKeyAsync(connection, saleId, draft.ClientPaymentKey, cancellationToken);
            if (raced is null)
            {
                throw;
            }

            return Match(raced, draft);
        }
        catch (SqlException exception) when (exception.Number == 547)
        {
            throw new SalePaymentException(
                SalePaymentCodes.CashSessionInvalid,
                "صندوق هذه الدفعة غير صالح لهذا الفرع.",
                StatusCodes.Status409Conflict);
        }

        return ToRecord(paymentId, saleId, cashSessionId, draft, false);
    }

    private static SalePaymentRecord Match(SalePaymentRecord existing, SalePaymentDraft draft)
    {
        var same = existing.PaymentMethodCode == draft.Method
            && existing.Amount == draft.Amount
            && existing.TenderedAmount == draft.TenderedAmount
            && existing.ChangeAmount == draft.ChangeAmount
            && existing.ExternalReference == draft.ExternalReference;
        if (!same)
        {
            throw new SalePaymentException(
                SalePaymentCodes.KeyConflict,
                "مفتاح الدفعة مستخدم بقيمة مختلفة.",
                StatusCodes.Status409Conflict);
        }

        return existing with { IdempotentReplay = true };
    }

    private static async Task InsertAsync(
        SqlConnection connection,
        Guid paymentId,
        Guid saleId,
        Guid cashSessionId,
        Guid branchId,
        Guid createdByUserId,
        SalePaymentDraft draft,
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
            connection);
        command.Parameters.AddWithValue("@id", paymentId);
        command.Parameters.AddWithValue("@sale", saleId);
        command.Parameters.AddWithValue("@session", cashSessionId);
        command.Parameters.AddWithValue("@branch", branchId);
        command.Parameters.AddWithValue("@key", draft.ClientPaymentKey);
        command.Parameters.AddWithValue("@method", draft.Method);
        AddMoney(command, "@amount", draft.Amount);
        AddNullableMoney(command, "@tendered", draft.TenderedAmount);
        AddMoney(command, "@change", draft.ChangeAmount);
        command.Parameters.AddWithValue("@reference", (object?)draft.ExternalReference ?? DBNull.Value);
        command.Parameters.AddWithValue("@user", createdByUserId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<(Guid BusinessId, Guid BranchId)?> ReadSaleAsync(
        SqlConnection connection,
        Guid saleId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT BusinessID, BranchID FROM dbo.Sales WHERE SaleID = @id;",
            connection);
        command.Parameters.AddWithValue("@id", saleId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetGuid(0), reader.GetGuid(1));
    }

    private static async Task<(Guid BusinessId, Guid BranchId, string Status)?> ReadSessionAsync(
        SqlConnection connection,
        Guid cashSessionId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT BusinessID, BranchID, Status FROM dbo.POSCashSessions WHERE CashSessionID = @id;",
            connection);
        command.Parameters.AddWithValue("@id", cashSessionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return (reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2));
    }

    private static async Task<bool> UserIsActiveAsync(
        SqlConnection connection,
        Guid userId,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            "SELECT 1 FROM dbo.Users WHERE UserID = @id AND IsActive = 1;",
            connection);
        command.Parameters.AddWithValue("@id", userId);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<SalePaymentRecord?> FindByKeyAsync(
        SqlConnection connection,
        Guid saleId,
        Guid clientPaymentKey,
        CancellationToken cancellationToken)
    {
        await using var command = new SqlCommand(
            """
            SELECT SalePaymentID, SaleID, CashSessionID, ClientPaymentKey, PaymentMethodCode,
                   Amount, TenderedAmount, ChangeAmount, ExternalReference
            FROM dbo.SalePayments
            WHERE SaleID = @sale AND ClientPaymentKey = @key;
            """,
            connection);
        command.Parameters.AddWithValue("@sale", saleId);
        command.Parameters.AddWithValue("@key", clientPaymentKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SalePaymentRecord(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetString(4),
            reader.GetDecimal(5),
            reader.IsDBNull(6) ? null : reader.GetDecimal(6),
            reader.GetDecimal(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            true);
    }

    private static SalePaymentRecord ToRecord(
        Guid paymentId,
        Guid saleId,
        Guid cashSessionId,
        SalePaymentDraft draft,
        bool replay) =>
        new(
            paymentId,
            saleId,
            cashSessionId,
            draft.ClientPaymentKey,
            draft.Method,
            draft.Amount,
            draft.TenderedAmount,
            draft.ChangeAmount,
            draft.ExternalReference,
            replay);

    private static void AddMoney(SqlCommand command, string name, decimal value)
    {
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Decimal)
        {
            Precision = 19,
            Scale = 4,
            Value = value,
        });
    }

    private static void AddNullableMoney(SqlCommand command, string name, decimal? value)
    {
        command.Parameters.Add(new SqlParameter(name, SqlDbType.Decimal)
        {
            Precision = 19,
            Scale = 4,
            Value = (object?)value ?? DBNull.Value,
        });
    }
}
