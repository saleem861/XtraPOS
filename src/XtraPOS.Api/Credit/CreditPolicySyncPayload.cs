namespace XtraPOS.Api.Credit;

public sealed record CreditPolicySyncPayload(
    Guid CustomerId,
    bool CreditEnabled,
    decimal CreditLimit,
    int PaymentTermDays,
    string CreditStatus,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    Guid? UpdatedBy);
