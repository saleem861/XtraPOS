using XtraPOS.Api.Membership;
using Xunit;

namespace XtraPOS.Api.Tests;

public class MembershipRulesTests
{
    private static MembershipVerificationFacts Facts(Action<MutableFacts> change)
    {
        var facts = new MutableFacts();
        change(facts);
        return facts.Build();
    }

    [Fact]
    public void GetNeverVerifies()
    {
        Assert.False(MembershipRules.GetMayVerify());
    }

    [Fact]
    public void PendingMatchingSessionVerifies()
    {
        var code = MembershipRules.RejectionCode(Facts(_ => { }));
        Assert.Null(code);
    }

    [Theory]
    [InlineData("session", "MEMBERSHIP_SESSION_REQUIRED")]
    [InlineData("revoked", "MEMBERSHIP_SESSION_REVOKED")]
    [InlineData("expiredSession", "MEMBERSHIP_SESSION_EXPIRED")]
    [InlineData("wrongCustomer", "MEMBERSHIP_WRONG_CUSTOMER")]
    [InlineData("wrongBusiness", "MEMBERSHIP_WRONG_BUSINESS")]
    [InlineData("expiredQr", "MEMBERSHIP_QR_EXPIRED")]
    [InlineData("used", "MEMBERSHIP_QR_ALREADY_USED")]
    [InlineData("cancelled", "MEMBERSHIP_QR_CANCELLED")]
    [InlineData("inactive", "MEMBERSHIP_CUSTOMER_INACTIVE")]
    [InlineData("wrongCounter", "MEMBERSHIP_WRONG_BUSINESS")]
    [InlineData("wrongSale", "MEMBERSHIP_QR_CANCELLED")]
    public void RejectsUnsafeVerification(string mode, string expected)
    {
        var code = MembershipRules.RejectionCode(Facts(facts =>
        {
            if (mode == "session") facts.SessionPresent = false;
            if (mode == "revoked") facts.SessionRevoked = true;
            if (mode == "expiredSession") facts.SessionExpired = true;
            if (mode == "wrongCustomer") facts.SessionCustomerId = Guid.NewGuid();
            if (mode == "wrongBusiness") facts.SessionBusinessId = Guid.NewGuid();
            if (mode == "expiredQr") facts.NowUtc = facts.ExpiresAtUtc.AddSeconds(1);
            if (mode == "used") facts.Status = MembershipRequestStatus.Verified;
            if (mode == "cancelled") facts.Status = MembershipRequestStatus.Cancelled;
            if (mode == "inactive") facts.CustomerActive = false;
            if (mode == "wrongCounter") facts.CounterMismatch = true;
            if (mode == "wrongSale") facts.SaleMismatch = true;
        }));
        Assert.Equal(expected, code);
    }

    [Fact]
    public void ProviderRulesRejectBadTokens()
    {
        var now = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        var claims = new MembershipProviderClaims("Google", "sub", "https://accounts.google.com", "client", now.AddMinutes(5), "nonce", "a@b.c", true);
        Assert.Null(MembershipProviderRules.Rejection(claims, claims.Issuer, claims.Audience, "nonce", now));
        Assert.Equal("MEMBERSHIP_PROVIDER_SIGNATURE", MembershipProviderRules.Rejection(claims with { SignatureValid = false }, claims.Issuer, claims.Audience, "nonce", now));
        Assert.Equal("MEMBERSHIP_PROVIDER_ISSUER", MembershipProviderRules.Rejection(claims with { Issuer = "https://appleid.apple.com" }, claims.Issuer, claims.Audience, "nonce", now));
        Assert.Equal("MEMBERSHIP_PROVIDER_AUDIENCE", MembershipProviderRules.Rejection(claims, claims.Issuer, "other", "nonce", now));
        Assert.Equal("MEMBERSHIP_PROVIDER_EXPIRED", MembershipProviderRules.Rejection(claims with { ExpiresAtUtc = now.AddMinutes(-1) }, claims.Issuer, claims.Audience, "nonce", now));
        Assert.Equal("MEMBERSHIP_PROVIDER_NONCE", MembershipProviderRules.Rejection(claims, claims.Issuer, claims.Audience, "other", now));
        Assert.Null(MembershipProviderRules.Rejection(claims with { Email = "relay@privaterelay.appleid.com" }, claims.Issuer, claims.Audience, "nonce", now));
    }

    [Fact]
    public void PendingEnrollmentCommitsOnlyAfterProofAndRejectsReuse()
    {
        var customer = Guid.NewGuid();
        var now = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal("MEMBERSHIP_OWNERSHIP_PROOF_REQUIRED", MembershipEnrollmentRules.CommitBlock(false, now.AddMinutes(10), now, null, customer, false));
        Assert.Equal("MEMBERSHIP_ENROLLMENT_EXPIRED", MembershipEnrollmentRules.CommitBlock(true, now.AddMinutes(-1), now, null, customer, false));
        Assert.Null(MembershipEnrollmentRules.CommitBlock(true, now.AddMinutes(10), now, null, customer, false));
        Assert.Equal("MEMBERSHIP_PROVIDER_CONFLICT", MembershipEnrollmentRules.CommitBlock(true, now.AddMinutes(10), now, Guid.NewGuid(), customer, false));
        Assert.Null(MembershipEnrollmentRules.CommitBlock(true, now, now, customer, customer, true));
        Assert.Equal("MEMBERSHIP_PROVIDER_CONFLICT", MembershipEnrollmentRules.CommitBlock(true, now, now, Guid.NewGuid(), customer, true));
    }

    [Fact]
    public void LinkedIdentitySkipsAnotherOtpAndConflictsStayRejected()
    {
        var customer = Guid.NewGuid();
        Assert.Null(MembershipLinkPolicy.Decide(customer, customer, false));
        Assert.Equal("MEMBERSHIP_PROVIDER_CONFLICT", MembershipLinkPolicy.Decide(Guid.NewGuid(), customer, true));
        Assert.Equal("MEMBERSHIP_OWNERSHIP_PROOF_REQUIRED", MembershipLinkPolicy.Decide(null, customer, false));
    }

    [Fact]
    public void PkceChallengeMatchesVerifier()
    {
        var (verifier, challenge) = MembershipPkce.Create();
        var expected = Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.Equal(expected, challenge);
        Assert.Contains("code_challenge_method=S256", MembershipPkce.AuthorizeUrl("Google", "client", "https://test-pos.sharbelfashion.com/api/membership/auth/google/callback", "state", "nonce", challenge));
    }

    [Fact]
    public void MigrationIsAdditive()
    {
        using var stream = typeof(XtraPOS.Api.Data.Central.CentralMigrationRunner).Assembly.GetManifestResourceStream("XtraPOS.Api.Data.Central.Migrations.019_SmartMembership.sql");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();
        Assert.Contains("UQ_CustomerMembershipIdentities_ActiveSubject", sql);
        Assert.DoesNotContain("INSERT INTO dbo.Customers", sql);
        Assert.DoesNotContain("DROP TABLE", sql);
    }

    private sealed class MutableFacts
    {
        public bool SessionPresent { get; set; } = true;
        public bool SessionRevoked { get; set; }
        public bool SessionExpired { get; set; }
        public Guid CustomerId { get; } = Guid.NewGuid();
        public Guid BusinessId { get; } = Guid.NewGuid();
        public Guid SessionCustomerId { get; set; }
        public Guid SessionBusinessId { get; set; }
        public MembershipRequestStatus Status { get; set; } = MembershipRequestStatus.Pending;
        public DateTime ExpiresAtUtc { get; } = new(2026, 11, 2, 0, 1, 0, DateTimeKind.Utc);
        public DateTime NowUtc { get; set; } = new(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        public bool CustomerActive { get; set; } = true;
        public bool CounterMismatch { get; set; }
        public bool SaleMismatch { get; set; }
        public Guid BranchId { get; } = Guid.NewGuid();
        public Guid CounterId { get; } = Guid.NewGuid();
        public Guid SaleId { get; } = Guid.NewGuid();

        public MembershipVerificationFacts Build() => new(
            true,
            SessionPresent,
            SessionRevoked,
            SessionExpired,
            SessionCustomerId == Guid.Empty ? CustomerId : SessionCustomerId,
            SessionBusinessId == Guid.Empty ? BusinessId : SessionBusinessId,
            CustomerId,
            BusinessId,
            BranchId,
            CounterId,
            SaleId,
            BranchId,
            CounterMismatch ? Guid.NewGuid() : CounterId,
            SaleMismatch ? Guid.NewGuid() : SaleId,
            Status,
            ExpiresAtUtc,
            NowUtc,
            CustomerActive,
            true);
    }
}
