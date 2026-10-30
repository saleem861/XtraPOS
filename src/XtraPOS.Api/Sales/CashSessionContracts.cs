namespace XtraPOS.Api.Sales;

public static class CashSessionCodes
{
    public const string InvalidOpeningAmount = "INVALID_OPENING_AMOUNT";
    public const string AlreadyOpen = "CASH_SESSION_ALREADY_OPEN";
    public const string NotOpen = "CASH_SESSION_NOT_OPEN";
    public const string AlreadyClosed = "CASH_SESSION_ALREADY_CLOSED";
    public const string InvalidCountedAmount = "INVALID_COUNTED_AMOUNT";
}

public static class CashSessionStatus
{
    public const string Open = "Open";
    public const string Closed = "Closed";
}

public sealed class CashSessionException(string code, string message, int statusCode) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record CashSessionState(
    Guid CashSessionId,
    string Status,
    string OpenedByDisplayName,
    DateTime OpenedAt,
    decimal OpeningCashAmount,
    decimal? ExpectedCashAmount,
    decimal? CountedCashAmount,
    decimal? CashDifference,
    DateTime? ClosedAt);

public abstract record CashSessionOpenResult(CashSessionState Session);

public sealed record CashSessionOpened(CashSessionState Session) : CashSessionOpenResult(Session);

public sealed record CashSessionAlreadyOpen(CashSessionState Session) : CashSessionOpenResult(Session);
