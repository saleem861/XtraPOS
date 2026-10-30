namespace XtraPOS.Api.Sales;

public static class PaymentMethodCodes
{
    public const string Cash = "Cash";
    public const string Card = "Card";
}

public static class SalePaymentCodes
{
    public const string InvalidAmount = "INVALID_PAYMENT_AMOUNT";
    public const string InvalidTenderedAmount = "INVALID_TENDERED_AMOUNT";
    public const string InvalidMethod = "INVALID_PAYMENT_METHOD";
    public const string InvalidKey = "INVALID_CLIENT_PAYMENT_KEY";
    public const string InvalidReference = "INVALID_EXTERNAL_REFERENCE";
    public const string SaleNotFound = "SALE_NOT_FOUND";
    public const string CashSessionInvalid = "CASH_SESSION_INVALID";
    public const string UserNotFound = "USER_NOT_FOUND";
    public const string KeyConflict = "PAYMENT_KEY_CONFLICT";
}

public sealed class SalePaymentException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Future checkout input. The client does not send branch, sale, cash session, or user identity.
/// </summary>
public sealed class CheckoutPaymentInput
{
    public Guid? ClientPaymentKey { get; set; }
    public string? Method { get; set; }
    public decimal? Amount { get; set; }
    public decimal? TenderedAmount { get; set; }
    public string? ExternalReference { get; set; }
}

public readonly record struct SalePaymentDraft(
    Guid ClientPaymentKey,
    string Method,
    decimal Amount,
    decimal? TenderedAmount,
    decimal ChangeAmount,
    string? ExternalReference);

public sealed record SalePaymentRecord(
    Guid SalePaymentId,
    Guid SaleId,
    Guid CashSessionId,
    Guid ClientPaymentKey,
    string PaymentMethodCode,
    decimal Amount,
    decimal? TenderedAmount,
    decimal ChangeAmount,
    string? ExternalReference,
    bool IdempotentReplay);
