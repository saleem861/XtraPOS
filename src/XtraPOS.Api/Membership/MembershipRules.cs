namespace XtraPOS.Api.Membership;

public enum MembershipRequestStatus
{
    Pending,
    Verified,
    Rejected,
    Expired,
    Cancelled
}

public sealed record MembershipVerificationFacts(
    bool FeatureEnabled,
    bool SessionPresent,
    bool SessionRevoked,
    bool SessionExpired,
    Guid? SessionCustomerId,
    Guid? SessionBusinessId,
    Guid RequestCustomerId,
    Guid RequestBusinessId,
    Guid RequestBranchId,
    Guid RequestCounterId,
    Guid RequestSaleContextId,
    Guid ExpectedBranchId,
    Guid ExpectedCounterId,
    Guid ExpectedSaleContextId,
    MembershipRequestStatus Status,
    DateTime ExpiresAtUtc,
    DateTime NowUtc,
    bool CustomerActive,
    bool TokenMatches);

public static class MembershipRules
{
    public static string? RejectionCode(MembershipVerificationFacts facts)
    {
        if (!facts.FeatureEnabled) return "MEMBERSHIP_SERVER_UNAVAILABLE";
        if (!facts.SessionPresent) return "MEMBERSHIP_SESSION_REQUIRED";
        if (facts.SessionRevoked) return "MEMBERSHIP_SESSION_REVOKED";
        if (facts.SessionExpired) return "MEMBERSHIP_SESSION_EXPIRED";
        if (!facts.TokenMatches) return "MEMBERSHIP_REQUEST_NOT_FOUND";
        if (facts.SessionBusinessId != facts.RequestBusinessId) return "MEMBERSHIP_WRONG_BUSINESS";
        if (facts.SessionCustomerId != facts.RequestCustomerId) return "MEMBERSHIP_WRONG_CUSTOMER";
        if (!facts.CustomerActive) return "MEMBERSHIP_CUSTOMER_INACTIVE";
        if (facts.Status == MembershipRequestStatus.Cancelled) return "MEMBERSHIP_QR_CANCELLED";
        if (facts.Status == MembershipRequestStatus.Verified) return "MEMBERSHIP_QR_ALREADY_USED";
        if (facts.Status == MembershipRequestStatus.Expired || facts.NowUtc >= facts.ExpiresAtUtc) return "MEMBERSHIP_QR_EXPIRED";
        if (facts.Status != MembershipRequestStatus.Pending) return "MEMBERSHIP_QR_ALREADY_USED";
        if (facts.RequestBranchId != facts.ExpectedBranchId || facts.RequestCounterId != facts.ExpectedCounterId) return "MEMBERSHIP_WRONG_BUSINESS";
        if (facts.RequestSaleContextId != facts.ExpectedSaleContextId) return "MEMBERSHIP_QR_CANCELLED";
        return null;
    }

    public static bool GetMayVerify() => false;
}
