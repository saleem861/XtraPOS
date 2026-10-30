namespace XtraPOS.Api.Credit;

public enum CreditDecision
{
    Approved,
    CreditDisabled,
    AccountInactive,
    LimitExceeded,
    InvalidAmount,
}

public static class CreditAvailability
{
    public const string AccountingCurrency = "ILS";

    public static bool HoldsCredit(string status, DateTime expiresAtUtc, DateTime utcNow)
    {
        if (string.Equals(status, "Posting", StringComparison.Ordinal))
        {
            return true;
        }

        return expiresAtUtc > utcNow
            && (string.Equals(status, "Pending", StringComparison.Ordinal)
                || string.Equals(status, "Authorized", StringComparison.Ordinal));
    }

    public static decimal Available(decimal creditLimit, decimal outstandingDebt, decimal activeReservations)
    {
        var raw = creditLimit - outstandingDebt - activeReservations;
        return raw < 0m ? 0m : raw;
    }

    public static CreditDecision Decide(bool creditEnabled, string creditStatus, decimal creditLimit, decimal outstandingDebt, decimal activeReservations, decimal requestedAmount)
    {
        if (requestedAmount <= 0m)
        {
            return CreditDecision.InvalidAmount;
        }

        if (!creditEnabled || creditLimit <= 0m)
        {
            return CreditDecision.CreditDisabled;
        }

        if (!string.Equals(creditStatus, "Active", StringComparison.Ordinal))
        {
            return CreditDecision.AccountInactive;
        }

        var available = Available(creditLimit, outstandingDebt, activeReservations);
        return requestedAmount <= available ? CreditDecision.Approved : CreditDecision.LimitExceeded;
    }
}
